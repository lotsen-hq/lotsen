namespace Lotsen.Hosting;

/// <summary>
/// Runs jobs in the background of the server, each until it ends or the host stops.
/// </summary>
/// <remarks>
/// Stopping the host cancels every job and waits for each to end. A job that the host's stop cancels ends quietly, so it records nothing. A job that throws anything else is logged.
/// </remarks>
internal sealed class BackgroundJobs : IHostedService, IDisposable
{
   private readonly CancellationTokenSource _stopping = new();
   private readonly Lock _lock = new();
   private readonly HashSet<Task> _running = [];
   private readonly ILogger<BackgroundJobs> _logger;
   private TaskCompletionSource _idle = Completed();
   private bool _stopped;

   public BackgroundJobs(ILogger<BackgroundJobs> logger)
   {
      _logger = logger;
   }

   /// <summary>Starts the job in the background, or does nothing once the host is stopping.</summary>
   /// <param name="job">The work, which receives a token that the host's stop cancels.</param>
   /// <returns>Whether the job started.</returns>
   public bool Run(Func<CancellationToken, Task> job)
   {
      Task running;
      lock (_lock)
      {
         if (_stopped)
         {
            return false;
         }

         if (_running.Count == 0)
         {
            _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
         }

         running = RunAsync(job);
         _running.Add(running);
      }

      running.ContinueWith(Finished, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
      return true;
   }

   /// <summary>Returns a task that completes when no job is running.</summary>
   public Task WhenIdleAsync()
   {
      lock (_lock)
      {
         return _idle.Task;
      }
   }

   public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

   public async Task StopAsync(CancellationToken cancellationToken)
   {
      Task[] running;
      lock (_lock)
      {
         _stopped = true;
         running = [.. _running];
      }

      // The jobs' callbacks run during the cancellation, so it happens outside the lock that Run takes.
      await _stopping.CancelAsync();

      await Task.WhenAll(running).WaitAsync(cancellationToken);
   }

   public void Dispose() => _stopping.Dispose();

   // Never throws, so a job's failure cannot go unobserved.
   private async Task RunAsync(Func<CancellationToken, Task> job)
   {
      // The job starts on the thread pool, so a job that blocks before its first await does not hold Run's caller.
      await Task.Yield();
      try
      {
         await job(_stopping.Token);
      }
      catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
      {
      }
      catch (Exception exception)
      {
         _logger.LogError(exception, "A background job failed.");
      }
   }

   private void Finished(Task job)
   {
      lock (_lock)
      {
         _running.Remove(job);
         if (_running.Count == 0)
         {
            _idle.TrySetResult();
         }
      }
   }

   private static TaskCompletionSource Completed()
   {
      var completed = new TaskCompletionSource();
      completed.SetResult();
      return completed;
   }
}
