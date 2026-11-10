using System.Text.Json.Nodes;
using Lotsen.AzureDevOps;
using Lotsen.Credentials;
using Lotsen.Git;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>
/// Covers how the Azure DevOps platform pins a tour, with a fake Azure DevOps server whose pull request points at a fixture repository, and a fake Azure CLI.
/// </summary>
/// <remarks>
/// The fixture's pull request has two iterations. The source branch, <c>feature</c>, forks from <see cref="_base"/>, and <c>main</c> moves on after the fork, so the merge base is <see cref="_base"/>
/// and differs from the target.
/// </remarks>
[Trait("Category", "Pinning")]
public sealed class AzureDevOpsPinningTests : IDisposable
{
   private const string CreateBody = $$"""{ "prUrl": "{{FakeAzureDevOps.PrUrl}}", "familiarity": "some" }""";

   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

   private readonly GitFixture _git = new();
   private readonly FakeAzureCli _az = new();
   private readonly CommitId _base;
   private readonly CommitId _firstHead;
   private readonly CommitId _head;
   private readonly CommitId _target;

   public AzureDevOpsPinningTests()
   {
      _base = _git.Commit("Base", ("a.txt", "one\n"));
      _git.Checkout("feature", create: true);
      _firstHead = _git.Commit("First push", ("a.txt", "two\n"));
      _head = _git.Commit("Second push", ("b.txt", "new\n"));
      _git.Checkout("main");
      _target = _git.Commit("Main moves on", ("c.txt", "main\n"));
      _az.SignedIn("lotsen-test-token-" + Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddHours(1));
   }

   private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

   [Fact]
   public async Task Tour_is_pinned_to_the_latest_iteration_and_labeled_with_its_organization_project_and_repository()
   {
      await using var azureDevOps = new FakeAzureDevOps();
      PointAtFixture(azureDevOps);
      await using var harness = await StartAsync(azureDevOps);

      var created = harness.CreateUnboundTour(CreateBody);
      await harness.PinsIdleAsync().WaitAsync(Timeout, Cancellation);

      Assert.Equal(AzureDevOpsPlatform.PlatformId, (string)created["target"]!["platform"]!);
      AssertPinnedToFixture((await harness.CallAsync(On("read_state", (string)created["id"]!)))["pin"]);
      Assert.Equal("contoso/shop/web #42", (string)(await harness.CallAsync("list_tours"))["tours"]![0]!["label"]!);
   }

   [Fact]
   public async Task Azure_CLI_without_a_sign_in_ends_in_failed_with_the_az_login_fix()
   {
      _az.Answer(1, "", "ERROR: Please run 'az login' to setup account.");

      var reason = await FailureReasonAsync(PointAtFixture);

      Assert.Contains("Run 'az login'", reason, StringComparison.Ordinal);
   }

   /// <summary>
   /// The git remote is an <see cref="AuthenticatingGitServer"/> that accepts only the token of the second sign-in, while the fake Azure DevOps API accepts every token. The refusal rejects the
   /// first token, so the pin again runs the Azure CLI again.
   /// </summary>
   [Fact]
   public async Task Git_remote_that_refuses_the_token_ends_in_failed_with_the_az_login_fix_and_the_pin_again_sends_a_new_token()
   {
      _az.SignedIn("token-before", DateTimeOffset.UtcNow.AddHours(1));
      await using var gitServer = await AuthenticatingGitServer.StartAsync(_git.OriginPath, "Bearer token-after");
      await using var azureDevOps = new FakeAzureDevOps();
      azureDevOps.PullRequest(gitServer.RepositoryUrl, (_firstHead, _base), (_head, _target));
      await using var harness = await StartAsync(azureDevOps);
      var id = (string)harness.CreateUnboundTour(CreateBody)["id"]!;
      await harness.PinsIdleAsync().WaitAsync(Timeout, Cancellation);
      var failed = (await harness.CallAsync(On("read_state", id)))["pin"];

      _az.SignedIn("token-after", DateTimeOffset.UtcNow.AddHours(1));
      await harness.AgentCallAsync(On("pin_tour", id));
      await harness.PinsIdleAsync().WaitAsync(Timeout, Cancellation);

      Assert.Equal("failed", State(failed));
      Assert.Equal(
         $"Git could not clone {gitServer.RepositoryUrl}: the remote refused lotsen's credential. Run 'az login' with an account that can read the repository, then try again.",
         (string?)failed!["reason"]);
      Assert.Equal(2, _az.Runs.Count);
      Assert.Equal("pinned", State((await harness.CallAsync(On("read_state", id)))["pin"]));
   }

   [Theory]
   [InlineData(401, "not signed in or token rejected. Run 'az login'")]
   [InlineData(403, "no access")]
   [InlineData(404, "pull request not found")]
   public async Task Refusal_by_Azure_DevOps_ends_in_failed_with_its_reason(int status, string expected)
   {
      var reason = await FailureReasonAsync(azureDevOps => azureDevOps.Refuse(status));

      Assert.Contains(FakeAzureDevOps.PrUrl, reason, StringComparison.Ordinal);
      Assert.Contains(expected, reason, StringComparison.Ordinal);
   }

   [Fact]
   public async Task Unreachable_Azure_DevOps_ends_in_failed_with_its_reason()
   {
      using var unreachable = HeldPort.Refusing();

      var reason = await FailureReasonAsync(PointAtFixture, new Uri($"http://127.0.0.1:{unreachable.Port}/"));

      Assert.Contains("could not reach Azure DevOps", reason, StringComparison.Ordinal);
   }

   [Fact]
   public async Task Pull_request_without_iterations_ends_in_failed_with_its_reason()
   {
      var reason = await FailureReasonAsync(azureDevOps => azureDevOps.PullRequest(_git.Url));

      Assert.Equal($"Azure DevOps lists no updates of the pull request {FakeAzureDevOps.PrUrl}, so it has no commits to show.", reason);
   }

   /// <summary>
   /// A 401 or 403 refuses the token or its account, so the pin again after a new sign-in sends the new token. A 404 says nothing about the token, so the pin again keeps the cached one.
   /// </summary>
   [Theory]
   [InlineData(401, true)]
   [InlineData(403, true)]
   [InlineData(404, false)]
   public async Task Pinning_again_after_a_refusal_sends_a_new_token_only_when_the_token_was_refused(int status, bool newToken)
   {
      _az.SignedIn("token-before", DateTimeOffset.UtcNow.AddHours(1));
      await using var azureDevOps = new FakeAzureDevOps();
      azureDevOps.Refuse(status);
      await using var harness = await StartAsync(azureDevOps);
      var id = (string)harness.CreateUnboundTour(CreateBody)["id"]!;
      await harness.PinsIdleAsync().WaitAsync(Timeout, Cancellation);
      Assert.Equal("failed", State((await harness.CallAsync(On("read_state", id)))["pin"]));
      var refusedRequests = azureDevOps.Server.Requests.Count;

      _az.SignedIn("token-after", DateTimeOffset.UtcNow.AddHours(1));
      PointAtFixture(azureDevOps);
      await harness.AgentCallAsync(On("pin_tour", id));
      await harness.PinsIdleAsync().WaitAsync(Timeout, Cancellation);

      var expected = newToken ? "token-after" : "token-before";
      Assert.All(azureDevOps.Server.Requests.Skip(refusedRequests), request => Assert.Contains($"Authorization: Bearer {expected}\r\n", request, StringComparison.Ordinal));
      Assert.Equal(newToken ? 2 : 1, _az.Runs.Count);
      AssertPinnedToFixture((await harness.CallAsync(On("read_state", id)))["pin"]);
   }

   public void Dispose()
   {
      _az.Dispose();
      _git.Dispose();
   }

   private static string? State(JsonNode? pin) => (string?)pin?["state"];

   private void PointAtFixture(FakeAzureDevOps azureDevOps) => azureDevOps.PullRequest(_git.Url, (_firstHead, _base), (_head, _target));

   private void AssertPinnedToFixture(JsonNode? pin)
   {
      Assert.NotNull(pin);
      Assert.Equal("pinned", State(pin));
      Assert.Equal(_base.Value, (string)pin["mergeBase"]!);
      Assert.Equal(_head.Value, (string)pin["head"]!);
      Assert.Equal(2, (int)pin["iteration"]!);
      Assert.Equal(_git.Url.AbsoluteUri, (string)pin["remoteUrl"]!);
   }

   /// <summary>Creates a tour against a fake Azure DevOps that the setup prepares, and returns the reason of its failed pin.</summary>
   private async Task<string> FailureReasonAsync(Action<FakeAzureDevOps> setup, Uri? serverOverride = null)
   {
      await using var azureDevOps = new FakeAzureDevOps();
      setup(azureDevOps);
      await using var harness = await StartAsync(azureDevOps, serverOverride);
      await using var events = await EventStream.OpenAsync(harness);

      var id = (string)harness.CreateUnboundTour(CreateBody)["id"]!;

      await events.NextAsync(Timeout);
      var pinned = await events.NextAsync(Timeout);
      Assert.Equal(("tourPinned", "failed"), (pinned.Type, State(pinned.Data["pin"])));
      var read = await harness.CallAsync(On("read_state", id));
      Assert.True(JsonNode.DeepEquals(pinned.Data["pin"], read["pin"]), $"read_state shows another pin: {read}");
      return (string)read["pin"]!["reason"]!;
   }

   private Task<ServerHarness> StartAsync(FakeAzureDevOps azureDevOps, Uri? serverOverride = null) =>
      ServerHarness.StartAsync(configure: options => options with
      {
         Platforms = [AzureDevOpsPlatform.Factory(new AzureDevOpsOptions { ServerOverride = serverOverride ?? azureDevOps.Server.BaseUrl }, new AzureCliOptions { Executable = _az.Executable }, accessTokens: null)],
      });
}
