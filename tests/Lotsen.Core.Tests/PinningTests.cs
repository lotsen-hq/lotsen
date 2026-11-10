using System.Net;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Lotsen.Git;
using Lotsen.Hosting;
using Lotsen.Operations;
using Lotsen.Platforms;
using Lotsen.Processes;
using Lotsen.Sessions;
using Lotsen.Tours;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>
/// Covers how a tour is pinned in the background to its pull request's merge base and head, through a test platform whose pull request points at a fixture repository.
/// </summary>
/// <remarks>
/// The fixture's pull request is in its second iteration. The source branch, <c>feature</c>, forks from <see cref="_base"/>, and <c>main</c> moves on after the fork, so the merge base is
/// <see cref="_base"/> and differs from the target.
/// </remarks>
[Trait("Category", "Pinning")]
public sealed class PinningTests : IDisposable
{
   private const string CreateBody = $$"""{ "prUrl": "{{TestPlatform.PrUrl}}", "familiarity": "some" }""";

   private const string PlanBody = """{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "title": "S", "chapter": "a", "kind": "risk" }] }""";

   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

   private readonly GitFixture _git = new();
   private readonly CommitId _base;
   private readonly CommitId _head;
   private readonly CommitId _target;

   public PinningTests()
   {
      _base = _git.Commit("Base", ("a.txt", "one\n"));
      _git.Checkout("feature", create: true);
      _git.Commit("First push", ("a.txt", "two\n"));
      _head = _git.Commit("Second push", ("b.txt", "new\n"));
      _git.Checkout("main");
      _target = _git.Commit("Main moves on", ("c.txt", "main\n"));
   }

   private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

   [Fact]
   public async Task Create_tour_returns_at_once_with_pinning_and_tourPinned_brings_the_merge_base_and_head()
   {
      var platform = PointAtFixture(new TestPlatform(hold: true));
      await using var harness = await ServerHarness.StartAsync(platform: platform);
      await using var events = await EventStream.OpenAsync(harness);

      var created = harness.CreateUnboundTour(CreateBody);
      await platform.FirstResolve.WaitAsync(Timeout, Cancellation);
      var listedWhilePinning = await harness.CallAsync("list_tours");
      platform.Release();
      var id = (string)created["id"]!;

      Assert.Equal("pinning", State(created["pin"]));
      Assert.Equal("pinning", State(listedWhilePinning["tours"]![0]!["pin"]));
      var announced = await events.NextAsync(Timeout);
      Assert.Equal(("tourCreated", "pinning"), (announced.Type, State(announced.Data["pin"])));

      var pinned = await events.NextAsync(Timeout);
      Assert.Equal(("tourPinned", id), (pinned.Type, (string)pinned.Data["tourId"]!));
      AssertPinnedToFixture(pinned.Data["pin"]);

      var read = await harness.CallAsync(On("read_state", id));
      var listed = await harness.CallAsync("list_tours");
      Assert.True(JsonNode.DeepEquals(pinned.Data["pin"], read["pin"]), $"read_state shows another pin: {read}");
      Assert.True(JsonNode.DeepEquals(read["pin"], listed["tours"]![0]!["pin"]), $"The list shows another pin: {listed}");
      Assert.Equal((string)pinned.Data["updatedAt"]!, (string)read["updatedAt"]!);
   }

   [Fact]
   public async Task Pin_stores_the_pull_request_state_that_the_platform_reported()
   {
      var platform = new TestPlatform();
      platform.PullRequest(_git.Url, _head, _target, iteration: 2, state: PullRequestState.Completed);
      await using var harness = await ServerHarness.StartAsync(platform: platform);

      var id = (string)harness.CreateUnboundTour(CreateBody)["id"]!;
      await harness.PinsIdleAsync().WaitAsync(Timeout, Cancellation);

      var row = (await harness.CallAsync("list_tours"))["tours"]!.AsArray().Single(tour => (string)tour!["id"]! == id)!;
      Assert.Equal(("pinned", "completed"), (State(row["pin"]), (string?)row["pullRequestState"]));
   }

   [Fact]
   public async Task Clone_authenticates_with_the_git_credential_of_the_tours_platform()
   {
      var credential = new TestGitCredential("lotsen-platform-token");
      await using var server = await AuthenticatingGitServer.StartAsync(_git.OriginPath, credential.Header);
      var platform = new TestPlatform { GitCredential = credential };
      platform.PullRequest(server.RepositoryUrl, _head, _target, iteration: 2);
      await using var harness = await ServerHarness.StartAsync(platform: platform);

      var id = await CreateAndAwaitPinsAsync(harness);

      AssertPinnedToFixture((await harness.CallAsync(On("read_state", id)))["pin"], server.RepositoryUrl);
      Assert.True(credential.Answers > 0, "Git asked the platform's git credential for no answer.");
      Assert.True(server.AuthorizedRequests > 0);
   }

   [Fact]
   public async Task Remote_that_refuses_the_platforms_credential_ends_in_failed_with_the_credentials_fix()
   {
      var credential = new TestGitCredential("lotsen-platform-token");
      await using var server = await AuthenticatingGitServer.StartAsync(_git.OriginPath, "Bearer another-token");
      var platform = new TestPlatform { GitCredential = credential };
      platform.PullRequest(server.RepositoryUrl, _head, _target, iteration: 2);

      var reason = await FailureReasonAsync(platform);

      Assert.Equal($"Git could not clone {server.RepositoryUrl}: the remote refused lotsen's credential. {TestGitCredential.Hint}", reason);
      Assert.Equal(1, credential.Refusals);
   }

   [Fact]
   public async Task Head_that_no_branch_holds_any_more_is_fetched_by_its_id()
   {
      var platform = PointAtFixture(new TestPlatform());
      _git.Git(_git.OriginPath, "branch", "--quiet", "-D", "feature");
      await using var harness = await ServerHarness.StartAsync(platform: platform);

      var id = await CreateAndAwaitPinsAsync(harness);

      AssertPinnedToFixture((await harness.CallAsync(On("read_state", id)))["pin"]);
   }

   [Fact]
   public async Task Pin_downloads_the_content_of_every_changed_file_into_the_clone()
   {
      await using var harness = await ServerHarness.StartAsync(platform: PointAtFixture(new TestPlatform()));

      var id = await CreateAndAwaitPinsAsync(harness);

      AssertPinnedToFixture((await harness.CallAsync(On("read_state", id)))["pin"]);
      var clone = Assert.Single(Directory.GetDirectories(harness.DataDirectory.ClonesPath));

      // With --missing=print, rev-list prints each object that the clone lacks with a leading "?" and downloads nothing.
      Assert.DoesNotContain(_git.Git(clone, "rev-list", "--objects", "--missing=print", _base.Value, _head.Value).Split('\n'), line => line.StartsWith('?'));
   }

   [Fact]
   public async Task Pin_whose_download_of_the_changed_files_fails_ends_pinned_with_a_warning()
   {
      var logs = new RecordingLoggerProvider();
      await using var harness = await StartAsync(PointAtFixture(new TestPlatform()), git: GitWithDiffTree(_ => Task.FromResult(GitRun.Exit(128, "fatal: the download broke\n"))), logs: logs);

      var id = await CreateAndAwaitPinsAsync(harness);

      AssertPinnedToFixture((await harness.CallAsync(On("read_state", id)))["pin"]);
      Assert.Contains(logs.Entries, entry => entry.Level == LogLevel.Warning && entry.Line.Contains(id, StringComparison.Ordinal) && entry.Line.Contains("the download broke.", StringComparison.Ordinal));
   }

   /// <summary>The git double holds the download until the run is canceled, so the server stops during the download.</summary>
   [Fact]
   public async Task Tour_whose_server_stops_during_the_download_of_the_changed_files_stays_pinning()
   {
      var started = Path.Combine(_git.ScratchPath, "download-started");
      await using var harness = await StartAsync(PointAtFixture(new TestPlatform()), git: GitWithDiffTree(async run =>
      {
         File.WriteAllText(started, "");
         return await run.DelayAsync(TimeSpan.FromSeconds(60)) ?? GitRun.Exit(0);
      }));
      var id = (string)harness.CreateUnboundTour(CreateBody)["id"]!;
      await WaitForFileAsync(started).WaitAsync(Timeout, Cancellation);

      await harness.StopServerAsync().WaitAsync(Timeout, Cancellation);

      Assert.True(TourId.TryParse(id, out var tourId));
      Assert.IsType<TourPin.Pinning>(new FileTourStore(harness.DataDirectory).Get(tourId)?.Pin);
   }

   [Fact]
   public async Task Platform_failure_ends_in_failed_with_its_reason()
   {
      var platform = new TestPlatform();
      platform.Fail("The pull request was not found. Check the URL.");

      var reason = await FailureReasonAsync(platform);

      Assert.Equal("The pull request was not found. Check the URL.", reason);
   }

   [Fact]
   public async Task Commit_that_the_repository_lacks_ends_in_failed_with_gits_reason_without_its_command_line()
   {
      var platform = new TestPlatform();
      platform.PullRequest(_git.Url, CommitId.Parse(new string('1', 40)), _target);

      var reason = await FailureReasonAsync(platform);

      Assert.StartsWith("Git could not fetch", reason, StringComparison.Ordinal);
      Assert.Contains("not our ref", reason, StringComparison.Ordinal);
      Assert.DoesNotContain("--", reason, StringComparison.Ordinal);
      Assert.DoesNotContain("refs/", reason, StringComparison.Ordinal);
   }

   [Fact]
   public async Task Repository_that_cannot_be_cloned_ends_in_failed_with_gits_reason()
   {
      var nowhere = new Uri(Path.Combine(_git.ScratchPath, "no-such-repository"));
      var platform = new TestPlatform();
      platform.PullRequest(nowhere, _head, _target);

      var reason = await FailureReasonAsync(platform);

      Assert.StartsWith($"Git could not clone {nowhere}", reason, StringComparison.Ordinal);
   }

   [Fact]
   public async Task Remote_that_lotsen_does_not_clone_ends_in_failed_with_the_reason()
   {
      var platform = new TestPlatform();
      platform.PullRequest(new Uri("ssh://platform.test/shop"), _head, _target);

      var reason = await FailureReasonAsync(platform);

      Assert.Contains("ssh://platform.test/shop", reason, StringComparison.Ordinal);
      Assert.Contains("clones only over https", reason, StringComparison.Ordinal);
   }

   /// <summary>The first server's platform has another ID, so the second server has no platform for the tour when it resumes the pin.</summary>
   [Fact]
   public async Task Tour_whose_platform_is_not_registered_ends_in_failed_naming_the_platform()
   {
      var other = new TestPlatform("other", TestPlatform.DefaultHost);
      await using var first = await ServerHarness.StartAsync(platform: other);
      var id = (string)first.CreateUnboundTour(CreateBody)["id"]!;
      await other.FirstResolve.WaitAsync(Timeout, Cancellation);
      await first.StopServerAsync().WaitAsync(Timeout, Cancellation);

      await using var second = await ServerHarness.StartAsync(dataDirectory: first.DataDirectory, platform: PointAtFixture(new TestPlatform()));
      await second.PinsIdleAsync().WaitAsync(Timeout, Cancellation);

      var pin = (await second.CallAsync(On("read_state", id)))["pin"]!;
      Assert.Equal("failed", State(pin));
      Assert.Contains("'other'", (string)pin["reason"]!, StringComparison.Ordinal);
   }

   [Fact]
   public async Task Pinning_again_after_a_failure_succeeds()
   {
      var platform = new TestPlatform();
      platform.Fail("Sign in, then try again.");
      await using var harness = await ServerHarness.StartAsync(platform: platform);
      await using var events = await EventStream.OpenAsync(harness);
      var id = (string)harness.CreateUnboundTour(CreateBody)["id"]!;
      await events.NextBesidesControlsAsync(Timeout);
      var failed = await events.NextBesidesControlsAsync(Timeout);
      Assert.Equal("failed", State(failed.Data["pin"]));

      PointAtFixture(platform);
      var retried = await harness.AgentCallAsync(On("pin_tour", id));

      Assert.Equal("pinning", State(retried["pin"]));
      var restarted = await events.NextBesidesControlsAsync(Timeout);
      Assert.Equal(("tourPinned", "pinning"), (restarted.Type, State(restarted.Data["pin"])));
      Assert.Equal((string)retried["updatedAt"]!, (string)restarted.Data["updatedAt"]!);
      var pinned = await events.NextBesidesControlsAsync(Timeout);
      Assert.Equal("tourPinned", pinned.Type);
      AssertPinnedToFixture(pinned.Data["pin"]);
      AssertPinnedToFixture((await harness.CallAsync(On("read_state", id)))["pin"]);
   }

   [Fact]
   public async Task Pinning_a_pinned_tour_again_is_refused_and_changes_nothing()
   {
      await using var harness = await ServerHarness.StartAsync(platform: PointAtFixture(new TestPlatform()));
      var id = await CreateAndAwaitPinsAsync(harness);
      var before = await harness.CallAsync(On("read_state", id));

      using var response = await harness.AgentSendJsonAsync(On("pin_tour", id));

      var error = JsonNode.Parse(await response.Content.ReadAsStringAsync(Cancellation))!;
      Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
      Assert.Equal("tourId", (string)error["field"]!);
      Assert.Contains("already pinned", (string)error["reason"]!, StringComparison.Ordinal);
      Assert.True(JsonNode.DeepEquals(before, await harness.CallAsync(On("read_state", id))));
   }

   [Fact]
   public async Task Pinning_a_tour_whose_pin_is_in_progress_returns_it_unchanged_and_starts_no_second_pin()
   {
      var platform = PointAtFixture(new TestPlatform(hold: true));
      await using var harness = await ServerHarness.StartAsync(platform: platform);
      var created = harness.CreateUnboundTour(CreateBody);
      var id = (string)created["id"]!;
      await platform.FirstResolve.WaitAsync(Timeout, Cancellation);

      var again = await harness.AgentCallAsync(On("pin_tour", id));
      platform.Release();
      await harness.PinsIdleAsync().WaitAsync(Timeout, Cancellation);

      Assert.True(JsonNode.DeepEquals(created, again), $"The tour changed: {again}");
      Assert.Equal(1, platform.Resolves);
      AssertPinnedToFixture((await harness.CallAsync(On("read_state", id)))["pin"]);
   }

   /// <summary>A tours directory without write permission makes the store of the first pin fail.</summary>
   [Fact]
   [UnsupportedOSPlatform("windows")]
   public async Task Pin_whose_result_could_not_be_stored_starts_again_on_pinning_again()
   {
      SkipOnWindows();
      var platform = PointAtFixture(new TestPlatform(hold: true));
      await using var harness = await ServerHarness.StartAsync(platform: platform);
      var id = (string)harness.CreateUnboundTour(CreateBody)["id"]!;
      await platform.FirstResolve.WaitAsync(Timeout, Cancellation);
      var toursPath = harness.DataDirectory.ToursPath;
      File.SetUnixFileMode(toursPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);
      try
      {
         platform.Release();
         await harness.PinsIdleAsync().WaitAsync(Timeout, Cancellation);
         Assert.Equal("pinning", State((await harness.CallAsync(On("read_state", id)))["pin"]));
      }
      finally
      {
         File.SetUnixFileMode(toursPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
      }

      var again = await harness.AgentCallAsync(On("pin_tour", id));
      await harness.PinsIdleAsync().WaitAsync(Timeout, Cancellation);

      Assert.Equal("pinning", State(again["pin"]));
      AssertPinnedToFixture((await harness.CallAsync(On("read_state", id)))["pin"]);
   }

   /// <summary>
   /// The pin again lands after the first job stored its failure and before that job ends, and every later resolve waits until the test lets it go. So the second job is still running when the test
   /// pins the tour once more.
   /// </summary>
   [Fact]
   public async Task Pin_job_that_ends_after_a_newer_job_started_leaves_the_newer_job_registered()
   {
      var resolves = 0;
      var later = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      var platform = new TestPlatform();
      platform.Answer(async cancellationToken =>
      {
         if (Interlocked.Increment(ref resolves) > 1)
         {
            await later.Task.WaitAsync(cancellationToken);
         }

         throw new PlatformException("Not found.");
      });
      await using var harness = await ServerHarness.StartAsync(platform: platform);
      var pins = harness.Server.Services.GetRequiredService<TourPins>();
      var pinPass = Passes.Admit<Access.AnySession.Pass>("pin_tour", await harness.AgentCallerAsync());
      var firstJobEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      TourPin? restartedPin = null;
      var stored = 0;
      var ended = 0;
      pins.AfterPinStored = id =>
      {
         if (Interlocked.Increment(ref stored) == 1)
         {
            restartedPin = pins.PinTour(pinPass, new PinTourInput(id)).Value?.Pin;
         }

         return Task.CompletedTask;
      };
      pins.AfterPinJob = _ =>
      {
         if (Interlocked.Increment(ref ended) == 1)
         {
            firstJobEnded.SetResult();
         }
      };

      var id = (string)harness.CreateUnboundTour(CreateBody)["id"]!;
      await firstJobEnded.Task.WaitAsync(Timeout, Cancellation);
      var again = await harness.AgentCallAsync(On("pin_tour", id));
      later.SetResult();
      await harness.PinsIdleAsync().WaitAsync(Timeout, Cancellation);

      Assert.IsType<TourPin.Pinning>(restartedPin);
      Assert.Equal("pinning", State(again["pin"]));
      Assert.Equal(2, resolves);
      Assert.Equal("failed", State((await harness.CallAsync(On("read_state", id)))["pin"]));
   }

   /// <summary>The pin again lands after the first job failed to store its result and before that job ends, so it must already find the tour without a running job.</summary>
   [Fact]
   [UnsupportedOSPlatform("windows")]
   public async Task Pin_again_right_after_a_pin_could_not_be_stored_starts_the_pin()
   {
      SkipOnWindows();
      var platform = PointAtFixture(new TestPlatform(hold: true));
      await using var harness = await ServerHarness.StartAsync(platform: platform);
      var pins = harness.Server.Services.GetRequiredService<TourPins>();
      var pinPass = Passes.Admit<Access.AnySession.Pass>("pin_tour", await harness.AgentCallerAsync());
      var toursPath = harness.DataDirectory.ToursPath;
      TourPin? againPin = null;
      var stored = 0;
      pins.AfterPinStored = id =>
      {
         if (Interlocked.Increment(ref stored) == 1)
         {
            File.SetUnixFileMode(toursPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            againPin = pins.PinTour(pinPass, new PinTourInput(id)).Value?.Pin;
         }

         return Task.CompletedTask;
      };
      var id = (string)harness.CreateUnboundTour(CreateBody)["id"]!;
      await platform.FirstResolve.WaitAsync(Timeout, Cancellation);
      File.SetUnixFileMode(toursPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);

      platform.Release();
      await harness.PinsIdleAsync().WaitAsync(Timeout, Cancellation);

      Assert.IsType<TourPin.Pinning>(againPin);
      AssertPinnedToFixture((await harness.CallAsync(On("read_state", id)))["pin"]);
   }

   [Fact]
   public async Task Plan_set_while_the_pin_is_in_progress_is_kept_when_the_pin_is_stored()
   {
      var platform = PointAtFixture(new TestPlatform(hold: true));
      await using var harness = await ServerHarness.StartAsync(platform: platform);
      var id = (string)(await harness.AgentCallAsync("create_tour", CreateBody))["id"]!;
      await platform.FirstResolve.WaitAsync(Timeout, Cancellation);

      var planned = await harness.AgentCallAsync(On("set_plan", id), PlanBody);
      platform.Release();
      await harness.PinsIdleAsync().WaitAsync(Timeout, Cancellation);

      Assert.Equal("pinning", State(planned["pin"]));
      var read = await harness.CallAsync(On("read_state", id));
      AssertPinnedToFixture(read["pin"]);
      Assert.True(JsonNode.DeepEquals(planned["plan"], read["plan"]), $"The plan was lost: {read}");
   }

   [Fact]
   public async Task Tour_left_pinning_when_the_server_stops_is_pinned_on_the_next_start()
   {
      var platform = PointAtFixture(new TestPlatform(hold: true));
      await using var first = await ServerHarness.StartAsync(platform: platform);
      var id = (string)first.CreateUnboundTour(CreateBody)["id"]!;
      await platform.FirstResolve.WaitAsync(Timeout, Cancellation);

      await first.StopServerAsync().WaitAsync(Timeout, Cancellation);

      Assert.True(TourId.TryParse(id, out var tourId));
      Assert.IsType<TourPin.Pinning>(new FileTourStore(first.DataDirectory).Get(tourId)?.Pin);
      platform.Release();
      await using var second = await ServerHarness.StartAsync(dataDirectory: first.DataDirectory, platform: platform);
      await second.PinsIdleAsync().WaitAsync(Timeout, Cancellation);
      AssertPinnedToFixture((await second.CallAsync(On("read_state", id)))["pin"]);
   }

   [Fact]
   [UnsupportedOSPlatform("windows")]
   public async Task Server_starts_and_resumes_the_other_pins_when_a_tour_file_cannot_be_opened()
   {
      SkipOnWindows();
      var platform = PointAtFixture(new TestPlatform(hold: true));
      await using var first = await ServerHarness.StartAsync(platform: platform);
      var closed = (string)first.CreateUnboundTour(CreateBody)["id"]!;
      var open = (string)first.CreateUnboundTour(CreateBody)["id"]!;
      await platform.FirstResolve.WaitAsync(Timeout, Cancellation);
      await first.StopServerAsync().WaitAsync(Timeout, Cancellation);
      File.SetUnixFileMode(Path.Combine(first.DataDirectory.ToursPath, $"{closed}.json"), UnixFileMode.None);
      platform.Release();

      await using var second = await ServerHarness.StartAsync(dataDirectory: first.DataDirectory, platform: platform);
      await second.PinsIdleAsync().WaitAsync(Timeout, Cancellation);

      AssertPinnedToFixture((await second.CallAsync(On("read_state", open)))["pin"]);
   }

   /// <summary>The platform holds the pin's resolve until the reviewer deleted the tour, so the pin clones and stores after the delete.</summary>
   [Fact]
   public async Task Pin_that_ends_after_its_tour_was_deleted_brings_no_tour_back_and_its_clone_is_pruned()
   {
      var platform = PointAtFixture(new TestPlatform(hold: true));
      await using var harness = await ServerHarness.StartAsync(platform: platform);
      var calls = new ThreadCalls(harness);
      var id = (string)harness.CreateUnboundTour(CreateBody)["id"]!;
      await platform.FirstResolve.WaitAsync(Timeout, Cancellation);
      await using var events = await EventStream.OpenAsync(harness, ServerHarness.Request(HttpMethod.Get, "api/events", await harness.UiCredentialAsync()));

      await calls.CallAsync(await calls.UiAsync(On("delete_tour", id)));
      platform.Release();
      await harness.PinsIdleAsync().WaitAsync(Timeout, Cancellation);

      Assert.Empty(Directory.EnumerateFileSystemEntries(harness.DataDirectory.ToursPath));
      Assert.Empty((await harness.CallAsync("list_tours"))["tours"]!.AsArray());
      using var read = await harness.SendJsonAsync(On("read_state", id));
      Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
      Assert.Equal(ServerEvents.TourDeleted.Name, (await events.NextAsync(Timeout)).Type);
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => events.NextAsync(TimeSpan.FromMilliseconds(500)));
      Assert.Single(Directory.GetDirectories(harness.DataDirectory.ClonesPath));
      Assert.Equal(1, (int)(await calls.CallAsync(await calls.UiAsync("prune_clones")))["removed"]!);
      Assert.Empty(Directory.GetDirectories(harness.DataDirectory.ClonesPath));
   }

   public void Dispose() => _git.Dispose();

   private static string? State(JsonNode? pin) => (string?)pin?["state"];

   private static void SkipOnWindows() => Assert.SkipWhen(OperatingSystem.IsWindows(), "The test changes Unix file modes.");

   private TestPlatform PointAtFixture(TestPlatform platform)
   {
      platform.PullRequest(_git.Url, _head, _target, iteration: 2);
      return platform;
   }

   private void AssertPinnedToFixture(JsonNode? pin, Uri? remoteUrl = null)
   {
      Assert.NotNull(pin);
      Assert.Equal("pinned", State(pin));
      Assert.Equal(_base.Value, (string)pin["mergeBase"]!);
      Assert.Equal(_head.Value, (string)pin["head"]!);
      Assert.Equal(2, (int)pin["iteration"]!);
      Assert.Equal((remoteUrl ?? _git.Url).AbsoluteUri, (string)pin["remoteUrl"]!);
   }

   /// <summary>Creates a tour on the platform and returns the reason of its failed pin.</summary>
   private static async Task<string> FailureReasonAsync(TestPlatform platform)
   {
      await using var harness = await ServerHarness.StartAsync(platform: platform);
      await using var events = await EventStream.OpenAsync(harness);

      var id = (string)harness.CreateUnboundTour(CreateBody)["id"]!;

      await events.NextAsync(Timeout);
      var pinned = await events.NextAsync(Timeout);
      Assert.Equal(("tourPinned", "failed"), (pinned.Type, State(pinned.Data["pin"])));
      var read = await harness.CallAsync(On("read_state", id));
      Assert.True(JsonNode.DeepEquals(pinned.Data["pin"], read["pin"]), $"read_state shows another pin: {read}");
      return (string)read["pin"]!["reason"]!;
   }

   private static async Task<string> CreateAndAwaitPinsAsync(ServerHarness harness)
   {
      var id = (string)harness.CreateUnboundTour(CreateBody)["id"]!;
      await harness.PinsIdleAsync().WaitAsync(Timeout, Cancellation);
      return id;
   }

   private static async Task WaitForFileAsync(string path)
   {
      while (!File.Exists(path))
      {
         await Task.Delay(50, Cancellation);
      }
   }

   // Returns git options whose diff-tree runs end with the step's outcome. During a pin, only the download of the changed files runs diff-tree.
   private static GitOptions GitWithDiffTree(Func<GitRun, Task<ProcessOutcome>> step) => GitDouble.Options(run => run.Subcommand == "diff-tree" ? step(run) : run.GitAsync());

   private static Task<ServerHarness> StartAsync(TestPlatform platform, GitOptions git, RecordingLoggerProvider? logs = null) =>
      ServerHarness.StartAsync(platform: platform, configure: options => options with { Git = git, LoggerProvider = logs ?? options.LoggerProvider });
}
