using Lotsen.Git;
using Lotsen.Hosting;
using Lotsen.Pinning;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lotsen.Tests;

/// <summary>Covers how the clone cache maintains its clones only while no clone use holds them, and how maintenance, uses and prunes exclude each other.</summary>
[Trait("Category", "Git")]
public sealed class CloneCacheMaintenanceTests : IDisposable
{
   private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

   private readonly GitFixture _fixture = new();
   private readonly ManualTimeProvider _time = new();
   private readonly CommitId _commit;

   // The arguments of each git run, separated by spaces, and "maintenance done" when a maintenance run ends.
   private readonly List<string> _log = [];

   public CloneCacheMaintenanceTests()
   {
      _commit = _fixture.Commit("Add a file", ("a.txt", "one\n"));
   }

   private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

   private string Started => Path.Combine(_fixture.ScratchPath, "maintenance-started");

   private string Go => Path.Combine(_fixture.ScratchPath, "maintenance-go");


   [Fact]
   public async Task Maintenance_skips_a_clone_that_a_use_holds_and_runs_once_the_use_ends()
   {
      var cache = NewCache(LoggingGit(waits: false));
      using (var held = await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, Cancellation))
      {
         _time.Advance(CloneCache.MaintenanceInterval);
         await cache.MaintainAsync(Cancellation);
         Assert.Empty(MaintenanceRuns());
      }

      await cache.MaintainAsync(Cancellation);

      Assert.Single(MaintenanceRuns());
      Assert.Equal(0, cache.HeldClones);
   }

   /// <summary>Each fetch adds a pack to the clone, which only maintenance combines.</summary>
   [Fact]
   public async Task Maintenance_runs_on_a_clone_only_once_its_interval_has_passed_and_combines_its_packs()
   {
      var cache = NewCache();
      (await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, Cancellation)).Dispose();
      for (var i = 0; i < 3; i++)
      {
         _fixture.Commit($"Change {i}", ("a.txt", $"{i}\n"));
         (await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, Cancellation)).Dispose();
      }

      var clone = Assert.Single(_fixture.Clones());
      var before = Packs(clone);
      await cache.MaintainAsync(Cancellation);
      var notDue = Packs(clone);
      _time.Advance(CloneCache.MaintenanceInterval);
      await cache.MaintainAsync(Cancellation);

      Assert.True(before >= 4, $"The clone holds {before} packs.");
      Assert.Equal(before, notDue);
      Assert.True(Packs(clone) < before, $"The clone still holds {Packs(clone)} of its {before} packs.");
   }

   [Fact]
   public async Task Open_after_maintenance_looks_up_again_a_commit_that_an_earlier_open_found()
   {
      var cache = NewCache(LoggingGit(waits: false));
      (await cache.OpenAsync(_fixture.Remote(), [_commit], FetchPolicy.Sync, Cancellation)).Dispose();
      (await cache.OpenAsync(_fixture.Remote(), [_commit], FetchPolicy.WhenMissing, Cancellation)).Dispose();
      var lookupsBefore = Lookups();
      _time.Advance(CloneCache.MaintenanceInterval);
      await cache.MaintainAsync(Cancellation);

      using var repository = await cache.OpenAsync(_fixture.Remote(), [_commit], FetchPolicy.WhenMissing, Cancellation);

      Assert.Single(MaintenanceRuns());
      Assert.Equal(lookupsBefore + 1, Lookups());
   }

   /// <summary>The git double holds the maintenance until the test lets it go, so the prune runs while maintenance holds the clone.</summary>
   [Fact]
   public async Task Prune_during_maintenance_leaves_the_clone()
   {
      var cache = NewCache(LoggingGit(waits: true));
      (await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, Cancellation)).Dispose();
      var clone = Assert.Single(_fixture.Clones());
      _time.Advance(CloneCache.MaintenanceInterval);

      var maintenance = cache.MaintainAsync(Cancellation);
      await WaitForFileAsync(Started);
      var removed = await cache.PruneAsync(() => [], null, Cancellation);
      var kept = Directory.Exists(clone);
      File.WriteAllText(Go, "");
      await maintenance.WaitAsync(Wait, Cancellation);

      Assert.Equal(0, removed);
      Assert.True(kept, "The prune moved the clone that maintenance holds.");
      Assert.Equal(1, await cache.PruneAsync(() => [], null, Cancellation));
   }

   /// <summary>
   /// The git double holds the maintenance until the test lets it go. The open's clone holds the commit, so the open runs only its lookup, and a cache that let it through would start that
   /// lookup before the open returned, before the test lets the maintenance go.
   /// </summary>
   [Fact]
   public async Task Open_that_starts_during_maintenance_runs_git_only_after_the_maintenance()
   {
      var cache = NewCache(LoggingGit(waits: true));
      (await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, Cancellation)).Dispose();
      _time.Advance(CloneCache.MaintenanceInterval);

      var maintenance = cache.MaintainAsync(Cancellation);
      await WaitForFileAsync(Started);
      var open = cache.OpenAsync(_fixture.Remote(), [_commit], FetchPolicy.WhenMissing, Cancellation);
      var waited = !open.IsCompleted;
      File.WriteAllText(Go, "");
      await maintenance.WaitAsync(Wait, Cancellation);
      using var repository = await open.WaitAsync(Wait, Cancellation);

      var lines = Log();
      var done = Array.IndexOf(lines, "maintenance done");
      Assert.True(waited);
      Assert.True(done >= 0);
      Assert.DoesNotContain(lines[..done], line => line.StartsWith("rev-parse", StringComparison.Ordinal));
      Assert.Contains(lines[done..], line => line.StartsWith("rev-parse", StringComparison.Ordinal));
      Assert.Equal("one\n", await repository.ReadFileAsync(_commit, "a.txt", Cancellation));
   }

   /// <summary>A hold runs no git, so it starts at once during maintenance and keeps the clone after it, until the hold ends.</summary>
   [Fact]
   public async Task Hold_that_starts_during_maintenance_keeps_the_clone_from_the_next_maintenance_and_prune()
   {
      var cache = NewCache(LoggingGit(waits: true));
      (await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, Cancellation)).Dispose();
      _time.Advance(CloneCache.MaintenanceInterval);

      var maintenance = cache.MaintainAsync(Cancellation);
      await WaitForFileAsync(Started);
      var hold = cache.Hold(_fixture.Remote());
      File.WriteAllText(Go, "");
      await maintenance.WaitAsync(Wait, Cancellation);
      _time.Advance(CloneCache.MaintenanceInterval);
      await cache.MaintainAsync(Cancellation);
      var removedWhileHeld = await cache.PruneAsync(() => [], null, Cancellation);
      hold.Dispose();

      Assert.Single(MaintenanceRuns());
      Assert.Equal(0, removedWhileHeld);
      Assert.Equal(0, cache.HeldClones);
   }

   [Fact]
   public async Task Canceled_maintenance_ends_its_hold_on_the_clone_and_leaves_it_due()
   {
      var cache = NewCache(LoggingGit(waits: true));
      (await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, Cancellation)).Dispose();
      _time.Advance(CloneCache.MaintenanceInterval);
      using var stop = new CancellationTokenSource();

      var maintenance = cache.MaintainAsync(stop.Token);
      await WaitForFileAsync(Started);
      await stop.CancelAsync();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => maintenance.WaitAsync(Wait, Cancellation));
      var held = cache.HeldClones;
      File.WriteAllText(Go, "");
      await cache.MaintainAsync(Cancellation);

      Assert.Equal(0, held);
      Assert.Equal(2, MaintenanceRuns().Count);
   }

   /// <summary>
   /// The git double's first maintenance run takes git's lock and waits, so the stop ends the run and leaves the lock, as a kill of a real git does. Git skips its maintenance and exits 0
   /// while that lock exists.
   /// </summary>
   [Fact]
   public async Task Maintenance_after_a_killed_run_combines_the_packs_and_stores_its_time()
   {
      var cache = NewCache(LockTakingGit());
      (await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, Cancellation)).Dispose();
      for (var i = 0; i < 3; i++)
      {
         _fixture.Commit($"Change {i}", ("a.txt", $"{i}\n"));
         (await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, Cancellation)).Dispose();
      }

      var clone = Assert.Single(_fixture.Clones());
      var before = Packs(clone);
      _time.Advance(CloneCache.MaintenanceInterval);
      using var stop = new CancellationTokenSource();
      var killed = cache.MaintainAsync(stop.Token);
      await WaitForFileAsync(Started);
      await stop.CancelAsync();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => killed.WaitAsync(Wait, Cancellation));
      var lockLeft = File.Exists(Path.Combine(clone, "objects", "maintenance.lock"));

      await cache.MaintainAsync(Cancellation);
      await cache.MaintainAsync(Cancellation);

      Assert.True(lockLeft, "The killed run left no lock.");
      Assert.True(Packs(clone) < before, $"The clone still holds {Packs(clone)} of its {before} packs.");
      Assert.Equal(2, MaintenanceRuns().Count);
   }

   /// <summary>A ref that the fetch wrote as a file stays a file, because <see cref="GitRunner"/> configures maintenance to pack no refs.</summary>
   [Fact]
   public async Task Maintenance_combines_the_packs_and_leaves_the_refs_unpacked()
   {
      var cache = NewCache();
      (await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, Cancellation)).Dispose();
      _fixture.Checkout("feature", create: true);
      _fixture.Commit("Feature", ("b.txt", "feature\n"));
      _fixture.Checkout("main");
      (await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, Cancellation)).Dispose();
      var clone = Assert.Single(_fixture.Clones());
      var feature = Path.Combine(clone, "refs", "heads", "feature");
      var before = Packs(clone);
      _time.Advance(CloneCache.MaintenanceInterval);

      await cache.MaintainAsync(Cancellation);
      var unpacked = File.Exists(feature);
      _fixture.Git(_fixture.OriginPath, "branch", "-D", "feature");
      (await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, Cancellation)).Dispose();

      Assert.True(Packs(clone) < before, $"The clone still holds {Packs(clone)} of its {before} packs.");
      Assert.True(unpacked, "Maintenance packed the refs.");
      Assert.Empty(_fixture.Git(clone, "for-each-ref", "refs/heads/feature").Trim());
   }

   /// <summary>A directory in place of git's maintenance lock cannot be deleted as a file.</summary>
   [Fact]
   public async Task Maintenance_whose_lock_cannot_be_deleted_runs_no_git_and_waits_the_interval()
   {
      var cache = NewCache(LoggingGit(waits: false));
      (await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, Cancellation)).Dispose();
      var gitLock = Path.Combine(Assert.Single(_fixture.Clones()), "objects", "maintenance.lock");
      Directory.CreateDirectory(gitLock);
      _time.Advance(CloneCache.MaintenanceInterval);

      await cache.MaintainAsync(Cancellation);
      Directory.Delete(gitLock);
      await cache.MaintainAsync(Cancellation);
      var beforeInterval = MaintenanceRuns().Count;
      _time.Advance(CloneCache.MaintenanceInterval);
      await cache.MaintainAsync(Cancellation);

      Assert.Equal(0, beforeInterval);
      Assert.Single(MaintenanceRuns());
   }

   /// <summary>The timer's check starts a background run, which maintains the clone that is due.</summary>
   [Fact]
   public async Task Maintenance_timer_maintains_a_due_clone_in_the_background()
   {
      var cache = NewCache(LoggingGit(waits: false));
      (await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, Cancellation)).Dispose();

      // The background run can read the clock at the timer's due time, before Advance reaches its target, so the clone is due before the timer exists.
      _time.Advance(CloneCache.MaintenanceInterval);
      using var jobs = new BackgroundJobs(NullLogger<BackgroundJobs>.Instance);
      using var maintenance = new CloneMaintenance(cache, jobs, _time);
      var beforeCheck = MaintenanceRuns().Count;

      _time.Advance(CloneMaintenance.CheckInterval);
      await jobs.WhenIdleAsync().WaitAsync(Wait, Cancellation);

      Assert.Equal(0, beforeCheck);
      Assert.Single(MaintenanceRuns());
   }

   /// <summary>Git's own maintenance would run inside a fetch or a lazy download, while a clone use holds the clone.</summary>
   [Theory]
   [InlineData("gc.auto", "0")]
   [InlineData("maintenance.auto", "false")]
   public async Task Git_runs_start_no_automatic_maintenance(string key, string value)
   {
      var output = await new GitRunner(new GitOptions()).RunAsync(new GitCall("Git could not read its configuration", ["config", "--get", key], TimeSpan.FromSeconds(30)) { WorkingDirectory = _fixture.ScratchPath }, Cancellation);

      Assert.Equal(value, output.Text.Trim());
   }

   public void Dispose() => _fixture.Dispose();

   private static async Task WaitForFileAsync(string path)
   {
      using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
      timeout.CancelAfter(Wait);
      while (!File.Exists(path))
      {
         await Task.Delay(50, timeout.Token);
      }
   }

   private static int Packs(string clone) => Directory.GetFiles(Path.Combine(clone, "objects", "pack"), "*.pack").Length;

   private List<string> MaintenanceRuns() => [.. Log().Where(line => line.StartsWith("maintenance run", StringComparison.Ordinal))];

   // The cache's lookups of a commit in a clone.
   private int Lookups() => Log().Count(line => line.StartsWith("rev-parse --verify", StringComparison.Ordinal));

   private string[] Log()
   {
      lock (_log)
      {
         return [.. _log];
      }
   }

   private void Append(string line)
   {
      lock (_log)
      {
         _log.Add(line);
      }
   }

   private CloneCache NewCache(GitOptions? options = null) => _fixture.NewCache(options, time: _time);

   // Returns git options that log each run's arguments. With waits, a maintenance run marks its start and waits for the go file, or until the fixture is gone, and every maintenance run that
   // runs git logs its end.
   private GitOptions LoggingGit(bool waits) => GitDouble.Options(async run =>
   {
      Append(string.Join(' ', run.Command.Arguments));
      if (run.Subcommand != "maintenance")
      {
         return await run.GitAsync();
      }

      if (waits)
      {
         File.WriteAllText(Started, "");
         if (await run.HoldAsync(() => File.Exists(Go) || !Directory.Exists(_fixture.ScratchPath)) is { } ended)
         {
            return ended;
         }
      }

      var outcome = await run.GitAsync();
      Append("maintenance done");
      return outcome;
   });

   // Returns git options that log each run's arguments. The first maintenance run creates git's maintenance lock in the clone, marks its start and waits until the fixture is gone, or until the
   // run is canceled. A later maintenance run runs git.
   private GitOptions LockTakingGit() => GitDouble.Options(async run =>
   {
      Append(string.Join(' ', run.Command.Arguments));
      if (run.Subcommand == "maintenance" && !File.Exists(Started))
      {
         File.WriteAllText(Path.Combine(run.Command.WorkingDirectory!, "objects", "maintenance.lock"), "");
         File.WriteAllText(Started, "");
         if (await run.HoldAsync(() => !Directory.Exists(_fixture.ScratchPath)) is { } ended)
         {
            return ended;
         }
      }

      return await run.GitAsync();
   });
}
