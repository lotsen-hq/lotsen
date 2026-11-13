using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using Lotsen.Json;

namespace Lotsen.Git;

/// <summary>How a file changed between two commits.</summary>
[JsonConverter(typeof(StrictEnumConverter<FileChangeKind>))]
internal enum FileChangeKind
{
   Added,
   Modified,
   Deleted,

   /// <summary>A move to another path, with or without edits, as git's rename detection pairs a deleted file with an added one.</summary>
   Renamed,
}

/// <summary>How a diff compares whitespace.</summary>
[JsonConverter(typeof(StrictEnumConverter<DiffWhitespace>))]
internal enum DiffWhitespace
{
   /// <summary>Every whitespace character counts, line endings included.</summary>
   Compare,

   /// <summary>Whitespace anywhere in a line does not count, so a line that differs from another only in whitespace or in its line ending equals it.</summary>
   Ignore,
}

/// <summary>A file that differs between two commits, with what git finds of its content.</summary>
/// <param name="Path">The file's path in the repository, with <c>/</c> between its parts. A renamed file has its path at the head commit.</param>
/// <param name="Kind">How the file changed.</param>
/// <param name="OldPath">The path of a renamed file at the base commit, or null for every other kind.</param>
/// <param name="Binary">Whether git treats the file's content as binary on either side.</param>
/// <param name="Size">The size in bytes of the larger of the file's blobs, or 0 for a submodule.</param>
/// <param name="LineEndingsOnly">Whether the file's only change is its line endings, as <see cref="GitRepository.GetChangedFilesAsync"/> finds it.</param>
/// <param name="HeadObjectId">
/// The object ID of the file's entry at the head commit, or null for a deleted file. A submodule's names the submodule's commit. It is the ID that <c>git rev-parse &lt;head&gt;:&lt;path&gt;</c>
/// returns, as <c>GitQueryTests.Changed_files_carry_the_object_id_of_their_entry_at_the_head</c> pins.
/// </param>
internal sealed record FileChange(string Path, FileChangeKind Kind, string? OldPath, bool Binary, long Size, bool LineEndingsOnly, string? HeadObjectId);

/// <summary>A path that differs between two commits, with how it changed.</summary>
/// <param name="Path">The file's path, as <see cref="FileChange.Path"/> gives it: a renamed file's path at the head commit, a deleted file's at the base.</param>
/// <param name="Kind">How the file changed.</param>
/// <param name="OldPath">The path of a renamed file at the base commit, or null for every other kind.</param>
internal sealed record ChangedPath(string Path, FileChangeKind Kind, string? OldPath = null);

/// <summary>What a directory entry in a commit's tree holds.</summary>
[JsonConverter(typeof(StrictEnumConverter<RepositoryEntryType>))]
internal enum RepositoryEntryType
{
   /// <summary>A file, executable or not.</summary>
   File,

   Directory,

   /// <summary>A symbolic link, whose blob holds the path it points to.</summary>
   Symlink,

   /// <summary>A submodule, which names a commit of another repository and has no content in this one.</summary>
   Submodule,
}

/// <summary>One entry of a directory in a commit's tree.</summary>
/// <param name="Path">The entry's repository path.</param>
/// <param name="Type">What the entry holds.</param>
internal sealed record RepositoryEntry(string Path, RepositoryEntryType Type);

/// <summary>A file's blob at a commit, with what a caller checks before it reads the content.</summary>
/// <param name="Commit">The commit the file was found at.</param>
/// <param name="Path">The file's path at the commit.</param>
/// <param name="ObjectId">The blob's object ID.</param>
/// <param name="Size">The blob's size in bytes.</param>
/// <param name="Binary">Whether git treats the content as binary, as <see cref="GitRepository.FindFileAsync"/> describes.</param>
internal sealed record FileBlob(CommitId Commit, string Path, string ObjectId, long Size, bool Binary);

/// <summary>One changed region of a file between two commits, in the line numbers of a hunk header of git's patch format, without context lines.</summary>
/// <param name="BaseStart">The first line of the region in the base file, from 1. With a count of 0, the line after which the head's lines go, 0 for the top of the file.</param>
/// <param name="BaseCount">The number of the region's lines in the base file.</param>
/// <param name="HeadStart">The first line of the region in the head file, from 1. With a count of 0, the line after which the base's lines went, 0 for the top of the file.</param>
/// <param name="HeadCount">The number of the region's lines in the head file.</param>
internal sealed record LineHunk(int BaseStart, int BaseCount, int HeadStart, int HeadCount);

/// <summary>
/// Answers queries at given commits from a clone in the <see cref="CloneCache"/>.
/// </summary>
/// <remarks>
/// <para>
/// The clone holds only the file contents that git downloaded before, so a query that reads another has git download it from the remote with the remote's credential. Queries do not take the
/// clone lock, because a fetch only adds objects and moves or deletes branches, and a query names its commits by ID. <c>CloneCacheTests.Query_runs_while_a_fetch_writes_a_pack_into_the_clone</c>
/// pins a query during a fetch.
/// </para>
/// <para>
/// The repository counts as a clone use until it is disposed, so neither <see cref="CloneCache.PruneAsync"/> nor <see cref="CloneCache.MaintainAsync"/> touches the clone meanwhile. Dispose it
/// once its queries are done.
/// </para>
/// </remarks>
internal sealed class GitRepository : IDisposable
{
   private readonly GitRemote _remote;
   private readonly GitRunner _git;
   private readonly IDisposable _use;
   private readonly Lock _contentsLock = new();

   // The contents that the latest lookup read, by object ID, which ReadTextAsync decodes without git. Every caller reads right after its lookup, so the latest one's are enough.
   private Dictionary<string, byte[]> _contents = new(StringComparer.Ordinal);

   internal GitRepository(string path, GitRemote remote, GitRunner git, IDisposable use)
   {
      Path = path;
      _remote = remote;
      _git = git;
      _use = use;
   }

   /// <summary>The absolute path of the clone.</summary>
   public string Path { get; }

   /// <summary>Ends this use of the clone. A second call does nothing.</summary>
   public void Dispose() => _use.Dispose();

   /// <summary>Returns the best common ancestor of two commits.</summary>
   /// <returns>The merge base, or null when the commits share no history.</returns>
   /// <exception cref="GitException">Git failed, as when the clone lacks one of the commits.</exception>
   public async Task<CommitId?> GetMergeBaseAsync(CommitId first, CommitId second, CancellationToken cancellationToken)
   {
      var output = await RunAsync($"Git could not find the merge base of {first} and {second}", ["merge-base", first.Value, second.Value], cancellationToken, successExitCodes: [0, 1]);
      return output.ExitCode == 1 ? null : CommitId.Parse(output.Text.Trim());
   }

   /// <summary>Returns the files that differ between two commits.</summary>
   /// <param name="from">The base commit.</param>
   /// <param name="to">The head commit.</param>
   /// <param name="cancellationToken">Stops the query.</param>
   /// <returns>
   /// The changed files, in git's path order. A moved file is one renamed file when git's rename detection pairs its two paths. A file is binary when <see cref="FindFileAsync"/> would find either of its blobs binary.
   /// A modified or renamed text file whose mode stays is line-endings-only when git, comparing lines without the carriage return before their line feeds, finds no changed line.
   /// </returns>
   /// <remarks>
   /// <para>
   /// Git pairs a deleted file with an added one when at least half of the file is unchanged, the default threshold of <c>-M</c> (https://git-scm.com/docs/git-diff-tree, option <c>-M</c>).
   /// To compare the files and count their lines, git downloads the content of every changed file that the clone lacks, as
   /// <c>GitQueryTests.Changed_files_download_the_content_of_every_changed_file_and_no_other</c> pins. <see cref="DownloadChangedFilesAsync"/> downloads the same contents in advance.
   /// A blob-less clone learns a blob's size only from the blob itself.
   /// </para>
   /// <para>
   /// The line-endings check is a second diff of the same files, which reads the contents that the first one downloaded. With <c>--ignore-cr-at-eol</c> git counts a line break added after the
   /// last line as no change, and a carriage return without a line feed as a change, as <c>GitQueryTests.Changed_files_mark_a_file_whose_only_change_is_its_line_endings</c> pins.
   /// </para>
   /// </remarks>
   /// <exception cref="GitException">Git failed, as when the clone lacks one of the commits.</exception>
   public async Task<IReadOnlyList<FileChange>> GetChangedFilesAsync(CommitId from, CommitId to, CancellationToken cancellationToken)
   {
      var failure = $"Git could not list the changes between {from} and {to}";
      var output = await RunAsync(failure, ChangedFilesDiff(from, to), cancellationToken);

      // With -z each field ends in NUL. Git writes every raw record, then every numstat record, and a renamed file's records hold its old path, then its new path.
      var fields = output.Text.Split('\0');
      var changes = new List<(FileChange File, string[] Blobs, bool NewContentSameMode)>();
      var binaryPaths = new HashSet<string>(StringComparer.Ordinal);
      for (var i = 0; i < fields.Length; i++)
      {
         if (fields[i].StartsWith(':'))
         {
            changes.Add(ReadRawRecord(fields, ref i));
         }
         else if (fields[i].Length > 0 && ReadNumstat(fields, ref i) is { Counts: "-\t-" } binary)
         {
            binaryPaths.Add(binary.Path);
         }
      }

      changes = [.. changes.Select(change => (change.File with { Binary = binaryPaths.Contains(change.File.Path) }, change.Blobs, change.NewContentSameMode))];
      var changedBeyondLineEndings = changes.Any(change => change.NewContentSameMode && !change.File.Binary)
         ? await GetPathsChangedBeyondLineEndingsAsync(from, to, failure, cancellationToken)
         : [];
      var sizes = await GetBlobSizesAsync([.. changes.SelectMany(change => change.Blobs).Distinct()], cancellationToken);
      return
      [
         .. changes.Select(change => change.File with
         {
            Size = change.Blobs.Select(blob => sizes[blob]).DefaultIfEmpty(0).Max(),
            LineEndingsOnly = change.NewContentSameMode && !change.File.Binary && !changedBeyondLineEndings.Contains(change.File.Path),
         }),
      ];
   }

   /// <summary>Returns the paths that differ between two commits and how each changed, without what the files' content tells.</summary>
   /// <param name="from">The base commit.</param>
   /// <param name="to">The head commit.</param>
   /// <param name="cancellationToken">Stops the query.</param>
   /// <returns>The changed paths. A moved file is one renamed path when git's rename detection pairs its two paths, as in <see cref="GetChangedFilesAsync"/>.</returns>
   /// <remarks>
   /// Git downloads only the content of the added and deleted files that the clone lacks, which rename detection compares. It reads no modified file, as
   /// <c>TourRemapTests.Remap_downloads_no_content_of_a_changed_file_that_nothing_in_the_tour_names</c> pins.
   /// </remarks>
   /// <exception cref="GitException">Git failed, as when the clone lacks one of the commits.</exception>
   public async Task<IReadOnlyList<ChangedPath>> GetChangedPathsAsync(CommitId from, CommitId to, CancellationToken cancellationToken)
   {
      var output = await RunAsync($"Git could not list the changes between {from} and {to}", ["diff-tree", "-r", "-z", "-M", "--raw", from.Value, to.Value], cancellationToken);
      var fields = output.Text.Split('\0');
      var changes = new List<ChangedPath>();
      for (var i = 0; i < fields.Length; i++)
      {
         if (fields[i].StartsWith(':'))
         {
            var file = ReadRawRecord(fields, ref i).File;
            changes.Add(new ChangedPath(file.Path, file.Kind, file.OldPath));
         }
      }

      return changes;
   }

   /// <summary>Downloads into the clone the content of every file that differs between two commits, so <see cref="GetChangedFilesAsync"/> and the diffs of those files download nothing.</summary>
   /// <remarks>
   /// It runs the diff of <see cref="GetChangedFilesAsync"/>, which downloads every blob that the diff reads and no other, as
   /// <c>GitQueryTests.Download_of_the_changed_files_fetches_the_content_of_every_changed_file_and_no_other</c> pins. It has the transfer timeout, because it can download far more than one query.
   /// </remarks>
   /// <exception cref="GitException">Git failed or did not finish within <see cref="GitOptions.TransferTimeout"/>.</exception>
   /// <exception cref="Credentials.AccessTokenUnavailableException">The remote asked for authentication, and the remote's credential has none.</exception>
   public async Task DownloadChangedFilesAsync(CommitId from, CommitId to, CancellationToken cancellationToken) =>
      await RunAsync($"Git could not download the changed files between {from} and {to}", ChangedFilesDiff(from, to), cancellationToken, timeout: _git.Options.TransferTimeout);

   /// <summary>Returns the files whose blob the clone lacks, and the paths that name nothing at their commit, with one lookup that downloads nothing.</summary>
   /// <param name="files">The files, each with its commit. The lookup skips a path that does not follow <see cref="RepositoryPath.IsValid"/>.</param>
   /// <param name="cancellationToken">Stops git.</param>
   /// <remarks>A path that names a directory or a submodule is not missing, because the clone holds the trees and git looks up no submodule.</remarks>
   /// <exception cref="GitException">Git failed.</exception>
   public async Task<IReadOnlyList<(CommitId Commit, string Path)>> FindMissingAsync(IReadOnlyList<(CommitId Commit, string Path)> files, CancellationToken cancellationToken)
   {
      List<(CommitId Commit, string Path)> named = [.. files.Where(file => RepositoryPath.IsValid(file.Path)).Distinct()];
      if (named.Count == 0)
      {
         return [];
      }

      var objects = await LookUpAsync(named, lazyFetch: false, "Git could not look up the files in the clone", cancellationToken);
      return [.. named.Where((_, i) => objects[i] is LookedUp.Missing)];
   }

   /// <summary>Downloads into the clone the content of each named file at a commit, so <see cref="FindFileAsync"/> and <see cref="ReadTextAsync"/> of those files download nothing.</summary>
   /// <param name="commit">The commit to look in.</param>
   /// <param name="paths">The files' repository paths. A path that names no file at the commit, as <see cref="FindFileAsync"/> finds one, is skipped.</param>
   /// <param name="cancellationToken">Stops git.</param>
   /// <remarks>
   /// <para>
   /// Git downloads in one fetch every blob that one diff reads, as <c>GitQueryTests.Download_of_named_files_fetches_them_in_one_fetch</c> pins. So the download is the numstat diff of
   /// <see cref="FindFileAsync"/>, with every file at once. It has the transfer timeout, because it can download far more than one query.
   /// </para>
   /// <para>
   /// Git matches a directory's path in a diff or a listing with every file under it, so a listing of the paths, which reads only trees, first keeps the paths that name a file exactly.
   /// <c>GitQueryTests.Download_of_named_files_fetches_exactly_their_contents_and_their_reads_need_no_remote</c> and
   /// <c>GitQueryTests.Download_of_a_directory_and_a_file_in_it_fetches_only_that_file</c> pin this.
   /// </para>
   /// </remarks>
   /// <exception cref="GitException">Git failed or did not finish within its timeout, as when the clone lacks the commit.</exception>
   /// <exception cref="Credentials.AccessTokenUnavailableException">The remote asked for authentication, and the remote's credential has none.</exception>
   public async Task DownloadFilesAsync(CommitId commit, IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
   {
      var named = paths.Where(RepositoryPath.IsValid).ToHashSet(StringComparer.Ordinal);
      if (named.Count == 0)
      {
         return;
      }

      // Each entry is "<mode> <type> <object>\t<path>" and ends in NUL.
      var failure = $"Git could not download the content of the files at {commit}";
      var listing = await RunAsync(failure, ["ls-tree", "-z", commit.Value, "--", .. named], cancellationToken);
      var files = listing.Text.Split('\0', StringSplitOptions.RemoveEmptyEntries)
         .Where(entry => entry[..entry.IndexOf('\t')].Split(' ') is [_, "blob", _])
         .Select(entry => entry[(entry.IndexOf('\t') + 1)..])
         .Where(named.Contains)
         .ToList();
      if (files.Count > 0)
      {
         await RunAsync(failure, ["diff-tree", "-r", "-z", "--numstat", EmptyTree(commit), commit.Value, "--", .. files], cancellationToken, timeout: _git.Options.TransferTimeout);
      }
   }

   /// <summary>Returns the file at a path of a commit, with its size and whether git treats it as binary.</summary>
   /// <param name="commit">The commit to look in.</param>
   /// <param name="path">The file's path in the repository, as <see cref="FileChange.Path"/> gives it.</param>
   /// <param name="cancellationToken">Stops the query.</param>
   /// <returns>
   /// The file, or null when the path names no file at the commit, as for a missing path, a directory or a submodule. A path that <see cref="RepositoryPath.IsValid"/> refuses names no file
   /// either, though git can rename a file to one.
   /// </returns>
   /// <remarks>
   /// <para>
   /// Git treats a blob as binary when a NUL byte appears in its first 8000 bytes, as <c>GitQueryTests.Binary_check_reads_the_first_8000_bytes_on_both_routes</c> pins. No attribute changes that:
   /// the clone is bare, so git reads no <c>.gitattributes</c> from the commits, and <see cref="GitRunner"/> disables the attribute files of the user and the system. The tests
   /// <c>Gitattributes_in_the_commits_do_not_make_a_text_file_binary</c> and <c>Users_attribute_files_do_not_change_the_binary_check_or_the_diffs</c> pin both.
   /// </para>
   /// <para>
   /// The query downloads the blob into the clone when the clone lacks it. It reads the content of a blob of at most <see cref="ReadableFile.LargeSize"/> bytes into memory and checks it the
   /// way git does, and <see cref="ReadTextAsync"/> then decodes that content without git. Above that size git checks the blob.
   /// </para>
   /// </remarks>
   /// <exception cref="GitException">Git failed, as when the clone lacks the commit.</exception>
   public async Task<FileBlob?> FindFileAsync(CommitId commit, string path, CancellationToken cancellationToken) => (await FindFilesAsync([(commit, path)], cancellationToken))[0];

   /// <summary>Returns the file at each path of a commit, as <see cref="FindFileAsync"/> finds one, with the git runs of one lookup.</summary>
   /// <returns>The files, in the order of the paths.</returns>
   /// <exception cref="GitException">Git failed, as when the clone lacks a commit.</exception>
   public async Task<IReadOnlyList<FileBlob?>> FindFilesAsync(IReadOnlyList<(CommitId Commit, string Path)> files, CancellationToken cancellationToken)
   {
      var valid = files.Select((file, index) => (file, index)).Where(entry => RepositoryPath.IsValid(entry.file.Path)).ToList();
      var found = await FindBlobsAsync([.. valid.Select(entry => entry.file)], cancellationToken);
      var results = new FileBlob?[files.Count];
      for (var i = 0; i < valid.Count; i++)
      {
         results[valid[i].index] = found[i];
      }

      return results;
   }

   /// <summary>Returns the files of a change at the two commits that <see cref="GetChangedFilesAsync"/> compared, as <see cref="FindFileAsync"/> finds a file.</summary>
   /// <param name="from">The base commit of the list that holds the change.</param>
   /// <param name="to">The head commit of that list.</param>
   /// <param name="change">The change, from that list.</param>
   /// <param name="cancellationToken">Stops the query.</param>
   /// <returns>
   /// The file at the base, or null for an added file, and the file at the head, or null for a deleted file. Either is null too when its path names no file, as for a submodule. Git gave the
   /// paths, so a path that <see cref="RepositoryPath.IsValid"/> refuses names a file here, as the old path of a rename from <c>old\name.cs</c> does.
   /// </returns>
   /// <exception cref="GitException">Git failed, as when the clone lacks one of the commits.</exception>
   public async Task<(FileBlob? Base, FileBlob? Head)> FindChangedFileAsync(CommitId from, CommitId to, FileChange change, CancellationToken cancellationToken)
   {
      List<(CommitId Commit, string Path)> sides = [];
      if (change.Kind != FileChangeKind.Added)
      {
         sides.Add((from, change.OldPath ?? change.Path));
      }

      if (change.Kind != FileChangeKind.Deleted)
      {
         sides.Add((to, change.Path));
      }

      var found = await FindBlobsAsync(sides, cancellationToken);
      return (change.Kind == FileChangeKind.Added ? null : found[0], change.Kind == FileChangeKind.Deleted ? null : found[^1]);
   }

   private async Task<IReadOnlyList<FileBlob?>> FindBlobsAsync(IReadOnlyList<(CommitId Commit, string Path)> files, CancellationToken cancellationToken)
   {
      if (files.Count == 0)
      {
         return [];
      }

      var failure = $"Git could not read {string.Join(" and ", files.Select(file => $"{file.Path} at {file.Commit}"))}";

      // Git would fetch a submodule's commit, which the remote does not hold, and fail the run, so the first lookup fetches nothing. Git names a submodule then, and a blob that the clone lacks
      // as missing, as it does a path that names nothing. The second lookup downloads the blobs that the clone lacks, and a path that names nothing stays missing.
      var objects = await LookUpAsync(files, lazyFetch: false, failure, cancellationToken);
      var missing = Enumerable.Range(0, files.Count).Where(i => objects[i] is LookedUp.Missing).ToList();
      if (missing.Count > 0)
      {
         var fetched = await LookUpAsync([.. missing.Select(i => files[i])], lazyFetch: true, failure, cancellationToken);
         for (var i = 0; i < missing.Count; i++)
         {
            objects[missing[i]] = fetched[i];
         }
      }

      var small = objects.OfType<LookedUp.Blob>().Where(blob => blob.Size <= ReadableFile.LargeSize).Select(blob => blob.ObjectId).Distinct().ToList();
      var contents = small.Count == 0 ? [] : await ReadBlobsAsync(small, failure, cancellationToken);
      lock (_contentsLock)
      {
         _contents = contents;
      }

      var results = new FileBlob?[files.Count];
      for (var i = 0; i < files.Count; i++)
      {
         if (objects[i] is LookedUp.Blob(var objectId, var size))
         {
            var (commit, path) = files[i];
            var binary = contents.TryGetValue(objectId, out var content) ? IsBinary(content) : await IsBinaryByGitAsync(commit, path, cancellationToken);
            results[i] = new FileBlob(commit, path, objectId, size, binary);
         }
      }

      return results;
   }

   // Returns what each "<commit>:<path>" names. With -Z each record of the input and the output ends in NUL, because a path may hold a newline.
   private async Task<LookedUp[]> LookUpAsync(IReadOnlyList<(CommitId Commit, string Path)> files, bool lazyFetch, string failure, CancellationToken cancellationToken)
   {
      var output = await RunAsync(
         failure, ["cat-file", "-Z", "--batch-check"], cancellationToken, input: string.Concat(files.Select(file => $"{file.Commit.Value}:{file.Path}\0")), lazyFetch: lazyFetch);

      // A record is "<object> <type> <size>", "<object> submodule", or "<name> missing" where the name is the input record.
      var records = output.Text.Split('\0');
      if (records.Length != files.Count + 1)
      {
         throw new GitException($"{failure}: git answered {records.Length - 1} lookups for {files.Count}.");
      }

      return
      [
         .. records[..^1].Select(LookedUp (record) => record.EndsWith(" missing", StringComparison.Ordinal)
            ? new LookedUp.Missing()
            : record.Split(' ') is [var objectId, "blob", var size]
               ? new LookedUp.Blob(objectId, long.Parse(size, NumberStyles.None, CultureInfo.InvariantCulture))
               : new LookedUp.NotAFile()),
      ];
   }

   // Returns each blob's content by its object ID. With -Z each output record is "<object> blob <size>", NUL, the content, NUL.
   private async Task<Dictionary<string, byte[]>> ReadBlobsAsync(IReadOnlyList<string> objectIds, string failure, CancellationToken cancellationToken)
   {
      var output = (await RunAsync(failure, ["cat-file", "-Z", "--batch"], cancellationToken, input: string.Concat(objectIds.Select(objectId => objectId + "\0")))).Output;
      var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
      var at = 0;
      foreach (var objectId in objectIds)
      {
         var headerEnd = Array.IndexOf(output, (byte)0, at);
         if (headerEnd < 0 || Encoding.UTF8.GetString(output, at, headerEnd - at).Split(' ') is not [var header, "blob", var sizeText] || header != objectId)
         {
            throw new GitException($"{failure}: git did not send the content of {objectId}.");
         }

         var size = int.Parse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture);
         contents[objectId] = output[(headerEnd + 1)..(headerEnd + 1 + size)];
         at = headerEnd + 1 + size + 1;
      }

      return contents;
   }

   // Git's binary check when no attribute applies: a NUL byte in the first 8000 bytes, as GitQueryTests.Binary_check_reads_the_first_8000_bytes_on_both_routes pins on both routes.
   private static bool IsBinary(byte[] content) => content.AsSpan(0, Math.Min(content.Length, 8000)).Contains((byte)0);

   // Against the empty tree, the file is an added file, and git counts "-" lines for a binary one as it does in the list of changed files.
   private async Task<bool> IsBinaryByGitAsync(CommitId commit, string path, CancellationToken cancellationToken)
   {
      var counts = await RunAsync($"Git could not read {path} at {commit}", ["diff-tree", "-r", "-z", "--numstat", EmptyTree(commit), commit.Value, "--", path], cancellationToken);
      return counts.Text.Split('\0').Contains($"-\t-\t{path}");
   }

   // What a "<commit>:<path>" lookup found: a blob, something that is not a file, or nothing in the clone.
   private closed record LookedUp
   {
      private LookedUp()
      {
      }

      public sealed record Blob(string ObjectId, long Size) : LookedUp;

      public sealed record NotAFile : LookedUp;

      public sealed record Missing : LookedUp;
   }

   /// <summary>Returns the entries of one directory at a commit, without the entries of its subdirectories.</summary>
   /// <param name="commit">The commit to look in.</param>
   /// <param name="path">The directory's repository path, or null for the root.</param>
   /// <param name="cancellationToken">Stops the query.</param>
   /// <returns>
   /// The entries in git's path order, or null when the path names no directory at the commit, as for a missing path, a file, a submodule or a path that <see cref="RepositoryPath.IsValid"/> refuses.
   /// </returns>
   /// <remarks>
   /// The query reads only trees, which the clone holds, so it downloads nothing, as <c>GitQueryTests.Directory_listing_downloads_no_file_content</c> pins. It has no file sizes, because a
   /// blob-less clone learns a blob's size only from the blob itself.
   /// </remarks>
   /// <exception cref="GitException">Git failed, as when the clone lacks the commit.</exception>
   public async Task<IReadOnlyList<RepositoryEntry>?> ListDirectoryAsync(CommitId commit, string? path, CancellationToken cancellationToken)
   {
      if (path is not null && !RepositoryPath.IsValid(path))
      {
         return null;
      }

      // With a trailing slash git lists what the directory holds, and nothing when the path is not a directory. An empty listing reads as no directory, because git add records no empty one.
      var prefix = path is null ? "" : path + "/";
      string[] arguments = path is null ? ["ls-tree", "-z", commit.Value] : ["ls-tree", "-z", commit.Value, "--", prefix];
      var listing = await RunAsync($"Git could not list {path ?? "the root directory"} at {commit}", arguments, cancellationToken);

      // Each entry is "<mode> <type> <object>\t<path>" and ends in NUL. A symbolic link is a blob with mode 120000, and a submodule is a commit.
      var entries = new List<RepositoryEntry>();
      foreach (var field in listing.Text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
      {
         var tab = field.IndexOf('\t');
         var entryPath = field[(tab + 1)..];
         if (!entryPath.StartsWith(prefix, StringComparison.Ordinal) || entryPath.IndexOf('/', prefix.Length) >= 0)
         {
            continue;
         }

         var type = field[..tab].Split(' ') switch
         {
            [_, "tree", _] => RepositoryEntryType.Directory,
            [_, "commit", _] => RepositoryEntryType.Submodule,
            ["120000", "blob", _] => RepositoryEntryType.Symlink,
            _ => RepositoryEntryType.File,
         };
         entries.Add(new RepositoryEntry(entryPath, type));
      }

      return path is not null && entries.Count == 0 ? null : entries;
   }

   /// <summary>Returns a file's text, decoded as UTF-8 with invalid bytes replaced.</summary>
   /// <remarks>It reads the whole blob into memory, so the caller checks <see cref="FileBlob.Size"/> first.</remarks>
   /// <exception cref="GitException">Git failed, as when the remote cannot be reached to download the content.</exception>
   public async Task<string> ReadTextAsync(FileBlob file, CancellationToken cancellationToken)
   {
      lock (_contentsLock)
      {
         if (_contents.TryGetValue(file.ObjectId, out var read))
         {
            return Encoding.UTF8.GetString(read);
         }
      }

      var content = await RunAsync($"Git could not read {file.Path} at {file.Commit}", ["cat-file", "blob", file.ObjectId], cancellationToken);
      return content.Text;
   }

   /// <summary>Returns the texts of several files by object ID, each decoded as <see cref="ReadTextAsync"/> decodes it, with at most one git run for all of them.</summary>
   /// <remarks>It reads every blob into memory at once, so the caller checks each <see cref="FileBlob.Size"/> first.</remarks>
   /// <exception cref="GitException">Git failed, as when the remote cannot be reached to download the content.</exception>
   public async Task<IReadOnlyDictionary<string, string>> ReadTextsAsync(IReadOnlyCollection<FileBlob> files, CancellationToken cancellationToken)
   {
      Dictionary<string, byte[]> contents;
      lock (_contentsLock)
      {
         contents = _contents;
      }

      var objectIds = files.Select(file => file.ObjectId).Distinct().ToList();
      var unread = objectIds.Where(objectId => !contents.ContainsKey(objectId)).ToList();
      var read = unread.Count == 0
         ? []
         : await ReadBlobsAsync(unread, $"Git could not read {string.Join(" and ", files.Where(file => unread.Contains(file.ObjectId)).Select(file => $"{file.Path} at {file.Commit}"))}", cancellationToken);
      return objectIds.ToDictionary(objectId => objectId, objectId => Encoding.UTF8.GetString(contents.TryGetValue(objectId, out var content) ? content : read[objectId]), StringComparer.Ordinal);
   }

   /// <summary>Returns the regions where a file's lines differ between two commits, comparing every whitespace character.</summary>
   /// <param name="baseFile">The file at the base commit.</param>
   /// <param name="headFile">The file at the head commit. Its path may differ from the base file's, as for a renamed file.</param>
   /// <param name="cancellationToken">Stops the diff.</param>
   /// <returns>The hunks in line order.</returns>
   /// <remarks>Git reads both blobs, but the hunks do not hold their text.</remarks>
   /// <exception cref="GitException">Git failed, as when the remote cannot be reached to download the content.</exception>
   public Task<IReadOnlyList<LineHunk>> DiffLinesAsync(FileBlob baseFile, FileBlob headFile, CancellationToken cancellationToken) =>
      DiffLinesAsync(baseFile, headFile, DiffWhitespace.Compare, cancellationToken);

   /// <summary>Returns the regions where a file's lines differ between two commits.</summary>
   /// <param name="baseFile">The file at the base commit.</param>
   /// <param name="headFile">The file at the head commit. Its path may differ from the base file's, as for a renamed file.</param>
   /// <param name="whitespace">How the diff compares whitespace.</param>
   /// <param name="cancellationToken">Stops the diff.</param>
   /// <returns>The hunks in line order.</returns>
   /// <remarks>Git reads both blobs, but the hunks do not hold their text.</remarks>
   /// <exception cref="GitException">Git failed, as when the remote cannot be reached to download the content.</exception>
   public async Task<IReadOnlyList<LineHunk>> DiffLinesAsync(FileBlob baseFile, FileBlob headFile, DiffWhitespace whitespace, CancellationToken cancellationToken)
   {
      // Git counts a carriage return as whitespace, so --ignore-all-space ignores line endings too. An added blank line still counts as an added line.
      string[] whitespaceFlags = whitespace == DiffWhitespace.Ignore ? ["--ignore-all-space"] : [];
      string[] patchFlags = ["-U0", "--full-index", "--no-color", "--no-ext-diff", "--no-textconv", .. whitespaceFlags];

      // Across two paths git diffs the blobs themselves, so rename detection does not matter, and the flags override the user's diff settings that diff-tree ignores.
      var patch = baseFile.Path == headFile.Path
         ? await RunAsync(
            $"Git could not diff {headFile.Path} between {baseFile.Commit} and {headFile.Commit}",
            ["diff-tree", "-p", "--no-renames", .. patchFlags, baseFile.Commit.Value, headFile.Commit.Value, "--", headFile.Path],
            cancellationToken)
         : await RunAsync(
            $"Git could not diff {baseFile.Path} at {baseFile.Commit} with {headFile.Path} at {headFile.Commit}",
            ["diff", "--inter-hunk-context=0", "--diff-algorithm=myers", .. patchFlags, baseFile.ObjectId, headFile.ObjectId],
            cancellationToken);

      // Only the section whose index line names both blobs counts. A change of the file's type, as to a symbolic link, is two other sections.
      var ownIndexLine = $"index {baseFile.ObjectId}..{headFile.ObjectId}";
      var hunks = new List<LineHunk>();
      var inOwnSection = false;
      foreach (var line in patch.Text.Split('\n'))
      {
         if (line.StartsWith("diff --git ", StringComparison.Ordinal))
         {
            inOwnSection = false;
            continue;
         }

         if (line == ownIndexLine || line.StartsWith(ownIndexLine + " ", StringComparison.Ordinal))
         {
            inOwnSection = true;
            continue;
         }

         // Every line of a hunk's body starts with +, - or a backslash, so a line that starts with "@@ " is a hunk header.
         if (inOwnSection && TryParseHunkHeader(line, out var hunk))
         {
            hunks.Add(hunk);
         }
      }

      return hunks;
   }

   // The diff that lists the changed files. The line counts make git read both blobs of every changed file but a submodule.
   private static string[] ChangedFilesDiff(CommitId from, CommitId to) => ["diff-tree", "-r", "-z", "-M", "--raw", "--numstat", from.Value, to.Value];

   // The empty tree's ID in each object format, as "git hash-object -t tree /dev/null" prints it. Git knows this tree without storing it.
   private static string EmptyTree(CommitId commit) =>
      commit.Value.Length == 40 ? "4b825dc642cb6eb9a060e54bf8d69288fbee4904" : "6ef19b41225c5369f1c104d45d8d85efa9b057b53b14b4b9b939dd74decc5321";

   // Returns the head path of each file with a changed line when git compares lines without the carriage return before their line feeds.
   private async Task<HashSet<string>> GetPathsChangedBeyondLineEndingsAsync(CommitId from, CommitId to, string failure, CancellationToken cancellationToken)
   {
      var output = await RunAsync(failure, ["diff-tree", "-r", "-z", "-M", "--numstat", "--ignore-cr-at-eol", from.Value, to.Value], cancellationToken);
      var fields = output.Text.Split('\0');
      var changed = new HashSet<string>(StringComparer.Ordinal);
      for (var i = 0; i < fields.Length; i++)
      {
         if (fields[i].Length > 0 && ReadNumstat(fields, ref i) is { Counts: not "0\t0" } record)
         {
            changed.Add(record.Path);
         }
      }

      return changed;
   }

   // Reads the raw record that starts at fields[i] and moves i to its last field. The blobs are those of the file's sides that hold a blob.
   private static (FileChange File, string[] Blobs, bool NewContentSameMode) ReadRawRecord(string[] fields, ref int i)
   {
      // A raw record is ":<base mode> <head mode> <base blob> <head blob> <status>", then its paths. An absent side has zeros, and a submodule's mode 160000 names a commit, not a blob.
      var record = fields[i][1..].Split(' ');
      var blobs = new[] { (record[0], record[2]), (record[1], record[3]) }
         .Where(side => side.Item1 is not ("000000" or "160000"))
         .Select(side => side.Item2)
         .ToArray();
      var (path, kind, oldPath) = record[4] switch
      {
         ['R', ..] => (fields[i + 2], FileChangeKind.Renamed, fields[i + 1]),
         "A" => (fields[i + 1], FileChangeKind.Added, null),
         "D" => (fields[i + 1], FileChangeKind.Deleted, null),
         _ => (fields[i + 1], FileChangeKind.Modified, (string?)null),
      };
      var file = new FileChange(path, kind, oldPath, Binary: false, Size: 0, LineEndingsOnly: false, HeadObjectId: record[1] == "000000" ? null : record[3]);
      i += file.OldPath is null ? 1 : 2;
      return (file, blobs, blobs.Length == 2 && record[0] == record[1] && record[2] != record[3]);
   }

   // Reads the numstat record that starts at fields[i] and moves i to its last field. A record is "<added>\t<deleted>\t<path>", or "<added>\t<deleted>\t" and two path fields for a rename.
   // A binary file counts "-" lines.
   private static (string Counts, string Path) ReadNumstat(string[] fields, ref int i)
   {
      var record = fields[i];
      var countsEnd = record.IndexOf('\t', record.IndexOf('\t') + 1);
      return (record[..countsEnd], record.EndsWith('\t') ? fields[i += 2] : record[(countsEnd + 1)..]);
   }

   // Returns the size in bytes of each blob, read from the object headers. Git downloads each blob that the clone lacks.
   private async Task<Dictionary<string, long>> GetBlobSizesAsync(IReadOnlyList<string> blobs, CancellationToken cancellationToken)
   {
      var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
      if (blobs.Count == 0)
      {
         return sizes;
      }

      // Each output line is "<object> blob <size>".
      var output = await RunAsync("Git could not read the sizes of the changed files", ["cat-file", "--batch-check"], cancellationToken, input: string.Join('\n', blobs) + "\n");
      foreach (var line in output.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
      {
         if (line.Split(' ') is [var blob, "blob", var size])
         {
            sizes[blob] = long.Parse(size, NumberStyles.None, CultureInfo.InvariantCulture);
         }
      }

      return blobs.All(sizes.ContainsKey) ? sizes : throw new GitException("Git could not read the sizes of the changed files: the remote did not send every file's content. Try again.");
   }

   private Task<GitOutput> RunAsync(
      string failure,
      IReadOnlyList<string> arguments,
      CancellationToken cancellationToken,
      IReadOnlyList<int>? successExitCodes = null,
      string? input = null,
      TimeSpan? timeout = null,
      bool lazyFetch = true) =>
      _git.RunAsync(
         new GitCall(failure, arguments, timeout ?? _git.Options.QueryTimeout)
         {
            GitDirectory = Path,
            WorkingDirectory = Path,
            Remote = _remote,
            SuccessExitCodes = successExitCodes ?? [0],
            Input = input,
            LazyFetch = lazyFetch,
         },
         cancellationToken);

   // A hunk header reads "@@ -<start>[,<count>] +<start>[,<count>] @@", where a missing count means 1.
   private static bool TryParseHunkHeader(string line, [NotNullWhen(true)] out LineHunk? hunk)
   {
      hunk = null;
      var parts = line.Split(' ');
      if (parts is not ["@@", ['-', ..] baseRange, ['+', ..] headRange, "@@", ..]
         || !TryParseRange(baseRange[1..], out var baseStart, out var baseCount)
         || !TryParseRange(headRange[1..], out var headStart, out var headCount))
      {
         return false;
      }

      hunk = new LineHunk(baseStart, baseCount, headStart, headCount);
      return true;
   }

   private static bool TryParseRange(string text, out int start, out int count)
   {
      count = 1;
      var comma = text.IndexOf(',');
      return int.TryParse(comma < 0 ? text : text[..comma], NumberStyles.None, CultureInfo.InvariantCulture, out start)
         && (comma < 0 || int.TryParse(text[(comma + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out count));
   }
}
