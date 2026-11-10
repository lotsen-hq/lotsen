using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Git;
using Lotsen.Hosting;
using Lotsen.Operations;
using Lotsen.Pinning;
using Lotsen.Platforms;
using Lotsen.Tours;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>
/// Covers how the reviewer deletes a tour with <c>delete_tour</c>, the <c>tourDeleted</c> event that every page receives, the writes in flight that store nothing once the tour is gone, and
/// how <c>delete_tour</c> and <c>prune_clones</c> remove the clones that no tour refers to.
/// </summary>
/// <remarks>The fixture's pull request changes <c>a.txt</c> on <c>feature</c>, while <c>main</c> moves on.</remarks>
[Trait("Category", "Store")]
public sealed class TourDeleteTests : IDisposable
{
   private const string Plan = """{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "title": "S", "chapter": "a", "kind": "inspect" }] }""";

   private const string Stop = """{ "headline": "The value changes.", "passages": [{ "side": "head", "path": "a.txt", "startLine": 1, "endLine": 1, "role": "primary" }] }""";

   private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

   private readonly GitFixture _git = new();
   private readonly GitFixture _other = new();
   private readonly RecordingLoggerProvider _logs = new();
   private readonly CommitId _head;
   private readonly CommitId _target;

   public TourDeleteTests()
   {
      _git.Commit("Base", ("a.txt", "one\n"));
      _git.Checkout("feature", create: true);
      _head = _git.Commit("Push", ("a.txt", "two\n"));
      _git.Checkout("main");
      _target = _git.Commit("Main moves on", ("c.txt", "main\n"));
      _other.Commit("Another repository", ("b.txt", "b\n"));
   }

   private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

   [Fact]
   public async Task Deleted_tour_has_no_file_and_its_reads_and_writes_are_refused_as_not_found()
   {
      await using var harness = await StartAsync();
      var calls = new ThreadCalls(harness);
      var kept = await CreatePinnedAsync(harness);
      var tourId = await CreatePinnedAsync(harness);

      var deleted = await calls.CallAsync(await calls.UiAsync(On("delete_tour", tourId)));

      Assert.True(JsonNode.DeepEquals(new JsonObject { ["tourId"] = tourId }, deleted), $"The answer is {deleted}.");
      Assert.False(File.Exists(Path.Combine(harness.DataDirectory.ToursPath, $"{tourId}.json")));
      var listed = await harness.CallAsync("list_tours");
      Assert.Equal([kept], listed["tours"]!.AsArray().Select(row => (string)row!["id"]!));
      using var read = await harness.SendJsonAsync(On("read_state", tourId));
      var error = await ThreadCalls.ErrorAsync(read, HttpStatusCode.NotFound, "tourNotFound");
      Assert.Equal($"tourId: no tour has the ID '{tourId}'.", (string)error["reason"]!);
      using var again = await calls.SendAsync(await calls.UiAsync(On("delete_tour", tourId)));
      await ThreadCalls.ErrorAsync(again, HttpStatusCode.NotFound, "tourNotFound");
      using var archive = await calls.SendAsync(await calls.UiAsync(On("set_tour_archived", tourId)), """{ "archived": true }""");
      await ThreadCalls.ErrorAsync(archive, HttpStatusCode.NotFound, "tourNotFound");
      Assert.False(File.Exists(Path.Combine(harness.DataDirectory.ToursPath, $"{tourId}.json")));
   }

   [Fact]
   public async Task Every_open_page_receives_the_tourDeleted_event()
   {
      await using var harness = await StartAsync();
      var calls = new ThreadCalls(harness);
      var tourId = await CreatePinnedAsync(harness);
      await using var first = await EventStream.OpenAsync(harness, ServerHarness.Request(HttpMethod.Get, "api/events", await harness.UiCredentialAsync()));
      await using var second = await EventStream.OpenAsync(harness, ServerHarness.Request(HttpMethod.Get, "api/events", await harness.UiCredentialAsync()));

      await calls.CallAsync(await calls.UiAsync(On("delete_tour", tourId)));

      foreach (var page in new[] { first, second })
      {
         var next = await page.NextAsync(ThreadCalls.EventWait);
         Assert.Equal(ServerEvents.TourDeleted.Name, next.Type);
         Assert.True(JsonNode.DeepEquals(new JsonObject { ["tourId"] = tourId }, next.Data), $"The event is {next.Data}.");
      }
   }

   [Fact]
   public async Task Deleting_the_last_tour_of_a_repository_removes_its_clone()
   {
      await using var harness = await StartAsync();
      var calls = new ThreadCalls(harness);
      var tourId = await CreatePinnedAsync(harness);
      Assert.Single(Clones(harness));

      await calls.CallAsync(await calls.UiAsync(On("delete_tour", tourId)));
      await harness.PinsIdleAsync().WaitAsync(Wait, Cancellation);

      Assert.Empty(Clones(harness));
   }

   [Fact]
   public async Task Deleting_one_of_two_tours_of_a_repository_keeps_its_clone()
   {
      await using var harness = await StartAsync();
      var calls = new ThreadCalls(harness);
      var deleted = await CreatePinnedAsync(harness);
      var kept = await CreatePinnedAsync(harness);
      var clone = Assert.Single(Clones(harness));

      await calls.CallAsync(await calls.UiAsync(On("delete_tour", deleted)));
      await harness.PinsIdleAsync().WaitAsync(Wait, Cancellation);

      Assert.Equal([clone], Clones(harness));
      var files = await harness.CallAsync(On("list_changed_files", kept));
      Assert.Equal(["a.txt"], files["files"]!.AsArray().Select(file => (string)file!["path"]!));
   }

   /// <summary>The server's cache clones a repository that no tour refers to, and a crashed clone left a temporary clone of the tour's own repository.</summary>
   [Fact]
   public async Task Prune_on_request_removes_unreferenced_and_temporary_clones_and_says_how_many()
   {
      await using var harness = await StartAsync();
      var calls = new ThreadCalls(harness);
      var tourId = await CreatePinnedAsync(harness);
      var clone = Assert.Single(Clones(harness));
      (await harness.Server.Services.GetRequiredService<CloneCache>().OpenAsync(_other.Remote(), [], FetchPolicy.Sync, Cancellation)).Dispose();
      Directory.CreateDirectory($"{clone}.999999.tmp");

      var pruned = await calls.CallAsync(await calls.UiAsync("prune_clones"));
      var again = await calls.CallAsync(await calls.UiAsync("prune_clones"));

      Assert.True(JsonNode.DeepEquals(new JsonObject { ["removed"] = 2 }, pruned), $"The answer is {pruned}.");
      Assert.True(JsonNode.DeepEquals(new JsonObject { ["removed"] = 0 }, again), $"The second answer is {again}.");
      Assert.Equal([clone], Clones(harness));
      Assert.Equal(["a.txt"], (await harness.CallAsync(On("list_changed_files", tourId)))["files"]!.AsArray().Select(file => (string)file!["path"]!));
   }

   /// <summary>
   /// The cache also holds the clone of a repository that no tour refers to. A second tour of the kept tour's repository then becomes unreadable: half written, or stored with another schema
   /// version.
   /// </summary>
   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public async Task Prune_on_request_passes_over_a_tour_of_another_schema_version_and_is_refused_while_another_tour_cannot_be_read(bool otherSchema)
   {
      await using var harness = await StartAsync();
      var calls = new ThreadCalls(harness);
      await CreatePinnedAsync(harness);
      var unreadable = await CreatePinnedAsync(harness);
      (await harness.Server.Services.GetRequiredService<CloneCache>().OpenAsync(_other.Remote(), [], FetchPolicy.Sync, Cancellation)).Dispose();
      Break(harness, unreadable, otherSchema);

      using var pruned = await calls.SendAsync(await calls.UiAsync("prune_clones"));

      if (otherSchema)
      {
         Assert.Equal(HttpStatusCode.OK, pruned.StatusCode);
         Assert.Single(Clones(harness));
      }
      else
      {
         var error = await ThreadCalls.ErrorAsync(pruned, HttpStatusCode.InternalServerError, "tourUnreadable");
         var file = Path.Combine(harness.DataDirectory.ToursPath, $"{unreadable}.json");
         Assert.Contains($"tour {unreadable} could not be read", (string)error["reason"]!);
         Assert.Contains($"{unreadable}.json in the tours directory of lotsen's data directory", (string)error["reason"]!);
         Assert.DoesNotContain(harness.DataDirectory.Path, (string)error["reason"]!);
         Assert.Equal(2, Clones(harness).Length);

         File.Delete(file);
         var again = await calls.CallAsync(await calls.UiAsync("prune_clones"));

         Assert.Equal(1, (int)again["removed"]!);
         Assert.Single(Clones(harness));
      }
   }

   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public async Task Prune_on_request_is_refused_while_a_tour_cannot_be_read_when_the_clone_directory_holds_no_clone(bool otherDirectory)
   {
      await using var harness = await StartAsync();
      var calls = new ThreadCalls(harness);
      var unreadable = await CreatePinnedAsync(harness);
      DirectoryTree.Delete(Assert.Single(Clones(harness)));
      if (otherDirectory)
      {
         Directory.CreateDirectory(Path.Combine(harness.DataDirectory.ClonesPath, "not-a-clone"));
      }

      Break(harness, unreadable, otherSchema: false);

      using var pruned = await calls.SendAsync(await calls.UiAsync("prune_clones"));

      var error = await ThreadCalls.ErrorAsync(pruned, HttpStatusCode.InternalServerError, "tourUnreadable");
      Assert.Contains($"tour {unreadable} could not be read", (string)error["reason"]!);
   }

   [Fact]
   public void Prune_refusal_for_several_unreadable_tours_names_each_tour_and_its_file()
   {
      var first = TourId.New();
      var second = TourId.New();

      var reason = OperationError.ToursUnreadable([(first, "a.json"), (second, "b.json")]).Reason;

      Assert.Contains($"tours {first}, {second} could not be read", reason);
      Assert.Contains("then prune again: a.json, b.json.", reason);
   }

   /// <summary>The deleted tour and an unreadable tour share one repository: the unreadable tour is half written, or stored with another schema version.</summary>
   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public async Task Deleting_the_last_readable_tour_of_a_repository_keeps_its_clone_while_another_tour_cannot_be_read(bool otherSchema)
   {
      await using var harness = await StartAsync();
      var calls = new ThreadCalls(harness);
      var deleted = await CreatePinnedAsync(harness);
      var unreadable = await CreatePinnedAsync(harness);
      Break(harness, unreadable, otherSchema);

      await calls.CallAsync(await calls.UiAsync(On("delete_tour", deleted)));
      await harness.PinsIdleAsync().WaitAsync(Wait, Cancellation);

      Assert.Equal(otherSchema ? 0 : 1, Clones(harness).Length);
   }

   /// <summary>The git double holds the pin in its download of the changed files, after the clone exists, while the tour is still pinning and so refers to no clone.</summary>
   [Fact]
   public async Task Prune_leaves_the_clone_that_a_running_pin_uses()
   {
      var started = Path.Combine(_git.ScratchPath, "download-started");
      var go = Path.Combine(_git.ScratchPath, "download-go");
      await using var harness = await StartAsync(GitThatWaitsInDiffTree(started, go));
      var calls = new ThreadCalls(harness);
      var tourId = (string)harness.CreateUnboundTour(ThreadCalls.CreateBody)["id"]!;
      await WaitForFileAsync(started);

      var pruned = await calls.CallAsync(await calls.UiAsync("prune_clones"));
      var kept = Clones(harness);
      File.WriteAllText(go, "");
      await harness.PinsIdleAsync().WaitAsync(Wait, Cancellation);

      Assert.Equal(0, (int)pruned["removed"]!);
      Assert.Single(kept);
      Assert.Equal("pinned", (string)(await harness.CallAsync(On("read_state", tourId)))["pin"]!["state"]!);
      Assert.Equal(kept, Clones(harness));
   }

   /// <summary>The hook runs the prune after the pin and before the pin is stored, while the tour is still pinning and so refers to no clone.</summary>
   [Fact]
   public async Task Prune_before_a_pin_is_stored_leaves_the_clone_of_the_pin()
   {
      await using var harness = await StartAsync();
      var calls = new ThreadCalls(harness);
      var pins = harness.Server.Services.GetRequiredService<TourPins>();
      JsonNode? pruned = null;
      pins.BeforePinStored = async _ => pruned = await calls.CallAsync(await calls.UiAsync("prune_clones"));

      var tourId = (string)harness.CreateUnboundTour(ThreadCalls.CreateBody)["id"]!;
      await harness.PinsIdleAsync().WaitAsync(Wait, Cancellation);
      pins.BeforePinStored = null;

      Assert.Equal(0, (int)pruned!["removed"]!);
      Assert.Single(Clones(harness));
      Assert.Equal("pinned", (string)(await harness.CallAsync(On("read_state", tourId)))["pin"]!["state"]!);
   }

   /// <summary>
   /// The new iteration comes from another spelling of the remote URL, so the check clones it apart from the pin's clone. The hook runs the prune before the check stores the available
   /// iteration that names that clone.
   /// </summary>
   [Fact]
   public async Task Prune_before_an_iteration_check_is_stored_leaves_the_clone_of_the_new_iteration()
   {
      var platform = PointAtFixture(new TestPlatform());
      await using var harness = await StartAsync(platform: platform);
      var calls = new ThreadCalls(harness);
      var tourId = await CreatePinnedAsync(harness);
      _git.Checkout("feature");
      var newHead = _git.Commit("Second push", ("a.txt", "three\n"));
      var otherSpelling = new Uri(_git.Url.AbsoluteUri + "/");
      platform.PullRequest(otherSpelling, newHead, _target, iteration: 2);
      var lifecycle = harness.Server.Services.GetRequiredService<IterationLifecycle>();
      JsonNode? pruned = null;
      lifecycle.BeforeCheckStored = async _ => pruned = await calls.CallAsync(await calls.UiAsync("prune_clones"));

      var checkedIteration = await calls.CallAsync(await calls.UiAsync(On("check_iteration", tourId)));
      lifecycle.BeforeCheckStored = null;

      Assert.Equal(0, (int)pruned!["removed"]!);
      Assert.Equal(otherSpelling.AbsoluteUri, (string)checkedIteration["availableIteration"]!["pin"]!["remoteUrl"]!);
      Assert.Equal(2, Clones(harness).Length);
   }

   /// <summary>The hook holds put_stop between its passage checks and its store update, which is where a delete lands.</summary>
   [Fact]
   public async Task Stop_whose_checks_end_after_the_delete_is_refused_and_brings_no_tour_back()
   {
      await using var harness = await StartAsync();
      var calls = new ThreadCalls(harness);
      var tourId = await CreatePinnedAsync(harness);
      var stopId = (string)(await harness.AgentCallAsync(On("set_plan", tourId), Plan))["plan"]!["stops"]![0]!["id"]!;
      var writer = harness.Server.Services.GetRequiredService<TourWriter>();
      writer.Hook = WriterHooks.AfterGit(async _ => await calls.CallAsync(await calls.UiAsync(On("delete_tour", tourId))), "put_stop");

      using var put = await harness.AgentSendJsonAsync(On("put_stop", tourId, stopId: stopId), Stop);
      writer.Hook = null;

      await ThreadCalls.ErrorAsync(put, HttpStatusCode.NotFound, "tourNotFound");
      Assert.Empty(Directory.EnumerateFileSystemEntries(harness.DataDirectory.ToursPath));
   }

   /// <summary>The platform holds the check's resolve, which finds a new head, until the tour is deleted.</summary>
   [Fact]
   public async Task Iteration_check_that_ends_after_the_delete_stores_nothing_and_publishes_nothing()
   {
      var platform = PointAtFixture(new TestPlatform());
      await using var harness = await StartAsync(platform: platform);
      var calls = new ThreadCalls(harness);
      var tourId = await CreatePinnedAsync(harness);
      _git.Checkout("feature");
      var newHead = _git.Commit("Second push", ("a.txt", "three\n"));
      var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      var resolves = platform.Resolves;
      platform.Answer(async cancellationToken =>
      {
         await release.Task.WaitAsync(cancellationToken);
         return new ResolvedPullRequest(new Uri(_git.Url.AbsoluteUri), newHead, _target, 2, PullRequestState.Completed);
      });
      await using var events = await EventStream.OpenAsync(harness, ServerHarness.Request(HttpMethod.Get, "api/events", await harness.UiCredentialAsync()));

      var check = calls.SendAsync(await calls.UiAsync(On("check_iteration", tourId)));
      await Poll.UntilAsync(() => platform.Resolves > resolves ? (object)true : null, Wait, () => "The iteration check did not ask the platform.");
      await calls.CallAsync(await calls.UiAsync(On("delete_tour", tourId)));
      release.SetResult();
      using var checkedIteration = await check.WaitAsync(Wait, Cancellation);
      await harness.PinsIdleAsync().WaitAsync(Wait, Cancellation);

      await ThreadCalls.ErrorAsync(checkedIteration, HttpStatusCode.NotFound, "tourNotFound");
      Assert.Empty(Directory.EnumerateFileSystemEntries(harness.DataDirectory.ToursPath));
      Assert.Equal(ServerEvents.TourDeleted.Name, (await events.NextAsync(ThreadCalls.EventWait)).Type);
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => events.NextAsync(TimeSpan.FromMilliseconds(500)));
   }

   public void Dispose()
   {
      _git.Dispose();
      _other.Dispose();
   }

   private static string[] Clones(ServerHarness harness) => Directory.Exists(harness.DataDirectory.ClonesPath) ? Directory.GetDirectories(harness.DataDirectory.ClonesPath) : [];

   // Makes the tour's file unreadable: stored with another schema version, or cut in half as by a partial write.
   private static void Break(ServerHarness harness, string tourId, bool otherSchema)
   {
      var path = Path.Combine(harness.DataDirectory.ToursPath, $"{tourId}.json");
      var content = File.ReadAllText(path);
      var current = $"\"schemaVersion\":{TourDocument.CurrentSchemaVersion}";
      Assert.Contains(current, content);
      File.WriteAllText(path, otherSchema ? content.Replace(current, $"\"schemaVersion\":{TourDocument.CurrentSchemaVersion - 1}") : content[..(content.Length / 2)]);
   }

   private async Task<string> CreatePinnedAsync(ServerHarness harness)
   {
      var tourId = (string)(await harness.AgentCallAsync("create_tour", ThreadCalls.CreateBody))["id"]!;
      await harness.PinsIdleAsync().WaitAsync(Wait, Cancellation);
      var pin = (await harness.CallAsync(On("read_state", tourId)))["pin"]!;
      var problems = _logs.Entries.Where(entry => entry.Level >= LogLevel.Warning).Select(entry => entry.Line);
      Assert.True((string?)pin["state"] == "pinned", $"The tour is not pinned: {pin.ToJsonString()}\nThe server's warnings and errors:\n{string.Join('\n', problems)}");
      return tourId;
   }

   private static async Task WaitForFileAsync(string path)
   {
      using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
      timeout.CancelAfter(Wait);
      while (!File.Exists(path))
      {
         await Task.Delay(50, timeout.Token);
      }
   }

   private TestPlatform PointAtFixture(TestPlatform platform)
   {
      platform.PullRequest(_git.Url, _head, _target);
      return platform;
   }

   // Returns git options that, for the diff-tree of the download of the changed files, mark the start and wait for the go file first.
   private static GitOptions GitThatWaitsInDiffTree(string started, string go) => GitDouble.Options(async run =>
   {
      if (run.Subcommand == "diff-tree")
      {
         File.WriteAllText(started, "");
         if (await run.HoldAsync(() => File.Exists(go)) is { } ended)
         {
            return ended;
         }
      }

      return await run.GitAsync();
   });

   private Task<ServerHarness> StartAsync(GitOptions? git = null, TestPlatform? platform = null) =>
      ServerHarness.StartAsync(platform: platform ?? PointAtFixture(new TestPlatform()), configure: options => options with { Git = git ?? options.Git, LoggerProvider = _logs });
}
