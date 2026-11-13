using Lotsen.Git;
using Lotsen.Pinning;
using Lotsen.Prose;
using Lotsen.Tours;

namespace Lotsen.Operations;

/// <summary>
/// Checks a stop's passages and the code references in its prose against a pinned tour's commits, and reads the passages' text from the tour's clone. A question's code range and file anchors use the
/// same checks.
/// </summary>
/// <remarks>
/// The file of a passage or a code reference must be a <see cref="ReadableFile"/> on its side, because the check reads the whole file. Each file is read once per instance, however many passages
/// and references name it. The reads are git facts, so a file that the fact memo holds runs no git. A lookup downloads the content that the clone lacks, so a write downloads its files first
/// with <see cref="GitPhase.DownloadAsync"/>, in one fetch.
/// </remarks>
internal sealed class StopPassages
{
   private readonly IFactScope _facts;
   private readonly TourPin.Pinned _pin;
   private readonly Dictionary<(FileSide Side, string Path), FileRead> _files = [];

   public StopPassages(IFactScope facts, TourPin.Pinned pin)
   {
      _facts = facts;
      _pin = pin;
   }

   /// <summary>Checks every passage and then every code reference of a stop, and returns the passages to store, each with its commit and text.</summary>
   /// <param name="passages">The passages, which follow <see cref="StopRules.CheckFields"/>.</param>
   /// <param name="kind">The stop's kind, which says whether the primary passage must touch the diff, as <see cref="StopContentRules.PrimaryTouchesDiff"/> returns.</param>
   /// <param name="references">The stop's code references, from <see cref="StopRules.ReadReferences(PutStopInput)"/>.</param>
   /// <param name="cancellationToken">Stops the checks.</param>
   /// <returns>The passages in input order, or the error of the first passage or code reference that fails a check, under its field.</returns>
   /// <remarks>One lookup finds the files of the passages and the references, as <see cref="ReadAllAsync"/> describes.</remarks>
   /// <exception cref="Platforms.ExternalFailureException">Git could not open or read the clone.</exception>
   public async Task<OperationResult<IReadOnlyList<StopPassage>>> CheckAsync(
      IReadOnlyList<PassageInput> passages, StopKind kind, IReadOnlyList<ProseReference> references, CancellationToken cancellationToken)
   {
      await ReadAllAsync([.. passages.Select(passage => (passage.Side, passage.Path)), .. FilesOf(references), .. await OtherSidesAsync(passages, kind, cancellationToken)], cancellationToken);
      var stored = new List<StopPassage>(passages.Count);
      for (var i = 0; i < passages.Count; i++)
      {
         var passage = passages[i];
         var field = FieldPath.Root.Then("passages").At(i);
         var read = await ReadLinesAsync(passage.Side, passage.Path, passage.StartLine, passage.EndLine, field, cancellationToken);
         if (read.Value is not { } lines)
         {
            return read.Error!;
         }

         if (passage.ExpectedFirstLine is { } expected && lines.Line(passage.StartLine).Trim() is var actual && actual != expected.Trim())
         {
            return OperationError.Invalid(
               field.Then("expectedFirstLine"),
               $"line {passage.StartLine} of '{passage.Path}' at the {SideName(passage.Side)} reads '{Shortened(actual)}', not '{Shortened(expected.Trim())}'. Check the line numbers against the tour's {SideName(passage.Side)}, commit {CommitOf(passage.Side)}.");
         }

         var mustTouch = passage.Role == PassageRole.Primary && StopContentRules.PrimaryTouchesDiff(kind);
         if (mustTouch && !await TouchesDiffAsync(passage.Side, passage.Path, passage.StartLine, passage.EndLine, cancellationToken))
         {
            return OperationError.Invalid(
               field,
               $"the primary passage of {StopContentRules.KindWithArticle(kind)} stop must touch the diff, but lines {passage.StartLine}-{passage.EndLine} of '{passage.Path}' are unchanged at the {SideName(passage.Side)}. Point it at changed lines, make it a related passage, or make the stop a context stop.");
         }

         stored.Add(new StopPassage(passage.Side, passage.Path, passage.StartLine, passage.EndLine, passage.Role, CommitOf(passage.Side), lines.Text(passage.StartLine, passage.EndLine), StopRules.Label(passage.Label)));
      }

      return await CheckEachReferenceAsync(references, cancellationToken) is { } error ? error : stored;
   }

   /// <summary>Checks that every code reference names a text file at its side, and lines in bounds there.</summary>
   /// <param name="references">The references, from <see cref="StopRules.ReadReferences(PutStopInput)"/> or <see cref="ProseReferences.Read"/>.</param>
   /// <param name="cancellationToken">Stops the checks.</param>
   /// <returns>The error of the first reference that fails a check, under its field and naming its link, or null when every reference passes.</returns>
   /// <remarks>
   /// One lookup finds the references' files, as <see cref="ReadAllAsync"/> describes. A code reference may point at unchanged code on either side, so the diff rule of a primary passage does
   /// not apply.
   /// </remarks>
   /// <exception cref="Platforms.ExternalFailureException">Git could not open or read the clone.</exception>
   public async Task<OperationError?> CheckReferencesAsync(IReadOnlyList<ProseReference> references, CancellationToken cancellationToken)
   {
      await ReadAllAsync(FilesOf(references), cancellationToken);
      return await CheckEachReferenceAsync(references, cancellationToken);
   }

   /// <summary>Returns the lines of a text file at a side, after checking that the file exists there and that the lines are in bounds.</summary>
   /// <param name="side">The side to read the file at.</param>
   /// <param name="path">The file's repository path, which follows <see cref="RepositoryPath.IsValid"/>.</param>
   /// <param name="startLine">The first line, from 1.</param>
   /// <param name="endLine">The last line, not before <paramref name="startLine"/>.</param>
   /// <param name="field">The input field of the lines, which each error names with its own property, as in <c>passages[0].path</c>.</param>
   /// <param name="cancellationToken">Stops the read.</param>
   /// <returns>The whole file's lines, or the error that says which check failed.</returns>
   /// <exception cref="Platforms.ExternalFailureException">Git could not open or read the clone.</exception>
   public async Task<OperationResult<FileLines>> ReadLinesAsync(FileSide side, string path, int startLine, int endLine, FieldPath field, CancellationToken cancellationToken)
   {
      var (lines, problem) = await ReadAsync(side, path, startLine, endLine, cancellationToken);
      return problem is null ? lines! : OperationError.Invalid(field.Then(problem.Property), problem.Text);
   }

   /// <summary>Returns the file at a side, after checking that the path names a file there. The file may be binary or large.</summary>
   /// <param name="side">The side to find the file at.</param>
   /// <param name="path">The file's repository path, which follows <see cref="RepositoryPath.IsValid"/>.</param>
   /// <param name="field">The input field of the file, whose <c>path</c> property the error names, as in <c>anchor.path</c>.</param>
   /// <param name="cancellationToken">Stops the query.</param>
   /// <exception cref="Platforms.ExternalFailureException">Git could not open or read the clone.</exception>
   public async Task<OperationResult<FileBlob>> FindFileAsync(FileSide side, string path, FieldPath field, CancellationToken cancellationToken) =>
      await _facts.FactAsync(new FactKey.Blob(CommitOf(side), path), cancellationToken) is { } file ? file : OperationError.Invalid(field.Then("path"), NotAFile(side, path));

   /// <inheritdoc cref="TourPin.Pinned.CommitOf"/>
   public CommitId CommitOf(FileSide side) => _pin.CommitOf(side);

   /// <summary>Returns the name of a side as served text uses it: <c>base</c> or <c>head</c>.</summary>
   public static string SideName(FileSide side) => side == FileSide.Base ? "base" : "head";

   private static IEnumerable<(FileSide Side, string Path)> FilesOf(IReadOnlyList<ProseReference> references) =>
      references.Select(reference => (reference.Reference.Side, reference.Reference.Path));

   /// <summary>Looks up every file that the instance has not read with one lookup, and reads each text file that the lookup found.</summary>
   /// <remarks>
   /// When git fails, the instance keeps none of the lookup's files, so each check looks up its own file in input order. A git failure then reaches the caller at the turn of the file that causes
   /// it, after the checks before it have run, and the first passage that fails a check is still the one reported.
   /// </remarks>
   private async Task ReadAllAsync(IEnumerable<(FileSide Side, string Path)> files, CancellationToken cancellationToken)
   {
      var unread = files.Distinct().Where(file => !_files.ContainsKey(file)).ToList();
      if (unread.Count == 0)
      {
         return;
      }

      try
      {
         await _facts.ReadAsync([.. unread.SelectMany(file => KeysOf(file.Side, file.Path))], cancellationToken);
      }
      catch (GitException)
      {
         return;
      }

      foreach (var (side, path) in unread)
      {
         _files[(side, path)] = ReadOf(side, path);
      }
   }

   // Returns the other side of each primary passage's file whose diff the check reads, so the one lookup of ReadAllAsync finds the files of the diff check too. A failed list returns none, and
   // the diff check then fails at its turn.
   private async Task<IReadOnlyList<(FileSide Side, string Path)>> OtherSidesAsync(IReadOnlyList<PassageInput> passages, StopKind kind, CancellationToken cancellationToken)
   {
      var primaries = passages.Where(passage => passage.Role == PassageRole.Primary).ToList();
      if (primaries.Count == 0 || !StopContentRules.PrimaryTouchesDiff(kind))
      {
         return [];
      }

      IReadOnlyList<FileChange> changes;
      try
      {
         changes = await _facts.FactAsync(new FactKey.ChangedFiles(_pin.MergeBase, _pin.Head), cancellationToken);
      }
      catch (GitException)
      {
         return [];
      }

      var others = new List<(FileSide Side, string Path)>();
      foreach (var primary in primaries)
      {
         if (PinChanges.At(changes, primary.Side, primary.Path) is { Kind: FileChangeKind.Modified or FileChangeKind.Renamed, Binary: false, Size: <= ReadableFile.MaxSize } changed)
         {
            others.Add(primary.Side == FileSide.Head ? (FileSide.Base, changed.OldPath ?? changed.Path) : (FileSide.Head, changed.Path));
         }
      }

      return [.. others.Where(other => RepositoryPath.IsValid(other.Path))];
   }

   private async Task<OperationError?> CheckEachReferenceAsync(IReadOnlyList<ProseReference> references, CancellationToken cancellationToken)
   {
      foreach (var (field, reference) in references)
      {
         var (_, problem) = await ReadAsync(reference.Side, reference.Path, reference.StartLine, reference.EndLine, cancellationToken);
         if (problem is not null)
         {
            return OperationError.Invalid(field, $"the link '{reference.Link}' cannot be followed. {problem.Text}");
         }
      }

      return null;
   }

   // Returns the file's lines, or the check that failed with the property of the input lines that it concerns. A file that the instance has not read gets a lookup of its own.
   private async Task<(FileLines? Lines, LineProblem? Problem)> ReadAsync(FileSide side, string path, int startLine, int endLine, CancellationToken cancellationToken)
   {
      if (!_files.TryGetValue((side, path), out var read))
      {
         await _facts.ReadAsync(KeysOf(side, path), cancellationToken);
         read = ReadOf(side, path);
         _files[(side, path)] = read;
      }

      return read switch
      {
         FileRead.NotAFile => (null, new LineProblem("path", NotAFile(side, path))),
         FileRead.Binary => (null, new LineProblem("path", $"'{path}' is a binary file at the {SideName(side)}, so it has no lines to point at.")),
         FileRead.TooLarge(var size) => (null, new LineProblem(
            "path", $"'{path}' is {size} bytes at the {SideName(side)}, more than the {ReadableFile.MaxSize} bytes that lotsen reads, so lotsen does not read its lines.")),
         FileRead.Text(var lines) when startLine > lines.Count =>
            (null, new LineProblem("startLine", $"'{path}' has {LineCount(lines)} at the {SideName(side)}, so line {startLine} does not exist.")),
         FileRead.Text(var lines) when endLine > lines.Count =>
            (null, new LineProblem("endLine", $"'{path}' has {LineCount(lines)} at the {SideName(side)}, so line {endLine} does not exist.")),
         FileRead.Text(var lines) => (lines, null),
      };
   }

   // The facts that a check of the file's lines reads. The lines of a file that is not readable read no content.
   private FactKey[] KeysOf(FileSide side, string path) => [new FactKey.Blob(CommitOf(side), path), new FactKey.Lines(CommitOf(side), path)];

   // Returns the lines of a file whose facts the scope read, or says why the file has none to read.
   private FileRead ReadOf(FileSide side, string path) => _facts.Fact(new FactKey.Blob(CommitOf(side), path)) switch
   {
      null => new FileRead.NotAFile(),
      { Binary: true } => new FileRead.Binary(),
      { Size: > ReadableFile.MaxSize } file => new FileRead.TooLarge(file.Size),
      _ => new FileRead.Text(_facts.Fact(new FactKey.Lines(CommitOf(side), path))!),
   };

   private static string LineCount(FileLines lines) => lines.Count == 1 ? "1 line" : $"{lines.Count} lines";

   private static string Shortened(string text) => text.Length <= 120 ? text : text[..120] + "…";

   private string NotAFile(FileSide side, string path) =>
      $"'{path}' is not a file at the {SideName(side)} of the tour, commit {CommitOf(side)}. A file that the change adds exists only at the head, and a file that it deletes only at the base.";

   /// <summary>Returns whether lines of a file at a side touch the pin's diff, the rule that the primary passage of a change stop follows, as <see cref="DiffTouch.Touches"/> says.</summary>
   /// <param name="side">The side of the lines.</param>
   /// <param name="path">The file's repository path at that side, which names a text file of at most <see cref="ReadableFile.MaxSize"/> bytes there.</param>
   /// <param name="startLine">The first line, from 1.</param>
   /// <param name="endLine">The last line, not before <paramref name="startLine"/> and not after the file's last line.</param>
   /// <param name="cancellationToken">Stops the query.</param>
   /// <exception cref="Platforms.ExternalFailureException">Git could not open or read the clone.</exception>
   public async Task<bool> TouchesDiffAsync(FileSide side, string path, int startLine, int endLine, CancellationToken cancellationToken) =>
      (await _facts.FactAsync(new FactKey.PinDiff(_pin.MergeBase, _pin.Head, side, path), cancellationToken)).Touches(startLine, endLine);
}

/// <summary>A failed check of the lines that a passage or a code reference names.</summary>
/// <param name="Property">The property of the input lines that the check concerns: <c>path</c>, <c>startLine</c> or <c>endLine</c>.</param>
/// <param name="Text">What failed, for Claude.</param>
internal sealed record LineProblem(string Property, string Text);

/// <summary>What the lookup of one file at one side found, which <see cref="StopPassages"/> keeps for every check that names the file.</summary>
internal closed record FileRead
{
   private FileRead()
   {
   }

   /// <summary>A path that names no file at the side, as for a missing path, a directory or a submodule.</summary>
   internal sealed record NotAFile : FileRead;

   /// <summary>A file that git treats as binary.</summary>
   internal sealed record Binary : FileRead;

   /// <summary>A text file of more than <see cref="ReadableFile.MaxSize"/> bytes, whose lines lotsen does not read.</summary>
   /// <param name="Size">The file's size in bytes.</param>
   internal sealed record TooLarge(long Size) : FileRead;

   /// <summary>A readable text file.</summary>
   /// <param name="Lines">The whole file's lines.</param>
   internal sealed record Text(FileLines Lines) : FileRead;
}
