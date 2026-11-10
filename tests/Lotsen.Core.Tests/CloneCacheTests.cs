using Lotsen.Git;
using Lotsen.Hosting;
using Lotsen.Pinning;
using Microsoft.Extensions.Logging;

namespace Lotsen.Tests;

/// <summary>Covers how the clone cache keeps one blob-less clone per repository in the data directory.</summary>
[Trait("Category", "Git")]
public sealed class CloneCacheTests : IDisposable
{
   private readonly GitFixture _fixture = new();

   [Fact]
   public async Task First_sync_creates_a_blob_less_clone_in_the_data_directory()
   {
      _fixture.Commit("Add a file", ("a.txt", "one\n"));

      await NewCache().OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, TestContext.Current.CancellationToken);

      var clone = Assert.Single(_fixture.Clones());
      Assert.StartsWith(_fixture.DataDirectory.Path, clone, StringComparison.Ordinal);
      Assert.Equal("true", _fixture.Git(clone, "config", "--get", "remote.origin.promisor").Trim());
      Assert.Equal("blob:none", _fixture.Git(clone, "config", "--get", "remote.origin.partialclonefilter").Trim());
      Assert.Contains(_fixture.Git(clone, "rev-list", "--objects", "--missing=print", "--all").Split('\n'), line => line.StartsWith('?'));
   }

   [Fact]
   public async Task Later_sync_fetches_into_the_existing_clone()
   {
      _fixture.Commit("Add a file", ("a.txt", "one\n"));
      var cache = NewCache();
      await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, TestContext.Current.CancellationToken);
      var clone = Assert.Single(_fixture.Clones());
      var marker = Path.Combine(clone, "lotsen-test-marker");
      File.WriteAllText(marker, "");
      var later = _fixture.Commit("Change the file", ("a.txt", "two\n"));

      var repository = await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, TestContext.Current.CancellationToken);

      Assert.Equal(clone, Assert.Single(_fixture.Clones()));
      Assert.True(File.Exists(marker));
      Assert.Equal("two\n", await repository.ReadFileAsync(later, "a.txt", TestContext.Current.CancellationToken));
   }

   /// <summary>The check for the commit must not download it on its own, or the clone would hold it under no ref.</summary>
   [Fact]
   public async Task Required_commit_that_no_branch_holds_is_fetched_by_its_id_and_kept_under_a_ref()
   {
      _fixture.Commit("Base", ("a.txt", "one\n"));
      _fixture.Checkout("feature", create: true);
      var orphaned = _fixture.Commit("On a branch that goes away", ("a.txt", "two\n"));
      _fixture.Checkout("main");
      _fixture.Git(_fixture.OriginPath, "branch", "--quiet", "-D", "feature");
      var cache = NewCache();
      await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, TestContext.Current.CancellationToken);

      var repository = await cache.OpenAsync(_fixture.Remote(), [orphaned], FetchPolicy.Sync, TestContext.Current.CancellationToken);

      Assert.Equal($"{orphaned} refs/lotsen/commits/{orphaned}", _fixture.Git(repository.Path, "for-each-ref", "--format=%(objectname) %(refname)", "refs/lotsen/").Trim());
      Assert.Equal("two\n", await repository.ReadFileAsync(orphaned, "a.txt", TestContext.Current.CancellationToken));
   }

   [Fact]
   public async Task Concurrent_syncs_of_one_repository_share_one_clone()
   {
      var commit = _fixture.Commit("Add a file", ("a.txt", "one\n"));
      var cache = NewCache();

      var repositories = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, TestContext.Current.CancellationToken)));

      var clone = Assert.Single(_fixture.Clones());
      Assert.Empty(Directory.GetFileSystemEntries(Path.GetDirectoryName(clone)!, "*.tmp"));
      foreach (var repository in repositories)
      {
         Assert.Equal("one\n", await repository.ReadFileAsync(commit, "a.txt", TestContext.Current.CancellationToken));
      }
   }

   /// <summary>The remote of one repository holds its first answer, so that repository's first sync waits while the other syncs are started.</summary>
   [Fact]
   public async Task Syncs_of_one_repository_wait_for_each_other_while_other_repositories_proceed()
   {
      _fixture.Commit("Add a file", ("a.txt", "one\n"));
      await using var held = new RecordingHttpServer(hold: true);
      var heldRemote = TestRemote.Of(held.RepositoryUrl("held"));
      var cache = NewCache();

      var first = cache.OpenAsync(heldRemote, [], FetchPolicy.Sync, TestContext.Current.CancellationToken);
      await held.FirstRequest.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
      var second = cache.OpenAsync(heldRemote, [], FetchPolicy.Sync, TestContext.Current.CancellationToken);
      await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, TestContext.Current.CancellationToken);
      await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

      Assert.Single(held.Requests);
      Assert.False(first.IsCompleted);
      Assert.False(second.IsCompleted);

      held.Release();
      await Assert.ThrowsAsync<GitException>(() => first);
      await Assert.ThrowsAsync<GitException>(() => second);
      Assert.True(held.Requests.Count > 1);
   }

   [Fact]
   public async Task Failed_clone_leaves_no_clone_behind()
   {
      var missing = TestRemote.Of(new Uri(Path.Combine(_fixture.ScratchPath, "missing")));

      await Assert.ThrowsAsync<GitException>(() => NewCache().OpenAsync(missing, [], FetchPolicy.Sync, TestContext.Current.CancellationToken));

      Assert.Empty(_fixture.Clones());
      Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(_fixture.DataDirectory.Path, "clones")));
   }

   /// <summary>A process that was killed during its clone left a temporary clone behind, under another process ID. Another repository's temporary clone sits beside it.</summary>
   [Fact]
   public async Task Clone_removes_the_temporary_clones_that_killed_processes_left_of_its_repository_only()
   {
      _fixture.Commit("Add a file", ("a.txt", "one\n"));
      var cache = NewCache();
      await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, TestContext.Current.CancellationToken);
      var clone = Assert.Single(_fixture.Clones());
      DirectoryTree.Delete(clone);
      var leftover = Directory.CreateDirectory($"{clone}.999999.tmp").FullName;
      File.WriteAllText(Path.Combine(leftover, "HEAD"), "partial");
      var otherRepository = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(clone)!, new string('0', 32) + ".999999.tmp")).FullName;

      await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, TestContext.Current.CancellationToken);

      Assert.False(Directory.Exists(leftover));
      Assert.True(Directory.Exists(otherRepository));
      Assert.True(Directory.Exists(clone));
   }

   [Fact]
   public async Task Failing_git_call_reports_the_reason_without_its_command_line()
   {
      var missing = TestRemote.Of(new Uri(Path.Combine(_fixture.ScratchPath, "missing")));

      var exception = await Assert.ThrowsAsync<GitException>(() => NewCache().OpenAsync(missing, [], FetchPolicy.Sync, TestContext.Current.CancellationToken));

      Assert.Contains("does not appear to be a git repository", exception.Message, StringComparison.Ordinal);
      Assert.DoesNotContain("--filter", exception.Message, StringComparison.Ordinal);
      Assert.DoesNotContain("clone --", exception.Message, StringComparison.Ordinal);
      Assert.DoesNotContain("fatal:", exception.Message, StringComparison.Ordinal);
   }

   [Fact]
   public async Task Git_call_that_outlasts_its_timeout_is_stopped_with_a_reason()
   {
      await using var held = new RecordingHttpServer(hold: true);
      var cache = NewCache(new GitOptions { TransferTimeout = TimeSpan.FromSeconds(1) });

      var exception = await Assert.ThrowsAsync<GitException>(() => cache.OpenAsync(TestRemote.Of(held.RepositoryUrl("slow")), [], FetchPolicy.Sync, TestContext.Current.CancellationToken));

      Assert.Contains("did not finish within", exception.Message, StringComparison.Ordinal);
      Assert.Empty(_fixture.Clones());
   }

   [Fact]
   public async Task Missing_git_executable_is_reported_as_such()
   {
      _fixture.Commit("Add a file", ("a.txt", "one\n"));
      var cache = NewCache(new GitOptions { Executable = Path.Combine(_fixture.ScratchPath, "no-such-git") });

      var exception = await Assert.ThrowsAsync<GitException>(() => cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, TestContext.Current.CancellationToken));

      Assert.Contains("git was not found", exception.Message, StringComparison.Ordinal);
   }

   /// <summary>The git executable exists but has no execute permission.</summary>
   [Fact]
   public async Task Git_that_cannot_be_started_is_reported_with_the_system_reason()
   {
      Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes do not apply on Windows.");
      _fixture.Commit("Add a file", ("a.txt", "one\n"));
      var executable = Path.Combine(_fixture.ScratchPath, "git-without-permission");
      File.WriteAllText(executable, "#!/bin/sh\n");
      var cache = NewCache(new GitOptions { Executable = executable });

      var exception = await Assert.ThrowsAsync<GitException>(() => cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, TestContext.Current.CancellationToken));

      Assert.Equal("Git could not be checked: git could not be started: Permission denied.", exception.Message);
   }

   [Fact]
   public async Task Open_of_a_clone_that_has_the_commits_runs_no_fetch()
   {
      var commit = _fixture.Commit("Add a file", ("a.txt", "one\n"));
      await NewCache().OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, TestContext.Current.CancellationToken);
      var git = new RecordingGit(_fixture.ScratchPath);

      var repository = await NewCache(git.Options).OpenAsync(_fixture.Remote(), [commit], FetchPolicy.WhenMissing, TestContext.Current.CancellationToken);

      Assert.Equal("one\n", await repository.ReadFileAsync(commit, "a.txt", TestContext.Current.CancellationToken));
      Assert.Empty(Runs(git, "fetch"));
   }

   [Fact]
   public async Task Open_for_commits_that_an_earlier_open_found_runs_no_git_lookup()
   {
      var commit = _fixture.Commit("Add a file", ("a.txt", "one\n"));
      var git = new RecordingGit(_fixture.ScratchPath);
      var cache = NewCache(git.Options);
      (await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, TestContext.Current.CancellationToken)).Dispose();
      (await cache.OpenAsync(_fixture.Remote(), [commit], FetchPolicy.WhenMissing, TestContext.Current.CancellationToken)).Dispose();
      var lookups = Runs(git, "rev-parse").Count;

      using var repository = await cache.OpenAsync(_fixture.Remote(), [commit], FetchPolicy.WhenMissing, TestContext.Current.CancellationToken);

      Assert.Equal(1, lookups);
      Assert.Equal(lookups, Runs(git, "rev-parse").Count);
   }

   /// <summary>The commit is on no branch, so the clone that replaces the pruned one lacks it until an open fetches it by its ID.</summary>
   [Fact]
   public async Task Open_after_a_prune_fetches_a_commit_that_only_the_pruned_clone_held()
   {
      _fixture.Commit("Base", ("a.txt", "one\n"));
      _fixture.Checkout("feature", create: true);
      var orphaned = _fixture.Commit("On a branch that goes away", ("a.txt", "two\n"));
      _fixture.Checkout("main");
      _fixture.Git(_fixture.OriginPath, "branch", "--quiet", "-D", "feature");
      var cache = NewCache();
      (await cache.OpenAsync(_fixture.Remote(), [orphaned], FetchPolicy.Sync, TestContext.Current.CancellationToken)).Dispose();
      await cache.PruneAsync(() => [], null, TestContext.Current.CancellationToken);
      (await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, TestContext.Current.CancellationToken)).Dispose();
      var clone = Assert.Single(_fixture.Clones());
      var refsBefore = LotsenRefs(clone);

      using var repository = await cache.OpenAsync(_fixture.Remote(), [orphaned], FetchPolicy.WhenMissing, TestContext.Current.CancellationToken);

      Assert.Empty(refsBefore);
      Assert.Equal($"{orphaned} refs/lotsen/commits/{orphaned}", LotsenRefs(clone));
   }

   [Fact]
   public async Task Open_of_a_clone_that_lacks_a_commit_fetches_it()
   {
      _fixture.Commit("Add a file", ("a.txt", "one\n"));
      var cache = NewCache();
      await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, TestContext.Current.CancellationToken);
      var later = _fixture.Commit("Change the file", ("a.txt", "two\n"));

      var repository = await cache.OpenAsync(_fixture.Remote(), [later], FetchPolicy.WhenMissing, TestContext.Current.CancellationToken);

      Assert.Equal("two\n", await repository.ReadFileAsync(later, "a.txt", TestContext.Current.CancellationToken));
   }

   // The first open takes the clone's lock before it yields, so the other three queue behind it while the clone is missing.
   [Fact]
   public async Task Opens_queued_behind_the_clone_that_brings_their_commits_run_no_fetch()
   {
      var commit = _fixture.Commit("Add a file", ("a.txt", "one\n"));
      var git = new RecordingGit(_fixture.ScratchPath);
      var cache = NewCache(git.Options);

      var repositories = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => cache.OpenAsync(_fixture.Remote(), [commit], FetchPolicy.WhenMissing, TestContext.Current.CancellationToken)));

      Assert.Single(Runs(git, "clone"));
      Assert.Empty(Runs(git, "fetch"));
      Assert.All(repositories, repository => Assert.Equal(repositories[0].Path, repository.Path));
   }

   [Theory]
   [InlineData("https://user:secret@dev.azure.com/contoso/shop/_git/web")]
   [InlineData("http://dev.azure.com/contoso/shop/_git/web")]
   [InlineData("ssh://dev.azure.com/contoso/shop/_git/web")]
   public async Task Remote_that_could_expose_a_credential_is_refused(string url)
   {
      await Assert.ThrowsAsync<UnsupportedRemoteException>(() => NewCache().OpenAsync(TestRemote.Of(new Uri(url)), [], FetchPolicy.Sync, TestContext.Current.CancellationToken));

      Assert.Empty(_fixture.Clones());
   }

   /// <summary>
   /// The remote's upload-pack sends the first part of the fetch's pack, then waits until the test lets it go, so the fetch holds the clone lock with a pack half written into the clone. The
   /// open and the reads run meanwhile, and the read of a file downloads its content into the same clone.
   /// </summary>
   [Fact]
   public async Task Query_runs_while_a_fetch_writes_a_pack_into_the_clone()
   {
      var first = _fixture.Commit("Add a file", ("a.txt", "one\n"));
      var cache = NewCache();
      (await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, TestContext.Current.CancellationToken)).Dispose();
      var clone = Assert.Single(_fixture.Clones());
      CommitId head = first;
      for (var i = 0; i < 10; i++)
      {
         head = _fixture.Commit($"Rewrite every file {i}", [.. Enumerable.Range(0, 1500).Select(file => ($"d{file / 10}/f{file % 10}.txt", (string?)$"{i} {file}\n"))]);
      }

      var started = Path.Combine(_fixture.ScratchPath, "upload-started");
      var go = Path.Combine(_fixture.ScratchPath, "upload-go");
      _fixture.Git(clone, "config", "remote.origin.uploadpack", UploadPackThatPauses(128 * 1024, started, go));

      var fetch = cache.OpenAsync(_fixture.Remote(), [head], FetchPolicy.Sync, TestContext.Current.CancellationToken);
      await WaitForFileAsync(started);
      var partial = Directory.GetFiles(Path.Combine(clone, "objects", "pack"), "tmp_pack_*");
      using (var repository = await cache.OpenAsync(_fixture.Remote(), [first], FetchPolicy.WhenMissing, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken))
      {
         Assert.NotNull(await repository.ListDirectoryAsync(first, null, TestContext.Current.CancellationToken));
         Assert.Equal(first, await repository.GetMergeBaseAsync(first, first, TestContext.Current.CancellationToken));
         Assert.Equal("one\n", await repository.ReadFileAsync(first, "a.txt", TestContext.Current.CancellationToken));
      }

      var fetchedDuringReads = fetch.IsCompleted;
      File.WriteAllText(go, "");
      using var fetched = await fetch.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

      Assert.NotEmpty(partial);
      Assert.False(fetchedDuringReads);
      Assert.Equal($"9 1499\n", await fetched.ReadFileAsync(head, "d149/f9.txt", TestContext.Current.CancellationToken));
   }

   /// <summary>
   /// The branch is in <c>packed-refs</c>, as a clone writes its first branches, so its deletion takes git's lock on that file and the branch's own lock, and writes a new <c>packed-refs</c>
   /// under the lock. The killed fetch leaves all three, and the test ages them past <see cref="CloneCache.StaleRefLockAge"/>.
   /// </summary>
   [Fact]
   public async Task Fetch_after_one_killed_while_it_deleted_a_branch_deletes_the_branch()
   {
      _fixture.Commit("Base", ("a.txt", "one\n"));
      _fixture.Checkout("feature", create: true);
      _fixture.Commit("Feature", ("b.txt", "feature\n"));
      _fixture.Checkout("main");
      var cache = NewCache();
      (await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, TestContext.Current.CancellationToken)).Dispose();
      var clone = Assert.Single(_fixture.Clones());
      _fixture.Git(_fixture.OriginPath, "branch", "--quiet", "-D", "feature");

      await KillFetchInItsRefUpdateAsync(cache, clone);
      var locksLeft = new[] { "packed-refs.lock", "packed-refs.new", Path.Combine("refs", "heads", "feature.lock") }.All(file => File.Exists(Path.Combine(clone, file)));
      AgeRefLocks(clone);
      (await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, TestContext.Current.CancellationToken)).Dispose();

      Assert.True(locksLeft, "The killed fetch left no lock on packed-refs and the branch, or no new packed-refs.");
      Assert.Empty(_fixture.Git(clone, "for-each-ref", "refs/heads/feature").Trim());
   }

   /// <summary>
   /// The clone's <c>HEAD</c> names the branch, so the branch's update takes git's lock on <c>HEAD</c> as well as the branch's own lock. The killed fetch leaves both, and the test ages them
   /// past <see cref="CloneCache.StaleRefLockAge"/>.
   /// </summary>
   [Fact]
   public async Task Fetch_after_one_killed_while_it_moved_the_default_branch_moves_the_branch()
   {
      _fixture.Commit("Base", ("a.txt", "one\n"));
      var cache = NewCache();
      (await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, TestContext.Current.CancellationToken)).Dispose();
      var clone = Assert.Single(_fixture.Clones());
      var later = _fixture.Commit("Change the file", ("a.txt", "two\n"));

      await KillFetchInItsRefUpdateAsync(cache, clone);
      var locksLeft = File.Exists(Path.Combine(clone, "HEAD.lock")) && File.Exists(Path.Combine(clone, "refs", "heads", "main.lock"));
      AgeRefLocks(clone);
      (await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, TestContext.Current.CancellationToken)).Dispose();

      Assert.True(locksLeft, "The killed fetch left no lock on HEAD and the branch.");
      Assert.Equal(later.Value, _fixture.Git(clone, "rev-parse", "refs/heads/main").Trim());
   }

   /// <summary>Locks younger than <see cref="CloneCache.StaleRefLockAge"/> could be those of a live git that this cache did not start, as one that outlived a crashed server.</summary>
   [Fact]
   public async Task Fetch_right_after_one_killed_while_it_deleted_a_branch_fails_with_gits_reason_and_leaves_the_locks()
   {
      _fixture.Commit("Base", ("a.txt", "one\n"));
      _fixture.Checkout("feature", create: true);
      _fixture.Commit("Feature", ("b.txt", "feature\n"));
      _fixture.Checkout("main");
      var cache = NewCache();
      (await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, TestContext.Current.CancellationToken)).Dispose();
      var clone = Assert.Single(_fixture.Clones());
      _fixture.Git(_fixture.OriginPath, "branch", "--quiet", "-D", "feature");
      await KillFetchInItsRefUpdateAsync(cache, clone);

      var exception = await Assert.ThrowsAsync<GitException>(() => cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, TestContext.Current.CancellationToken));

      Assert.Contains("File exists", exception.Message, StringComparison.Ordinal);
      Assert.True(File.Exists(Path.Combine(clone, "packed-refs.lock")));
      Assert.True(File.Exists(Path.Combine(clone, "refs", "heads", "feature.lock")));
   }

   /// <summary>The link points outside the clone, and the lock under it is older than <see cref="CloneCache.StaleRefLockAge"/>.</summary>
   [Fact]
   public async Task Fetch_leaves_a_lock_under_a_link_in_the_refs_directory()
   {
      Assert.SkipWhen(OperatingSystem.IsWindows(), "The test creates symbolic links.");
      _fixture.Commit("Base", ("a.txt", "one\n"));
      var cache = NewCache();
      (await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, TestContext.Current.CancellationToken)).Dispose();
      var clone = Assert.Single(_fixture.Clones());
      var outside = Directory.CreateDirectory(Path.Combine(_fixture.ScratchPath, "outside")).FullName;
      var outsideLock = Path.Combine(outside, "x.lock");
      File.WriteAllText(outsideLock, "");
      Directory.CreateSymbolicLink(Path.Combine(clone, "refs", "linked"), outside);
      AgeRefLocks(outside);

      (await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, TestContext.Current.CancellationToken)).Dispose();

      Assert.True(File.Exists(outsideLock), "The fetch deleted a lock outside the clone.");
   }

   /// <summary>The lock is gone by the time of the check, as when another git removed it after the cache listed it.</summary>
   [Fact]
   public void Missing_ref_lock_is_not_stale()
   {
      Assert.False(CloneCache.IsStaleRefLock(new FileInfo(Path.Combine(_fixture.ScratchPath, "gone.lock")), DateTimeOffset.UtcNow));
   }

   /// <summary>The directory under <c>refs/</c> has no permissions, and the root lock is older than <see cref="CloneCache.StaleRefLockAge"/>.</summary>
   [Fact]
   public async Task Fetch_with_an_unreadable_directory_under_refs_deletes_the_root_locks_and_warns()
   {
      if (OperatingSystem.IsWindows())
      {
         Assert.Skip("Unix file modes do not apply on Windows.");
         return;
      }

      _fixture.Commit("Base", ("a.txt", "one\n"));
      var logs = new RecordingLoggerProvider();
      var cache = _fixture.NewCache(logger: logs.CreateLogger<CloneCache>());
      (await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, TestContext.Current.CancellationToken)).Dispose();
      var clone = Assert.Single(_fixture.Clones());
      var sealedDirectory = Directory.CreateDirectory(Path.Combine(clone, "refs", "sealed")).FullName;
      File.WriteAllText(Path.Combine(clone, "packed-refs.lock"), "");
      AgeRefLocks(clone);
      File.SetUnixFileMode(sealedDirectory, UnixFileMode.None);

      try
      {
         await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, TestContext.Current.CancellationToken);
      }
      catch (GitException)
      {
         // Git may refuse the unreadable directory too. The test covers only what the cache did before the fetch.
      }
      finally
      {
         File.SetUnixFileMode(sealedDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
      }

      Assert.False(File.Exists(Path.Combine(clone, "packed-refs.lock")));
      Assert.Contains(logs.Entries, entry => entry.Level == LogLevel.Warning && entry.Line.Contains(sealedDirectory, StringComparison.Ordinal));
   }

   [Fact]
   public async Task Cache_holds_no_clone_state_once_every_use_has_ended()
   {
      _fixture.Commit("Add a file", ("a.txt", "one\n"));
      var cache = NewCache();
      var other = TestRemote.Of(new Uri(Path.Combine(_fixture.ScratchPath, "other")));

      var repositories = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, TestContext.Current.CancellationToken)));
      var holds = new[] { cache.Hold(_fixture.Remote()), cache.Hold(other) };
      await Assert.ThrowsAsync<GitException>(() => cache.OpenAsync(other, [], FetchPolicy.Sync, TestContext.Current.CancellationToken));
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, new CancellationToken(canceled: true)));
      var whileHeld = cache.HeldClones;
      foreach (var use in repositories.Concat<IDisposable>(holds))
      {
         use.Dispose();
      }

      Assert.Equal(2, whileHeld);
      Assert.Equal(0, cache.HeldClones);
   }

   public void Dispose() => _fixture.Dispose();

   private static async Task WaitForFileAsync(string path)
   {
      using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
      timeout.CancelAfter(TimeSpan.FromSeconds(30));
      while (!File.Exists(path))
      {
         await Task.Delay(50, timeout.Token);
      }
   }

   // Writes an upload-pack for the clone's remote whose first run sends the given number of bytes of its answer, marks that, and waits for the go file before it sends the rest. Later runs, as
   // for a file content's download, answer at once. A wait also ends once the fixture is gone, so a test that fails during the wait leaves no process behind.
   //
   // `dd` with one-byte blocks passes each byte on as it reads it. `head -c` from GNU coreutils 9.11 holds a short read until it has the whole count, so git's first exchange stalls and this test
   // times out.
   private string UploadPackThatPauses(int bytes, string started, string go)
   {
      var script = Path.Combine(_fixture.ScratchPath, "upload-pack.sh");
      var once = Path.Combine(_fixture.ScratchPath, "upload-paused");
      ShellScript.Write(script, $$"""
         #!/bin/sh
         if mkdir '{{once}}' 2>/dev/null; then
            git upload-pack "$@" | { dd bs=1 count={{bytes}} 2>/dev/null; touch '{{started}}'; while [ ! -f '{{go}}' ] && [ -d '{{_fixture.ScratchPath}}' ]; do sleep 0.05; done; cat; }
         else
            exec git upload-pack "$@"
         fi
         """);

      // Git runs the configured upload-pack through its shell, which reads a backslash in a Windows path as an escape.
      return script.Replace('\\', '/');
   }

   // Opens the clone with a reference-transaction hook that waits once git holds its ref locks, until the fixture is gone, then cancels the open, so git is killed holding the locks as a stop
   // or a timeout kills it. The hook is removed then.
   private async Task KillFetchInItsRefUpdateAsync(CloneCache cache, string clone)
   {
      var hook = Path.Combine(clone, "hooks", "reference-transaction");
      var started = Path.Combine(_fixture.ScratchPath, "ref-update-started");
      ShellScript.Write(hook, $"""
         #!/bin/sh
         if [ "$1" = prepared ]; then
            touch '{started}'
            while [ -d '{_fixture.ScratchPath}' ]; do sleep 0.05; done
         fi
         """);
      using var stop = new CancellationTokenSource();
      var killed = cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, stop.Token);
      await WaitForFileAsync(started);
      await stop.CancelAsync();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => killed.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
      File.Delete(hook);
   }

   // Sets the last write time of each ref lock under the directory to before the cache's bound, as a git killed that long ago leaves them.
   private static void AgeRefLocks(string directory)
   {
      var old = DateTime.UtcNow - CloneCache.StaleRefLockAge - TimeSpan.FromMinutes(1);
      var locks = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
         .Where(file => file.EndsWith(".lock", StringComparison.Ordinal) || file.EndsWith("packed-refs.new", StringComparison.Ordinal));
      foreach (var file in locks)
      {
         File.SetLastWriteTimeUtc(file, old);
      }
   }

   private string LotsenRefs(string clone) => _fixture.Git(clone, "for-each-ref", "--format=%(objectname) %(refname)", "refs/lotsen/").Trim();

   // The double's runs of this git subcommand, one argument list each.
   private static List<string> Runs(RecordingGit git, string subcommand) => [.. git.Arguments.Split('\n').Where(line => line.Split(' ').Contains(subcommand))];

   private CloneCache NewCache(GitOptions? options = null) => _fixture.NewCache(options);
}
