using System.Collections.Concurrent;
using Lotsen.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lotsen.Tests;

/// <summary>Covers how the manual clock's advances let the work that a timer starts on another thread see the timer's due time, or reach the deadline of a loop that polls the clock.</summary>
public sealed class ManualTimeProviderTests
{
   private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
   private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(1);
   private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

   private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

   /// <summary>Each job reads the clock only once the advance has passed its timer's fire, as a job that the thread pool starts late does.</summary>
   [Fact]
   public async Task AdvanceAsync_that_settles_on_the_jobs_lets_each_job_that_a_timer_queues_read_its_timers_due_time()
   {
      var clock = new ManualTimeProvider();
      using var jobs = new BackgroundJobs(NullLogger<BackgroundJobs>.Instance);
      var start = clock.GetUtcNow();
      var pastFire = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      var seen = new ConcurrentQueue<DateTimeOffset>();
      QueueClockReadOnFire(clock, jobs, TimeSpan.FromMinutes(5), pastFire.Task, seen);
      QueueClockReadOnFire(clock, jobs, TimeSpan.FromMinutes(8), pastFire.Task, seen);

      await clock.AdvanceAsync(TimeSpan.FromMinutes(10), () =>
      {
         pastFire.TrySetResult();
         return jobs.WhenIdleAsync();
      }).WaitAsync(Wait, Cancellation);

      Assert.Equal([start + TimeSpan.FromMinutes(5), start + TimeSpan.FromMinutes(8)], seen);
      Assert.Equal(start + TimeSpan.FromMinutes(10), clock.GetUtcNow());
   }

   /// <summary>Each job reads the clock only once the advance has returned, the latest that the thread pool can start it.</summary>
   [Fact]
   public async Task Advance_lets_a_job_that_a_timer_queues_read_a_time_past_its_timers_due_time()
   {
      var clock = new ManualTimeProvider();
      using var jobs = new BackgroundJobs(NullLogger<BackgroundJobs>.Instance);
      var start = clock.GetUtcNow();
      var pastFire = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      var seen = new ConcurrentQueue<DateTimeOffset>();
      QueueClockReadOnFire(clock, jobs, TimeSpan.FromMinutes(5), pastFire.Task, seen);
      QueueClockReadOnFire(clock, jobs, TimeSpan.FromMinutes(8), pastFire.Task, seen);

      clock.Advance(TimeSpan.FromMinutes(10));
      pastFire.SetResult();
      await jobs.WhenIdleAsync().WaitAsync(Wait, Cancellation);

      Assert.Equal([start + TimeSpan.FromMinutes(10), start + TimeSpan.FromMinutes(10)], seen);
   }

   [Fact]
   public async Task AdvanceUntilAsync_completes_a_loop_that_arms_its_next_delay_from_a_stale_read()
   {
      var clock = new ManualTimeProvider();
      var start = clock.GetUtcNow();
      var loop = PollUntilDeadlineAsync(clock, Cancellation);

      await clock.AdvanceUntilAsync(loop, Deadline, Wait);

      await loop;
      Assert.True(clock.GetUtcNow() >= start + Deadline * 2, $"The clock stopped at {clock.GetUtcNow():O}.");
   }

   [Fact]
   public async Task Advance_leaves_a_loop_that_arms_its_next_delay_from_a_stale_read_waiting_on_a_timer_past_the_clock()
   {
      var clock = new ManualTimeProvider();
      using var stop = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
      var loop = PollUntilDeadlineAsync(clock, stop.Token);

      clock.Advance(Deadline);
      var armed = await Poll.UntilAsync(() => clock.Timers.FirstOrDefault(timer => timer.Due is not null), Wait, () => "The loop did not arm its next delay.");

      Assert.True(armed.Due > clock.GetUtcNow(), $"The delay is due at {armed.Due:O}, by the clock's {clock.GetUtcNow():O}.");
      Assert.False(loop.IsCompleted);
      await stop.CancelAsync();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loop);
   }

   [Fact]
   public async Task AdvanceUntilAsync_fails_once_the_work_has_not_completed_within_the_limit()
   {
      var clock = new ManualTimeProvider();
      var never = new TaskCompletionSource();

      await Assert.ThrowsAsync<TimeoutException>(() => clock.AdvanceUntilAsync(never.Task, Deadline, TimeSpan.FromMilliseconds(200)));
   }

   // The job waits for the gate before it reads the clock, so the test decides how far the advance has gone by then.
   private static void QueueClockReadOnFire(ManualTimeProvider clock, BackgroundJobs jobs, TimeSpan due, Task gate, ConcurrentQueue<DateTimeOffset> seen) =>
      clock.CreateTimer(_ => jobs.Run(async _ =>
      {
         await gate;
         seen.Enqueue(clock.GetUtcNow());
      }), null, due, System.Threading.Timeout.InfiniteTimeSpan);

   // The first wait arms only once the clock has passed the deadline, as a poll whose I/O outlasts the advance, so the stale first read sends the loop to wait once more.
   private static async Task PollUntilDeadlineAsync(ManualTimeProvider clock, CancellationToken cancellationToken)
   {
      var deadline = clock.GetUtcNow() + Deadline;
      var first = true;
      while (clock.GetUtcNow() < deadline)
      {
         if (first)
         {
            first = false;
            await Poll.UntilAsync(() => clock.GetUtcNow() >= deadline ? clock : null, Wait, () => "The clock did not reach the loop's deadline.");
         }

         await Task.Delay(PollInterval, clock, cancellationToken);
      }
   }
}
