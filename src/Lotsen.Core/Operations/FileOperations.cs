using System.ComponentModel;
using System.Text.Json.Serialization;
using Lotsen.Git;
using Lotsen.Hosting;
using Lotsen.Json;
using Lotsen.Pinning;
using Lotsen.Platforms;
using Lotsen.Tours;

namespace Lotsen.Operations;

/// <summary>What a file operation returns for a large file.</summary>
[JsonConverter(typeof(StrictEnumConverter<LargeFileContent>))]
internal enum LargeFileContent
{
   /// <summary>Only the size of a large file.</summary>
   Withhold,

   /// <summary>The content of a large file, up to <see cref="FileOperations.MaxFileSize"/>.</summary>
   Include,
}

/// <summary>The input of <c>list_changed_files</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ListChangedFilesInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId);

/// <summary>The input of <c>read_file_text</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReadFileTextInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId,
   [property: Description("The side to read the file at: base for the merge base, head for the head.")]
   FileSide Side,
   [property: Description(FileOperations.PathDescription)]
   string Path,
   [property: Description(FileOperations.LargeDescription)]
   LargeFileContent Large = LargeFileContent.Withhold);

/// <summary>The input of <c>diff_file</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DiffFileInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId,
   [property: Description(FileOperations.PathDescription)]
   string Path,
   [property: Description("The file's repository path at the base, for a renamed file: the oldPath that list_changed_files gives it. Then path is its path at the head. Omit it when the path is the same on both sides.")]
   string? OldPath = null,
   [property: Description(FileOperations.LargeDescription)]
   LargeFileContent Large = LargeFileContent.Withhold,
   [property: Description("How the diff compares whitespace: compare, the default, counts every whitespace character, and ignore counts a line that differs from the other side only in whitespace or in its line ending as unchanged. The texts are the same either way.")]
   DiffWhitespace Whitespace = DiffWhitespace.Compare);

/// <summary>The input of <c>list_directory</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ListDirectoryInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId,
   [property: Description("The side to list the directory at: base for the merge base, head for the head.")]
   FileSide Side,
   [property: Description("The directory's repository path, relative, with forward slashes. Omit it for the repository's root directory.")]
   string? Path = null);

/// <summary>The input of <c>mark_file_reviewed</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record MarkFileReviewedInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId,
   [property: Description(FileOperations.ReviewedPathDescription)]
   string Path,
   [property: Description("The file's headObjectId from list_changed_files, or null for a deleted file. The mark counts only while the file at the pinned head has this object ID, or for null, while the file stays deleted.")]
   string? HeadObjectId);

/// <summary>The input of <c>clear_file_reviewed</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ClearFileReviewedInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId,
   [property: Description(FileOperations.ReviewedPathDescription)]
   string Path);

/// <summary>The output of <c>list_changed_files</c>.</summary>
/// <param name="MergeBase">The commit of the base side.</param>
/// <param name="Head">The commit of the head side.</param>
/// <param name="Files">The files that differ between the two commits, in git's path order.</param>
/// <param name="ReviewedPaths">The paths of the files whose review mark counts, in the order of <paramref name="Files"/>.</param>
internal sealed record ChangedFileList(CommitId MergeBase, CommitId Head, IReadOnlyList<ChangedFile> Files, IReadOnlyList<string> ReviewedPaths);

/// <summary>The output of <c>mark_file_reviewed</c> and <c>clear_file_reviewed</c>.</summary>
/// <param name="Marks">Every review mark that the tour stores, in path order, with the marks that no longer count.</param>
internal sealed record ReviewMarkList(IReadOnlyList<ReviewMark> Marks);

/// <summary>The output of <c>list_directory</c>.</summary>
/// <param name="Path">The directory's repository path, or null for the root directory.</param>
/// <param name="Side">The side the directory was listed at.</param>
/// <param name="Commit">The commit that the side names.</param>
/// <param name="Entries">The directory's entries in git's path order, as <see cref="GitRepository.ListDirectoryAsync"/> returns them.</param>
internal sealed record DirectoryListing(string? Path, FileSide Side, CommitId Commit, IReadOnlyList<RepositoryEntry> Entries);

/// <summary>The output of <c>read_file_text</c>. The <c>content</c> property in JSON names the derived type.</summary>
/// <param name="Path">The file's repository path.</param>
/// <param name="Side">The side the file was read at.</param>
/// <param name="Commit">The commit that the side names.</param>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "content")]
[JsonDerivedType(typeof(Decoded), "text")]
[JsonDerivedType(typeof(Binary), "binary")]
[JsonDerivedType(typeof(Large), "large")]
internal abstract record FileText(string Path, FileSide Side, CommitId Commit)
{
   /// <summary>A file's text.</summary>
   /// <param name="Text">The file's text, decoded as <see cref="GitRepository.ReadTextAsync"/> describes.</param>
   internal sealed record Decoded(string Path, FileSide Side, CommitId Commit, string Text) : FileText(Path, Side, Commit);

   /// <summary>A file that git treats as binary, without its content.</summary>
   internal sealed record Binary(string Path, FileSide Side, CommitId Commit) : FileText(Path, Side, Commit);

   /// <summary>A large file without its content.</summary>
   /// <param name="Size">The file's size in bytes.</param>
   internal sealed record Large(string Path, FileSide Side, CommitId Commit, long Size) : FileText(Path, Side, Commit);
}

/// <summary>
/// The file operations: they read a pinned tour's changed files, directories, file texts and file diffs at its two commits, and set and clear the reviewer's review marks on its changed files.
/// </summary>
/// <remarks>
/// A read of a tour that is not pinned is refused with its pin, because without a pin no commit is known. Every path must follow <see cref="RepositoryPath.IsValid"/>, and a path that does not
/// answers not-found, the same as a missing file.
/// </remarks>
internal sealed class FileOperations
{
   public const string PathDescription = "The file's repository path, relative, with forward slashes.";

   public const string LargeDescription = "Whether to return the content of a large file: withhold, the default, returns only its size, and include returns its content.";

   public const string ReviewedPathDescription = "The changed file's repository path, as list_changed_files names it: a renamed file by its path at the head.";

   /// <summary>The size in bytes above which a file is large: its text and diff are withheld unless the caller includes them.</summary>
   public const long LargeFileSize = ReadableFile.LargeSize;

   /// <summary>The largest file in bytes whose text and diff the operations return, even when the caller includes large files: the largest <see cref="ReadableFile"/>.</summary>
   public const long MaxFileSize = ReadableFile.MaxSize;

   private readonly TourWriter _writer;
   private readonly CloneCache _clones;
   private readonly IGitFacts _facts;
   private readonly PullRequestPlatforms _platforms;
   private readonly BackgroundJobs _jobs;
   private readonly RunningDownloads<DownloadKey> _downloads;
   private readonly ILogger<FileOperations> _logger;

   public FileOperations(TourWriter writer, CloneCache clones, IGitFacts facts, PullRequestPlatforms platforms, BackgroundJobs jobs, ILogger<FileOperations> logger)
   {
      _writer = writer;
      _clones = clones;
      _facts = facts;
      _platforms = platforms;
      _jobs = jobs;
      _downloads = new RunningDownloads<DownloadKey>();
      _logger = logger;
   }

   /// <summary>
   /// Returns the tour's changed files, with the marks that <see cref="GitRepository.GetChangedFilesAsync(CommitId, CommitId, bool, CancellationToken)"/> finds with <see cref="LargeFileSize"/>, and
   /// the paths of those whose review mark counts.
   /// </summary>
   /// <remarks>
   /// A review mark counts while its object ID is the file's <see cref="ChangedFile.HeadObjectId"/> at the pinned head, so a head that changes the file clears it. A deleted file's mark, without an
   /// object ID, counts while the file stays deleted. The object IDs come from the same git call as the list.
   /// </remarks>
   public Task<OperationResult<ChangedFileList>> ListChangedFilesAsync(ListChangedFilesInput input, CancellationToken cancellationToken) =>
      OnPinnedFactsAsync(
         input.TourId,
         paths: [],
         async Task<OperationResult<ChangedFileList>> (facts, pin) =>
         {
            var changes = await facts.FactAsync(new FactKey.ChangedFiles(pin.MergeBase, pin.Head), cancellationToken);
            var files = changes.Select(change => ChangedFile.Of(change, LargeFileSize)).ToList();
            var read = _writer.ReadMarks(input.TourId);
            if (read.Value is not { } marks)
            {
               return read.Error!;
            }

            var counted = marks.Select(mark => (mark.Path, mark.HeadObjectId)).ToHashSet();
            return new ChangedFileList(pin.MergeBase, pin.Head, files, [.. files.Where(file => counted.Contains((file.Path, file.HeadObjectId))).Select(file => file.Path)]);
         });

   /// <summary>Sets the reviewer's review mark on a file of the tour, in place of any mark on the same path.</summary>
   /// <remarks>
   /// It neither reads git nor checks that the tour changes the file. A mark on a path or an object ID that the changed-file list does not hold counts for nothing, as
   /// <see cref="ListChangedFilesAsync"/> checks.
   /// </remarks>
   public OperationResult<ReviewMarkList> MarkFileReviewed(Access.Reviewer.Pass pass, MarkFileReviewedInput input)
   {
      if (!RepositoryPath.IsValid(input.Path))
      {
         return OperationError.FileNotFound($"'{input.Path}' is not a file path in the repository. Give {RepositoryPath.Expected}.");
      }

      CommitId? objectId = null;
      if (input.HeadObjectId is not null)
      {
         if (!CommitId.TryParse(input.HeadObjectId, out var parsed))
         {
            return OperationError.Invalid("headObjectId", "give the file's headObjectId from list_changed_files, a full object ID, or null for a deleted file.");
         }

         objectId = parsed;
      }

      var mark = new ReviewMark(input.Path, objectId?.Value);
      return UpdateReviewMarks(
         pass,
         input.TourId,
         marks => marks.Contains(mark) ? marks : [.. marks.Where(other => other.Path != mark.Path).Append(mark).OrderBy(other => other.Path, StringComparer.Ordinal)]);
   }

   /// <summary>Clears the reviewer's review mark on a file of the tour. A file without a mark keeps none.</summary>
   public OperationResult<ReviewMarkList> ClearFileReviewed(Access.Reviewer.Pass pass, ClearFileReviewedInput input)
   {
      if (!RepositoryPath.IsValid(input.Path))
      {
         return OperationError.FileNotFound($"'{input.Path}' is not a file path in the repository. Give {RepositoryPath.Expected}.");
      }

      return UpdateReviewMarks(pass, input.TourId, marks => marks.Any(mark => mark.Path == input.Path) ? [.. marks.Where(mark => mark.Path != input.Path)] : marks);
   }

   /// <summary>Returns the entries of one directory at one side of the tour.</summary>
   /// <remarks>The listing has no sizes and no binary marks, so <see cref="ReadFileTextAsync"/> applies the binary and large-file rules when a file is read.</remarks>
   public Task<OperationResult<DirectoryListing>> ListDirectoryAsync(ListDirectoryInput input, CancellationToken cancellationToken) =>
      OnPinnedRepositoryAsync(
         input.TourId,
         input.Path is null ? [] : [("path", input.Path)],
         async Task<OperationResult<DirectoryListing>> (repository, pin) =>
         {
            var commit = pin.CommitOf(input.Side);
            var entries = await repository.ListDirectoryAsync(commit, input.Path, cancellationToken);
            if (entries is null)
            {
               return OperationError.FileNotFound($"'{input.Path}' is not a directory at the {SideName(input.Side)} of tour {input.TourId}, commit {commit}.");
            }

            return new DirectoryListing(input.Path, input.Side, commit, entries);
         },
         cancellationToken);

   /// <summary>Returns a file's text at one side of the tour.</summary>
   /// <remarks>
   /// A binary file comes without its content. A file above <see cref="LargeFileSize"/> comes with only its size, unless the input includes large files. Then a file above
   /// <see cref="MaxFileSize"/> is refused.
   /// </remarks>
   public Task<OperationResult<FileText>> ReadFileTextAsync(ReadFileTextInput input, CancellationToken cancellationToken) =>
      OnPinnedFactsAsync(
         input.TourId,
         [("path", input.Path)],
         async Task<OperationResult<FileText>> (facts, pin) =>
         {
            var commit = pin.CommitOf(input.Side);
            var file = await facts.FactAsync(new FactKey.Blob(commit, input.Path), cancellationToken);
            if (file is null)
            {
               return OperationError.FileNotFound($"'{input.Path}' is not a file at the {SideName(input.Side)} of tour {input.TourId}, commit {commit}.");
            }

            if (file.Binary)
            {
               return new FileText.Binary(input.Path, input.Side, commit);
            }

            return CheckSize(file.Size, input.Large) switch
            {
               SizeCheck.Withhold => new FileText.Large(input.Path, input.Side, commit, file.Size),
               SizeCheck.Refuse => TooLarge(input.Path, file.Size),
               _ => new FileText.Decoded(input.Path, input.Side, commit, (await facts.FactAsync(new FactKey.Lines(commit, input.Path), cancellationToken))!.Content),
            };
         });

   /// <summary>Returns the diff of a file between the tour's two commits, or across a rename when the input has an old path.</summary>
   /// <remarks>
   /// Across a rename both paths must name a file, the old one at the base and the new one at the head. Otherwise the operation answers not-found. A file that is binary on either side comes
   /// without its content. A file whose larger side is above <see cref="LargeFileSize"/> comes with only that size, unless the input includes large files. Then a file above
   /// <see cref="MaxFileSize"/> is refused.
   /// </remarks>
   public Task<OperationResult<FileDiff>> DiffFileAsync(DiffFileInput input, CancellationToken cancellationToken) =>
      OnPinnedFactsAsync(
         input.TourId,
         input.OldPath is null ? [("path", input.Path)] : [("path", input.Path), ("oldPath", input.OldPath)],
         async Task<OperationResult<FileDiff>> (facts, pin) =>
         {
            FactKey.Blob baseKey = new(pin.MergeBase, input.OldPath ?? input.Path), headKey = new(pin.Head, input.Path);
            await facts.ReadAsync([baseKey, headKey], cancellationToken);
            var (baseFile, headFile) = (facts.Fact(baseKey), facts.Fact(headKey));
            if (input.OldPath is { } oldPath && baseFile is null)
            {
               return OperationError.FileNotFound($"'{oldPath}' is not a file at the base of tour {input.TourId}. Give the oldPath that list_changed_files gives a renamed file.", "oldPath");
            }

            if (input.OldPath is not null && headFile is null)
            {
               return OperationError.FileNotFound($"'{input.Path}' is not a file at the head of tour {input.TourId}.");
            }

            if (baseFile is null && headFile is null)
            {
               return OperationError.FileNotFound($"'{input.Path}' is a file on neither side of tour {input.TourId}.");
            }

            if (baseFile is { Binary: true } || headFile is { Binary: true })
            {
               return new FileDiff.Binary();
            }

            var size = Math.Max(baseFile?.Size ?? 0, headFile?.Size ?? 0);
            return CheckSize(size, input.Large) switch
            {
               SizeCheck.Withhold => new FileDiff.Large(size),
               SizeCheck.Refuse => TooLarge(input.Path, size),
               _ => await FileDiff.ReadTextAsync(facts, baseFile, headFile, input.Whitespace, cancellationToken),
            };
         });

   private OperationResult<ReviewMarkList> UpdateReviewMarks(Access.Reviewer.Pass pass, TourId id, Func<IReadOnlyList<ReviewMark>, IReadOnlyList<ReviewMark>> change)
   {
      var written = _writer.WriteMarks(pass, id, change);
      return written.Value is { } marks ? new ReviewMarkList(marks) : written.Error!;
   }

   private static string SideName(FileSide side) => side == FileSide.Base ? "base" : "head";

   private static SizeCheck CheckSize(long size, LargeFileContent large) =>
      size <= LargeFileSize ? SizeCheck.Read
      : large == LargeFileContent.Withhold ? SizeCheck.Withhold
      : size <= MaxFileSize ? SizeCheck.Read
      : SizeCheck.Refuse;

   private static OperationError TooLarge(string path, long size) =>
      OperationError.Invalid("large", $"'{path}' is {size} bytes, more than the {MaxFileSize} bytes that lotsen returns. Open the file in a clone of the repository instead.");

   // Runs the query on the pinned tour's git facts, after the tour, its pin and the paths pass their checks.
   private Task<OperationResult<T>> OnPinnedFactsAsync<T>(
      TourId id, IReadOnlyList<(string Field, string Path)> paths, Func<IFactScope, TourPin.Pinned, Task<OperationResult<T>>> query)
      where T : class =>
      OnPinnedTourAsync(id, paths, (tour, pin) => OnFactsAsync(tour, pin, facts => query(facts, pin)));

   // Runs the query on the pinned tour's clone, after the tour, its pin and the paths pass their checks.
   private Task<OperationResult<T>> OnPinnedRepositoryAsync<T>(
      TourId id, IReadOnlyList<(string Field, string Path)> paths, Func<GitRepository, TourPin.Pinned, Task<OperationResult<T>>> query, CancellationToken cancellationToken)
      where T : class =>
      OnPinnedTourAsync(id, paths, async (tour, pin) =>
      {
         try
         {
            using var repository = await _clones.OpenAsync(RemoteOf(tour, pin), [pin.MergeBase, pin.Head], FetchPolicy.WhenMissing, cancellationToken);
            return await query(repository, pin);
         }
         catch (ExternalFailureException failure)
         {
            return Unavailable(tour, failure);
         }
      });

   private async Task<OperationResult<T>> OnPinnedTourAsync<T>(
      TourId id, IReadOnlyList<(string Field, string Path)> paths, Func<Tour, TourPin.Pinned, Task<OperationResult<T>>> query)
      where T : class
   {
      var read = _writer.Read(id);
      if (read.Value is not { } tour)
      {
         return read.Error!;
      }

      if (tour.Pin is not TourPin.Pinned pin)
      {
         return OperationError.TourNotPinned(tour);
      }

      if (paths.FirstOrDefault(entry => !RepositoryPath.IsValid(entry.Path)) is ({ } field, var path))
      {
         return OperationError.FileNotFound($"'{path}' is not a file path in the repository. Give {RepositoryPath.Expected}.", field);
      }

      return await query(tour, pin);
   }

   /// <summary>Runs a query on the git facts of a pinned tour, from a fact scope over its clone, which holds both of the tour's commits.</summary>
   /// <param name="tour">The tour, as the caller read it.</param>
   /// <param name="pin">The tour's pin.</param>
   /// <param name="query">The query, which passes its own token to the scope's reads. An external failure inside it becomes the result's error too.</param>
   /// <returns>
   /// The query's result, or <see cref="OperationErrorCode.RepositoryUnavailable"/> with the failure's reason when an <see cref="ExternalFailureException"/> stopped the open or the query, as
   /// for a platform that lotsen does not know, a missing access token or a git failure.
   /// </returns>
   /// <remarks>The scope opens the clone only for a fact that the fact memo lacks, so a query that the memo answers runs no git.</remarks>
   public async Task<OperationResult<T>> OnFactsAsync<T>(Tour tour, TourPin.Pinned pin, Func<IFactScope, Task<OperationResult<T>>> query)
      where T : class
   {
      try
      {
         await using var facts = _facts.Open(RemoteOf(tour, pin), [pin.MergeBase, pin.Head], FetchPolicy.WhenMissing);
         return await query(facts);
      }
      catch (ExternalFailureException failure)
      {
         return Unavailable(tour, failure);
      }
   }

   /// <summary>Downloads into a pinned tour's clone the content of each named file at its side that the clone lacks, so later reads of those files download nothing.</summary>
   /// <param name="tour">The tour, as the caller read it.</param>
   /// <param name="pin">The tour's pin.</param>
   /// <param name="files">The files, each with its side. A path that names no file at its side is skipped, as <see cref="GitRepository.DownloadFilesAsync"/> says.</param>
   /// <param name="cancellationToken">Stops the wait for the download, but not the download.</param>
   /// <remarks>
   /// <para>
   /// A file whose blob and lines the fact memo holds needs no content, so the download skips it, and a call that skips every file runs no git. The download first looks up the other files
   /// without downloading, then downloads those that the clone lacks, in one git run per side, as <see cref="GitRepository.FindMissingAsync"/> and
   /// <see cref="GitRepository.DownloadFilesAsync"/> do.
   /// </para>
   /// <para>
   /// The download is a background job with a clone use of its own, which runs on the server's stop token with <see cref="GitOptions.TransferTimeout"/>. So a download whose caller stopped
   /// waiting still completes, and the caller's next try finds the files in the clone. A file that a download in progress fetches joins that download, as <see cref="RunningDownloads{TKey}"/> says.
   /// </para>
   /// <para>
   /// The reads after the download fetch what it did not, so the download is best effort. An <see cref="ExternalFailureException"/> that stops it is logged, not returned, and each read then
   /// refuses in its own order. Any other exception that the download throws reaches every waiter. Nothing is downloaded once the server is stopping.
   /// </para>
   /// </remarks>
   /// <exception cref="OperationCanceledException">The token was canceled, or the server's stop ended the download.</exception>
   public async Task DownloadFilesAsync(Tour tour, TourPin.Pinned pin, IReadOnlyCollection<(FileSide Side, string Path)> files, CancellationToken cancellationToken)
   {
      files = [.. files.Where(file => !Known(pin.CommitOf(file.Side), file.Path))];
      if (files.Count == 0)
      {
         return;
      }

      TourRemote remote;
      try
      {
         remote = RemoteOf(tour, pin);
      }
      catch (ExternalFailureException)
      {
         // The reads after the download fail on the same remote and refuse with this failure.
         return;
      }

      var (running, started) = _downloads.Claim(files.Select(file => new DownloadKey(remote.Url, pin.CommitOf(file.Side), file.Path)));
      if (started is not null && !_jobs.Run(stopping => DownloadAsync(tour, pin, remote, started, stopping)))
      {
         started.End();
      }

      await RunningDownloads<DownloadKey>.WaitAsync(started is null ? running : [.. running, started], cancellationToken);
   }

   // Runs the download as a job. Any exception but an ExternalFailureException or the stop's cancellation ends the download with that exception, so its waiters rethrow it, and the jobs log it.
   private async Task DownloadAsync(Tour tour, TourPin.Pinned pin, TourRemote remote, RunningDownloads<DownloadKey>.Download download, CancellationToken stopping)
   {
      try
      {
         try
         {
            using var repository = await _clones.OpenAsync(remote, [pin.MergeBase, pin.Head], FetchPolicy.WhenMissing, stopping);
            var missing = await repository.FindMissingAsync([.. download.Keys.Select(key => (key.Commit, key.Path))], stopping);
            foreach (var commit in missing.GroupBy(file => file.Commit))
            {
               await repository.DownloadFilesAsync(commit.Key, [.. commit.Select(file => file.Path)], stopping);
            }
         }
         catch (ExternalFailureException failure)
         {
            _logger.LogWarning("The files that a write of tour {TourId} reads could not be downloaded in advance. {Reason}", tour.Id.Value, failure.Reason);
         }

         download.End();
      }
      catch (Exception exception) when (!stopping.IsCancellationRequested)
      {
         download.End(exception);
         throw;
      }
      finally
      {
         download.Cancel();
      }
   }

   // A file whose blob the memo holds, and whose lines too when it is readable, needs no content from the clone.
   private bool Known(CommitId commit, string path) =>
      _facts.Known.TryGet(new FactKey.Blob(commit, path), out var file) && (!ReadableFile.Is(file) || _facts.Known.TryGet(new FactKey.Lines(commit, path), out _));

   private TourRemote RemoteOf(Tour tour, TourPin.Pinned pin) => TourRemote.OfPin(pin, _platforms.Of(tour.Target).GitCredential);

   private OperationError Unavailable(Tour tour, ExternalFailureException failure)
   {
      _logger.LogWarning("The repository of tour {TourId} could not be read. {Reason}", tour.Id.Value, failure.Reason);
      return OperationError.RepositoryUnavailable(tour.Id, failure.Reason);
   }

   private readonly record struct DownloadKey(Uri RemoteUrl, CommitId Commit, string Path);

   private enum SizeCheck
   {
      Read,
      Withhold,
      Refuse,
   }
}
