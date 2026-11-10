using Lotsen.Git;
using Lotsen.Hosting;
using Lotsen.Platforms;

namespace Lotsen.Pinning;

/// <summary>Reads git facts from the clone cache's clones, and keeps the facts it read in a memo that a restart empties.</summary>
/// <remarks>
/// A fact never changes once read, so the memo never invalidates one. It evicts the least recently used facts once their estimated size passes its budget, and a pass that needs an evicted fact
/// reads it again.
/// </remarks>
internal sealed class CloneGitFacts : IGitFacts
{
   private readonly CloneCache _clones;
   private readonly ILogger<CloneGitFacts> _logger;
   private readonly FactMemo _memo;
   // The pins whose changed files a scope downloaded, by remote, each with the remote's removals from the clone cache at the download, so a later clone of the remote has no record.
   private readonly Dictionary<(Uri Remote, CommitId MergeBase, CommitId Head), int> _downloaded = [];

   /// <param name="size">The memo's budget, as <see cref="FactMemo"/> estimates sizes.</param>
   public CloneGitFacts(CloneCache clones, ILogger<CloneGitFacts> logger, FactCacheSize size)
      : this(clones, logger, size.Bytes)
   {
      logger.LogInformation("The git fact cache keeps up to {Mebibytes} MiB.", size.Mebibytes);
   }

   /// <param name="budget">The memo's budget in bytes.</param>
   internal CloneGitFacts(CloneCache clones, ILogger<CloneGitFacts> logger, long budget)
   {
      _clones = clones;
      _logger = logger;
      _memo = new FactMemo(budget);
   }

   public FactSet Known => _memo;

   public IFactScope Open(TourRemote remote, IReadOnlyCollection<CommitId> commits, FetchPolicy policy) => new Scope(this, remote, commits, policy, _clones.Hold(remote));

   public bool Downloaded(Uri remote, CommitId mergeBase, CommitId head)
   {
      lock (_downloaded)
      {
         return _downloaded.TryGetValue((remote, mergeBase, head), out var removals) && removals == _clones.RemovalsOf(remote);
      }
   }

   private sealed class Scope : IFactScope
   {
      private readonly CloneGitFacts _owner;
      private readonly TourRemote _remote;
      private readonly IReadOnlyCollection<CommitId> _commits;
      private readonly FetchPolicy _policy;
      private readonly IDisposable _hold;
      private readonly Dictionary<FactKey, object?> _read = [];
      private readonly Dictionary<(CommitId MergeBase, CommitId Head), PinDiffReader> _diffs = [];
      private readonly HashSet<(CommitId MergeBase, CommitId Head)> _downloaded = [];
      private readonly FactSet _known;
      private GitRepository? _repository;

      public Scope(CloneGitFacts owner, TourRemote remote, IReadOnlyCollection<CommitId> commits, FetchPolicy policy, IDisposable hold)
      {
         _owner = owner;
         _remote = remote;
         _commits = commits;
         _policy = policy;
         _hold = hold;
         _known = new ReadFacts(this).With(owner._memo);
      }

      public FactSet Known => _known;

      // A read of facts that the scope or the memo holds already runs no git, and does not open the clone.
      public async Task ReadAsync(IReadOnlyCollection<FactKey> keys, CancellationToken cancellationToken)
      {
         foreach (var key in keys)
         {
            if (!TryKeep(key, out _))
            {
               Add(key, await ReadAsync(await RepositoryAsync(cancellationToken), key, cancellationToken));
            }
         }
      }

      public async Task OpenCloneAsync(CancellationToken cancellationToken) => await RepositoryAsync(cancellationToken);

      public async Task WarmAsync(CommitId mergeBase, CommitId head, CancellationToken cancellationToken)
      {
         if (!_downloaded.Contains((mergeBase, head)))
         {
            await DownloadOnceAsync(await RepositoryAsync(cancellationToken), mergeBase, head, cancellationToken);
         }
      }

      public ValueTask DisposeAsync()
      {
         _repository?.Dispose();
         _hold.Dispose();
         return ValueTask.CompletedTask;
      }

      private async Task<GitRepository> RepositoryAsync(CancellationToken cancellationToken) =>
         _repository ??= await _owner._clones.OpenAsync(_remote, _commits, _policy, cancellationToken);

      private void Add(FactKey key, object? value)
      {
         lock (_read)
         {
            _read[key] = value;
         }

         _owner._memo.Add(key, value);
      }

      // Returns whether the scope or the memo holds the fact. The scope keeps a fact that it found in the memo, so an eviction cannot make a later pass of FactRounds ask for it again.
      private bool TryKeep(FactKey key, out object? value)
      {
         lock (_read)
         {
            if (_read.TryGetValue(key, out value))
            {
               return true;
            }
         }

         if (!_owner._memo.TryFind(key, out value))
         {
            return false;
         }

         lock (_read)
         {
            _read[key] = value;
         }

         return true;
      }

      private async Task<object?> ReadAsync(GitRepository repository, FactKey key, CancellationToken cancellationToken)
      {
         switch (key)
         {
            case FactKey.MergeBaseOf(var source, var target):
               return await repository.GetMergeBaseAsync(source, target, cancellationToken);
            case FactKey.ChangedPaths(var from, var to):
               return await repository.GetChangedPathsAsync(from, to, cancellationToken);
            case FactKey.Blob(var commit, var path):
               return await repository.FindFileAsync(commit, path, cancellationToken);
            case FactKey.Lines(var commit, var path):
               var file = await BlobAsync(repository, commit, path, cancellationToken);
               return ReadableFile.Is(file) ? new FileLines(await repository.ReadTextAsync(file, cancellationToken)) : null;
            case FactKey.Hunks(var from, var path, var to, var newPath):
               var oldFile = await BlobAsync(repository, from, path, cancellationToken);
               var newFile = await BlobAsync(repository, to, newPath, cancellationToken);
               return ReadableFile.Is(oldFile) && ReadableFile.Is(newFile) ? await repository.DiffLinesAsync(oldFile, newFile, cancellationToken) : null;
            case FactKey.PinDiff(var mergeBase, var head, var side, var path):
               if (!_diffs.TryGetValue((mergeBase, head), out var diffs))
               {
                  await DownloadAsync(repository, mergeBase, head, cancellationToken);
                  diffs = new PinDiffReader(repository, mergeBase, head);
                  _diffs[(mergeBase, head)] = diffs;
               }

               return await diffs.ReadAsync(side, path, cancellationToken);
            default:
               throw new InvalidOperationException($"No git read answers a fact of the kind {key.GetType().Name}.");
         }
      }

      // The listing would download the changed files' contents within the query timeout, so the download runs first, within the transfer timeout. A failed download leaves that to the listing.
      private async Task DownloadAsync(GitRepository repository, CommitId mergeBase, CommitId head, CancellationToken cancellationToken)
      {
         if (_downloaded.Contains((mergeBase, head)))
         {
            return;
         }

         try
         {
            await DownloadOnceAsync(repository, mergeBase, head, cancellationToken);
         }
         catch (ExternalFailureException failure)
         {
            _owner._logger.LogWarning(
               "The changed files between {MergeBase} and {Head} could not be downloaded, so listing them downloads them. {Reason}", mergeBase.Value, head.Value, failure.Reason);
         }
      }

      // Records the download in the scope and in its owner, so neither downloads the pin again.
      private async Task DownloadOnceAsync(GitRepository repository, CommitId mergeBase, CommitId head, CancellationToken cancellationToken)
      {
         await repository.DownloadChangedFilesAsync(mergeBase, head, cancellationToken);
         _downloaded.Add((mergeBase, head));
         lock (_owner._downloaded)
         {
            _owner._downloaded[(_remote.Url, mergeBase, head)] = _owner._clones.RemovalsOf(_remote.Url);
         }
      }

      // Returns the blob from the facts, or finds it and adds it to them.
      private async Task<FileBlob?> BlobAsync(GitRepository repository, CommitId commit, string path, CancellationToken cancellationToken)
      {
         var key = new FactKey.Blob(commit, path);
         if (TryKeep(key, out var known))
         {
            return (FileBlob?)known;
         }

         var found = await repository.FindFileAsync(commit, path, cancellationToken);
         Add(key, found);
         return found;
      }

      private sealed class ReadFacts : FactSet
      {
         private readonly Scope _scope;

         public ReadFacts(Scope scope)
         {
            _scope = scope;
         }

         internal override bool TryFind(FactKey key, out object? value)
         {
            lock (_scope._read)
            {
               return _scope._read.TryGetValue(key, out value);
            }
         }
      }
   }
}

/// <summary>The facts read so far, up to a budget of their estimated sizes, with the least recently used evicted first. It is safe for concurrent use.</summary>
internal sealed class FactMemo : FactSet
{
   private readonly Lock _lock = new();
   private readonly long _budget;
   private readonly Dictionary<FactKey, LinkedListNode<(FactKey Key, object? Value, long Size)>> _entries = [];
   private readonly LinkedList<(FactKey Key, object? Value, long Size)> _order = new();
   private long _size;

   /// <param name="budget">The largest sum of the facts' estimated sizes, in bytes.</param>
   public FactMemo(long budget)
   {
      _budget = budget;
   }

   /// <summary>The number of facts held.</summary>
   public int Count
   {
      get
      {
         lock (_lock)
         {
            return _entries.Count;
         }
      }
   }

   /// <summary>Adds the fact as the most recently used, then evicts the least recently used facts while the sum of the sizes passes the budget. A fact larger than the budget is not kept.</summary>
   public void Add(FactKey key, object? value)
   {
      var size = SizeOf(value);
      lock (_lock)
      {
         if (_entries.Remove(key, out var old))
         {
            _order.Remove(old);
            _size -= old.Value.Size;
         }

         if (size > _budget)
         {
            return;
         }

         _entries[key] = _order.AddFirst((key, value, size));
         _size += size;
         while (_size > _budget && _order.Last is { } last)
         {
            _order.RemoveLast();
            _entries.Remove(last.Value.Key);
            _size -= last.Value.Size;
         }
      }
   }

   internal override bool TryFind(FactKey key, out object? value)
   {
      lock (_lock)
      {
         if (_entries.TryGetValue(key, out var node))
         {
            _order.Remove(node);
            _order.AddFirst(node);
            value = node.Value.Value;
            return true;
         }
      }

      value = null;
      return false;
   }

   // Estimates the bytes that the fact holds: two for each character of its text, and a fixed amount for each record.
   private static long SizeOf(object? value) => 64 + value switch
   {
      FileLines lines => 2L * lines.Length + 4L * lines.Count,
      IReadOnlyList<ChangedPath> paths => paths.Sum(path => 64L + 2L * (path.Path.Length + (path.OldPath?.Length ?? 0))),
      IReadOnlyList<LineHunk> hunks => 32L * hunks.Count,
      DiffTouch.Regions regions => 2L * regions.Lines.Length + 4L * regions.Lines.Count + 16L * regions.Changed.Count,
      FileBlob blob => 2L * (blob.Path.Length + blob.ObjectId.Length),
      _ => 0,
   };
}
