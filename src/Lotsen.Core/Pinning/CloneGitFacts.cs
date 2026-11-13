using Lotsen.Git;
using Lotsen.Hosting;
using Lotsen.Platforms;
using Lotsen.Tours;

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
         var missing = keys.Distinct().Where(key => !TryKeep(key, out _)).ToList();
         if (missing.Count == 0)
         {
            return;
         }

         var repository = await RepositoryAsync(cancellationToken);
         await ReadFilesAsync(repository, missing, cancellationToken);
         foreach (var key in missing)
         {
            await FactAsync(repository, key, cancellationToken);
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

      // Reads the facts of the keys' blobs and lines that the facts lack, with one lookup for the blobs and one read for the texts that the lookup did not read ahead.
      private async Task ReadFilesAsync(GitRepository repository, IReadOnlyCollection<FactKey> keys, CancellationToken cancellationToken)
      {
         List<(CommitId Commit, string Path)> unknown =
         [
            .. keys
               .Select(key => key switch
               {
                  FactKey.Blob(var commit, var path) => (commit, path),
                  FactKey.Lines(var commit, var path) => ((CommitId, string)?)(commit, path),
                  _ => null,
               })
               .OfType<(CommitId Commit, string Path)>()
               .Distinct()
               .Where(file => !TryKeep(new FactKey.Blob(file.Commit, file.Path), out _)),
         ];
         if (unknown.Count > 0)
         {
            var found = await repository.FindFilesAsync(unknown, cancellationToken);
            for (var i = 0; i < unknown.Count; i++)
            {
               Add(new FactKey.Blob(unknown[i].Commit, unknown[i].Path), found[i]);
            }
         }

         var lines = keys.OfType<FactKey.Lines>().Distinct().Where(key => !TryKeep(key, out _)).Select(key => (Key: key, File: Kept(new FactKey.Blob(key.Commit, key.Path)))).ToList();
         var texts = await repository.ReadTextsAsync([.. lines.Select(line => line.File).Where(ReadableFile.Is).OfType<FileBlob>()], cancellationToken);
         foreach (var (key, file) in lines)
         {
            Add(key, ReadableFile.Is(file) ? new FileLines(texts[file.ObjectId]) : null);
         }
      }

      private T Kept<T>(FactKey.Of<T> key) => TryKeep(key, out var value) ? (T)value! : throw new InvalidOperationException($"The scope holds no fact of {key}.");

      // Returns the fact from the facts, or reads it and adds it to them.
      private async Task<T> FactAsync<T>(GitRepository repository, FactKey.Of<T> key, CancellationToken cancellationToken) => (T)(await FactAsync(repository, (FactKey)key, cancellationToken))!;

      private async Task<object?> FactAsync(GitRepository repository, FactKey key, CancellationToken cancellationToken)
      {
         if (TryKeep(key, out var known))
         {
            return known;
         }

         var read = await ReadAsync(repository, key, cancellationToken);
         Add(key, read);
         return read;
      }

      private async Task<object?> ReadAsync(GitRepository repository, FactKey key, CancellationToken cancellationToken)
      {
         switch (key)
         {
            case FactKey.MergeBaseOf(var source, var target):
               return await repository.GetMergeBaseAsync(source, target, cancellationToken);
            case FactKey.ChangedPaths(var from, var to):
               return await repository.GetChangedPathsAsync(from, to, cancellationToken);
            case FactKey.ChangedFiles(var from, var to):
               return await repository.GetChangedFilesAsync(from, to, cancellationToken);
            case FactKey.Blob(var commit, var path):
               return await repository.FindFileAsync(commit, path, cancellationToken);
            case FactKey.Lines(var commit, var path):
               var file = await FactAsync(repository, new FactKey.Blob(commit, path), cancellationToken);
               return ReadableFile.Is(file) ? new FileLines(await repository.ReadTextAsync(file, cancellationToken)) : null;
            case FactKey.Hunks(var from, var path, var to, var newPath, var whitespace):
               await ReadFilesAsync(repository, [new FactKey.Blob(from, path), new FactKey.Blob(to, newPath)], cancellationToken);
               var oldFile = await FactAsync(repository, new FactKey.Blob(from, path), cancellationToken);
               var newFile = await FactAsync(repository, new FactKey.Blob(to, newPath), cancellationToken);
               return ReadableFile.Is(oldFile) && ReadableFile.Is(newFile) ? await repository.DiffLinesAsync(oldFile, newFile, whitespace, cancellationToken) : null;
            case FactKey.PinDiff(var mergeBase, var head, var side, var path):
               return await ReadPinDiffAsync(repository, mergeBase, head, side, path, cancellationToken);
            default:
               throw new InvalidOperationException($"No git read answers a fact of the kind {key.GetType().Name}.");
         }
      }

      private async Task<DiffTouch> ReadPinDiffAsync(GitRepository repository, CommitId mergeBase, CommitId head, FileSide side, string path, CancellationToken cancellationToken)
      {
         // The download of a pin's changed files lets the list read the clone alone. A scope runs one before it lists a pin that no scope downloaded into the current clone.
         var changesKey = new FactKey.ChangedFiles(mergeBase, head);
         if (!TryKeep(changesKey, out _))
         {
            await DownloadAsync(repository, mergeBase, head, cancellationToken);
         }

         var changed = (await FactAsync(repository, changesKey, cancellationToken)).FirstOrDefault(file => side == FileSide.Head
            ? file.Kind != FileChangeKind.Deleted && file.Path == path
            : file.Kind != FileChangeKind.Added && (file.OldPath ?? file.Path) == path);
         if (changed is null)
         {
            return new DiffTouch.Unchanged();
         }

         // The viewer shows a binary file or one above the largest size it reads as changed throughout, without regions.
         if (changed.Binary || changed.Size > ReadableFile.MaxSize)
         {
            return new DiffTouch.Throughout();
         }

         // The other side's path can be one that a passage cannot hold, as in a rename from old\name.cs, and the diff still pairs the two blobs. A blob fact names no file at such a path,
         // so that pair comes from a lookup of its own.
         var basePath = changed.OldPath ?? changed.Path;
         var hunksKey = new FactKey.Hunks(mergeBase, basePath, head, changed.Path);
         FileBlob? baseFile, headFile;
         if (RepositoryPath.IsValid(basePath) && RepositoryPath.IsValid(changed.Path))
         {
            var sides = new List<FactKey>();
            if (changed.Kind != FileChangeKind.Added)
            {
               sides.Add(new FactKey.Lines(mergeBase, basePath));
            }

            if (changed.Kind != FileChangeKind.Deleted)
            {
               sides.Add(new FactKey.Lines(head, changed.Path));
            }

            await ReadFilesAsync(repository, sides, cancellationToken);
            baseFile = changed.Kind == FileChangeKind.Added ? null : await FactAsync(repository, new FactKey.Blob(mergeBase, basePath), cancellationToken);
            headFile = changed.Kind == FileChangeKind.Deleted ? null : await FactAsync(repository, new FactKey.Blob(head, changed.Path), cancellationToken);
         }
         else
         {
            (baseFile, headFile) = await repository.FindChangedFileAsync(mergeBase, head, changed, cancellationToken);
         }

         var sideFile = side == FileSide.Base ? baseFile : headFile;
         var lines = sideFile is null ? new FileLines("") : await LinesAsync(repository, sideFile, cancellationToken);
         if (baseFile is null || headFile is null)
         {
            // The viewer shows the whole text of a file on one side as one change.
            return new DiffTouch.Regions(lines, lines.Length == 0 ? [] : [(0, lines.Length)]);
         }

         var hunks = await FactAsync(repository, hunksKey, cancellationToken) ?? await repository.DiffLinesAsync(baseFile, headFile, cancellationToken);
         return new DiffTouch.Regions(lines, [.. hunks.Select(hunk => side == FileSide.Base ? lines.HunkRange(hunk.BaseStart, hunk.BaseCount) : lines.HunkRange(hunk.HeadStart, hunk.HeadCount))]);
      }

      // The diff list counts a file as text that its blob may call binary, and the regions then still need its text.
      private async Task<FileLines> LinesAsync(GitRepository repository, FileBlob file, CancellationToken cancellationToken) =>
         await FactAsync(repository, new FactKey.Lines(file.Commit, file.Path), cancellationToken) ?? new FileLines(await repository.ReadTextAsync(file, cancellationToken));

      // The listing would download the changed files' contents within the query timeout, so the download runs first, within the transfer timeout. A failed download leaves that to the listing.
      private async Task DownloadAsync(GitRepository repository, CommitId mergeBase, CommitId head, CancellationToken cancellationToken)
      {
         if (_downloaded.Contains((mergeBase, head)) || _owner.Downloaded(_remote.Url, mergeBase, head))
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
      IReadOnlyList<FileChange> files => files.Sum(file => 128L + 2L * (file.Path.Length + (file.OldPath?.Length ?? 0) + (file.HeadObjectId?.Length ?? 0))),
      IReadOnlyList<LineHunk> hunks => 32L * hunks.Count,
      DiffTouch.Regions regions => 2L * regions.Lines.Length + 4L * regions.Lines.Count + 16L * regions.Changed.Count,
      FileBlob blob => 2L * (blob.Path.Length + blob.ObjectId.Length),
      _ => 0,
   };
}
