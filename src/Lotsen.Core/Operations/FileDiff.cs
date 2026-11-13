using System.Text.Json.Serialization;
using Lotsen.Git;
using Lotsen.Pinning;

namespace Lotsen.Operations;

/// <summary>A file that differs between the two commits of a tour, as <c>list_changed_files</c> returns it.</summary>
/// <param name="Path">The file's path in the repository. A renamed file has its path at the head commit.</param>
/// <param name="Kind">How the file changed.</param>
/// <param name="OldPath">The path of a renamed file at the base commit, or null for every other kind. The JSON leaves it out when null.</param>
/// <param name="Binary">Whether git treats the file's content as binary on either side. The JSON leaves it out when false.</param>
/// <param name="Large">The file's size when its blob on either side is larger than the large-file size of the list, or null. The JSON leaves it out when null.</param>
/// <param name="LineEndingsOnly">Whether the file's only change is its line endings, as <see cref="FileChange.LineEndingsOnly"/> says. The JSON leaves it out when false.</param>
/// <param name="HeadObjectId">The object ID of the file's entry at the head commit, as <see cref="FileChange.HeadObjectId"/> says, or null for a deleted file. The JSON leaves it out when null.</param>
internal sealed record ChangedFile(
   string Path,
   FileChangeKind Kind,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OldPath = null,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Binary = false,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LargeFile? Large = null,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool LineEndingsOnly = false,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? HeadObjectId = null)
{
   /// <summary>Returns the changed file of a change that git found.</summary>
   /// <param name="change">The change.</param>
   /// <param name="largeSize">The size in bytes above which a blob makes its file large.</param>
   public static ChangedFile Of(FileChange change, long largeSize) =>
      new(change.Path, change.Kind, change.OldPath, change.Binary, change.Size > largeSize ? new LargeFile(change.Size) : null, change.LineEndingsOnly, change.HeadObjectId);
}

/// <summary>The size of a large file.</summary>
/// <param name="Size">The size in bytes of the larger of the file's blobs.</param>
internal sealed record LargeFile(long Size);

/// <summary>
/// One changed region of a file, with the fields of the <c>Change</c> that a <c>diffConfig.override</c> in <c>@codemirror/merge</c> returns
/// (https://cdn.jsdelivr.net/npm/@codemirror/merge@6.12.3/dist/index.d.ts).
/// </summary>
/// <remarks>
/// The offsets index the texts of the <see cref="FileDiff.Text"/> in UTF-16 code units, as JavaScript strings do. Each range covers whole lines with their line breaks. A range with equal ends is an
/// insertion point.
/// </remarks>
/// <param name="FromA">The start of the region in the base text.</param>
/// <param name="ToA">The end of the region in the base text, exclusive.</param>
/// <param name="FromB">The start of the region in the head text.</param>
/// <param name="ToB">The end of the region in the head text, exclusive.</param>
internal sealed record TextChange(int FromA, int ToA, int FromB, int ToB);

/// <summary>A file's diff between two commits, as <c>diff_file</c> returns it. The <c>content</c> property in JSON names the derived type.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "content")]
[JsonDerivedType(typeof(Text), "text")]
[JsonDerivedType(typeof(Binary), "binary")]
[JsonDerivedType(typeof(Large), "large")]
internal abstract record FileDiff
{
   private FileDiff()
   {
   }

   /// <summary>Reads a file's texts at the base and the head and returns them with the regions where they differ.</summary>
   /// <param name="facts">The git facts of the clone, which holds both commits.</param>
   /// <param name="baseFile">The file at the base commit, or null when it does not exist there.</param>
   /// <param name="headFile">The file at the head commit, or null when it does not exist there. Its path may differ from the base file's, as for a renamed file.</param>
   /// <param name="whitespace">How the diff compares whitespace. The texts stay the same either way, and the changes index them.</param>
   /// <param name="cancellationToken">Stops the diff.</param>
   /// <returns>The diff. With one file null, the other file's whole text is one change.</returns>
   /// <remarks>Each file must be a <see cref="ReadableFile"/>, because the diff reads its whole text.</remarks>
   /// <exception cref="Platforms.ExternalFailureException">Git could not open or read the clone, as when the remote cannot be reached to download the content.</exception>
   public static async Task<Text> ReadTextAsync(IFactScope facts, FileBlob? baseFile, FileBlob? headFile, DiffWhitespace whitespace, CancellationToken cancellationToken)
   {
      FactKey.Lines? baseKey = baseFile is null ? null : new(baseFile.Commit, baseFile.Path);
      FactKey.Lines? headKey = headFile is null ? null : new(headFile.Commit, headFile.Path);
      FactKey.Hunks? hunksKey = baseFile is null || headFile is null ? null : new(baseFile.Commit, baseFile.Path, headFile.Commit, headFile.Path, whitespace);
      await facts.ReadAsync([.. new FactKey?[] { baseKey, headKey, hunksKey }.OfType<FactKey>()], cancellationToken);
      var baseLines = baseKey is null ? null : Readable(facts.Fact(baseKey));
      var headLines = headKey is null ? null : Readable(facts.Fact(headKey));
      return Of(baseLines, headLines, hunksKey is null ? [] : Readable(facts.Fact(hunksKey)));
   }

   /// <summary>Returns the diff of a file's lines at the base and the head, with the hunks of git's diff between them.</summary>
   /// <param name="baseLines">The file's lines at the base, or null when it does not exist there.</param>
   /// <param name="headLines">The file's lines at the head, or null when it does not exist there.</param>
   /// <param name="hunks">The hunks of git's diff, which a file on one side alone does not read.</param>
   /// <returns>The diff. With one side null, the other side's whole text is one change.</returns>
   public static Text Of(FileLines? baseLines, FileLines? headLines, IReadOnlyList<LineHunk> hunks)
   {
      if (baseLines is null || headLines is null)
      {
         var length = (baseLines ?? headLines)?.Length ?? 0;
         IReadOnlyList<TextChange> whole = length == 0 ? [] : [baseLines is null ? new TextChange(0, 0, 0, length) : new TextChange(0, length, 0, 0)];
         return new Text(baseLines?.Content, headLines?.Content, whole);
      }

      var changes = new List<TextChange>();
      foreach (var hunk in hunks)
      {
         var (fromA, toA) = baseLines.HunkRange(hunk.BaseStart, hunk.BaseCount);
         var (fromB, toB) = headLines.HunkRange(hunk.HeadStart, hunk.HeadCount);
         changes.Add(new TextChange(fromA, toA, fromB, toB));
      }

      return new Text(baseLines.Content, headLines.Content, changes);
   }

   private static T Readable<T>(T? fact)
      where T : class => fact ?? throw new InvalidOperationException("A file of the diff is not a readable file, which the caller checks first.");

   /// <summary>A file's text at two commits and the regions where they differ.</summary>
   /// <param name="BaseText">The text at the base commit, or null when the file does not exist there.</param>
   /// <param name="HeadText">The text at the head commit, or null when the file does not exist there.</param>
   /// <param name="Changes">The changed regions, in order. A missing text counts as empty.</param>
   internal sealed record Text(string? BaseText, string? HeadText, IReadOnlyList<TextChange> Changes) : FileDiff;

   /// <summary>The diff of a file that git treats as binary on either side. It holds none of the file's content.</summary>
   internal sealed record Binary : FileDiff;

   /// <summary>The diff of a large file without its content.</summary>
   /// <param name="Size">The size in bytes of the larger of the file's blobs.</param>
   internal sealed record Large(long Size) : FileDiff;
}
