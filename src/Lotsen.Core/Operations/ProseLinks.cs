using Lotsen.Prose;
using Lotsen.Tours;

namespace Lotsen.Operations;

/// <summary>Checks the <c>ref:</c> links of an agent session's prose, a reply, an agent message or a synthesis, against the tour's pinned commits.</summary>
internal static class ProseLinks
{
   /// <summary>Checks each link as <see cref="StopPassages.CheckReferencesAsync"/> checks a stop's code references, after one download of the links' files, as <see cref="GitPhase.DownloadAsync"/> does.</summary>
   /// <remarks>Prose with a link relies on the pin, which <paramref name="git"/> records. Prose without a link needs no pin.</remarks>
   /// <param name="git">The write's git phase.</param>
   /// <param name="files">Opens the tour's clone.</param>
   /// <param name="text">The prose, or null for none.</param>
   /// <param name="cancellationToken">Stops git, but not the download, as <see cref="GitPhase.DownloadAsync"/> says.</param>
   /// <returns>The links, or the refusal of the first link that fails a check.</returns>
   public static Task<OperationResult<IReadOnlyList<ProseReference>>> CheckAsync(GitPhase git, FileOperations files, string? text, CancellationToken cancellationToken) =>
      CheckAsync(git, files, text, [], cancellationToken);

   /// <summary>Checks each link as <see cref="CheckAsync(GitPhase, FileOperations, string, CancellationToken)"/> does, with the files that the phase reads after the links in the same download.</summary>
   /// <param name="git">The write's git phase.</param>
   /// <param name="files">Opens the tour's clone.</param>
   /// <param name="text">The prose, or null for none.</param>
   /// <param name="laterReads">The files, each with its side. Prose without a link downloads none of them.</param>
   /// <param name="cancellationToken">Stops git, but not the download, as <see cref="GitPhase.DownloadAsync"/> says.</param>
   /// <returns>The links, or the refusal of the first link that fails a check.</returns>
   public static async Task<OperationResult<IReadOnlyList<ProseReference>>> CheckAsync(
      GitPhase git, FileOperations files, string? text, IReadOnlyList<(FileSide Side, string Path)> laterReads, CancellationToken cancellationToken)
   {
      var prose = ProseReferences.Read(text is null ? [] : [("text", text)]);
      if (prose.Value is not { } references)
      {
         return OperationError.Invalid(prose.Fault!);
      }

      if (references.Count == 0)
      {
         return OperationResult<IReadOnlyList<ProseReference>>.Of(references);
      }

      await git.DownloadAsync(files, [.. references.Select(link => (link.Reference.Side, link.Reference.Path)), .. laterReads]);

      return await git.OnPassagesAsync<IReadOnlyList<ProseReference>>(
         files,
         "text",
         async checks => await checks.CheckReferencesAsync(references, cancellationToken) is { } error ? error : references.ToList());
   }
}
