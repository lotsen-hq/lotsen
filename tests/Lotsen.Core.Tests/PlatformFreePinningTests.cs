using System.Text.Json.Nodes;
using Lotsen.Git;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>
/// Covers how a tour of a platform-free repository is pinned with git alone, against a fixture repository that the test-support git server serves over loopback, and how it reads files at its
/// snapshot.
/// </summary>
/// <remarks>
/// The fixture's default branch, <c>main</c>, moves on after <c>feature</c> forks from it. The annotated tag <c>v1</c> and the branch <c>both</c> share a name with each other's kind: <c>both</c>
/// is a tag on the base commit and a branch on <c>feature</c>.
/// </remarks>
[Trait("Category", "Pinning")]
public sealed class PlatformFreePinningTests : IAsyncLifetime
{
   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

   private readonly GitFixture _git = new();
   private readonly CommitId _base;
   private readonly CommitId _main;
   private readonly CommitId _feature;
   private AuthenticatingGitServer _server = null!;
   private ServerHarness _harness = null!;

   public PlatformFreePinningTests()
   {
      _base = _git.Commit("Base", ("a.txt", "one\n"));
      _git.Git(_git.OriginPath, "tag", "--annotate", "v1", "--message", "Release one");
      _git.Git(_git.OriginPath, "tag", "both");
      _git.Checkout("feature", create: true);
      _feature = _git.Commit("Feature", ("b.txt", "feature\n"));
      _git.Git(_git.OriginPath, "branch", "both");
      _git.Checkout("main");
      _main = _git.Commit("Main moves on", ("c.txt", "main\n"));
   }

   private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

   public async ValueTask InitializeAsync()
   {
      _server = await AuthenticatingGitServer.StartAsync(_git.OriginPath, accepted: null);
      _harness = await ServerHarness.StartAsync();
   }

   public async ValueTask DisposeAsync()
   {
      await _harness.DisposeAsync();
      await _server.DisposeAsync();
      _git.Dispose();
   }

   [Fact]
   public async Task Repository_url_that_no_platform_recognizes_pins_its_default_branch_with_git_and_keeps_a_target_without_a_platform()
   {
      var created = await _harness.AgentCallAsync("create_tour", CreateBody(_server.RepositoryUrl.AbsoluteUri + "/", @ref: null));
      await _harness.PinsIdleAsync().WaitAsync(Timeout, Cancellation);

      Assert.True(
         JsonNode.DeepEquals(JsonNode.Parse($$"""{ "kind": "repository", "url": "{{_server.RepositoryUrl.AbsoluteUri}}" }"""), created["target"]),
         created["target"]!.ToJsonString());
      AssertSnapshotOf(_main, (await _harness.CallAsync(On("read_state", (string)created["id"]!)))["pin"]);
   }

   [Theory]
   [InlineData("feature", "feature")]
   [InlineData("v1", "base")]
   [InlineData("both", "base")]
   public async Task Ref_resolves_through_git_to_its_commit_with_a_tag_before_a_branch_of_the_same_name(string @ref, string expected)
   {
      var id = await CreateAndAwaitPinsAsync(CreateBody(_server.RepositoryUrl.AbsoluteUri, @ref));

      AssertSnapshotOf(expected == "base" ? _base : _feature, (await _harness.CallAsync(On("read_state", id)))["pin"]);
   }

   [Fact]
   public async Task Full_commit_id_pins_that_commit()
   {
      var id = await CreateAndAwaitPinsAsync(CreateBody(_server.RepositoryUrl.AbsoluteUri, _feature.Value));

      AssertSnapshotOf(_feature, (await _harness.CallAsync(On("read_state", id)))["pin"]);
   }

   [Fact]
   public async Task Commit_that_the_remote_lacks_ends_in_failed_with_gits_reason()
   {
      var id = await CreateAndAwaitPinsAsync(CreateBody(_server.RepositoryUrl.AbsoluteUri, new string('1', 40)));

      var pin = (await _harness.CallAsync(On("read_state", id)))["pin"]!;
      Assert.Equal("failed", (string?)pin["state"]);
      Assert.StartsWith("Git could not fetch", (string)pin["reason"]!, StringComparison.Ordinal);
   }

   [Fact]
   public async Task Ref_that_the_repository_does_not_have_ends_in_failed_with_a_reason_that_names_it()
   {
      var id = await CreateAndAwaitPinsAsync(CreateBody(_server.RepositoryUrl.AbsoluteUri, "no-such-branch"));

      var pin = (await _harness.CallAsync(On("read_state", id)))["pin"]!;
      Assert.Equal("failed", (string?)pin["state"]);
      Assert.Equal("The repository has no branch or tag 'no-such-branch'. Give a branch, a tag or a full commit ID that the repository has.", (string)pin["reason"]!);
   }

   /// <summary>Git can never prompt, so the pin fails as soon as the remote asks, and git sends the remote no credential.</summary>
   [Fact]
   public async Task Remote_that_asks_for_a_credential_ends_in_failed_with_the_reason_that_only_public_repositories_work()
   {
      _server.Accept("Bearer private-only");

      var id = await CreateAndAwaitPinsAsync(CreateBody(_server.RepositoryUrl.AbsoluteUri, @ref: null));

      var pin = (await _harness.CallAsync(On("read_state", id)))["pin"]!;
      Assert.Equal("failed", (string?)pin["state"]);
      Assert.Equal(PlatformFreeRepositories.PrivateRefusal, (string)pin["reason"]!);
      Assert.Equal((0, 0), (_server.AuthorizedRequests, _server.RefusedRequests));
      Assert.True(_server.RequestsWithoutHeader > 0);
   }

   /// <summary>The redirect's target is outside the remote's URL, and git still asks lotsen there, so the reason stays the public-only one.</summary>
   [Fact]
   public async Task Remote_that_redirects_to_a_location_that_asks_for_a_credential_ends_in_failed_with_the_reason_that_only_public_repositories_work()
   {
      _server.Accept("Bearer private-only");
      var moved = _server.UrlOf("/moved");
      _server.Redirect(moved, _server.RepositoryUrl);

      var id = await CreateAndAwaitPinsAsync(CreateBody(moved.AbsoluteUri, @ref: null));

      var pin = (await _harness.CallAsync(On("read_state", id)))["pin"]!;
      Assert.Equal(("failed", PlatformFreeRepositories.PrivateRefusal), ((string?)pin["state"], (string?)pin["reason"]));
      Assert.Equal((0, 0), (_server.AuthorizedRequests, _server.RefusedRequests));
   }

   [Fact]
   public async Task Snapshot_reads_a_files_text_and_lists_a_directory_at_its_commit()
   {
      var id = await CreateAndAwaitPinsAsync(CreateBody(_server.RepositoryUrl.AbsoluteUri, "feature"));

      var text = await _harness.CallAsync(On("read_file_text", id), """{ "side": "head", "path": "b.txt" }""");
      var listed = await _harness.CallAsync(On("list_directory", id), """{ "side": "head" }""");

      Assert.Equal(("text", _feature.Value, "feature\n"), ((string)text["content"]!, (string)text["commit"]!, (string)text["text"]!));
      Assert.Equal(["a.txt", "b.txt"], listed["entries"]!.AsArray().Select(entry => (string)entry!["path"]!));
   }

   [Fact]
   public async Task Find_tours_matches_a_platform_free_repository_by_its_normalized_url_and_its_ref()
   {
      var feature = (string)(await _harness.AgentCallAsync("create_tour", CreateBody(_server.RepositoryUrl.AbsoluteUri + "/", "feature")))["id"]!;
      await _harness.AgentCallAsync("create_tour", CreateBody(_server.RepositoryUrl.AbsoluteUri, @ref: null));
      await _harness.AgentCallAsync("create_tour", CreateBody(_server.UrlOf("/other").AbsoluteUri, "feature"));
      await _harness.PinsIdleAsync().WaitAsync(Timeout, Cancellation);

      var found = await _harness.AgentCallAsync("find_tours", $$"""{ "repository": { "url": "{{_server.RepositoryUrl.AbsoluteUri}}", "ref": "feature" } }""");
      var all = await _harness.AgentCallAsync("find_tours", $$"""{ "repository": { "url": "{{_server.RepositoryUrl.AbsoluteUri}}" } }""");

      var tour = Assert.Single(found["tours"]!.AsArray())!;
      Assert.Equal((feature, $"{_server.RepositoryUrl.Authority}/repo@feature"), ((string)tour["id"]!, (string)tour["label"]!));
      Assert.Equal(2, all["tours"]!.AsArray().Count);
   }

   private static string CreateBody(string url, string? @ref)
   {
      var repository = new JsonObject { ["url"] = url, ["purpose"] = "orient", ["title"] = "How the shop is built" };
      if (@ref is not null)
      {
         repository["ref"] = @ref;
      }

      return new JsonObject { ["repository"] = repository, ["familiarity"] = "new" }.ToJsonString();
   }

   private async Task<string> CreateAndAwaitPinsAsync(string body)
   {
      var id = (string)_harness.CreateUnboundTour(body)["id"]!;
      await _harness.PinsIdleAsync().WaitAsync(Timeout, Cancellation);
      return id;
   }

   private void AssertSnapshotOf(CommitId head, JsonNode? pin) =>
      Assert.True(
         JsonNode.DeepEquals(JsonNode.Parse($$"""{ "state": "snapshot", "head": "{{head}}", "remoteUrl": "{{_server.RepositoryUrl.AbsoluteUri}}" }"""), pin),
         pin?.ToJsonString());
}
