using Lotsen.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lotsen.Tests;

/// <summary>Covers how background jobs run and how the host's stop ends them.</summary>
[Trait("Category", "Pinning")]
public sealed class BackgroundJobsTests
{
   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

   /// <summary>
   /// The stop's cancellation runs the job's callback on the stopping thread, and the callback waits for another thread that starts a job, as a request that creates a tour does. The other thread is a
   /// thread of its own, so its start does not depend on the thread pool.
   /// </summary>
   [Fact]
   public async Task Stop_whose_cancellation_waits_for_another_thread_that_starts_a_job_ends()
   {
      using var jobs = new BackgroundJobs(NullLogger<BackgroundJobs>.Instance);
      var otherThreadStarted = false;
      var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      jobs.Run(async cancellationToken =>
      {
         using var registration = cancellationToken.Register(() =>
         {
            var other = new Thread(() => jobs.Run(_ => Task.CompletedTask));
            other.Start();
            otherThreadStarted = other.Join(Timeout);
         });
         registered.SetResult();
         await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
      });
      await registered.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

      await Task.Run(() => jobs.StopAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken).WaitAsync(Timeout * 2, TestContext.Current.CancellationToken);

      Assert.True(otherThreadStarted, "The other thread's start of a job waited for the stop's lock.");
      await jobs.WhenIdleAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);
   }
}
