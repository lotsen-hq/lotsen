using System.Diagnostics;
using Lotsen.Git;
using Lotsen.Prose;
using Lotsen.Tours;

namespace Lotsen.Pinning;

/// <summary>
/// Computes what moving a pinned tour to a new iteration does to its stops' passages and code references, to its threads' anchors and to its plan's file marks, from git facts alone. It reads no git
/// and stores nothing.
/// </summary>
/// <remarks>
/// <para>
/// Each line range maps through git's diff of its file from the commit it was read at to the new pin's commit of its side. A passage and a code anchor carry that commit. A code reference carries
/// none, so it maps from the tour's pin. When the two commits are equal, the range stays as it is.
/// </para>
/// <para>
/// A range moves when no hunk of its file's diff touches its lines. A hunk touches it when the hunk changes or removes one of its lines, or inserts lines between two of them. The range's lines
/// shift by the lines that the hunks above it add or remove, its path follows a rename, and its text is read again at the new commit when its file changed. Anything else makes the range stale:
/// it keeps its old place, and nothing guesses a new one. The primary passage of a change stop is stale too when, once moved, it no longer touches the new pin's diff.
/// </para>
/// <para>
/// A stop's content, a plan's file marks and a code or file anchor hold only the paths that <see cref="RepositoryPath.IsValid"/> accepts, and git can rename a file to a path that it refuses.
/// Such a rename makes a passage, a code reference, a code anchor or a file anchor in the file stale, and a file mark on the file keeps its stored path.
/// </para>
/// <para>
/// A fact that the set lacks leaves the item that needs it undecided, and the pass carries on, so one pass records every miss that the facts it has can reach.
/// </para>
/// </remarks>
internal sealed class TourRemap
{
   private readonly FactReader _facts;
   private readonly TourPin.Pinned _from;
   private readonly TourPin.Pinned _to;

   private TourRemap(FactReader facts, TourPin.Pinned from, TourPin.Pinned to)
   {
      _facts = facts;
      _from = from;
      _to = to;
   }

   /// <summary>Returns how each passage, code reference, anchor and file mark of a pinned tour maps from its pin onto a new pin, or the facts that the mapping needs first.</summary>
   /// <param name="tour">The tour, pinned to <paramref name="from"/>.</param>
   /// <param name="from">The tour's pin.</param>
   /// <param name="to">The new pin.</param>
   /// <param name="facts">The git facts known so far.</param>
   public static Decision<RemapResult> Map(Tour tour, TourPin.Pinned from, TourPin.Pinned to, FactSet facts)
   {
      var reader = new FactReader(facts);
      var remap = new TourRemap(reader, from, to);
      var stops = (tour.Plan?.Stops ?? []).OfType<Stop.Written>().Select(remap.MapStop).ToList();
      var anchors = tour.Threads.Where(thread => thread.Anchor is ThreadAnchor.CodeRange or ThreadAnchor.WholeFile).Select(thread => (thread, Outcome: remap.MapAnchor(thread.Anchor))).ToList();
      var marks = remap.MapFileMarks(tour.Plan?.FileMarks ?? []);
      return Decision<RemapResult>.Of(reader, () => new RemapResult(
         from,
         to,
         [.. stops.Select(stop => stop!)],
         [.. anchors.Select(entry => new AnchorRemap(entry.thread.Id, entry.thread.Anchor, entry.Outcome!))],
         [.. marks.Select(mark => mark!)]));
   }

   // A mark follows a rename between the heads, unless a stored plan refuses the new path or another mark holds it, which keeps one mark for each path. Any other mark keeps its path.
   private List<FileMarkRemap?> MapFileMarks(IReadOnlyList<FileMark> marks)
   {
      var paths = marks.Select(mark => mark.Path).ToHashSet(StringComparer.Ordinal);
      return [.. marks.Select(mark =>
      {
         ChangedPath? change = null;
         if (_from.Head != _to.Head && !TryFindChange(_from.Head, mark.Path, _to.Head, out change))
         {
            return null;
         }

         var moved = change is { Kind: FileChangeKind.Renamed } && RepositoryPath.IsValid(change.Path) && !paths.Contains(change.Path);
         return new FileMarkRemap(mark, moved ? change!.Path : mark.Path);
      })];
   }

   // Returns null when a fact that the stop needs is missing.
   private StopRemap? MapStop(Stop.Written stop)
   {
      var content = stop.Content;
      var passages = new List<PassageRemap>();
      var known = true;
      foreach (var (passage, index) in content.Passages.Select((passage, index) => (passage, index)))
      {
         var mapped = MapStoredLines(passage.Side, passage.Path, passage.StartLine, passage.EndLine, passage.Commit, passage.Text)?
            .Select(place => passage with { Path = place.Path, StartLine = place.StartLine, EndLine = place.EndLine, Commit = place.Commit, Text = place.Text! });
         if (mapped is Remapped<StopPassage>.Moved { To: var moved } && moved.Role == PassageRole.Primary && StopContentRules.PrimaryTouchesDiff(stop.Kind))
         {
            mapped = !_facts.TryGet(new FactKey.PinDiff(_to.MergeBase, _to.Head, moved.Side, moved.Path), out var diff) ? null
               : diff.Touches(moved.StartLine, moved.EndLine) ? mapped
               : new Remapped<StopPassage>.Stale(StaleReason.OffDiff);
         }

         known &= mapped is not null;
         passages.Add(new PassageRemap(index, passage, mapped!));
      }

      // put_stop refused prose with a malformed link or too deep a nesting, so the stored prose reads without an error.
      var references = ProseReferences.Of(content).Value ?? throw new InvalidOperationException($"The prose of stop '{stop.Id}' does not read.");
      var mappedReferences = new List<ReferenceRemap>();
      foreach (var reference in references)
      {
         var (side, path, startLine, endLine) = (reference.Reference.Side, reference.Reference.Path, reference.Reference.StartLine, reference.Reference.EndLine);
         var mapped = MapStoredLines(side, path, startLine, endLine, _from.CommitOf(side), null)?.Select(place => new ReferencePlace(place.Path, place.StartLine, place.EndLine));
         known &= mapped is not null;
         mappedReferences.Add(new ReferenceRemap(reference, mapped!));
      }

      return known ? new StopRemap(stop.Id, stop.Version, passages, mappedReferences) : null;
   }

   // Maps a code anchor or a file anchor, and returns null when a fact that the anchor needs is missing. Every other anchor stays as it is, so the remap leaves it out.
   private Remapped<ThreadAnchor>? MapAnchor(ThreadAnchor anchor)
   {
      switch (anchor)
      {
         case ThreadAnchor.CodeRange range:
            return MapStoredLines(range.Side, range.Path, range.StartLine, range.EndLine, range.Commit, range.Text)?
               .Select<ThreadAnchor>(place => range with { Path = place.Path, StartLine = place.StartLine, EndLine = place.EndLine, Commit = place.Commit, Text = place.Text! });
         case ThreadAnchor.WholeFile file:
            var target = _to.CommitOf(file.Side);
            if (file.Commit == target)
            {
               return new Remapped<ThreadAnchor>.Moved(file);
            }

            if (!TryFindChange(file.Commit, file.Path, target, out var change))
            {
               return null;
            }

            // Git lists a file that a submodule replaces as changed, not deleted.
            if (change is not null)
            {
               if (change.Kind == FileChangeKind.Deleted)
               {
                  return new Remapped<ThreadAnchor>.Stale(StaleReason.FileGone);
               }

               // The git layer reads no file at such a path.
               if (!RepositoryPath.IsValid(change.Path))
               {
                  return new Remapped<ThreadAnchor>.Stale(StaleReason.PathRefused);
               }

               if (!_facts.TryGet(new FactKey.Blob(target, change.Path), out var blob))
               {
                  return null;
               }

               if (blob is null)
               {
                  return new Remapped<ThreadAnchor>.Stale(StaleReason.FileGone);
               }
            }

            var newPath = change?.Path ?? file.Path;
            return RepositoryPath.IsValid(newPath) ? new Remapped<ThreadAnchor>.Moved(file with { Path = newPath, Commit = target }) : new Remapped<ThreadAnchor>.Stale(StaleReason.PathRefused);
         default:
            throw new UnreachableException($"The remap maps only code and file anchors, not {anchor.GetType().Name}.");
      }
   }

   // Maps the lines of a stop's content or a code anchor as MapLines does, and makes them stale when they move to a path that neither can hold.
   private Remapped<MovedLines>? MapStoredLines(FileSide side, string path, int startLine, int endLine, CommitId commit, string? text)
   {
      var mapped = MapLines(side, path, startLine, endLine, commit, text);
      return mapped is Remapped<MovedLines>.Moved { To.Path: var newPath } && !RepositoryPath.IsValid(newPath) ? new Remapped<MovedLines>.Stale(StaleReason.PathRefused) : mapped;
   }

   // Maps lines read at a commit to the new pin's commit of their side, or returns null when a fact is missing. The text is the lines' text at the commit, or null to read it there.
   private Remapped<MovedLines>? MapLines(FileSide side, string path, int startLine, int endLine, CommitId commit, string? text)
   {
      var target = _to.CommitOf(side);
      if (commit == target)
      {
         return new Remapped<MovedLines>.Moved(new MovedLines(path, startLine, endLine, commit, text));
      }

      if (!TryFindChange(commit, path, target, out var change))
      {
         return null;
      }

      if (change is null)
      {
         // The file has the same content at both commits.
         return new Remapped<MovedLines>.Moved(new MovedLines(path, startLine, endLine, target, text));
      }

      if (change.Kind == FileChangeKind.Deleted)
      {
         return new Remapped<MovedLines>.Stale(StaleReason.FileGone);
      }

      // The git layer reads no file at such a path.
      if (!RepositoryPath.IsValid(change.Path))
      {
         return new Remapped<MovedLines>.Stale(StaleReason.PathRefused);
      }

      if (!_facts.TryGet(new FactKey.Hunks(commit, path, target, change.Path), out var hunks))
      {
         return null;
      }

      if (hunks is null)
      {
         return new Remapped<MovedLines>.Stale(StaleReason.Unreadable);
      }

      if (Shift(hunks, startLine, endLine) is not { } shift)
      {
         return new Remapped<MovedLines>.Stale(StaleReason.LinesChanged);
      }

      // Lines that no hunk touches are the same at both commits. The comparison keeps a diff that git reports in another form, such as a change of the file's type, from moving the lines.
      var (newStart, newEnd) = (startLine + shift, endLine + shift);
      if (!_facts.TryGet(new FactKey.Lines(target, change.Path), out var newLines))
      {
         return null;
      }

      if (newLines is null)
      {
         return new Remapped<MovedLines>.Stale(StaleReason.Unreadable);
      }

      var oldText = text;
      if (oldText is null)
      {
         if (!_facts.TryGet(new FactKey.Lines(commit, path), out var oldLines))
         {
            return null;
         }

         oldText = oldLines is not null && endLine <= oldLines.Count ? oldLines.Text(startLine, endLine) : null;
      }

      var newText = newEnd <= newLines.Count ? newLines.Text(newStart, newEnd) : null;
      return newText is not null && newText == oldText
         ? new Remapped<MovedLines>.Moved(new MovedLines(change.Path, newStart, newEnd, target, newText))
         : new Remapped<MovedLines>.Stale(StaleReason.LinesChanged);
   }

   // Returns how far the hunks above the lines move them, or null when a hunk changes one of the lines or inserts lines between two of them.
   private static int? Shift(IReadOnlyList<LineHunk> hunks, int startLine, int endLine)
   {
      var shift = 0;
      foreach (var hunk in hunks)
      {
         // A hunk without base lines inserts its lines after its base start line.
         var above = hunk.BaseCount == 0 ? hunk.BaseStart < startLine : hunk.BaseStart + hunk.BaseCount <= startLine;
         var below = hunk.BaseCount == 0 ? hunk.BaseStart >= endLine : hunk.BaseStart > endLine;
         if (above)
         {
            shift += hunk.HeadCount - hunk.BaseCount;
         }
         else if (!below)
         {
            return null;
         }
      }

      return shift;
   }

   // Finds the change of the file at the path between the commits, following a rename, which is null when the file is the same at both. Returns false when the changed paths are missing.
   private bool TryFindChange(CommitId from, string path, CommitId to, out ChangedPath? change)
   {
      if (!_facts.TryGet(new FactKey.ChangedPaths(from, to), out var changes))
      {
         change = null;
         return false;
      }

      change = changes.FirstOrDefault(file => file.Kind != FileChangeKind.Added && (file.OldPath ?? file.Path) == path);
      return true;
   }

   // A range's new place. The text is null only when the caller gave none and the file is the same at both commits.
   private sealed record MovedLines(string Path, int StartLine, int EndLine, CommitId Commit, string? Text);
}

/// <summary>How a tour's passages, code references, anchors and file marks map from its pin onto a new pin, as <see cref="TourRemap.Map"/> returns it.</summary>
/// <remarks>
/// The tour may change between the mapping and its use, so each item names what it was computed from: a stop its ID and version, a passage its index and the stored passage, a code reference its
/// field and link, an anchor its thread's ID and the stored anchor, and a file mark the stored mark. Apply an item only while the tour still holds what it names.
/// </remarks>
/// <param name="From">The pin that the mapping started from: the tour's pin when it was read.</param>
/// <param name="To">The new pin.</param>
/// <param name="Stops">Each stop of the plan with content, in plan order. The list omits a pending stop, which has nothing to map.</param>
/// <param name="Anchors">
/// Each thread whose anchor names code, a code anchor or a file anchor, in the tour's thread order, notes included. The list omits every other anchor, which stays as it is.
/// </param>
/// <param name="FileMarks">Each file mark of the plan, in the plan's order.</param>
internal sealed record RemapResult(TourPin.Pinned From, TourPin.Pinned To, IReadOnlyList<StopRemap> Stops, IReadOnlyList<AnchorRemap> Anchors, IReadOnlyList<FileMarkRemap> FileMarks)
{
   /// <summary>The stale stops, in plan order.</summary>
   public IReadOnlyList<StopRemap> StaleStops => [.. Stops.Where(stop => stop.Stale)];
}

/// <summary>How one stop's current content maps onto a new pin. Earlier stop versions keep their commits.</summary>
/// <param name="StopId">The stop's ID.</param>
/// <param name="Version">The stop version whose content was mapped.</param>
/// <param name="Passages">Each passage of the content, in content order.</param>
/// <param name="References">Each code reference in the content's prose, in field order.</param>
internal sealed record StopRemap(string StopId, int Version, IReadOnlyList<PassageRemap> Passages, IReadOnlyList<ReferenceRemap> References)
{
   /// <summary>Whether a passage or a code reference of the stop is stale, which makes the stop stale. An overview without passages or references is never stale.</summary>
   public bool Stale => StalePassages.Any() || StaleReferences.Any();

   /// <summary>The stale passages, in content order.</summary>
   public IEnumerable<PassageRemap> StalePassages => Passages.Where(passage => passage.Outcome is Remapped<StopPassage>.Stale);

   /// <summary>The stale code references, in field order.</summary>
   public IEnumerable<ReferenceRemap> StaleReferences => References.Where(reference => reference.Outcome is Remapped<ReferencePlace>.Stale);
}

/// <summary>How one passage maps onto a new pin.</summary>
/// <param name="Index">The passage's index in the content's passages.</param>
/// <param name="Passage">The passage as the content holds it.</param>
/// <param name="Outcome">The moved passage, with its new commit, path, lines and text, or why it is stale.</param>
internal sealed record PassageRemap(int Index, StopPassage Passage, Remapped<StopPassage> Outcome);

/// <summary>How one code reference in a stop's prose maps onto a new pin.</summary>
/// <param name="Reference">The reference under its prose field's <see cref="ProseField.Path"/>, with the link as the prose holds it.</param>
/// <param name="Outcome">The reference's new path and lines on its side, or why it is stale.</param>
internal sealed record ReferenceRemap(ProseReference Reference, Remapped<ReferencePlace> Outcome);

/// <summary>The new path and lines of a moved code reference, whose side stays.</summary>
/// <param name="Path">The file's repository path at the new pin's side.</param>
/// <param name="StartLine">The first line, from 1.</param>
/// <param name="EndLine">The last line, not before <see cref="StartLine"/>.</param>
internal sealed record ReferencePlace(string Path, int StartLine, int EndLine);

/// <summary>How one file mark maps onto a new pin. A mark is never stale: a mark whose file left the diff keeps its path.</summary>
/// <param name="Mark">The mark as the plan holds it.</param>
/// <param name="Path">The mark's path on the new pin: its file's path at the new head after a rename, or else the stored path, as <see cref="TourRemap"/> says.</param>
internal sealed record FileMarkRemap(FileMark Mark, string Path);

/// <summary>How one thread's anchor maps onto a new pin.</summary>
/// <param name="ThreadId">The thread's ID.</param>
/// <param name="Anchor">The anchor as the thread holds it.</param>
/// <param name="Outcome">The moved anchor, or why it cannot be moved. A stale anchor stays on its commit.</param>
internal sealed record AnchorRemap(string ThreadId, ThreadAnchor Anchor, Remapped<ThreadAnchor> Outcome);

/// <summary>Where an item lands on a new pin: moved to a new place, or stale at its old one.</summary>
/// <typeparam name="T">The item with its new place.</typeparam>
internal abstract record Remapped<T>
{
   private Remapped()
   {
   }

   /// <summary>Returns this outcome for another item: a moved item's new place made into the other item, or a stale item's reason.</summary>
   public Remapped<TOther> Select<TOther>(Func<T, TOther> moved) => this switch
   {
      Moved { To: var to } => new Remapped<TOther>.Moved(moved(to)),
      Stale { Reason: var reason } => new Remapped<TOther>.Stale(reason),
      _ => throw new UnreachableException(),
   };

   /// <summary>An item that moves, which may be the place it had.</summary>
   /// <param name="To">The item at its new place.</param>
   internal sealed record Moved(T To) : Remapped<T>;

   /// <summary>An item that cannot be moved, which keeps its old place.</summary>
   /// <param name="Reason">Why it cannot be moved.</param>
   internal sealed record Stale(StaleReason Reason) : Remapped<T>;
}

/// <summary>Why a line range or a file cannot be moved onto a new pin.</summary>
internal enum StaleReason
{
   /// <summary>A hunk changes or removes one of the range's lines, or inserts lines between two of them.</summary>
   LinesChanged,

   /// <summary>The file does not exist at the new pin's side.</summary>
   FileGone,

   /// <summary>The file at the new pin's side is binary, larger than <see cref="ReadableFile.MaxSize"/>, or not a file whose lines git compares.</summary>
   Unreadable,

   /// <summary>The primary passage of a change stop moves, but no longer touches the diff of the new pin.</summary>
   OffDiff,

   /// <summary>
   /// A rename gives the file a path that <see cref="RepositoryPath.IsValid"/> refuses, such as one with a backslash, and a stop's content and a code or file anchor hold only the paths that it
   /// accepts.
   /// </summary>
   PathRefused,
}
