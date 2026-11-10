using Lotsen.Git;
using Lotsen.Hosting;
using Lotsen.Pinning;
using Lotsen.Processes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lotsen.Tests;

/// <summary>Covers how the clone cache prunes the clones that no tour keeps and the temporary clones that a crashed clone left, and how it leaves alone a clone in use.</summary>
[Trait("Category", "Store")]
public sealed class ClonePruneTests : IDisposable
{
   private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

   private readonly GitFixture _fixture = new();
   private readonly GitFixture _other = new();

   public ClonePruneTests()
   {
      _fixture.Commit("Add a file", ("a.txt", "one\n"));
      _other.Commit("Add another file", ("b.txt", "two\n"));
   }

   private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

   private string ClonesPath => _fixture.DataDirectory.ClonesPath;

   [Fact]
   public async Task Prune_removes_each_clone_that_is_not_kept_and_keeps_the_kept_one()
   {
      var cache = NewCache();
      (await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, Cancellation)).Dispose();
      var kept = Assert.Single(_fixture.Clones());
      (await cache.OpenAsync(_other.Remote(), [], FetchPolicy.Sync, Cancellation)).Dispose();

      var removed = await cache.PruneAsync(() => [_fixture.Url], null, Cancellation);

      Assert.Equal(1, removed);
      Assert.Equal([kept], _fixture.Clones());
   }

   [Fact]
   public async Task Prune_limited_to_some_repositories_removes_only_their_clones()
   {
      var cache = NewCache();
      (await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, Cancellation)).Dispose();
      var untouched = Assert.Single(_fixture.Clones());
      (await cache.OpenAsync(_other.Remote(), [], FetchPolicy.Sync, Cancellation)).Dispose();

      var removed = await cache.PruneAsync(() => [], [_other.Url], Cancellation);

      Assert.Equal(1, removed);
      Assert.Equal([untouched], _fixture.Clones());
   }

   /// <summary>A process killed during its clone left a temporary clone of a kept repository and one of a repository that no tour keeps. A file and a directory that no clone names sit beside them.</summary>
   [Fact]
   public async Task Prune_removes_the_temporary_clones_that_a_crashed_clone_left_and_leaves_other_entries()
   {
      var cache = NewCache();
      (await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, Cancellation)).Dispose();
      var clone = Assert.Single(_fixture.Clones());
      var keptLeftover = Directory.CreateDirectory($"{clone}.999999.tmp").FullName;
      File.WriteAllText(Path.Combine(keptLeftover, "HEAD"), "partial");
      var otherLeftover = Directory.CreateDirectory(Path.Combine(ClonesPath, new string('0', 32) + ".999999.tmp")).FullName;
      var stranger = Directory.CreateDirectory(Path.Combine(ClonesPath, "notes")).FullName;
      var note = Path.Combine(ClonesPath, "README");
      File.WriteAllText(note, "Not a clone.");

      var removed = await cache.PruneAsync(() => [_fixture.Url], null, Cancellation);

      Assert.Equal(2, removed);
      Assert.False(Directory.Exists(keptLeftover));
      Assert.False(Directory.Exists(otherLeftover));
      Assert.True(Directory.Exists(clone));
      Assert.True(Directory.Exists(stranger));
      Assert.True(File.Exists(note));
   }

   [Fact]
   public async Task Prune_keeps_a_clone_while_a_repository_opened_from_it_is_in_use_and_removes_it_after()
   {
      var cache = NewCache();
      var repository = await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, Cancellation);
      var clone = Assert.Single(_fixture.Clones());

      var whileInUse = await cache.PruneAsync(() => [], null, Cancellation);
      var read = await repository.ReadFileAsync(CommitOf("main"), "a.txt", Cancellation);
      repository.Dispose();
      var afterUse = await cache.PruneAsync(() => [], null, Cancellation);

      Assert.Equal(0, whileInUse);
      Assert.Equal("one\n", read);
      Assert.Equal(1, afterUse);
      Assert.False(Directory.Exists(clone));
   }

   /// <summary>The last use of the clone ends while the prune reads which clones to keep, as when a pin stores its result after the prune read the tours.</summary>
   [Fact]
   public async Task Prune_keeps_a_clone_whose_last_use_ends_while_it_reads_the_clones_to_keep()
   {
      var cache = NewCache();
      var repository = await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, Cancellation);
      var clone = Assert.Single(_fixture.Clones());

      var removed = await cache.PruneAsync(() =>
      {
         repository.Dispose();
         return [];
      }, null, Cancellation);
      var kept = Directory.Exists(clone);
      var next = await cache.PruneAsync(() => [], null, Cancellation);

      Assert.Equal(0, removed);
      Assert.True(kept, "The prune removed a clone whose last use ended after it started.");
      Assert.Equal(1, next);
   }

   /// <summary>The git double finishes the clone into its temporary directory, then waits, so the prune runs while the temporary clone exists and the clone is in progress.</summary>
   [Fact]
   public async Task Prune_keeps_the_temporary_clone_of_a_clone_in_progress()
   {
      var started = Path.Combine(_fixture.ScratchPath, "clone-started");
      var go = Path.Combine(_fixture.ScratchPath, "clone-go");
      var cache = NewCache(GitThatWaitsAfterClone(started, go));

      var sync = cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, Cancellation);
      await WaitForFileAsync(started);
      var temporary = Assert.Single(Directory.GetDirectories(ClonesPath, "*.tmp"));
      var removed = await cache.PruneAsync(() => [], null, Cancellation);
      var kept = Directory.Exists(temporary);
      File.WriteAllText(go, "");
      using var repository = await sync.WaitAsync(Wait, Cancellation);

      Assert.Equal(0, removed);
      Assert.True(kept, "The prune removed the temporary clone of the clone in progress.");
      Assert.Equal("one\n", await repository.ReadFileAsync(CommitOf("main"), "a.txt", Cancellation));
   }

   /// <summary>The sync runs after the prune took the clone out of the cache and before it deletes its files, which is where the sync of a pin that starts during the prune lands.</summary>
   [Fact]
   public async Task Pin_sync_that_starts_while_a_prune_removes_the_clone_gets_a_complete_clone_of_its_own()
   {
      var directories = new HookedCloneDirectories(_fixture);
      var cache = _fixture.NewCache(directories: directories);
      (await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, Cancellation)).Dispose();
      var clone = Assert.Single(_fixture.Clones());
      GitRepository? synced = null;
      directories.BeforeRemove = _ => synced ??= cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, Cancellation).WaitAsync(Wait, Cancellation).GetAwaiter().GetResult();

      var removed = await cache.PruneAsync(() => [], null, Cancellation);
      directories.BeforeRemove = null;

      using var repository = Assert.IsType<GitRepository>(synced);
      Assert.Equal(1, removed);
      Assert.Equal([clone], _fixture.Clones());
      Assert.Equal("one\n", await repository.ReadFileAsync(CommitOf("main"), "a.txt", Cancellation));
      Assert.Equal(0, await cache.PruneAsync(() => [], [], Cancellation));
      Assert.Equal(0, await cache.PruneAsync(() => [_fixture.Url], null, Cancellation));
   }

   /// <summary>Each directory's name is a clone's or a temporary clone's name with a line feed at the end, which no clone of the cache has.</summary>
   [Fact]
   public async Task Prune_leaves_a_directory_whose_name_only_starts_like_a_clone_name()
   {
      Assert.SkipWhen(OperatingSystem.IsWindows(), "A directory name on Windows cannot hold a line feed.");
      var clone = Directory.CreateDirectory(Path.Combine(ClonesPath, new string('3', 32) + "\n")).FullName;
      var temporary = Directory.CreateDirectory(Path.Combine(ClonesPath, new string('4', 32) + ".1.tmp\n")).FullName;

      var removed = await NewCache().PruneAsync(() => [], null, Cancellation);

      Assert.Equal(0, removed);
      Assert.True(Directory.Exists(clone));
      Assert.True(Directory.Exists(temporary));
   }

   [Fact]
   public async Task Prune_that_is_canceled_before_it_starts_removes_nothing()
   {
      var cache = NewCache();
      (await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, Cancellation)).Dispose();
      var clone = Assert.Single(_fixture.Clones());

      var removed = await cache.PruneAsync(() => [], null, new CancellationToken(canceled: true));

      Assert.Equal(0, removed);
      Assert.True(Directory.Exists(clone));
   }

   /// <summary>The prune is canceled after it moved both clones out of the cache, just before it deletes the first one's files.</summary>
   [Fact]
   public async Task Prune_that_is_canceled_while_it_removes_finishes_the_clone_in_hand_and_leaves_the_rest_to_the_next_prune()
   {
      var directories = new HookedCloneDirectories(_fixture);
      var cache = _fixture.NewCache(directories: directories);
      (await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, Cancellation)).Dispose();
      (await cache.OpenAsync(_other.Remote(), [], FetchPolicy.Sync, Cancellation)).Dispose();
      using var stop = new CancellationTokenSource();
      directories.BeforeRemove = _ => stop.Cancel();

      var removed = await cache.PruneAsync(() => [], null, stop.Token);
      directories.BeforeRemove = null;
      var first = Assert.Single(directories.Removed);
      var left = Assert.Single(Directory.GetDirectories(ClonesPath, "*.removed"));
      var next = await cache.PruneAsync(() => [], null, Cancellation);

      Assert.Equal(2, removed);
      Assert.False(Directory.Exists(first.Path));
      Assert.NotEqual(first.Path, left);
      Assert.Equal(0, next);
      Assert.Empty(Directory.GetDirectories(ClonesPath));
   }

   /// <summary>A link named as a clone and one named as a temporary clone point outside the cache, and a clone that no tour keeps holds a link that points there too.</summary>
   [Fact]
   public async Task Prune_never_follows_or_removes_a_link_out_of_the_clone_cache()
   {
      SkipOnWindows();
      var cache = NewCache();
      (await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, Cancellation)).Dispose();
      var clone = Assert.Single(_fixture.Clones());
      var outside = Directory.CreateDirectory(Path.Combine(_fixture.ScratchPath, "outside")).FullName;
      var precious = Path.Combine(outside, "precious.txt");
      File.WriteAllText(precious, "Keep me.");
      Directory.CreateSymbolicLink(Path.Combine(clone, "escape"), outside);
      var cloneLink = Directory.CreateSymbolicLink(Path.Combine(ClonesPath, new string('1', 32)), outside).FullName;
      var temporaryLink = Directory.CreateSymbolicLink(Path.Combine(ClonesPath, new string('2', 32) + ".1.tmp"), outside).FullName;

      var removed = await cache.PruneAsync(() => [], null, Cancellation);

      Assert.Equal(1, removed);
      Assert.False(Directory.Exists(clone));
      Assert.True(File.Exists(precious));
      Assert.NotNull(new DirectoryInfo(cloneLink).LinkTarget);
      Assert.NotNull(new DirectoryInfo(temporaryLink).LinkTarget);
   }

   [Fact]
   public async Task Prune_removes_nothing_when_the_clone_cache_directory_is_a_link()
   {
      SkipOnWindows();
      var cache = NewCache();
      (await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, Cancellation)).Dispose();
      var moved = Path.Combine(_fixture.ScratchPath, "moved-clones");
      Directory.Move(ClonesPath, moved);
      Directory.CreateSymbolicLink(ClonesPath, moved);

      var removed = await cache.PruneAsync(() => [], null, Cancellation);

      Assert.Equal(0, removed);
      Assert.Single(Directory.GetDirectories(moved));
   }

   [Fact]
   public async Task Prune_removes_nothing_when_the_clones_to_keep_are_unknown()
   {
      var cache = NewCache();
      (await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, Cancellation)).Dispose();
      var clone = Assert.Single(_fixture.Clones());
      Directory.CreateDirectory($"{clone}.999999.tmp");

      var removed = await cache.PruneAsync(() => null, null, Cancellation);

      Assert.Equal(0, removed);
      Assert.Equal(2, Directory.GetDirectories(ClonesPath).Length);
   }

   [Fact]
   public async Task Prune_without_a_clone_cache_directory_removes_nothing() => Assert.Equal(0, await NewCache().PruneAsync(() => [], null, Cancellation));

   public void Dispose()
   {
      _fixture.Dispose();
      _other.Dispose();
   }

   private static void SkipOnWindows() => Assert.SkipWhen(OperatingSystem.IsWindows(), "The test creates symbolic links.");

   private static async Task WaitForFileAsync(string path)
   {
      using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
      timeout.CancelAfter(Wait);
      while (!File.Exists(path))
      {
         await Task.Delay(50, timeout.Token);
      }
   }

   private CommitId CommitOf(string branch) => CommitId.Parse(_fixture.Git(_fixture.OriginPath, "rev-parse", branch).Trim());

   // Returns git options that, for a clone, run it, then mark the start and wait for the go file before they return.
   private static GitOptions GitThatWaitsAfterClone(string started, string go) => GitDouble.Options(async run =>
   {
      var outcome = await run.GitAsync();
      if (run.Subcommand != "clone" || outcome is not ProcessOutcome.Exited { Code: 0 })
      {
         return outcome;
      }

      File.WriteAllText(started, "");
      return await run.HoldAsync(() => File.Exists(go)) ?? outcome;
   });

   private CloneCache NewCache(GitOptions? options = null) => _fixture.NewCache(options);
}
