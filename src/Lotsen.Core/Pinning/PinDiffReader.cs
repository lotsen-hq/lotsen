using Lotsen.Git;
using Lotsen.Tours;

namespace Lotsen.Pinning;

/// <summary>Reads from a clone what a pin's diff shows of a file at a side, as a <see cref="DiffTouch"/>. It lists the pin's changed files once per instance.</summary>
internal sealed class PinDiffReader
{
   private readonly GitRepository _repository;
   private readonly CommitId _mergeBase;
   private readonly CommitId _head;
   private IReadOnlyList<FileChange>? _changedFiles;

   /// <param name="repository">The clone, which holds both commits.</param>
   /// <param name="mergeBase">The pin's merge base.</param>
   /// <param name="head">The pin's head.</param>
   public PinDiffReader(GitRepository repository, CommitId mergeBase, CommitId head)
   {
      _repository = repository;
      _mergeBase = mergeBase;
      _head = head;
   }

   /// <summary>Returns what the pin's diff shows of a file at a side, the same regions that the viewer's diff of the file shows.</summary>
   /// <param name="side">The side of the file.</param>
   /// <param name="path">The file's repository path at that side.</param>
   /// <param name="cancellationToken">Stops the query.</param>
   /// <exception cref="GitException">Git could not read the clone.</exception>
   public async Task<DiffTouch> ReadAsync(FileSide side, string path, CancellationToken cancellationToken)
   {
      // The first pin, an iteration update before its store and a fact scope before its first diff read of a pin download the content of every changed file, so the list reads the clone alone
      // unless that download failed or has not finished.
      _changedFiles ??= await _repository.GetChangedFilesAsync(_mergeBase, _head, cancellationToken);
      var changed = _changedFiles.FirstOrDefault(file => side == FileSide.Head
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

      // The other side's path can be one that a passage cannot hold, as in a rename from old\name.cs, and the diff still pairs the two blobs.
      var (baseFile, headFile) = await _repository.FindChangedFileAsync(_mergeBase, _head, changed, cancellationToken);
      var sideFile = side == FileSide.Base ? baseFile : headFile;
      var lines = new FileLines(sideFile is null ? "" : await _repository.ReadTextAsync(sideFile, cancellationToken));
      if (baseFile is null || headFile is null)
      {
         // The viewer shows the whole text of a file on one side as one change.
         return new DiffTouch.Regions(lines, lines.Length == 0 ? [] : [(0, lines.Length)]);
      }

      var hunks = await _repository.DiffLinesAsync(baseFile, headFile, cancellationToken);
      return new DiffTouch.Regions(lines, [.. hunks.Select(hunk => side == FileSide.Base ? lines.HunkRange(hunk.BaseStart, hunk.BaseCount) : lines.HunkRange(hunk.HeadStart, hunk.HeadCount))]);
   }
}
