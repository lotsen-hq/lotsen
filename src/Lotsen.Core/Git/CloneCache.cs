using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Lotsen.Credentials;
using Lotsen.Hosting;
using Lotsen.Pinning;
using Lotsen.Platforms;

namespace Lotsen.Git;

/// <summary>
/// A git repository that lotsen clones from, and where its credential comes from.
/// </summary>
/// <param name="Url">The repository's URL: <c>https</c>, <c>file</c>, or <c>http</c> to a loopback address. It must not hold user information, because git would store that in the clone's
/// configuration.</param>
/// <param name="Credential">The credential that git presents when the remote asks for authentication, or null for none.</param>
internal sealed record GitRemote(Uri Url, IGitCredential? Credential);

/// <summary>The exception for a remote whose URL is not one that <see cref="GitRemote"/> allows.</summary>
internal sealed class UnsupportedRemoteException : ExternalFailureException
{
   /// <param name="message">Why lotsen does not clone from the URL.</param>
   public UnsupportedRemoteException(string message)
      : base(message, null)
   {
   }
}

/// <summary>
/// Keeps one blob-less partial clone per repository in <c>clones/</c> in the data directory.
/// </summary>
/// <remarks>
/// <para>
/// A clone holds every commit and tree, and only the file contents that git downloaded. Git downloads a file's content when a query first reads it, or in advance with
/// <see cref="GitRepository.DownloadChangedFilesAsync"/>. The cache keeps all its state in the instance, so one instance per data directory must be the only writer of its clones. The server's
/// claim lock makes it one per process, and tests share a cache by sharing the instance.
/// </para>
/// <para>
/// A clone use or maintenance holds a clone. A clone use lasts from before its open starts until it is disposed, and only a use runs git on the clone's commits. Maintenance holds the clone while
/// <see cref="MaintainAsync"/> runs git's maintenance on it. The cache decides who holds a clone under one lock, so the two exclude each other and a prune. A prune moves no clone that either
/// holds. Maintenance starts only on a clone that no use holds. A use that starts during maintenance runs no git until the maintenance ends.
/// </para>
/// <para>
/// A clone's opens take its clone lock for a clone or a fetch, so those run one at a time. Queries take no lock, as <see cref="GitRepository"/> says. The cache keeps a clone's state, with its
/// clone lock, only while a use or maintenance holds the clone, so the map of states stays bounded.
/// </para>
/// </remarks>
internal sealed class CloneCache
{
   private const string TemporarySuffix = ".tmp";

   private const string RemovedSuffix = ".removed";

   // The file in a clone that holds the time of its last maintenance, or of its clone.
   private const string MaintainedFile = "lotsen-maintained";

   // The ref locks that git creates outside refs/.
   private static readonly string[] RootRefLocks = ["HEAD.lock", "packed-refs.lock", "packed-refs.new"];

   // A link under refs/ can point outside the clone, so the search for ref locks follows none. A directory that the search cannot read fails it, so the cache logs that.
   private static readonly EnumerationOptions RefLockSearch = new() { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };

   private readonly DataDirectory _dataDirectory;
   private readonly ICloneDirectories _directories;
   private readonly GitRunner _git;
   private readonly TimeProvider _time;
   private readonly ILogger<CloneCache> _logger;

   // Guards _clones, _removals, _heldCommits and _endedDuringPrune. Nothing awaits or runs git while it holds the lock.
   private readonly Lock _lock = new();

   // The commits that git found in each clone, by the clone's path. Only a prune, a new clone and maintenance's gc can remove a commit from a clone, because no git run of the cache starts git's
   // own gc, so each of those forgets the clone's commits.
   private readonly Dictionary<string, HashSet<CommitId>> _heldCommits = new(StringComparer.Ordinal);

   // The state of each clone that a use or maintenance holds, by the clone's path. A prune moves a clone only when the clone has no entry.
   private readonly Dictionary<string, CloneState> _clones = new(StringComparer.Ordinal);

   // The number of times a prune moved each clone out of the cache, by the clone's path.
   private readonly Dictionary<string, int> _removals = new(StringComparer.Ordinal);

   // Prunes run one at a time, so no two delete the same removed clone. Maintenance runs one at a time, so it never gc's two clones at once.
   private readonly SemaphoreSlim _pruning = new(1, 1);
   private readonly SemaphoreSlim _maintaining = new(1, 1);

   // The paths of the clones whose last use ended while a prune runs, or null while none runs. PruneAsync keeps them, as its remarks say.
   private HashSet<string>? _endedDuringPrune;

   public CloneCache(DataDirectory dataDirectory, ICloneDirectories directories, GitRunner git, TimeProvider time, ILogger<CloneCache> logger)
   {
      _dataDirectory = dataDirectory;
      _directories = directories;
      _git = git;
      _time = time;
      _logger = logger;
   }

   /// <summary>How long a clone goes from its clone or its last maintenance until <see cref="MaintainAsync"/> maintains it again.</summary>
   public static TimeSpan MaintenanceInterval => TimeSpan.FromDays(1);

   /// <summary>How long ago git must have last written a ref lock before an open deletes the lock.</summary>
   /// <remarks>It relies on a live git's ref transaction, during which git holds the locks, ending sooner than this.</remarks>
   public static TimeSpan StaleRefLockAge => TimeSpan.FromMinutes(2);

   /// <summary>The number of clones whose state the cache holds: those that a use or maintenance holds.</summary>
   internal int HeldClones
   {
      get
      {
         lock (_lock)
         {
            return _clones.Count;
         }
      }
   }

   /// <summary>Returns how many times a prune removed the remote's clone since the cache was made, so a record about one clone of the remote can tell a later clone from it.</summary>
   public int RemovalsOf(Uri remoteUrl)
   {
      lock (_lock)
      {
         return _removals.GetValueOrDefault(ClonePathOf(remoteUrl));
      }
   }

   /// <summary>Returns a clone use of the remote's clone that holds the commits, after it clones the repository or fetches into the clone as the policy says.</summary>
   /// <remarks>
   /// <para>
   /// <see cref="FetchPolicy.Sync"/> fetches the remote's branches, or clones the repository when the cache holds no clone, then fetches each commit that the clone still lacks by its ID.
   /// A commit can be on no branch, as after its branch was deleted. The clone keeps each commit fetched by ID under <c>refs/lotsen/commits/</c>, so a later fetch that prunes branches cannot drop it.
   /// </para>
   /// <para>
   /// <see cref="FetchPolicy.WhenMissing"/> returns the clone without a fetch when it holds every commit, and syncs it otherwise. It checks the commits again under the clone lock, so an open that
   /// waited for another open fetches nothing when that open brought them. Git looks up only the commits that it has not found in the clone since the clone's last gc.
   /// </para>
   /// </remarks>
   /// <param name="remote">The tour's remote.</param>
   /// <param name="commits">The commits that the returned clone must hold.</param>
   /// <param name="policy">Whether the open fetches the remote's branches, or only the commits that the clone lacks.</param>
   /// <param name="cancellationToken">Stops the wait and git.</param>
   /// <returns>The clone, which counts as a use until it is disposed. After a sync it holds every branch of the remote as it was during the call.</returns>
   /// <exception cref="UnsupportedRemoteException">The remote's URL is not one that <see cref="GitRemote"/> allows.</exception>
   /// <exception cref="GitException">A lookup, the clone or a fetch failed, as when the remote does not have a commit. A failed clone leaves nothing in the cache.</exception>
   /// <exception cref="GitAuthException">The remote refused the remote's credential.</exception>
   /// <exception cref="AccessTokenUnavailableException">The remote asked for authentication, and the remote's credential has none.</exception>
   public async Task<GitRepository> OpenAsync(TourRemote remote, IReadOnlyCollection<CommitId> commits, FetchPolicy policy, CancellationToken cancellationToken)
   {
      CheckUrl(remote.Url);
      var path = ClonePathOf(remote.Url);
      var (use, maintained) = TakeUse(path);
      try
      {
         // Even a read waits for the whole gc, because git warns that a process that runs while gc runs can fail or corrupt the repository
         // (https://github.com/git/git/blob/v2.53.0/Documentation/git-gc.adoc#L153-L169).
         await maintained.WaitAsync(cancellationToken);
         var whenMissing = policy == FetchPolicy.WhenMissing;
         return whenMissing && await HasCommitsAsync(remote.Url, path, commits, cancellationToken)
            ? new GitRepository(path, remote.Remote, _git, use)
            : await SyncAsync(remote.Remote, path, commits, whenMissing, use, cancellationToken);
      }
      catch
      {
         use.Dispose();
         throw;
      }
   }

   /// <summary>Returns a clone use of the remote's clone that runs no git, so a prune and maintenance leave the clone until the use is disposed. The clone need not exist yet.</summary>
   /// <exception cref="UnsupportedRemoteException">The remote's URL is not one that <see cref="GitRemote"/> allows.</exception>
   public IDisposable Hold(TourRemote remote)
   {
      CheckUrl(remote.Url);
      return TakeUse(ClonePathOf(remote.Url)).Use;
   }

   /// <summary>Removes each clone that no kept remote URL names and that nothing holds, and each temporary clone that a clone cut short left, with no clone in progress for it.</summary>
   /// <remarks>
   /// <para>
   /// It moves each clone out of the cache under the lock that every new use takes, so an open that starts during the prune makes a new clone. A failed move or delete is logged and leaves the
   /// clone where it is, or for the next prune.
   /// </para>
   /// <para>
   /// It also keeps each clone whose last use ends while the prune runs, because <paramref name="keep"/> may have read the URLs before the result that refers to that clone was stored. So a clone
   /// stays when its user stores what refers to it before it ends its use.
   /// </para>
   /// <para>
   /// It acts only on the directories that <see cref="ICloneDirectories.List"/> returns, so a link in <c>clones/</c> stays, and a <c>clones/</c> that is a link prunes nothing.
   /// </para>
   /// </remarks>
   /// <param name="keep">Returns the remote URLs whose clones stay, or null to prune nothing.</param>
   /// <param name="only">The remote URLs whose clones and temporary clones the prune may remove, or null for every one.</param>
   /// <param name="cancellationToken">
   /// Stops the prune before its next clone, and never halfway through one. A later prune that covers the clones that this one moved out of the cache by then deletes their files.
   /// </param>
   /// <returns>The number of clones and temporary clones removed, or 0 when the prune was canceled before it started.</returns>
   public async Task<int> PruneAsync(Func<IReadOnlyCollection<Uri>?> keep, IReadOnlyCollection<Uri>? only, CancellationToken cancellationToken)
   {
      try
      {
         await _pruning.WaitAsync(cancellationToken);
      }
      catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
      {
         return 0;
      }

      try
      {
         var directories = _directories.List();
         if (directories is null)
         {
            _logger.LogWarning("The clone directory {Path} is a link, so no clone was pruned.", _dataDirectory.ClonesPath);
            return 0;
         }

         lock (_lock)
         {
            _endedDuringPrune = new HashSet<string>(StringComparer.Ordinal);
         }

         try
         {
            return keep() is { } kept ? Remove(directories, kept, only, cancellationToken) : 0;
         }
         finally
         {
            lock (_lock)
            {
               _endedDuringPrune = null;
            }
         }
      }
      finally
      {
         _pruning.Release();
      }
   }

   /// <summary>Runs git's maintenance on each clone that is due and that no use holds, one clone at a time.</summary>
   /// <remarks>
   /// <para>
   /// A clone is due once <see cref="MaintenanceInterval"/> has passed since its clone or its last maintenance. A clone that a use holds waits for a later run. A failed maintenance is logged, and
   /// the clone waits the interval too.
   /// </para>
   /// <para>
   /// The clones' git runs never start git's automatic maintenance, as <see cref="GitRunner"/> configures them, so this is the only maintenance. A call while another call runs does nothing,
   /// because that call covers every due clone.
   /// </para>
   /// </remarks>
   /// <param name="cancellationToken">Stops git, and the run before its next clone.</param>
   /// <exception cref="OperationCanceledException">The token was canceled.</exception>
   public async Task MaintainAsync(CancellationToken cancellationToken)
   {
      if (!await _maintaining.WaitAsync(0, cancellationToken))
      {
         return;
      }

      try
      {
         foreach (var directory in _directories.List() ?? [])
         {
            cancellationToken.ThrowIfCancellationRequested();

            // A use names its clone by this path, so maintenance takes the clone under the same key.
            var clonePath = Path.Combine(_dataDirectory.ClonesPath, directory.CloneName);
            if (directory.Kind == CloneDirectoryKind.Clone && IsDue(clonePath) && TryStartMaintenance(clonePath) is { } maintenance)
            {
               try
               {
                  await RunMaintenanceAsync(clonePath, cancellationToken);
               }
               finally
               {
                  EndMaintenance(clonePath, maintenance);
               }
            }
         }
      }
      finally
      {
         _maintaining.Release();
      }
   }

   // Call it while holding _pruning, with _endedDuringPrune set.
   private int Remove(IReadOnlyList<CloneDirectory> directories, IReadOnlyCollection<Uri> keep, IReadOnlyCollection<Uri>? only, CancellationToken cancellationToken)
   {
      var kept = keep.Select(CloneNameOf).ToHashSet(StringComparer.Ordinal);
      var allowed = only?.Select(CloneNameOf).ToHashSet(StringComparer.Ordinal);
      var removing = new List<CloneDirectory>();
      var removed = 0;
      foreach (var directory in directories)
      {
         if (cancellationToken.IsCancellationRequested)
         {
            break;
         }

         if (allowed?.Contains(directory.CloneName) == false)
         {
            continue;
         }

         if (directory.Kind == CloneDirectoryKind.Removed)
         {
            removing.Add(directory);
         }
         else if ((directory.Kind == CloneDirectoryKind.Temporary || !kept.Contains(directory.CloneName)) && TryTakeOut(directory) is { } moved)
         {
            removing.Add(moved);
            removed++;
         }
      }

      foreach (var directory in removing)
      {
         if (cancellationToken.IsCancellationRequested)
         {
            break;
         }

         try
         {
            _directories.Remove(directory);
         }
         catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
         {
            _logger.LogWarning("The removed clone {Path} could not be deleted, so the next prune deletes it: {Reason}", directory.Path, exception.Message);
         }
      }

      if (removed > 0)
      {
         _logger.LogInformation("Pruned {Count} clones.", removed);
      }

      return removed;
   }

   // Moves a clone or temporary clone to a removed name when nothing holds its clone, and returns the moved directory, or null when it stays.
   private CloneDirectory? TryTakeOut(CloneDirectory directory)
   {
      var clonePath = Path.Combine(_dataDirectory.ClonesPath, directory.CloneName);
      lock (_lock)
      {
         if (_clones.ContainsKey(clonePath) || _endedDuringPrune?.Contains(clonePath) == true)
         {
            return null;
         }

         var removed = $"{clonePath}.{Guid.NewGuid():N}{RemovedSuffix}";
         try
         {
            DirectoryTree.Move(directory.Path, removed);
            _removals[clonePath] = _removals.GetValueOrDefault(clonePath) + 1;
            if (directory.Kind == CloneDirectoryKind.Clone)
            {
               _heldCommits.Remove(clonePath);
            }

            return directory with { Path = removed, Kind = CloneDirectoryKind.Removed };
         }
         catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
         {
            _logger.LogWarning("The clone {Path} could not be pruned: {Reason}", directory.Path, exception.Message);
            return null;
         }
      }
   }

   // Returns the new use with the maintenance that the use waits for before it runs git, which is complete when none runs.
   private (CloneUse Use, Task Maintained) TakeUse(string clonePath)
   {
      lock (_lock)
      {
         if (!_clones.TryGetValue(clonePath, out var state))
         {
            state = new CloneState();
            _clones.Add(clonePath, state);
         }

         state.Uses++;
         return (new CloneUse(this, clonePath, state), state.Maintenance?.Task ?? Task.CompletedTask);
      }
   }

   private void EndUse(string clonePath, CloneState state)
   {
      lock (_lock)
      {
         if (--state.Uses == 0)
         {
            _endedDuringPrune?.Add(clonePath);
            if (state.Maintenance is null)
            {
               _clones.Remove(clonePath);
            }
         }
      }
   }

   // Returns the maintenance that now holds the clone, or null when a use holds it or a prune moved it.
   private TaskCompletionSource? TryStartMaintenance(string clonePath)
   {
      lock (_lock)
      {
         if (_clones.ContainsKey(clonePath) || !Directory.Exists(clonePath))
         {
            return null;
         }

         var state = new CloneState { Maintenance = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
         _clones.Add(clonePath, state);
         _heldCommits.Remove(clonePath);
         return state.Maintenance;
      }
   }

   private void EndMaintenance(string clonePath, TaskCompletionSource maintenance)
   {
      lock (_lock)
      {
         var state = _clones[clonePath];
         state.Maintenance = null;
         if (state.Uses == 0)
         {
            _clones.Remove(clonePath);
         }
      }

      maintenance.SetResult();
   }

   // Call it while the maintenance holds the clone.
   private async Task RunMaintenanceAsync(string clonePath, CancellationToken cancellationToken)
   {
      var started = Stopwatch.GetTimestamp();
      try
      {
         // Git skips its maintenance and exits 0 while objects/maintenance.lock exists, and a run killed by a stop or its timeout leaves that file
         // (https://github.com/git/git/blob/v2.53.0/builtin/gc.c#L1800-L1814). A git gc that outlived its server still refuses a second repack by its gc.pid
         // (https://github.com/git/git/blob/v2.53.0/builtin/gc.c#L1006-L1013).
         File.Delete(Path.Combine(clonePath, "objects", "maintenance.lock"));

         // Maintenance never needs a file content that the clone lacks, so it downloads none. It gets the transfer timeout, so the gc of a large clone can finish, and an open of the clone
         // can wait that long.
         await _git.RunAsync(
            new GitCall($"Git could not maintain the clone {clonePath}", ["maintenance", "run", "--task=gc", "--quiet"], _git.Options.TransferTimeout)
            {
               GitDirectory = clonePath,
               WorkingDirectory = clonePath,
               LazyFetch = false,
            },
            cancellationToken);
         _logger.LogInformation("Maintained the clone {Path} in {Milliseconds} ms.", clonePath, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
      }
      catch (GitException exception)
      {
         _logger.LogWarning("{Reason}", exception.Message);
      }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
      {
         _logger.LogWarning("Git's maintenance lock in the clone {Path} could not be deleted, so the clone was not maintained: {Reason}", clonePath, exception.Message);
      }

      StoreMaintained(clonePath);
   }

   private bool IsDue(string clonePath)
   {
      try
      {
         var text = File.ReadAllText(Path.Combine(clonePath, MaintainedFile));
         return !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var maintained) || _time.GetUtcNow() - maintained >= MaintenanceInterval;
      }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
      {
         return true;
      }
   }

   private void StoreMaintained(string clonePath)
   {
      try
      {
         File.WriteAllText(Path.Combine(clonePath, MaintainedFile), _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
      }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
      {
         _logger.LogWarning("The time of the clone's maintenance could not be stored in {Path}, so the next run maintains it again: {Reason}", clonePath, exception.Message);
      }
   }

   // The caller holds the use, which the returned repository then holds.
   private async Task<GitRepository> SyncAsync(GitRemote remote, string path, IReadOnlyCollection<CommitId> requiredCommits, bool skipWhenComplete, CloneUse use, CancellationToken cancellationToken)
   {
      var gate = use.Gate;
      await gate.WaitAsync(cancellationToken);
      try
      {
         if (skipWhenComplete && await HasCommitsAsync(remote.Url, path, requiredCommits, cancellationToken))
         {
            return new GitRepository(path, remote, _git, use);
         }

         var started = Stopwatch.GetTimestamp();
         var cloned = !Directory.Exists(path);
         try
         {
            if (cloned)
            {
               ForgetCommits(path);
               await CloneAsync(remote, path, cancellationToken);
            }
            else
            {
               // The clone lock and this open's clone use keep every other git of this cache from writing the clone's refs. So a ref lock here is a killed git's, or a live git's from outside
               // the cache. DeleteRefLocks keeps each lock younger than StaleRefLockAge, whether a live git or a git killed within that time left it.
               DeleteRefLocks(path);
               await FetchAsync(remote, path, cancellationToken);
            }

            var missing = new List<CommitId>();
            foreach (var commit in requiredCommits.Distinct())
            {
               if (!await HasCommitAsync(remote.Url, path, commit, cancellationToken))
               {
                  missing.Add(commit);
               }
            }

            if (missing.Count > 0)
            {
               await FetchCommitsAsync(remote, path, missing, cancellationToken);
            }

            RememberCommits(path, requiredCommits);
         }
         catch (GitException exception)
         {
            _logger.LogWarning("{Reason}", exception.Message);
            throw;
         }

         _logger.LogInformation("{Action} {Remote} in {Milliseconds} ms.", cloned ? "Cloned" : "Fetched", remote.Url, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
         return new GitRepository(path, remote, _git, use);
      }
      finally
      {
         gate.Release();
      }
   }

   // A URL with user information would put that credential into the clone's configuration, and plain HTTP would send the remote's credential readable to the network.
   private static void CheckUrl(Uri url)
   {
      if (!url.IsAbsoluteUri || url.UserInfo.Length > 0)
      {
         throw new UnsupportedRemoteException("A remote URL must be absolute and must not hold user information.");
      }

      var allowed = url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeFile || (url.Scheme == Uri.UriSchemeHttp && url.IsLoopback);
      if (!allowed)
      {
         throw new UnsupportedRemoteException($"lotsen cannot clone the repository from {url.AbsoluteUri}, because it clones only over https or from a local path.");
      }
   }

   // The URL's hash names the clone, so any URL maps to one short, valid directory name.
   private static string CloneNameOf(Uri url) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url.AbsoluteUri)))[..32];

   private string ClonePathOf(Uri url) => Path.Combine(_dataDirectory.ClonesPath, CloneNameOf(url));

   private async Task CloneAsync(GitRemote remote, string path, CancellationToken cancellationToken)
   {
      var url = remote.Url;
      _dataDirectory.EnsureExists();
      UserOnlyStorage.CreateDirectory(_dataDirectory.ClonesPath);

      // The clone is built under a temporary name and renamed when complete, so a clone that fails or is cut short never looks like a clone to a later sync. A process killed during its clone leaves
      // its temporary clone, and only this process writes the data directory, so every temporary clone of this repository is a leftover.
      var temporaryPath = $"{path}.{Environment.ProcessId}{TemporarySuffix}";
      foreach (var leftover in Directory.EnumerateDirectories(_dataDirectory.ClonesPath, $"{Path.GetFileName(path)}.*{TemporarySuffix}"))
      {
         DirectoryTree.Delete(leftover);
      }

      try
      {
         await _git.RunAsync(
            new GitCall(
               $"Git could not clone {url}",
               ["clone", "--bare", "--filter=blob:none", "--no-tags", "--quiet", "--", url.AbsoluteUri, temporaryPath],
               _git.Options.TransferTimeout)
            {
               WorkingDirectory = _dataDirectory.ClonesPath,
               Remote = remote,
            },
            cancellationToken);

         // A new clone holds one pack, so it is not due for maintenance.
         StoreMaintained(temporaryPath);
         DirectoryTree.Move(temporaryPath, path);
      }
      catch
      {
         DirectoryTree.Delete(temporaryPath);
         throw;
      }
   }

   // Deletes the clone's ref locks that git last wrote more than StaleRefLockAge ago, because a killed git leaves them and each one then fails every later update of its refs
   // (https://github.com/git/git/blob/v2.53.0/refs/packed-backend.c#L1374-L1383). The CloneCacheTests.Fetch_after_one_killed_* tests pin which files a killed fetch leaves. A ref name never ends
   // in .lock (https://git-scm.com/docs/git-check-ref-format), so each such file under refs/ is a lock.
   private void DeleteRefLocks(string clonePath)
   {
      var staleBefore = _time.GetUtcNow() - StaleRefLockAge;
      var (deleted, kept) = (0, 0);
      try
      {
         // The root locks come first, so a directory under refs/ that cannot be read leaves them deleted.
         foreach (var file in RootRefLocks.Select(name => Path.Combine(clonePath, name)).Concat(Directory.EnumerateFiles(Path.Combine(clonePath, "refs"), "*.lock", RefLockSearch)))
         {
            var info = new FileInfo(file);
            if (IsStaleRefLock(info, staleBefore))
            {
               info.Delete();
               deleted++;
            }
            else if (info.Exists)
            {
               kept++;
            }
         }
      }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
      {
         _logger.LogWarning("The ref locks in the clone {Path} could not all be deleted, so a fetch that updates their refs fails: {Reason}", clonePath, exception.Message);
      }

      if (deleted > 0)
      {
         _logger.LogInformation("Deleted {Count} ref locks that git wrote more than {Age} ago in the clone {Path}.", deleted, StaleRefLockAge, clonePath);
      }

      if (kept > 0)
      {
         _logger.LogInformation(
            "Kept {Count} ref locks in the clone {Path} that git wrote less than {Age} ago, or later than now, so a fetch that updates their refs fails until they are older.",
            kept,
            clonePath,
            StaleRefLockAge);
      }
   }

   /// <summary>Returns whether the file is a ref lock that <see cref="DeleteRefLocks"/> deletes: one that exists and that git last wrote before the time.</summary>
   internal static bool IsStaleRefLock(FileInfo file, DateTimeOffset staleBefore) => file.Exists && new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero) < staleBefore;

   private Task FetchAsync(GitRemote remote, string path, CancellationToken cancellationToken) =>
      _git.RunAsync(
         new GitCall(
            $"Git could not fetch from {remote.Url}",
            ["fetch", "--prune", "--no-tags", "--quiet", "origin", "+refs/heads/*:refs/heads/*"],
            _git.Options.TransferTimeout)
         {
            GitDirectory = path,
            WorkingDirectory = path,
            Remote = remote,
         },
         cancellationToken);

   // Returns whether a clone exists at the path and holds every commit. Git looks up only the commits that it has not found in the clone before.
   private async Task<bool> HasCommitsAsync(Uri url, string path, IReadOnlyCollection<CommitId> commits, CancellationToken cancellationToken)
   {
      if (!Directory.Exists(path))
      {
         return false;
      }

      foreach (var commit in UnknownCommits(path, commits))
      {
         if (!await HasCommitAsync(url, path, commit, cancellationToken))
         {
            return false;
         }

         RememberCommits(path, [commit]);
      }

      return true;
   }

   private List<CommitId> UnknownCommits(string clonePath, IReadOnlyCollection<CommitId> commits)
   {
      lock (_lock)
      {
         var held = _heldCommits.GetValueOrDefault(clonePath);
         return [.. commits.Distinct().Where(commit => held?.Contains(commit) != true)];
      }
   }

   private void RememberCommits(string clonePath, IEnumerable<CommitId> commits)
   {
      lock (_lock)
      {
         if (!_heldCommits.TryGetValue(clonePath, out var held))
         {
            held = [];
            _heldCommits.Add(clonePath, held);
         }

         held.UnionWith(commits);
      }
   }

   private void ForgetCommits(string clonePath)
   {
      lock (_lock)
      {
         _heldCommits.Remove(clonePath);
      }
   }

   private async Task<bool> HasCommitAsync(Uri url, string path, CommitId commit, CancellationToken cancellationToken)
   {
      var output = await _git.RunAsync(
         new GitCall(
            $"Git could not look up commit {commit} in the clone of {url}",
            ["rev-parse", "--verify", "--quiet", "--end-of-options", $"{commit.Value}^{{commit}}"],
            _git.Options.QueryTimeout)
         {
            GitDirectory = path,
            WorkingDirectory = path,
            SuccessExitCodes = [0, 1],
            LazyFetch = false,
         },
         cancellationToken);
      return output.ExitCode == 0;
   }

   private Task FetchCommitsAsync(GitRemote remote, string path, IReadOnlyList<CommitId> commits, CancellationToken cancellationToken) =>
      _git.RunAsync(
         new GitCall(
            $"Git could not fetch {(commits.Count == 1 ? "commit" : "commits")} {string.Join(", ", commits)} from {remote.Url}",
            [
               "fetch", "--no-tags", "--quiet", "origin",
               .. commits.Select(commit => $"+{commit.Value}:refs/lotsen/commits/{commit.Value}"),
            ],
            _git.Options.TransferTimeout)
         {
            GitDirectory = path,
            WorkingDirectory = path,
            Remote = remote,
         },
         cancellationToken);

   // The state of a clone that a use or maintenance holds. The cache's lock guards its fields.
   private sealed class CloneState
   {
      public int Uses { get; set; }

      // The lock that a clone's opens take for a clone or a fetch. Each open holds a use, so the lock lives as long as its state.
      public SemaphoreSlim Gate { get; } = new(1, 1);

      // The maintenance that holds the clone, or null for none.
      public TaskCompletionSource? Maintenance { get; set; }
   }

   // One use of a clone, which a GitRepository or a hold holds. Disposing it again does nothing.
   private sealed class CloneUse : IDisposable
   {
      private readonly CloneCache _cache;
      private readonly string _clonePath;
      private readonly CloneState _state;
      private int _released;

      public CloneUse(CloneCache cache, string clonePath, CloneState state)
      {
         _cache = cache;
         _clonePath = clonePath;
         _state = state;
      }

      public SemaphoreSlim Gate => _state.Gate;

      public void Dispose()
      {
         if (Interlocked.Exchange(ref _released, 1) == 0)
         {
            _cache.EndUse(_clonePath, _state);
         }
      }
   }
}
