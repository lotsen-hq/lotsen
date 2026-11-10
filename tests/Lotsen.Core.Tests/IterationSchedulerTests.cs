using Lotsen.Operations;
using Lotsen.Pinning;
using Lotsen.Platforms;
using Lotsen.Tours;
using Microsoft.Extensions.Logging.Abstractions;
using static Lotsen.Tests.IterationRows;

namespace Lotsen.Tests;

/// <summary>Covers when <see cref="IterationScheduler"/> runs a tour's iteration check, and with which answer, under a clock that moves only when a test advances it.</summary>
/// <remarks>
/// The schedule debounces for 2 seconds, reuses an answer for 30 seconds, polls every 3 minutes and keeps a read tour in the poll for 15 minutes. A check's job starts on the thread that
/// starts it and runs until the test finishes it, so the test sees each start as it happens.
/// </remarks>
[Trait("Category", "Remap")]
public sealed class IterationSchedulerTests : IDisposable
{
   private static readonly TourId Other = TourId.TryParse("otheriterationab", out var id) ? id : throw new InvalidOperationException("The ID does not parse.");

   private readonly ManualTimeProvider _clock = new();
   private readonly List<Run> _runs = [];
   private bool _refuses;
   private IterationScheduler? _scheduler;

   private IterationScheduler Scheduler => _scheduler ??= new(IterationSchedule.Default, _clock, Start, RunAsync, NullLogger<IterationScheduler>.Instance);

   public void Dispose() => _scheduler?.Dispose();

   [Fact]
   public async Task Nudge_waits_for_the_debounce_and_nudges_within_it_join_one_check()
   {
      Scheduler.Nudge(Id, Nudge.ReviewerAction);
      _clock.Advance(TimeSpan.FromSeconds(1));
      Scheduler.Nudge(Id, Nudge.ReviewerRead);
      _clock.Advance(TimeSpan.FromMilliseconds(999));

      Assert.Empty(_runs);
      _clock.Advance(TimeSpan.FromMilliseconds(1));
      var run = Assert.Single(_runs);
      Assert.Null(run.Reuse);
      await FinishAsync(run, Resolved());
      Assert.Single(_runs);
   }

   [Fact]
   public async Task Check_reuses_an_answer_younger_than_the_interval_and_asks_the_platform_again_after_it()
   {
      var answer = Resolved();
      await RunOnceAsync(Nudge.ReviewerAction, answer);
      _clock.Advance(TimeSpan.FromSeconds(25));

      Scheduler.Nudge(Id, Nudge.ReviewerRead);
      _clock.Advance(IterationSchedule.Default.Debounce);
      Assert.Same(answer, _runs[^1].Reuse);
      await FinishAsync(_runs[^1], answer);
      _clock.Advance(TimeSpan.FromSeconds(3));
      Scheduler.Nudge(Id, Nudge.ReviewerAction);
      _clock.Advance(IterationSchedule.Default.Debounce);

      Assert.Equal(3, _runs.Count);
      Assert.Null(_runs[^1].Reuse);
   }

   [Fact]
   public async Task Tour_change_reuses_the_answer_whatever_its_age_and_the_poll_never_reuses_it()
   {
      var answer = Resolved();
      await RunOnceAsync(Nudge.ReviewerRead, answer);
      _clock.Advance(TimeSpan.FromMinutes(2));

      Scheduler.Nudge(Id, Nudge.TourChanged);
      _clock.Advance(IterationSchedule.Default.Debounce);
      Assert.Same(answer, _runs[^1].Reuse);
      await FinishAsync(_runs[^1], answer);
      _clock.Advance(TimeSpan.FromMinutes(1));

      Assert.Equal(3, _runs.Count);
      Assert.Null(_runs[^1].Reuse);
   }

   [Fact]
   public async Task Nudges_while_a_check_runs_run_one_more_check_after_it()
   {
      Scheduler.Nudge(Id, Nudge.ReviewerAction);
      _clock.Advance(IterationSchedule.Default.Debounce);
      Scheduler.Nudge(Id, Nudge.ReviewerAction);
      Scheduler.Nudge(Id, Nudge.SessionAttached);
      _clock.Advance(IterationSchedule.Default.Debounce);
      Assert.Single(_runs);

      await FinishAsync(_runs[0], Resolved());
      _clock.Advance(IterationSchedule.Default.Debounce);

      Assert.Equal(2, _runs.Count);
   }

   /// <summary>The answer before the second check is older than the interval, so the second check asks the platform, and the nudge arrives while it runs.</summary>
   [Theory]
   [InlineData(nameof(Nudge.ReviewerAction), true)]
   [InlineData(nameof(Nudge.TourChanged), true)]
   [InlineData(nameof(Nudge.Timer), false)]
   public async Task Nudge_while_a_check_runs_judges_the_age_of_the_answer_that_the_check_stored(string reason, bool reuses)
   {
      await RunOnceAsync(Nudge.ReviewerAction, Resolved());
      _clock.Advance(TimeSpan.FromSeconds(31));
      Scheduler.Nudge(Id, Nudge.ReviewerAction);
      _clock.Advance(IterationSchedule.Default.Debounce);
      Assert.Null(_runs[^1].Reuse);

      Scheduler.Nudge(Id, Enum.Parse<Nudge>(reason));
      var answer = Resolved();
      await FinishAsync(_runs[^1], answer);
      _clock.Advance(IterationSchedule.Default.Debounce);

      Assert.Equal(3, _runs.Count);
      Assert.Same(reuses ? answer : null, _runs[^1].Reuse);
   }

   /// <summary>The second check reuses an answer 25 seconds old, and ends 10 seconds later, past the interval of that answer.</summary>
   [Fact]
   public async Task Nudge_while_a_check_runs_asks_the_platform_once_the_answer_ages_past_the_interval_during_the_check()
   {
      var answer = Resolved();
      await RunOnceAsync(Nudge.ReviewerAction, answer);
      _clock.Advance(TimeSpan.FromSeconds(23));
      Scheduler.Nudge(Id, Nudge.ReviewerAction);
      _clock.Advance(IterationSchedule.Default.Debounce);
      Assert.Same(answer, _runs[^1].Reuse);

      Scheduler.Nudge(Id, Nudge.ReviewerAction);
      _clock.Advance(TimeSpan.FromSeconds(10));
      await FinishAsync(_runs[^1], answer);
      _clock.Advance(IterationSchedule.Default.Debounce);

      Assert.Equal(3, _runs.Count);
      Assert.Null(_runs[^1].Reuse);
   }

   /// <summary>The second nudged check reuses the first one's answer, and the request comes within the interval of that answer.</summary>
   [Fact]
   public async Task Check_now_skips_the_debounce_and_the_reuse()
   {
      var answer = Resolved();
      await RunOnceAsync(Nudge.ReviewerAction, answer);
      await RunOnceAsync(Nudge.ReviewerAction, answer);
      Assert.Same(answer, _runs[^1].Reuse);

      var check = Scheduler.CheckNowAsync(Id, TestContext.Current.CancellationToken);

      Assert.Equal(3, _runs.Count);
      Assert.Null(_runs[^1].Reuse);
      var result = new CheckRun(null, null, Resolved());
      _runs[^1].Done.SetResult(result);
      Assert.Same(result, await check);
   }

   /// <summary>Two requests arrive while a nudged check runs, so they join one check after it, which asks the platform afresh.</summary>
   [Fact]
   public async Task Check_now_while_a_check_runs_runs_once_after_it_and_requests_join_that_check()
   {
      Scheduler.Nudge(Id, Nudge.ReviewerAction);
      _clock.Advance(IterationSchedule.Default.Debounce);

      var first = Scheduler.CheckNowAsync(Id, TestContext.Current.CancellationToken);
      var second = Scheduler.CheckNowAsync(Id, TestContext.Current.CancellationToken);
      Assert.Single(_runs);
      await FinishAsync(_runs[0], Resolved());

      Assert.Equal(2, _runs.Count);
      Assert.Null(_runs[1].Reuse);
      var result = new CheckRun(null, null, Resolved());
      _runs[1].Done.SetResult(result);
      Assert.Same(result, await first);
      Assert.Same(result, await second);
   }

   /// <summary>Each failure in a row doubles the 30 second interval from the failure, until it reaches the 3 minute poll interval.</summary>
   [Theory]
   [InlineData(1, 60)]
   [InlineData(2, 120)]
   [InlineData(3, 180)]
   [InlineData(4, 180)]
   public async Task Failed_checks_in_a_row_double_the_wait_before_the_next_platform_request_up_to_the_poll_interval(int failures, int seconds)
   {
      for (var i = 0; i < failures; i++)
      {
         var check = Scheduler.CheckNowAsync(Id, TestContext.Current.CancellationToken);
         await FinishAsync(_runs[^1], new PlatformAnswer.Unreachable("The platform is down.", _clock.GetUtcNow()), failure: "The platform is down.");
         await check;
      }

      var before = _runs.Count;
      Scheduler.Nudge(Id, Nudge.ReviewerAction);
      _clock.Advance(TimeSpan.FromSeconds(seconds) - TimeSpan.FromMilliseconds(1));
      Assert.Equal(before, _runs.Count);
      _clock.Advance(TimeSpan.FromMilliseconds(1));

      Assert.Equal(before + 1, _runs.Count);
   }

   /// <summary>The reviewer reads both tours, the other's pull request is completed, and the window closes after the second poll.</summary>
   [Fact]
   public async Task Poll_checks_a_tour_read_within_the_window_and_skips_a_completed_pull_request()
   {
      Scheduler.Nudge(Id, Nudge.ReviewerRead);
      Scheduler.Nudge(Other, Nudge.ReviewerRead);
      _clock.Advance(IterationSchedule.Default.Debounce);
      await FinishAsync(_runs.Single(run => run.Id == Id), Resolved());
      await FinishAsync(_runs.Single(run => run.Id == Other), Resolved(PullRequestState.Completed));

      var polled = new List<TourId>();
      for (var poll = 0; poll < 6; poll++)
      {
         var before = _runs.Count;
         _clock.Advance(IterationSchedule.Default.PollInterval);
         foreach (var run in _runs.Skip(before).ToList())
         {
            polled.Add(run.Id);
            Assert.Null(run.Reuse);
            await FinishAsync(run, Resolved());
         }
      }

      Assert.Equal([Id, Id, Id, Id], polled);
   }

   /// <summary>The debounce timer fires on its own thread before the scheduler stores it, and that thread waits for the scheduler's lock.</summary>
   [Fact]
   public void Debounce_timer_that_fires_before_the_scheduler_stores_it_starts_the_check()
   {
      _clock.FiresEarly = true;

      Scheduler.Nudge(Id, Nudge.ReviewerAction);
      _clock.JoinEarlyFires();

      lock (_runs)
      {
         Assert.Single(_runs);
      }
   }

   /// <summary>A request runs the due check at once, and the debounce timer of that check fires late, after a later nudge made the tour due again.</summary>
   [Fact]
   public async Task Debounce_timer_of_an_earlier_due_check_that_fires_late_starts_no_check()
   {
      Scheduler.Nudge(Id, Nudge.ReviewerAction);
      var late = _clock.LastTimer;
      var check = Scheduler.CheckNowAsync(Id, TestContext.Current.CancellationToken);
      await FinishAsync(_runs[^1], Resolved());
      await check;
      Scheduler.Nudge(Id, Nudge.ReviewerAction);

      late.FireAlthoughDisposed();
      Assert.Single(_runs);
      _clock.Advance(IterationSchedule.Default.Debounce);

      Assert.Equal(2, _runs.Count);
   }

   [Fact]
   public void Forget_drops_a_due_check()
   {
      Scheduler.Nudge(Id, Nudge.ReviewerAction);

      Scheduler.Forget(Id);
      _clock.Advance(TimeSpan.FromMinutes(1));

      Assert.Empty(_runs);
   }

   [Fact]
   public async Task Forget_gives_a_request_that_waits_behind_a_running_check_a_run_without_a_tour()
   {
      Scheduler.Nudge(Id, Nudge.ReviewerAction);
      _clock.Advance(IterationSchedule.Default.Debounce);
      var waiting = Scheduler.CheckNowAsync(Id, TestContext.Current.CancellationToken);

      Scheduler.Forget(Id);
      await FinishAsync(_runs[0], Resolved());

      Assert.Null((await waiting).Tour);
      Assert.Single(_runs);
   }

   /// <summary>The other tour's debounce timer fires after the dispose, as a timer thread does that took its callback just before the dispose.</summary>
   [Fact]
   public async Task Disposed_scheduler_starts_no_check_and_cancels_a_request_that_waits()
   {
      Scheduler.Nudge(Id, Nudge.ReviewerAction);
      _clock.Advance(IterationSchedule.Default.Debounce);
      var waiting = Scheduler.CheckNowAsync(Id, TestContext.Current.CancellationToken);
      Scheduler.Nudge(Other, Nudge.ReviewerAction);
      var late = _clock.LastTimer;

      Scheduler.Dispose();
      late.FireAlthoughDisposed();
      await FinishAsync(_runs[0], Resolved());
      _clock.Advance(TimeSpan.FromMinutes(5));
      Scheduler.Nudge(Id, Nudge.ReviewerAction);
      _clock.Advance(TimeSpan.FromMinutes(5));

      Assert.Single(_runs);
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
   }

   [Fact]
   public async Task Check_that_the_host_no_longer_starts_cancels_its_request()
   {
      _refuses = true;

      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Scheduler.CheckNowAsync(Id, TestContext.Current.CancellationToken));
      Assert.Empty(_runs);
   }

   private PlatformAnswer.Resolved Resolved(PullRequestState state = PullRequestState.Active) => Answer(NewHead, state: state) with { AnsweredAt = _clock.GetUtcNow() };

   // Runs one nudged check of Id after its debounce, and finishes it with the answer.
   private async Task RunOnceAsync(Nudge reason, PlatformAnswer answer)
   {
      Scheduler.Nudge(Id, reason);
      _clock.Advance(IterationSchedule.Default.Debounce);
      await FinishAsync(_runs[^1], answer);
   }

   // Finishes the check with the answer, and waits until the scheduler has taken its end.
   private static async Task FinishAsync(Run run, PlatformAnswer answer, string? failure = null)
   {
      run.Done.SetResult(new CheckRun(TourValues.Tour(), failure, answer));
      await run.Job!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
   }

   // Starts the job on the caller's thread, which records its check before the job's first wait, and keeps the job with its check.
   private bool Start(Func<CancellationToken, Task> job)
   {
      if (_refuses)
      {
         return false;
      }

      var running = job(TestContext.Current.CancellationToken);
      lock (_runs)
      {
         _runs[^1].Job = running;
      }

      return true;
   }

   // Records the check synchronously, so a test sees it as soon as the scheduler starts it.
   private Task<CheckRun> RunAsync(TourId tour, PlatformAnswer? reuse, CancellationToken cancellationToken)
   {
      var run = new Run(tour, reuse);
      lock (_runs)
      {
         _runs.Add(run);
      }

      return run.Done.Task.WaitAsync(cancellationToken);
   }

   private sealed class Run
   {
      public Run(TourId id, PlatformAnswer? reuse)
      {
         Id = id;
         Reuse = reuse;
      }

      public TourId Id { get; }

      public PlatformAnswer? Reuse { get; }

      public TaskCompletionSource<CheckRun> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

      public Task? Job { get; set; }
   }
}
