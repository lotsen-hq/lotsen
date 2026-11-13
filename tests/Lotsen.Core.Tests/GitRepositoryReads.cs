using Lotsen.Git;
using Lotsen.Operations;

namespace Lotsen.Tests;

/// <summary>Reads changed files, files and diffs by path in the shapes that the file operations return, without their size checks, for tests of the git layer.</summary>
internal static class GitRepositoryReads
{
   /// <summary>A large-file size that no test file reaches.</summary>
   public const long NoLargeFiles = long.MaxValue;

   public static Task<IReadOnlyList<ChangedFile>> ListChangedFilesAsync(this GitRepository repository, CommitId from, CommitId to, CancellationToken cancellationToken) =>
      repository.ListChangedFilesAsync(from, to, NoLargeFiles, cancellationToken);

   /// <summary>Returns the changed files as <c>list_changed_files</c> shapes them with the large-file size.</summary>
   public static async Task<IReadOnlyList<ChangedFile>> ListChangedFilesAsync(this GitRepository repository, CommitId from, CommitId to, long largeSize, CancellationToken cancellationToken) =>
      [.. (await repository.GetChangedFilesAsync(from, to, cancellationToken)).Select(change => ChangedFile.Of(change, largeSize))];

   /// <summary>Returns the files without their head object IDs, for comparisons that name only the other properties.</summary>
   public static IEnumerable<ChangedFile> WithoutObjectIds(this IEnumerable<ChangedFile> files) => files.Select(file => file with { HeadObjectId = null });

   /// <summary>Returns the file's text at the commit, or null when the path names no file there.</summary>
   public static async Task<string?> ReadFileAsync(this GitRepository repository, CommitId commit, string path, CancellationToken cancellationToken) =>
      await repository.FindFileAsync(commit, path, cancellationToken) is { } file ? await repository.ReadTextAsync(file, cancellationToken) : null;

   public static Task<FileDiff.Text> DiffFileAsync(this GitRepository repository, CommitId from, CommitId to, string path, CancellationToken cancellationToken) =>
      repository.DiffFileAsync(from, path, to, path, cancellationToken);

   public static Task<FileDiff.Text> DiffFileAsync(this GitRepository repository, CommitId from, string fromPath, CommitId to, string toPath, CancellationToken cancellationToken) =>
      repository.DiffFileAsync(from, fromPath, to, toPath, DiffWhitespace.Compare, cancellationToken);

   /// <summary>Returns the diff that <c>diff_file</c> builds from the files at the two paths.</summary>
   public static async Task<FileDiff.Text> DiffFileAsync(
      this GitRepository repository, CommitId from, string fromPath, CommitId to, string toPath, DiffWhitespace whitespace, CancellationToken cancellationToken)
   {
      var found = await repository.FindFilesAsync([(from, fromPath), (to, toPath)], cancellationToken);
      var (baseFile, headFile) = (found[0], found[1]);
      var baseLines = baseFile is null ? null : new FileLines(await repository.ReadTextAsync(baseFile, cancellationToken));
      var headLines = headFile is null ? null : new FileLines(await repository.ReadTextAsync(headFile, cancellationToken));
      var hunks = baseFile is null || headFile is null ? [] : await repository.DiffLinesAsync(baseFile, headFile, whitespace, cancellationToken);
      return FileDiff.Of(baseLines, headLines, hunks);
   }
}
