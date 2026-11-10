using Lotsen.Hosting;

namespace Lotsen.Git;

/// <summary>Starts <see cref="CloneCache.MaintainAsync"/> as a background job, on a timer and on request after each prune.</summary>
/// <remarks>
/// A background server lives only while agent sessions stay open, so the timer checks often and the clone cache maintains each clone only once its interval has passed. The host's stop cancels
/// a run, and a run after the stop never starts.
/// </remarks>
internal sealed class CloneMaintenance : IDisposable
{
   private readonly CloneCache _clones;
   private readonly BackgroundJobs _jobs;
   private readonly ITimer _timer;

   public CloneMaintenance(CloneCache clones, BackgroundJobs jobs, TimeProvider time)
   {
      _clones = clones;
      _jobs = jobs;
      _timer = time.CreateTimer(_ => Request(), null, CheckInterval, CheckInterval);
   }

   /// <summary>The time from the server's start to the first check for due clones, and between two checks.</summary>
   public static TimeSpan CheckInterval => TimeSpan.FromMinutes(10);

   /// <summary>Starts a maintenance run of the clones that are due, unless the host is stopping.</summary>
   public void Request() => _jobs.Run(_clones.MaintainAsync);

   public void Dispose() => _timer.Dispose();
}
