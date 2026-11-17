using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Git;
using Lotsen.Platforms;
using Lotsen.Tours;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>
/// Covers how a repository tour is pinned in the background to a snapshot of the one commit that its ref names, through a test platform whose repository points at a fixture repository.
/// </summary>
/// <remarks>The fixture's default branch, <c>main</c>, moves on after <c>feature</c> forks from it, so each of the two branches names its own commit.</remarks>
[Trait("Category", "Pinning")]
public sealed class RepositoryPinningTests : IDisposable
{
   private const string RepositoryUrl = $"https://{TestPlatform.DefaultHost}/shop";

   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

   private readonly GitFixture _git = new();
   private readonly CommitId _main;
   private readonly CommitId _feature;

   public RepositoryPinningTests()
   {
      _git.Commit("Base", ("a.txt", "one\n"));
      _git.Checkout("feature", create: true);
      _feature = _git.Commit("Feature", ("b.txt", "feature\n"));
      _git.Checkout("main");
      _main = _git.Commit("Main moves on", ("c.txt", "main\n"));
   }

   private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

   [Fact]
   public async Task Create_tour_with_a_repository_url_and_a_ref_pins_the_one_commit_that_the_ref_names()
   {
      await using var harness = await ServerHarness.StartAsync(platform: PointAtFixture(new TestPlatform()));

      var created = await harness.AgentCallAsync("create_tour", CreateBody("feature", words: "Start at the checkout."));
      var id = (string)created["id"]!;
      await harness.PinsIdleAsync().WaitAsync(Timeout, Cancellation);

      Assert.True(
         JsonNode.DeepEquals(JsonNode.Parse($$"""{ "kind": "repository", "url": "{{RepositoryUrl}}", "ref": "feature", "platform": "{{TestPlatform.DefaultId}}" }"""), created["target"]),
         created["target"]!.ToJsonString());
      Assert.Equal(("orient", "How the shop is built", "Start at the checkout."), ((string?)created["purpose"], (string?)created["title"], (string?)created["words"]));
      Assert.Equal("pinning", State(created["pin"]));
      var read = await harness.CallAsync(On("read_state", id));
      AssertSnapshotOf(_feature, read["pin"]);
      var clone = Assert.Single(Directory.GetDirectories(harness.DataDirectory.ClonesPath));
      Assert.Equal("commit", _git.Git(clone, "cat-file", "-t", _feature.Value).Trim());
   }

   /// <summary>The target keeps no ref, so the tour follows the default branch whatever its name.</summary>
   [Fact]
   public async Task Repository_url_without_a_ref_pins_the_default_branch_and_keeps_the_target_without_a_ref()
   {
      await using var harness = await ServerHarness.StartAsync(platform: PointAtFixture(new TestPlatform()));

      var id = await CreateAndAwaitPinsAsync(harness, CreateBody(@ref: null));

      var read = await harness.CallAsync(On("read_state", id));
      AssertSnapshotOf(_main, read["pin"]);
      Assert.False(read["target"]!.AsObject().ContainsKey("ref"), read["target"]!.ToJsonString());
   }

   [Fact]
   public async Task Ref_that_the_repository_does_not_have_ends_in_failed_with_the_platforms_reason()
   {
      await using var harness = await ServerHarness.StartAsync(platform: PointAtFixture(new TestPlatform()));

      var id = await CreateAndAwaitPinsAsync(harness, CreateBody("no-such-branch"));

      var pin = (await harness.CallAsync(On("read_state", id)))["pin"]!;
      Assert.Equal("failed", State(pin));
      Assert.Equal("The test platform has no branch, tag or commit 'no-such-branch' in the repository.", (string)pin["reason"]!);
   }

   [Fact]
   public async Task Commit_that_the_remote_lacks_ends_in_failed_with_gits_reason()
   {
      var platform = new TestPlatform();
      platform.Repository(_git.Url, CommitId.Parse(new string('1', 40)));
      await using var harness = await ServerHarness.StartAsync(platform: platform);

      var id = await CreateAndAwaitPinsAsync(harness, CreateBody(@ref: null));

      var pin = (await harness.CallAsync(On("read_state", id)))["pin"]!;
      Assert.Equal("failed", State(pin));
      Assert.StartsWith("Git could not fetch", (string)pin["reason"]!, StringComparison.Ordinal);
   }

   [Fact]
   public async Task Clone_authenticates_with_the_git_credential_of_the_repositorys_platform()
   {
      var credential = new TestGitCredential("lotsen-platform-token");
      await using var server = await AuthenticatingGitServer.StartAsync(_git.OriginPath, credential.Header);
      var platform = new TestPlatform { GitCredential = credential };
      platform.Repository(server.RepositoryUrl, _main, branches: new Dictionary<string, CommitId> { ["feature"] = _feature });
      await using var harness = await ServerHarness.StartAsync(platform: platform);

      var id = await CreateAndAwaitPinsAsync(harness, CreateBody("feature"));

      AssertSnapshotOf(_feature, (await harness.CallAsync(On("read_state", id)))["pin"], server.RepositoryUrl);
      Assert.True(credential.Answers > 0, "Git asked the platform's git credential for no answer.");
      Assert.True(server.AuthorizedRequests > 0);
   }

   [Fact]
   public async Task Repository_tour_whose_pin_failed_is_pinned_again_by_pin_tour()
   {
      var platform = new TestPlatform();
      platform.FailRepository(new PlatformException("Sign in, then try again."));
      await using var harness = await ServerHarness.StartAsync(platform: platform);
      var id = await CreateAndAwaitPinsAsync(harness, CreateBody("feature"));
      Assert.Equal("failed", State((await harness.CallAsync(On("read_state", id)))["pin"]));

      PointAtFixture(platform);
      var retried = await harness.AgentCallAsync(On("pin_tour", id));
      await harness.PinsIdleAsync().WaitAsync(Timeout, Cancellation);

      Assert.Equal("pinning", State(retried["pin"]));
      AssertSnapshotOf(_feature, (await harness.CallAsync(On("read_state", id)))["pin"]);
   }

   [Fact]
   public async Task Pinning_a_snapshot_again_is_refused_and_changes_nothing()
   {
      await using var harness = await ServerHarness.StartAsync(platform: PointAtFixture(new TestPlatform()));
      var id = await CreateAndAwaitPinsAsync(harness, CreateBody("feature"));
      var before = await harness.CallAsync(On("read_state", id));

      using var response = await harness.AgentSendJsonAsync(On("pin_tour", id));

      var error = await ThreadCalls.ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal($"tourId: tour {id} is already pinned to commit {_feature}. Only a tour whose pin failed, or stopped without a result, can be pinned again.", (string)error["reason"]!);
      Assert.True(JsonNode.DeepEquals(before, await harness.CallAsync(On("read_state", id))));
   }

   [Fact]
   public async Task Repository_tour_left_pinning_when_the_server_stops_is_pinned_on_the_next_start()
   {
      var platform = PointAtFixture(new TestPlatform(hold: true));
      await using var first = await ServerHarness.StartAsync(platform: platform);
      var id = (string)first.CreateUnboundTour(CreateBody("feature"))["id"]!;
      await platform.FirstResolve.WaitAsync(Timeout, Cancellation);

      await first.StopServerAsync().WaitAsync(Timeout, Cancellation);

      Assert.True(TourId.TryParse(id, out var tourId));
      Assert.IsType<TourPin.Pinning>(new FileTourStore(first.DataDirectory).Get(tourId)?.Pin);
      platform.Release();
      await using var second = await ServerHarness.StartAsync(dataDirectory: first.DataDirectory, platform: platform);
      await second.PinsIdleAsync().WaitAsync(Timeout, Cancellation);
      AssertSnapshotOf(_feature, (await second.CallAsync(On("read_state", id)))["pin"]);
   }

   public void Dispose() => _git.Dispose();

   /// <summary>Returns the input of <c>create_tour</c> for a tour of the purpose, orient by default, of the test platform's repository at the ref, or at its default branch for null.</summary>
   internal static string CreateBody(string? @ref, string? words = null, string purpose = "orient")
   {
      var repository = new JsonObject { ["url"] = RepositoryUrl, ["purpose"] = purpose, ["title"] = "How the shop is built" };
      if (@ref is not null)
      {
         repository["ref"] = @ref;
      }

      if (words is not null)
      {
         repository["words"] = words;
      }

      return new JsonObject { ["repository"] = repository, ["familiarity"] = "new" }.ToJsonString();
   }

   private static string? State(JsonNode? pin) => (string?)pin?["state"];

   private static async Task<string> CreateAndAwaitPinsAsync(ServerHarness harness, string body)
   {
      var id = (string)harness.CreateUnboundTour(body)["id"]!;
      await harness.PinsIdleAsync().WaitAsync(Timeout, Cancellation);
      return id;
   }

   private TestPlatform PointAtFixture(TestPlatform platform)
   {
      platform.Repository(_git.Url, _main, branches: new Dictionary<string, CommitId> { ["feature"] = _feature });
      return platform;
   }

   private void AssertSnapshotOf(CommitId head, JsonNode? pin, Uri? remoteUrl = null) =>
      Assert.True(
         JsonNode.DeepEquals(JsonNode.Parse($$"""{ "state": "snapshot", "head": "{{head}}", "remoteUrl": "{{(remoteUrl ?? _git.Url).AbsoluteUri}}" }"""), pin),
         pin?.ToJsonString());
}
