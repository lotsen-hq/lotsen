using Lotsen.Git;
using Lotsen.Hosting;
using Lotsen.Operations;
using Lotsen.Pinning;
using Lotsen.Platforms;
using Lotsen.Server;
using Lotsen.Sessions;
using Lotsen.Tours;
using Microsoft.Extensions.Logging.Abstractions;
using static Lotsen.Tests.IterationRows;

namespace Lotsen.Tests;

/// <summary>
/// Covers how <see cref="IterationLifecycle"/> stores what the pure core decides: the iteration update and the iteration check on the real writer, with a test platform and a dictionary of git facts.
/// </summary>
/// <remarks>
/// The tour has two inspect stops: <c>s1</c> on line 2 of a.txt, which the new head changes, and <c>s2</c> on line 1 of b.txt, which it leaves. Its available iteration is <see cref="NewPin"/>, and
/// a session is bound to it. The facts also hold the new pin's diff of c0.txt to c9.txt, for the stops that a test adds.
/// </remarks>
[Trait("Category", "Remap")]
public sealed class IterationLifecycleTests : IDisposable
{
   public static TheoryData<string> ExternalFailures => ExternalFailureKinds.Names;

   private readonly string _root = Directory.CreateTempSubdirectory("lotsen-tests-").FullName;
   private readonly FileTourStore _store;
   private readonly EventBroadcaster _events = new();
   private readonly AgentSessions _sessions = new();
   private readonly ServerCredentials _credentials = ServerCredentials.CreateRandom();
   private readonly TestPlatform _platform = new();
   private readonly RecordingLoggerProvider _logs = new();
   private readonly FakeFacts _facts;
   private readonly TourWriter _writer;
   private readonly IterationLifecycle _lifecycle;
   private readonly AgentSession _session;
   private readonly Tour _tour;

   public IterationLifecycleTests()
   {
      var dataDirectory = new DataDirectory(Path.Combine(_root, "data"));
      _store = new FileTourStore(dataDirectory);
      _writer = new TourWriter(dataDirectory, _events, _sessions, TimeProvider.System, NullLogger<TourWriter>.Instance);
      var truth = Facts("a.txt").Changed("a.txt", Text(1, 10, (2, "TWO")), new LineHunk(2, 1, 2, 1)).InDiff("a.txt").InDiff("b.txt");
      for (var i = 0; i < 10; i++)
      {
         truth.InDiff($"c{i}.txt");
      }

      // The facts of a check that finds the pinned head again after an update moved the tour onto the new head.
      truth.Add(new FactKey.MergeBaseOf(Head, Target), Base)
         .Add(new FactKey.ChangedPaths(NewHead, Head), [new ChangedPath("a.txt", FileChangeKind.Modified)])
         .Add(new FactKey.PinDiff(Base, Head, FileSide.Head, "a.txt"), new DiffTouch.Throughout())
         .Add(new FactKey.PinDiff(Base, Head, FileSide.Head, "b.txt"), new DiffTouch.Throughout());

      _facts = new FakeFacts(truth.Build());
      _platform.PullRequest(Remote, NewHead, Target, iteration: 2);
      _lifecycle = new IterationLifecycle(_writer, new PullRequestPlatforms([_platform]), _facts, TimeProvider.System, _logs.CreateLogger<IterationLifecycle>());
      var created = _store.Create(PullRequest, Familiarity.Some);
      _tour = _store.Change(created.Id, tour => tour with
      {
         Pin = Pin,
         Plan = new Plan([new Chapter("c1", "Chapter")], [Stop("s1", StopKind.Inspect, Passage("a.txt", 2, 2)), Stop("s2", StopKind.Inspect, Passage("b.txt", 1, 1))], []),
         AvailableIteration = new AvailableIteration(NewPin, ["s1"], Now),
      })!;
      _session = _sessions.TryOpen()!;
      _writer.Exclusive(locked => locked.BindNew(_tour.Id, _session.Recipient));
   }

   private Access.ReviewerToSession.Pass Pass => Passes.Admit<Access.ReviewerToSession.Pass>("update_iteration", Passes.Ui(_credentials, _sessions));

   public void Dispose()
   {
      _sessions.Dispose();
      Directory.Delete(_root, recursive: true);
   }

   /// <summary>A check found the facts before, so the memo holds every fact that the update needs.</summary>
   [Fact]
   public async Task Update_on_a_memo_hit_asks_no_platform_opens_no_scope_and_stores_once_with_its_events()
   {
      _facts.RememberAll();
      var before = _store.Get(_tour.Id)!;
      using var subscription = _events.Subscribe();

      var updated = (await UpdateAsync()).Value!;

      Assert.Equal((0, 0), (_platform.Resolves, _facts.Opens));
      Assert.Equal(NewPin, updated.Pin);
      Assert.Equal(["s1"], updated.StaleStops);
      var stored = _store.Get(_tour.Id)!;
      Assert.Equal((before.Revision + 1, NewPin, null), (stored.Revision, stored.Pin, stored.AvailableIteration));
      Assert.Equal(2, ((Stop.Written)stored.Plan!.Stops[0]).StaleOn);
      var types = Drain(subscription);
      Assert.Equal(
         [ServerEvents.TourPinned.Name, ServerEvents.AvailableIterationChanged.Name, ServerEvents.PlanSet.Name, ServerEvents.ThreadCreated.Name, ServerEvents.ControlsChanged.Name],
         types.Select(received => received.Type));
      Assert.All(types, received => Assert.Equal(stored.Revision, received.Revision));
   }

   [Fact]
   public async Task Update_with_an_empty_memo_reads_its_facts_in_one_scope_then_stores()
   {
      var updated = await UpdateAsync();

      Assert.Equal(NewPin, updated.Value?.Pin);
      Assert.Equal((0, 1), (_platform.Resolves, _facts.Opens));
   }

   /// <summary>The tour's only stop goes stale, so the update's remap reads no diff of the new pin.</summary>
   [Fact]
   public async Task Update_whose_remap_reads_no_diff_of_the_new_pin_downloads_its_changed_files_once()
   {
      KeepOnlyTheStaleStop();

      var updated = await UpdateAsync();

      Assert.Equal(NewPin, updated.Value?.Pin);
      Assert.Equal(1, _facts.DownloadsOf(NewPin));
   }

   [Fact]
   public async Task Update_downloads_the_changed_files_of_the_new_pin_before_it_stores_the_update()
   {
      KeepOnlyTheStaleStop();
      TourPin? storedWhileDownloading = null;
      _facts.OnWarm = () => storedWhileDownloading = _store.Get(_tour.Id)!.Pin;

      var updated = await UpdateAsync();

      Assert.Equal(NewPin, updated.Value?.Pin);
      Assert.Equal(Pin, storedWhileDownloading);
      Assert.Equal(1, _facts.DownloadsOf(NewPin));
   }

   [Fact]
   public async Task Update_canceled_during_its_download_stores_nothing()
   {
      KeepOnlyTheStaleStop();
      using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
      _facts.OnWarm = cancel.Cancel;

      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _lifecycle.UpdateAsync(Pass, InputOf(NewPin), cancel.Token));

      Assert.Equal(Pin, _store.Get(_tour.Id)!.Pin);
   }

   /// <summary>The first update leaves s1 stale, so its decided thread waits for the session, and a check then finds the pinned head again.</summary>
   [Fact]
   public async Task Update_refused_while_an_earlier_update_waits_downloads_nothing()
   {
      KeepOnlyTheStaleStop();
      Assert.Equal(NewPin, (await UpdateAsync()).Value?.Pin);
      _writer.Exclusive(_ => _store.Change(_tour.Id, tour => tour with { AvailableIteration = new AvailableIteration(Pin with { Iteration = 3 }, ["s1"], Now) }));

      var refused = await UpdateAsync();

      Assert.NotNull(refused.Error);
      Assert.Equal(0, _facts.DownloadsOf(Pin));
   }

   /// <summary>The notice showed iteration 2, and a check stored the same head as iteration 3 before the update.</summary>
   [Fact]
   public async Task Update_to_another_iteration_than_the_tour_holds_is_refused_and_downloads_nothing()
   {
      KeepOnlyTheStaleStop();
      var stored = NewPin with { Iteration = 3 };
      _writer.Exclusive(_ => _store.Change(_tour.Id, tour => tour with { AvailableIteration = new AvailableIteration(stored, ["s1"], Now) }));

      var refused = await UpdateAsync(NewPin);

      Assert.Equal(OperationErrorCode.IterationNotAvailable, refused.Error?.Code);
      Assert.Contains("since the notice showed update 2", refused.Error!.Reason, StringComparison.Ordinal);
      Assert.Equal((Pin, stored), (_store.Get(_tour.Id)!.Pin, _store.Get(_tour.Id)!.AvailableIteration?.Pin));
      Assert.Equal((0, 0, 0), (_facts.DownloadsOf(NewPin), _facts.DownloadsOf(stored), _facts.Opens));
   }

   /// <summary>The stop on b.txt maps, so the update's remap reads the new pin's diff of b.txt after the update downloaded the pin's changed files.</summary>
   [Fact]
   public async Task Update_whose_scope_downloaded_the_new_pin_downloads_it_no_second_time()
   {
      var updated = await UpdateAsync();

      Assert.Equal(NewPin, updated.Value?.Pin);
      Assert.Equal((1, 1), (_facts.DownloadsOf(NewPin), _facts.Opens));
   }

   /// <summary>The tour's only stop goes stale, so the check before the update read no diff of the new pin and downloaded nothing, and the memo holds every fact.</summary>
   [Fact]
   public async Task Update_on_a_memo_hit_whose_pin_no_scope_downloaded_downloads_its_changed_files()
   {
      _facts.RememberFacts();
      KeepOnlyTheStaleStop();

      var updated = await UpdateAsync();

      Assert.Equal(NewPin, updated.Value?.Pin);
      Assert.Equal(1, _facts.DownloadsOf(NewPin));
   }

   /// <summary>The download before the store fails, and the tour keeps only the stop that goes stale.</summary>
   [Fact]
   public async Task Update_whose_download_of_the_changed_files_fails_returns_the_stored_update()
   {
      _facts.WarmFailure = new GitException("git could not download the changed files.");
      KeepOnlyTheStaleStop();

      var updated = await UpdateAsync();

      Assert.Equal(NewPin, updated.Value?.Pin);
      Assert.Equal(NewPin, _store.Get(_tour.Id)!.Pin);
   }

   /// <summary>Before each pass, a stop on another file lands, whose facts the memo lacks, so no attempt can store.</summary>
   [Fact]
   public async Task Update_whose_tour_needs_new_facts_before_every_pass_is_refused_after_three_attempts_and_stores_nothing()
   {
      _facts.RememberAll();
      var passes = 0;
      _lifecycle.BeforeUpdatePass = _ =>
      {
         AddStop($"c{passes++}.txt");
         return Task.CompletedTask;
      };

      var result = await UpdateAsync();

      Assert.Equal(OperationErrorCode.TourChanged, result.Error?.Code);
      Assert.Contains("Move again", result.Error!.Reason, StringComparison.Ordinal);
      Assert.Equal(3, passes);
      Assert.Equal(Pin, _store.Get(_tour.Id)!.Pin);
   }

   /// <summary>A stop on a file whose facts the memo lacks lands before the first pass, so the second pass maps it.</summary>
   [Fact]
   public async Task Update_maps_a_stop_that_lands_before_its_pass_on_a_file_whose_facts_it_reads_first()
   {
      _facts.RememberAll();
      _lifecycle.BeforeUpdatePass = _ =>
      {
         _lifecycle.BeforeUpdatePass = null;
         AddStop("c0.txt");
         return Task.CompletedTask;
      };

      var updated = await UpdateAsync();

      Assert.Equal(["s1"], updated.Value!.StaleStops);
      Assert.Equal(3, _store.Get(_tour.Id)!.Plan!.Stops.Count);
      Assert.Equal(1, _facts.Opens);
   }

   /// <summary>
   /// The first pass needs facts, then a check stores another available iteration before the second pass: the same head as iteration 3 from a fork, or the same pin with another stale
   /// stop, or none.
   /// </summary>
   [Theory]
   [InlineData("iteration 3 from a fork", true)]
   [InlineData("same pin, other stale stops", false)]
   [InlineData("none", true)]
   public async Task Update_whose_available_iteration_a_check_changes_between_its_passes_applies_only_the_same_pin(string stored, bool refused)
   {
      var fork = new Uri("https://platform.test/fork/shop.git");
      AvailableIteration? next = stored switch
      {
         "iteration 3 from a fork" => new AvailableIteration(NewPin with { Iteration = 3, RemoteUrl = fork }, ["s1"], Now),
         "same pin, other stale stops" => new AvailableIteration(NewPin, ["s1", "s2"], Now),
         _ => null,
      };
      var passes = 0;
      _lifecycle.BeforeUpdatePass = _ =>
      {
         if (passes++ == 1)
         {
            _writer.Exclusive(_ => _store.Change(_tour.Id, tour => tour with { AvailableIteration = next }));
         }

         return Task.CompletedTask;
      };

      var result = await UpdateAsync();

      Assert.Equal(refused ? OperationErrorCode.IterationNotAvailable : null, result.Error?.Code);
      Assert.Equal(refused ? Pin : NewPin, _store.Get(_tour.Id)!.Pin);
      Assert.True(next is null || !refused || result.Error!.Reason.Contains("moved on to update 3", StringComparison.Ordinal), result.Error?.Reason);
      Assert.All(_facts.Remotes, remote => Assert.Equal(Remote, remote));
   }

   [Fact]
   public async Task Update_after_the_session_ended_before_its_pass_is_refused_and_stores_nothing()
   {
      _facts.RememberAll();
      var before = _store.Get(_tour.Id)!;
      _lifecycle.BeforeUpdatePass = _ =>
      {
         _session.End();
         _writer.SettleEnds();
         return Task.CompletedTask;
      };

      var result = await UpdateAsync();

      Assert.Equal(OperationErrorCode.NoSessionAttached, result.Error?.Code);
      var after = _store.Get(_tour.Id)!;
      Assert.Equal((before.Pin, before.AvailableIteration!.Pin, before.UpdatedAt), (after.Pin, after.AvailableIteration!.Pin, after.UpdatedAt));
      Assert.Empty(after.Threads);
   }

   [Fact]
   public async Task Update_after_a_take_over_before_its_pass_reports_to_the_new_session()
   {
      _facts.RememberAll();
      var other = _sessions.TryOpen()!;
      _lifecycle.BeforeUpdatePass = _ =>
      {
         _writer.Exclusive(locked => locked.Attach(_tour.Id, other.Recipient, takeOver: true));
         return Task.CompletedTask;
      };

      var updated = (await UpdateAsync()).Value!;

      var request = Assert.Single(Assert.IsType<QuestionThread.Sent.Decided>(updated.Thread).LastQuestion!.Requests);
      Assert.Equal(other.Id, request.SessionId);
   }

   [Fact]
   public async Task Two_updates_that_reach_their_passes_together_store_one_update_and_refuse_the_other()
   {
      _facts.RememberAll();
      var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      var arrived = 0;
      var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      _lifecycle.BeforeUpdatePass = async _ =>
      {
         if (Interlocked.Increment(ref arrived) == 2)
         {
            both.TrySetResult();
         }

         await gate.Task;
      };

      var first = UpdateAsync();
      var second = UpdateAsync();
      await both.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
      gate.SetResult();
      var results = await Task.WhenAll(first, second);

      Assert.Single(results, result => result.Value is not null);
      Assert.Single(results, result => result.Error?.Code == OperationErrorCode.IterationNotAvailable);
      Assert.Single(_store.Get(_tour.Id)!.Threads);
   }

   [Fact]
   public async Task Check_that_finds_the_same_iteration_again_stores_nothing_and_one_that_finds_the_pinned_head_clears_it()
   {
      var first = await _lifecycle.CheckAsync(_tour.Id, null, TestContext.Current.CancellationToken);
      var revision = _store.Get(_tour.Id)!.Revision;

      var again = await _lifecycle.CheckAsync(_tour.Id, null, TestContext.Current.CancellationToken);
      _platform.PullRequest(Remote, Head, Target);
      using var subscription = _events.Subscribe();
      var cleared = await _lifecycle.CheckAsync(_tour.Id, null, TestContext.Current.CancellationToken);

      Assert.Equal((null, null), (first.Failure, again.Failure));
      Assert.Equal(["s1"], first.Tour!.AvailableIteration!.StaleStops);
      Assert.Equal(revision, again.Tour!.Revision);
      Assert.Null(cleared.Tour!.AvailableIteration);
      Assert.Contains(ServerEvents.AvailableIterationChanged.Name, Drain(subscription).Select(received => received.Type));
   }

   /// <summary>The platform holds the check's answer, the pinned head, while the update moves the tour onto the new head, so the check decides on the new pin and finds the earlier head.</summary>
   [Fact]
   public async Task Check_that_finds_the_pinned_head_after_an_update_moved_the_pin_reads_the_facts_of_the_new_pin()
   {
      _facts.RememberAll();
      var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      _platform.Answer(async cancellationToken =>
      {
         await gate.Task.WaitAsync(cancellationToken);
         return new ResolvedPullRequest(Remote, Head, Target, 1, PullRequestState.Active);
      });

      var check = _lifecycle.CheckAsync(_tour.Id, null, TestContext.Current.CancellationToken);
      await Poll.UntilAsync(() => _platform.Resolves > 0 ? "" : null, TimeSpan.FromSeconds(10), () => "The check did not ask the platform.");
      Assert.Equal(NewPin, (await UpdateAsync()).Value?.Pin);
      gate.SetResult();
      var run = await check;

      Assert.True(run.Failure is null, run.Failure + string.Join("\n", _logs.Lines));
      Assert.Equal(Pin, run.Tour!.AvailableIteration!.Pin);
      Assert.Equal(["s1"], run.Tour.AvailableIteration.StaleStops);
   }

   [Fact]
   public async Task Check_with_a_reused_answer_asks_no_platform()
   {
      var answer = Answer(NewHead) with { AnsweredAt = DateTimeOffset.UtcNow };

      var run = await _lifecycle.CheckAsync(_tour.Id, answer, TestContext.Current.CancellationToken);

      Assert.Same(answer, run.Answer);
      Assert.Equal(0, _platform.Resolves);
   }

   [Theory]
   [MemberData(nameof(ExternalFailures))]
   public async Task Check_whose_platform_fails_keeps_the_available_iteration_and_reports_the_reason(string failure)
   {
      _platform.Answer(_ => Task.FromException<ResolvedPullRequest>(ExternalFailureKinds.Of(failure, "Run 'az login', then try again.")));

      var run = await _lifecycle.CheckAsync(_tour.Id, null, TestContext.Current.CancellationToken);

      Assert.Equal("Run 'az login', then try again.", run.Failure);
      Assert.Equal(NewPin, run.Tour!.AvailableIteration!.Pin);
   }

   [Fact]
   public async Task Check_of_a_tour_whose_platform_is_not_registered_reports_the_platform()
   {
      var lifecycle = new IterationLifecycle(_writer, new PullRequestPlatforms([new TestPlatform("other")]), _facts, TimeProvider.System, _logs.CreateLogger<IterationLifecycle>());

      var run = await lifecycle.CheckAsync(_tour.Id, null, TestContext.Current.CancellationToken);

      Assert.Contains($"does not know the platform '{TestPlatform.DefaultId}'", run.Failure, StringComparison.Ordinal);
      Assert.Equal(NewPin, run.Tour!.AvailableIteration!.Pin);
   }

   /// <summary>The git read fails after the platform answered with a completed pull request.</summary>
   [Theory]
   [MemberData(nameof(ExternalFailures))]
   public async Task Check_whose_git_read_fails_stores_the_state_keeps_the_available_iteration_and_reports_the_reason(string failure)
   {
      _platform.PullRequest(Remote, NewHead, Target, iteration: 2, state: PullRequestState.Completed);
      _facts.Failure = ExternalFailureKinds.Of(failure, "git could not read the clone.");

      var run = await _lifecycle.CheckAsync(_tour.Id, null, TestContext.Current.CancellationToken);

      Assert.Equal("git could not read the clone.", run.Failure);
      Assert.Equal((PullRequestState.Completed, NewPin), (run.Tour!.PullRequestState, run.Tour.AvailableIteration!.Pin));
   }

   [Theory]
   [MemberData(nameof(ExternalFailures))]
   public async Task Update_whose_git_read_fails_is_refused_with_the_reason_and_stores_nothing(string failure)
   {
      _facts.Failure = ExternalFailureKinds.Of(failure, "git could not read the clone.");

      var updated = await UpdateAsync();

      Assert.Equal(OperationErrorCode.IterationNotAvailable, updated.Error?.Code);
      Assert.EndsWith("git could not read the clone.", updated.Error!.Problem, StringComparison.Ordinal);
      Assert.Equal(Pin, _store.Get(_tour.Id)!.Pin);
   }

   /// <summary>Each read of facts lands a stop on another file, so every pass under the lock needs facts again.</summary>
   [Fact]
   public async Task Check_whose_tour_needs_new_facts_before_every_store_gives_up_after_three_attempts_and_says_so()
   {
      var reads = 0;
      _facts.OnRead = () => AddStop($"c{reads++}.txt");

      var run = await _lifecycle.CheckAsync(_tour.Id, null, TestContext.Current.CancellationToken);

      Assert.Contains("the tour changed during every attempt", run.Failure, StringComparison.Ordinal);
   }

   [Fact]
   public async Task First_pin_stores_the_pin_and_the_pull_requests_state()
   {
      _platform.PullRequest(Remote, NewHead, Target, iteration: 2, state: PullRequestState.Completed);
      (TourPin Pin, PullRequestState? State)? stored = null;

      await _lifecycle.PinAsync(_tour.Id, PullRequest, (pin, state) =>
      {
         stored = (pin, state);
         return Task.CompletedTask;
      }, TestContext.Current.CancellationToken);

      Assert.Equal((new TourPin.Pinned(Base, NewHead, 2, Remote), PullRequestState.Completed), stored);
      Assert.Equal(1, _facts.Opens);
   }

   /// <summary>The memo holds the merge base, so only the sync of the clone runs git, and it fails as for an expired sign-in.</summary>
   [Theory]
   [MemberData(nameof(ExternalFailures))]
   public async Task First_pin_on_a_memo_hit_whose_sync_fails_stores_the_failure_with_its_reason(string failure)
   {
      _facts.RememberAll();
      _facts.CloneFailure = ExternalFailureKinds.Of(failure, "git could not fetch the pull request's commits. Run 'az login', then try again.");

      var stored = await PinAsync();

      Assert.Equal(new TourPin.Failed("git could not fetch the pull request's commits. Run 'az login', then try again."), stored);
   }

   /// <summary>The scope's read throws an exception that the pin does not turn into a failed pin, as a fault of the core or a cancel does.</summary>
   [Theory]
   [InlineData(nameof(InvalidOperationException))]
   [InlineData(nameof(OperationCanceledException))]
   public async Task First_pin_that_throws_disposes_its_scope(string exception)
   {
      _facts.Failure = exception == nameof(InvalidOperationException) ? new InvalidOperationException("The core faulted.") : new OperationCanceledException();

      await Assert.ThrowsAnyAsync<Exception>(PinAsync);

      Assert.Equal((1, 1), (_facts.Opens, _facts.Disposes));
   }

   [Fact]
   public async Task First_pin_of_another_kind_of_target_stores_the_failure_and_asks_no_platform()
   {
      TourPin? stored = null;

      await _lifecycle.PinAsync(_tour.Id, new OtherTarget(), (pin, _) =>
      {
         stored = pin;
         return Task.CompletedTask;
      }, TestContext.Current.CancellationToken);

      Assert.Equal(new TourPin.Failed("lotsen cannot pin this kind of tour target."), stored);
      Assert.Equal((0, 0), (_platform.Resolves, _facts.Opens));
   }

   [Fact]
   public async Task First_pin_whose_download_of_the_changed_files_fails_stores_the_pin()
   {
      _facts.RememberAll();
      _facts.WarmFailure = new GitException("git could not download the changed files.");

      var stored = await PinAsync();

      Assert.Equal(new TourPin.Pinned(Base, NewHead, 2, Remote), stored);
   }

   /// <summary>A check found the available iteration first. Then a write moves s2 onto line 2 of a.txt, so an update would mark it stale too, and the check that its store nudges reuses the answer.</summary>
   [Fact]
   public async Task Store_that_changes_a_stop_under_an_available_iteration_nudges_a_check_that_reuses_the_answer()
   {
      using var jobs = new BackgroundJobs(NullLogger<BackgroundJobs>.Instance);
      using var scheduler = new IterationScheduler(IterationSchedule.Default with { Debounce = TimeSpan.Zero }, TimeProvider.System, jobs, _lifecycle, _writer, NullLogger<IterationScheduler>.Instance);
      await scheduler.CheckNowAsync(_tour.Id, TestContext.Current.CancellationToken);
      var resolves = _platform.Resolves;

      var stored = _writer.Exclusive(locked => locked.Write(_tour.Id, edit =>
      {
         var s2 = (Stop.Written)edit.Latest.Plan!.Stops[1];
         var moved = s2 with { Content = new StopContent("Moved.", [Passage("a.txt", 2, 2)]), Version = 2, Earlier = [new StopVersion(s2.Title, s2.Kind, s2.Content, Now)] };
         return edit.Store(edit.Latest with { Plan = edit.Latest.Plan with { Stops = [edit.Latest.Plan.Stops[0], moved] } }, written => written.Tour);
      }));
      Assert.Null(stored.Error);
      await jobs.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

      Assert.Equal(["s1", "s2"], _store.Get(_tour.Id)!.AvailableIteration!.StaleStops);
      Assert.Equal(resolves, _platform.Resolves);
   }

   [Fact]
   public async Task Store_that_changes_no_stop_mark_or_anchor_nudges_no_check()
   {
      using var jobs = new BackgroundJobs(NullLogger<BackgroundJobs>.Instance);
      using var scheduler = new IterationScheduler(IterationSchedule.Default with { Debounce = TimeSpan.Zero }, TimeProvider.System, jobs, _lifecycle, _writer, NullLogger<IterationScheduler>.Instance);

      _writer.Exclusive(locked => locked.Write(_tour.Id, edit => edit.Store(edit.Latest with { Archived = true }, written => written.Tour)));

      Assert.True(jobs.WhenIdleAsync().IsCompleted);
      Assert.Equal(0, _platform.Resolves);
   }

   [Fact]
   public async Task Iteration_update_nudges_a_check_of_the_new_pin()
   {
      _facts.RememberAll();
      using var jobs = new BackgroundJobs(NullLogger<BackgroundJobs>.Instance);
      using var scheduler = new IterationScheduler(IterationSchedule.Default with { Debounce = TimeSpan.Zero }, TimeProvider.System, jobs, _lifecycle, _writer, NullLogger<IterationScheduler>.Instance);

      await UpdateAsync();
      await jobs.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

      Assert.Equal(1, _platform.Resolves);
      Assert.Null(_store.Get(_tour.Id)!.AvailableIteration);
   }

   private static List<ServerEvent> Drain(EventBroadcaster.Subscription subscription)
   {
      var received = new List<ServerEvent>();
      while (subscription.Reader.TryRead(out var next))
      {
         received.Add((ServerEvent)next);
      }

      return received;
   }

   private sealed record OtherTarget : TourTarget;

   private async Task<TourPin> PinAsync()
   {
      TourPin? stored = null;
      await _lifecycle.PinAsync(_tour.Id, PullRequest, (pin, _) =>
      {
         stored = pin;
         return Task.CompletedTask;
      }, TestContext.Current.CancellationToken);
      return stored!;
   }

   // Leaves the tour with s1 alone, which the new head makes stale, so the update's remap reads no diff of the new pin.
   private void KeepOnlyTheStaleStop() => _store.Change(_tour.Id, tour => tour with { Plan = tour.Plan! with { Stops = [tour.Plan.Stops[0]] } });

   // Updates to the available iteration as the store holds it when the update starts, or to NewPin when the tour has none.
   private Task<OperationResult<UpdatedIteration>> UpdateAsync() => UpdateAsync(_store.Get(_tour.Id)!.AvailableIteration?.Pin ?? NewPin);

   private Task<OperationResult<UpdatedIteration>> UpdateAsync(TourPin.Pinned shown) => _lifecycle.UpdateAsync(Pass, InputOf(shown), TestContext.Current.CancellationToken);

   private UpdateIterationInput InputOf(TourPin.Pinned shown) => new(_tour.Id, new PinInput(shown.MergeBase, shown.Head, shown.Iteration, shown.RemoteUrl));

   // Adds an inspect stop on line 1 of the file, under the writer's lock, past its checks.
   private void AddStop(string path) => _writer.Exclusive(_ => _store.Change(_tour.Id, tour => tour with
   {
      Plan = tour.Plan! with { Stops = [.. tour.Plan.Stops, Stop($"x{tour.Plan.Stops.Count}", StopKind.Inspect, Passage(path, 1, 1))] },
   }));

   // Git facts from a dictionary, whose memo starts empty. Each scope reads from the dictionary into the memo.
   private sealed class FakeFacts : IGitFacts
   {
      private readonly FactSet _truth;
      private readonly Dictionary<FactKey, object?> _memo = [];
      private readonly List<Uri> _remotes = [];
      private readonly Dictionary<(CommitId MergeBase, CommitId Head), int> _downloads = [];
      private int _opens;
      private int _disposes;

      public FakeFacts(FactSet truth)
      {
         _truth = truth;
      }

      public int Opens => Volatile.Read(ref _opens);

      public int Disposes => Volatile.Read(ref _disposes);

      /// <summary>The remote URL of each scope that opened, in order.</summary>
      public IReadOnlyList<Uri> Remotes
      {
         get
         {
            lock (_remotes)
            {
               return [.. _remotes];
            }
         }
      }

      /// <summary>Returns how often a scope downloaded the changed files of the pin, as a diff read of the pin or a warm does.</summary>
      public int DownloadsOf(TourPin.Pinned pin)
      {
         lock (_downloads)
         {
            return _downloads.GetValueOrDefault((pin.MergeBase, pin.Head));
         }
      }

      public bool Downloaded(Uri remote, CommitId mergeBase, CommitId head)
      {
         lock (_downloads)
         {
            return _downloads.ContainsKey((mergeBase, head));
         }
      }

      /// <summary>Runs at the start of each download of a scope that the clone needs.</summary>
      public Action? OnWarm { get; set; }

      /// <summary>Runs before each read of a scope.</summary>
      public Action? OnRead { get; set; }

      /// <summary>What each read of a scope throws, or null to read.</summary>
      public Exception? Failure { get; set; }

      /// <summary>What a scope's first call that needs the clone throws, as a failed sync does, or null to open the clone.</summary>
      public Exception? CloneFailure { get; set; }

      /// <summary>What a scope's download of the changed files throws once the clone is open, or null to download.</summary>
      public Exception? WarmFailure { get; set; }

      public FactSet Known
      {
         get
         {
            lock (_memo)
            {
               return FactSet.Of(new Dictionary<FactKey, object?>(_memo));
            }
         }
      }

      // Copies every fact that the rows of this class's tour need into the memo, as a check before the update would, and records the download of that check's diff reads.
      public void RememberAll()
      {
         RememberFacts();
         Download(Base, NewHead);
      }

      // Copies the facts alone, as a check would whose remap read no diff of the new pin.
      public void RememberFacts()
      {
         foreach (var key in Keys())
         {
            Assert.True(_truth.TryFind(key, out var value));
            lock (_memo)
            {
               _memo[key] = value;
            }
         }
      }

      public IFactScope Open(TourRemote remote, IReadOnlyCollection<CommitId> commits, FetchPolicy policy)
      {
         Interlocked.Increment(ref _opens);
         lock (_remotes)
         {
            _remotes.Add(remote.Url);
         }

         return new Scope(this);
      }

      private static IEnumerable<FactKey> Keys() =>
      [
         new FactKey.MergeBaseOf(NewHead, Target),
         new FactKey.ChangedPaths(Head, NewHead),
         new FactKey.Hunks(Head, "a.txt", NewHead, "a.txt"),
         new FactKey.Lines(NewHead, "a.txt"),
         new FactKey.PinDiff(Base, NewHead, FileSide.Head, "a.txt"),
         new FactKey.PinDiff(Base, NewHead, FileSide.Head, "b.txt"),
      ];

      private void Download(CommitId mergeBase, CommitId head)
      {
         lock (_downloads)
         {
            _downloads[(mergeBase, head)] = _downloads.GetValueOrDefault((mergeBase, head)) + 1;
         }
      }

      private sealed class Scope : IFactScope
      {
         private readonly FakeFacts _owner;
         private readonly HashSet<(CommitId MergeBase, CommitId Head)> _downloaded = [];
         private bool _cloneOpen;

         public Scope(FakeFacts owner)
         {
            _owner = owner;
         }

         public FactSet Known => _owner.Known;

         public Task OpenCloneAsync(CancellationToken cancellationToken)
         {
            OpenClone();
            return Task.CompletedTask;
         }

         public Task ReadAsync(IReadOnlyCollection<FactKey> keys, CancellationToken cancellationToken)
         {
            if (_owner.Failure is { } failure)
            {
               throw failure;
            }

            var known = _owner.Known;
            var missing = keys.Where(key => !known.TryFind(key, out _)).ToList();
            if (missing.Count == 0)
            {
               return Task.CompletedTask;
            }

            OpenClone();
            _owner.OnRead?.Invoke();
            foreach (var key in missing)
            {
               Assert.True(_owner._truth.TryFind(key, out var value), $"The pass asked for {key}, which the rows do not hold.");
               lock (_owner._memo)
               {
                  _owner._memo[key] = value;
               }

               if (key is FactKey.PinDiff(var mergeBase, var head, _, _) && _downloaded.Add((mergeBase, head)))
               {
                  _owner.Download(mergeBase, head);
               }
            }

            return Task.CompletedTask;
         }

         public Task WarmAsync(CommitId mergeBase, CommitId head, CancellationToken cancellationToken)
         {
            if (_downloaded.Contains((mergeBase, head)))
            {
               return Task.CompletedTask;
            }

            _owner.OnWarm?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            OpenClone();
            if (_owner.WarmFailure is { } failure)
            {
               return Task.FromException(failure);
            }

            _downloaded.Add((mergeBase, head));
            _owner.Download(mergeBase, head);
            return Task.CompletedTask;
         }

         public ValueTask DisposeAsync()
         {
            Interlocked.Increment(ref _owner._disposes);
            return ValueTask.CompletedTask;
         }

         // The clone opens on the first call that needs it, and a failed open leaves it closed for the next call, as in the real scope.
         private void OpenClone()
         {
            if (!_cloneOpen && _owner.CloneFailure is { } failure)
            {
               throw failure;
            }

            _cloneOpen = true;
         }
      }
   }
}
