using Lotsen.Hosting;
using Lotsen.Pinning;
using Lotsen.Platforms;
using Lotsen.Tours;

namespace Lotsen.Operations;

/// <summary>Why a tour's iteration check is due.</summary>
internal enum Nudge
{
   /// <summary>A read of the tour by the reviewer, which also keeps the tour in the poll.</summary>
   ReviewerRead,

   /// <summary>An operation of the reviewer's on the tour, such as a question.</summary>
   ReviewerAction,

   /// <summary>An attach of the tour by an agent session.</summary>
   SessionAttached,

   /// <summary>A store that changed the stops, the file marks or the code and file anchors of a tour with an available iteration, so its stale stops may differ now.</summary>
   TourChanged,

   /// <summary>An iteration update, after which a newer head can be the next available iteration.</summary>
   Repinned,

   /// <summary>The poll of a tour that the reviewer read within the active window.</summary>
   Timer,
}

/// <summary>The timings of the iteration scheduler.</summary>
/// <param name="Debounce">How long a nudged check waits, so that nudges close together run one check.</param>
/// <param name="MinInterval">How long a platform answer serves later checks of its tour instead of a new request. Each failed check in a row doubles it, up to <paramref name="PollInterval"/>.</param>
/// <param name="PollInterval">How often the poll checks the tours that the reviewer read within <paramref name="ActiveWindow"/>.</param>
/// <param name="ActiveWindow">How long after the reviewer's last read of a tour the poll checks it.</param>
internal sealed record IterationSchedule(TimeSpan Debounce, TimeSpan MinInterval, TimeSpan PollInterval, TimeSpan ActiveWindow)
{
   /// <summary>The server's timings: a 2 second debounce, a 30 second interval, a 3 minute poll and a 15 minute window.</summary>
   public static IterationSchedule Default { get; } = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(15));
}

/// <summary>Decides when each tour's iteration check runs, and runs at most one check of a tour at a time, as a background job.</summary>
/// <remarks>
/// <para>
/// Each tour is idle, due or running. A nudge of an idle tour makes it due after the debounce, and nudges while it is due join that check. A nudge while a check runs makes the tour due once
/// the check ends. A check reuses the last platform answer of its tour while the answer is younger than the interval, and always for <see cref="Nudge.TourChanged"/>, which needs no new answer.
/// A <see cref="Nudge.Timer"/> always asks the platform.
/// </para>
/// <para>
/// The poll nudges every tour that the reviewer read within the active window, except one whose pull request the last answer reported as completed or abandoned. After a failed check, a check
/// that asks the platform waits for the doubled interval since the failure.
/// </para>
/// <para>
/// Once disposed, it starts no check, and a check that the host's stop cancels completes its waiting callers as canceled.
/// </para>
/// </remarks>
internal sealed class IterationScheduler : IDisposable
{
   private readonly Lock _lock = new();
   private readonly Dictionary<TourId, Entry> _entries = [];
   private readonly IterationSchedule _schedule;
   private readonly TimeProvider _time;
   private readonly Func<Func<CancellationToken, Task>, bool> _start;
   private readonly Func<TourId, PlatformAnswer?, CancellationToken, Task<CheckRun>> _run;
   private readonly ILogger<IterationScheduler> _logger;
   private ITimer? _poll;
   private bool _disposed;

   /// <summary>Schedules the lifecycle's checks as background jobs, and nudges the checks that the writer's stores call for.</summary>
   public IterationScheduler(IterationSchedule schedule, TimeProvider time, BackgroundJobs jobs, IterationLifecycle lifecycle, TourWriter writer, ILogger<IterationScheduler> logger)
      : this(schedule, time, jobs.Run, lifecycle.CheckAsync, logger)
   {
      writer.ObserveStores(Observe);
   }

   /// <param name="schedule">The timings.</param>
   /// <param name="time">The clock and the timers.</param>
   /// <param name="start">Starts a job in the background and returns whether it started, which it does not once the host is stopping.</param>
   /// <param name="run">Runs one check of the tour, with the answer to reuse or null.</param>
   /// <param name="logger">The log.</param>
   internal IterationScheduler(
      IterationSchedule schedule,
      TimeProvider time,
      Func<Func<CancellationToken, Task>, bool> start,
      Func<TourId, PlatformAnswer?, CancellationToken, Task<CheckRun>> run,
      ILogger<IterationScheduler> logger)
   {
      _schedule = schedule;
      _time = time;
      _start = start;
      _run = run;
      _logger = logger;
   }

   private enum RunState
   {
      Idle,
      Due,
      Running,
   }

   /// <summary>Makes the tour's check due for the reason, without waiting or throwing.</summary>
   public void Nudge(TourId id, Nudge reason)
   {
      lock (_lock)
      {
         if (!_disposed)
         {
            Nudged(id, EntryOf(id), reason, _time.GetUtcNow());
         }
      }
   }

   /// <summary>Runs a check of the tour that asks the platform now, without the debounce, and returns what it left.</summary>
   /// <remarks>While a check of the tour runs, this one runs once that check ends, and requests in the meantime join it, so each sees the platform as it was after the request.</remarks>
   /// <exception cref="OperationCanceledException">The token was canceled, or the host stopped the check.</exception>
   public async Task<CheckRun> CheckNowAsync(TourId id, CancellationToken cancellationToken)
   {
      TaskCompletionSource<CheckRun> completes;
      lock (_lock)
      {
         ObjectDisposedException.ThrowIf(_disposed, this);
         var entry = EntryOf(id);
         if (entry.State == RunState.Running)
         {
            completes = entry.Waiting ??= NewCompletion();
         }
         else
         {
            completes = NewCompletion();
            entry.Fresh = true;
            StartRun(id, entry, completes);
         }
      }

      return await completes.Task.WaitAsync(cancellationToken);
   }

   /// <summary>Drops the tour's schedule, as after its delete. A waiting <see cref="CheckNowAsync"/> gets a run without a tour.</summary>
   public void Forget(TourId id)
   {
      lock (_lock)
      {
         if (_entries.Remove(id, out var entry))
         {
            entry.Timer?.Dispose();
            entry.Waiting?.TrySetResult(new CheckRun(null, null, null));
            entry.Waiting = null;
         }
      }
   }

   public void Dispose()
   {
      lock (_lock)
      {
         _disposed = true;
         _poll?.Dispose();
         foreach (var entry in _entries.Values)
         {
            entry.Timer?.Dispose();
            entry.Waiting?.TrySetCanceled();
         }
      }
   }

   private static TaskCompletionSource<CheckRun> NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

   // A store that changes what an update to the available iteration maps changes its stale stops, and an update can leave a newer head to find.
   private void Observe(StoredChange change)
   {
      var (before, after) = (change.Before, change.After);
      if (before.Pin is TourPin.Pinned && after.Pin is TourPin.Pinned && before.Pin != after.Pin)
      {
         Nudge(after.Id, Operations.Nudge.Repinned);
      }
      else if (after.AvailableIteration is not null && before.Pin == after.Pin && !SameRemapInput(before, after))
      {
         Nudge(after.Id, Operations.Nudge.TourChanged);
      }
   }

   // Whether the two tours hold the same stops with content, at the same versions and markers, the same file marks and the same code and file anchors, which is all that the remap reads.
   private static bool SameRemapInput(Tour before, Tour after) =>
      Written(before).SequenceEqual(Written(after))
      && (before.Plan?.FileMarks ?? []).SequenceEqual(after.Plan?.FileMarks ?? [])
      && Anchors(before).SequenceEqual(Anchors(after));

   private static IEnumerable<(string Id, int Version, int? StaleOn)> Written(Tour tour) =>
      (tour.Plan?.Stops ?? []).OfType<Stop.Written>().Select(stop => (stop.Id, stop.Version, stop.StaleOn));

   private static IEnumerable<(string Id, ThreadAnchor Anchor)> Anchors(Tour tour) =>
      tour.Threads.Where(thread => thread.Anchor is ThreadAnchor.CodeRange or ThreadAnchor.WholeFile).Select(thread => (thread.Id, thread.Anchor));

   // Call it under the lock.
   private Entry EntryOf(TourId id)
   {
      if (!_entries.TryGetValue(id, out var entry))
      {
         entry = new Entry();
         _entries[id] = entry;
      }

      return entry;
   }

   // Call it under the lock.
   private void Nudged(TourId id, Entry entry, Nudge reason, DateTimeOffset now)
   {
      if (reason == Operations.Nudge.ReviewerRead)
      {
         entry.ReadAt = now;
         ArmPoll();
      }

      var (timer, needsAnswer) = (reason == Operations.Nudge.Timer, reason != Operations.Nudge.TourChanged);
      switch (entry.State)
      {
         // The run replaces the answer, so its end judges the age of the new one.
         case RunState.Running:
            entry.Again = true;
            entry.AgainTimer |= timer;
            entry.AgainNeedsAnswer |= needsAnswer;
            break;
         case RunState.Due:
            entry.Fresh |= Fresh(entry, timer, needsAnswer, now);
            break;
         default:
            Schedule(id, entry, Fresh(entry, timer, needsAnswer, now), now);
            break;
      }
   }

   // Call it under the lock. Returns whether a run for these nudges asks the platform instead of reusing the entry's last answer.
   private bool Fresh(Entry entry, bool timer, bool needsAnswer, DateTimeOffset now) =>
      timer || entry.Answer is not PlatformAnswer.Resolved answer || (needsAnswer && now - answer.At >= Interval(entry));

   // Call it under the lock. The interval doubles with each failed check in a row, up to the poll interval.
   private TimeSpan Interval(Entry entry)
   {
      var interval = _schedule.MinInterval;
      for (var i = 0; i < entry.Failures && interval < _schedule.PollInterval; i++)
      {
         interval *= 2;
      }

      return interval < _schedule.PollInterval ? interval : _schedule.PollInterval;
   }

   // Call it under the lock, for an idle tour.
   private void Schedule(TourId id, Entry entry, bool fresh, DateTimeOffset now)
   {
      entry.State = RunState.Due;
      entry.Fresh = fresh;
      var due = now + _schedule.Debounce;
      if (fresh && entry.FailedAt is { } failedAt && failedAt + Interval(entry) > due)
      {
         due = failedAt + Interval(entry);
      }

      if (due <= now)
      {
         StartRun(id, entry, null);
         return;
      }

      // The callback captures the generation by value, because the timer can fire before CreateTimer returns.
      var generation = ++entry.Generation;
      entry.Timer = _time.CreateTimer(_ => Fire(id, entry, generation), null, due - now, Timeout.InfiniteTimeSpan);
   }

   private void Fire(TourId id, Entry entry, int generation)
   {
      lock (_lock)
      {
         if (!_disposed && entry.State == RunState.Due && entry.Generation == generation && _entries.TryGetValue(id, out var current) && ReferenceEquals(current, entry))
         {
            StartRun(id, entry, null);
         }
      }
   }

   // Call it under the lock. A run that asks the platform afresh satisfies every nudge that made the tour due.
   private void StartRun(TourId id, Entry entry, TaskCompletionSource<CheckRun>? completes)
   {
      entry.Timer?.Dispose();
      entry.Timer = null;
      entry.State = RunState.Running;
      entry.Again = false;
      entry.AgainTimer = false;
      entry.AgainNeedsAnswer = false;
      var reuse = entry.Fresh ? null : entry.Answer as PlatformAnswer.Resolved;
      if (!_start(cancellationToken => RunAsync(id, entry, reuse, completes, cancellationToken)))
      {
         entry.State = RunState.Idle;
         completes?.TrySetCanceled();
      }
   }

   private async Task RunAsync(TourId id, Entry entry, PlatformAnswer? reuse, TaskCompletionSource<CheckRun>? completes, CancellationToken cancellationToken)
   {
      CheckRun? run = null;
      try
      {
         run = await _run(id, reuse, cancellationToken);
      }
      catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
      {
      }
      catch (Exception exception)
      {
         _logger.LogError(exception, "The iteration check of tour {TourId} failed.", id.Value);
      }
      finally
      {
         Finished(id, entry, run);
         if (run is null)
         {
            completes?.TrySetCanceled(CancellationToken.None);
         }
         else
         {
            completes?.TrySetResult(run);
         }
      }
   }

   private void Finished(TourId id, Entry entry, CheckRun? run)
   {
      lock (_lock)
      {
         var now = _time.GetUtcNow();
         entry.State = RunState.Idle;
         if (run?.Answer is { } answer)
         {
            entry.Answer = answer;
         }

         if (run is { Failure: not null })
         {
            entry.Failures++;
            entry.FailedAt = now;
         }
         else if (run is not null)
         {
            entry.Failures = 0;
            entry.FailedAt = null;
         }

         if (_disposed || !_entries.TryGetValue(id, out var current) || !ReferenceEquals(current, entry))
         {
            entry.Waiting?.TrySetCanceled();
            entry.Waiting = null;
         }
         else if (entry.Waiting is { } waiting)
         {
            entry.Waiting = null;
            entry.Fresh = true;
            StartRun(id, entry, waiting);
         }
         else if (run is { Tour: null })
         {
            _entries.Remove(id);
         }
         else if (entry.Again)
         {
            Schedule(id, entry, Fresh(entry, entry.AgainTimer, entry.AgainNeedsAnswer, now), now);
         }
      }
   }

   // Call it under the lock.
   private void ArmPoll()
   {
      if (_poll is null && !_disposed)
      {
         _poll = _time.CreateTimer(_ => Poll(), null, _schedule.PollInterval, Timeout.InfiniteTimeSpan);
      }
   }

   private void Poll()
   {
      lock (_lock)
      {
         _poll?.Dispose();
         _poll = null;
         if (_disposed)
         {
            return;
         }

         var now = _time.GetUtcNow();
         var active = _entries.Where(entry => entry.Value.ReadAt is { } read && now - read < _schedule.ActiveWindow).ToList();
         foreach (var (id, entry) in active)
         {
            if (entry.Answer is not PlatformAnswer.Resolved { Latest.State: not PullRequestState.Active })
            {
               Nudged(id, entry, Operations.Nudge.Timer, now);
            }
         }

         if (active.Count > 0)
         {
            ArmPoll();
         }
      }
   }

   // The schedule of one tour, which only the scheduler's lock guards.
   private sealed class Entry
   {
      public RunState State { get; set; }

      public ITimer? Timer { get; set; }

      // The number of the debounce timer that Schedule created last, which only that timer's callback starts a run for.
      public int Generation { get; set; }

      // Whether the next run asks the platform instead of reusing the last answer.
      public bool Fresh { get; set; }

      // Whether a nudge arrived while the current run ran, whether one of them was the poll's, and whether one of them needs an answer younger than the interval.
      public bool Again { get; set; }

      public bool AgainTimer { get; set; }

      public bool AgainNeedsAnswer { get; set; }

      // The callers of CheckNowAsync that wait for the run after the current one.
      public TaskCompletionSource<CheckRun>? Waiting { get; set; }

      public PlatformAnswer? Answer { get; set; }

      public int Failures { get; set; }

      public DateTimeOffset? FailedAt { get; set; }

      public DateTimeOffset? ReadAt { get; set; }
   }
}
