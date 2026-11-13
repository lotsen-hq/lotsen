using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lotsen.Git;
using Lotsen.Hosting;

namespace Lotsen.GitHub;

/// <summary>
/// Numbers the iterations of GitHub pull requests, which GitHub does not number, by counting the changes of each pull request's head that it is given.
/// </summary>
/// <remarks>
/// <para>
/// It keeps one iteration record per pull request, <c>github/iterations/&lt;key&gt;.json</c> in the data directory, with the last head and the count. The key is a hash of the owner, the
/// repository and the number in lowercase, so one pull request in any letter case has one record, and the file name holds no character that a file system refuses.
/// </para>
/// <para>
/// A missing or malformed record restarts the numbering at 1, and the next call replaces a malformed one. <see cref="Platforms.ResolvedPullRequest.Iteration"/> states what a restart does to
/// the numbers.
/// </para>
/// </remarks>
internal sealed class GitHubIterationStore
{
   // The gate serializes each read and write of a record, so no count is lost. Every store shares it, because UserOnlyStorage.WriteFileAtomically needs one writer of a path at a time in a
   // process. Only the server resolves pull requests, and the claim lock keeps a second server off its data directory.
   private static readonly Lock Writes = new();

   private readonly DataDirectory _dataDirectory;
   private readonly string _folder;
   private readonly ILogger<GitHubIterationStore> _logger;

   public GitHubIterationStore(DataDirectory dataDirectory, ILogger<GitHubIterationStore> logger)
   {
      _dataDirectory = dataDirectory;
      _folder = Path.Combine(dataDirectory.Path, GitHubPlatform.PlatformId, "iterations");
      _logger = logger;
   }

   /// <summary>Returns the iteration number of the pull request at this head, and stores the head and the number when the head differs from the last one.</summary>
   /// <returns>1 for the first head of the pull request, the last number plus 1 for a head other than the last one, or the last number for the same head.</returns>
   /// <exception cref="IOException">The record could not be opened or written. A record that could not be opened stays as it was.</exception>
   /// <exception cref="UnauthorizedAccessException">The record or its folder could not be opened or written. A record that could not be opened stays as it was.</exception>
   public int IterationNumber(GitHubPullRequest pullRequest, CommitId head)
   {
      var path = PathOf(pullRequest);
      lock (Writes)
      {
         // Creating the folders first also restores their user-only modes, so a folder that lost them does not fail every read.
         UserOnlyStorage.CreateDirectory(_dataDirectory.Path);
         UserOnlyStorage.CreateDirectory(Path.GetDirectoryName(_folder)!);
         UserOnlyStorage.CreateDirectory(_folder);
         var last = Read(path);
         if (last is not null && last.Head == head.Value)
         {
            return last.Count;
         }

         var next = new IterationRecord(head.Value, last is null ? 1 : checked(last.Count + 1));
         UserOnlyStorage.WriteFileAtomically(path, stream => JsonSerializer.Serialize(stream, next, IterationRecordJsonContext.Default.IterationRecord));
         return next.Count;
      }
   }

   /// <summary>Returns the absolute path of the pull request's iteration record.</summary>
   public string PathOf(GitHubPullRequest pullRequest)
   {
      var key = $"{pullRequest.Owner}/{pullRequest.Repository}/{pullRequest.Number}".ToLowerInvariant();
      return Path.Combine(_folder, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".json");
   }

   // Returns the record, or null when it is missing or malformed. Other read errors propagate, because the record may still hold a count that a later call can read.
   private IterationRecord? Read(string path)
   {
      try
      {
         // Sharing delete lets a write on Windows rename over the file while this read holds it open.
         using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
         var record = JsonSerializer.Deserialize(stream, IterationRecordJsonContext.Default.IterationRecord);

         // A record at the highest count cannot be raised for a new head, so it counts as malformed.
         return record is { Count: > 0 and < int.MaxValue } && CommitId.TryParse(record.Head, out var head) && head.Value == record.Head
            ? record
            : Malformed(path, "it holds no head and count of an iteration");
      }
      catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
      {
         return null;
      }
      catch (JsonException exception)
      {
         return Malformed(path, exception.Message);
      }
   }

   private IterationRecord? Malformed(string path, string reason)
   {
      _logger.LogWarning("The iteration record {Path} is malformed, so the pull request's iterations are numbered again from 1: {Reason}", path, reason);
      return null;
   }
}

/// <summary>The content of an iteration record.</summary>
/// <param name="Head">The last head that the store was given, as <see cref="CommitId.Value"/> holds it.</param>
/// <param name="Count">The iteration number of that head, from 1.</param>
internal sealed record IterationRecord(string Head, int Count);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(IterationRecord))]
internal sealed partial class IterationRecordJsonContext : JsonSerializerContext;
