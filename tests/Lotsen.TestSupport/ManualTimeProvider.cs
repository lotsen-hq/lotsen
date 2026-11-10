namespace Lotsen.Tests;

/// <summary>A clock that moves only when a test advances it, whose timers fire only then, and which keeps every timer it created so a test can fire one late.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
   private static readonly TimeSpan StepPause = TimeSpan.FromMilliseconds(10);

   private readonly Lock _lock = new();
   private readonly List<ManualTimer> _timers = [];
   private readonly List<Thread> _earlyFires = [];
   private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

   public IReadOnlyList<ManualTimer> Timers
   {
      get
      {
         lock (_lock)
         {
            return [.. _timers];
         }
      }
   }

   public ManualTimer LastTimer => Timers[^1];

   /// <summary>
   /// Whether <see cref="CreateTimer"/> fires each new timer on a thread of its own before it returns, once that thread waits or ends, as a system timer can that comes due before its creator
   /// stores it. <see cref="JoinEarlyFires"/> waits for those threads.
   /// </summary>
   public bool FiresEarly { get; set; }

   public override DateTimeOffset GetUtcNow()
   {
      lock (_lock)
      {
         return _now;
      }
   }

   public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
   {
      var timer = new ManualTimer(this, callback, state);
      lock (_lock)
      {
         _timers.Add(timer);
      }

      timer.Change(dueTime, period);
      if (FiresEarly)
      {
         FireEarly(timer);
      }

      return timer;
   }

   /// <summary>Waits until every thread that fired a timer early has ended.</summary>
   public void JoinEarlyFires()
   {
      List<Thread> threads;
      lock (_lock)
      {
         threads = [.. _earlyFires];
      }

      foreach (var thread in threads)
      {
         if (!thread.Join(TimeSpan.FromSeconds(10)))
         {
            throw new TimeoutException("A timer that fired early did not finish its callback.");
         }
      }
   }

   // CreateTimer returns only once the thread waits or ends, as when the callback waits for a lock that the timer's creator holds.
   private void FireEarly(ManualTimer timer)
   {
      var thread = new Thread(timer.Fire) { IsBackground = true };
      lock (_lock)
      {
         _earlyFires.Add(thread);
      }

      thread.Start();
      var deadline = Environment.TickCount64 + 10_000;
      var spin = new SpinWait();
      while ((thread.ThreadState & (ThreadState.WaitSleepJoin | ThreadState.Stopped)) == 0)
      {
         if (Environment.TickCount64 > deadline)
         {
            throw new TimeoutException("A timer that fired early neither waited nor ended.");
         }

         spin.SpinOnce();
      }
   }

   /// <summary>Moves the clock and fires, in due order, each live timer that comes due.</summary>
   public void Advance(TimeSpan by)
   {
      DateTimeOffset target;
      lock (_lock)
      {
         target = _now + by;
      }

      while (NextDue(target) is { } timer)
      {
         lock (_lock)
         {
            _now = timer.Due!.Value;
         }

         timer.Fire();
      }

      lock (_lock)
      {
         _now = target;
      }
   }

   /// <summary>Moves the clock as <see cref="Advance"/> does, but holds the clock at each timer's due time until the work that the timer started has settled.</summary>
   /// <remarks>
   /// Use it when a timer's callback hands work to another thread that reads the clock, such as a background job. Under <see cref="Advance"/> that work can read any time up to the target.
   /// </remarks>
   /// <param name="by">How far to move the clock.</param>
   /// <param name="settle">
   /// Returns a task that completes once the work that a fire started is done. It runs after each fire and once more after the clock reaches the target. It must not wait for this clock, which
   /// stands still while it runs.
   /// </param>
   public async Task AdvanceAsync(TimeSpan by, Func<Task> settle)
   {
      DateTimeOffset target;
      lock (_lock)
      {
         target = _now + by;
      }

      while (NextDue(target) is { } timer)
      {
         lock (_lock)
         {
            _now = timer.Due!.Value;
         }

         timer.Fire();
         await settle();
      }

      lock (_lock)
      {
         _now = target;
      }

      await settle();
   }

   /// <summary>Moves the clock by a step at a time, as <see cref="Advance"/> does, until the task completes.</summary>
   /// <remarks>
   /// Use it for a loop that polls the clock and arms its next wait from what it read, which <see cref="Advance"/> can leave waiting on a timer past the target. The steps continue past any
   /// time that a late read saw, so the loop reaches its deadline. After each step it waits a short real time for the task, so the loop's work between two clock reads has time to run.
   /// </remarks>
   /// <param name="done">The work to drive. The advance ends once the task completes in any state, and leaves the task's outcome to the caller.</param>
   /// <param name="step">How far each step moves the clock.</param>
   /// <param name="limit">The real time to wait for the task before the advance fails.</param>
   /// <exception cref="TimeoutException">The task has not completed within the limit.</exception>
   public async Task AdvanceUntilAsync(Task done, TimeSpan step, TimeSpan limit)
   {
      var deadline = Environment.TickCount64 + (long)limit.TotalMilliseconds;
      while (!done.IsCompleted)
      {
         if (Environment.TickCount64 > deadline)
         {
            throw new TimeoutException($"The work did not complete within {limit} of real time, while the clock moved by {step} at a time to {GetUtcNow():O}.");
         }

         Advance(step);
         await done.WaitAsync(StepPause).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
      }
   }

   private ManualTimer? NextDue(DateTimeOffset target)
   {
      lock (_lock)
      {
         return _timers.Where(timer => timer.Due is { } due && due <= target).OrderBy(timer => timer.Due).FirstOrDefault();
      }
   }

   /// <summary>One timer of <see cref="ManualTimeProvider"/>, which fires once at its due time.</summary>
   internal sealed class ManualTimer : ITimer
   {
      private readonly ManualTimeProvider _owner;
      private readonly TimerCallback _callback;
      private readonly object? _state;

      public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
      {
         _owner = owner;
         _callback = callback;
         _state = state;
      }

      /// <summary>When the timer fires next, or null when it is stopped or disposed.</summary>
      public DateTimeOffset? Due { get; private set; }

      public bool Change(TimeSpan dueTime, TimeSpan period)
      {
         Due = dueTime == Timeout.InfiniteTimeSpan ? null : _owner.GetUtcNow() + dueTime;
         return true;
      }

      /// <summary>Runs the callback as a timer thread does that took the callback just before the timer was disposed.</summary>
      public void FireAlthoughDisposed() => _callback(_state);

      public void Dispose() => Due = null;

      public ValueTask DisposeAsync()
      {
         Dispose();
         return ValueTask.CompletedTask;
      }

      internal void Fire()
      {
         Due = null;
         _callback(_state);
      }
   }
}
