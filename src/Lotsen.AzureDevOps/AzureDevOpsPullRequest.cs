using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Lotsen.AzureDevOps;

/// <summary>
/// An Azure DevOps pull request, taken apart from its web URL.
/// </summary>
/// <param name="Repository">The pull request's repository, at the host form of the URL.</param>
/// <param name="PullRequestId">The pull request's number.</param>
/// <param name="Url">The normalized URL: the repository's normalized URL followed by <c>/pullrequest/{id}</c>.</param>
internal sealed record AzureDevOpsPullRequest(AzureDevOpsRepository Repository, int PullRequestId, Uri Url)
{
   /// <summary>The URL forms that <see cref="TryParse"/> accepts, for messages.</summary>
   public const string ExpectedForms =
      "https://dev.azure.com/{organization}/{project}/_git/{repository}/pullrequest/{id} or https://{organization}.visualstudio.com/{project}/_git/{repository}/pullrequest/{id}";

   /// <summary>The short name of the pull request, such as <c>contoso/shop/web #42</c>.</summary>
   public string Label => $"{Repository.Label} #{PullRequestId}";

   /// <summary>Returns whether the other pull request is this one.</summary>
   /// <remarks>The repositories compare as <see cref="AzureDevOpsRepository.IsSame"/> says, and the numbers exactly.</remarks>
   public bool IsSame(AzureDevOpsPullRequest other) => PullRequestId == other.PullRequestId && Repository.IsSame(other.Repository);

   /// <summary>Returns whether the text is the HTTPS web URL of an Azure DevOps pull request.</summary>
   public static bool TryParse([NotNullWhen(true)] string? text, [NotNullWhen(true)] out AzureDevOpsPullRequest? pullRequest)
   {
      pullRequest = null;
      if (!AzureDevOpsRepository.TryWebPath(text, out _, out var host, out var organization, out var path)
         || path.Length != 5
         || AzureDevOpsRepository.OfWebPath(host, organization, path) is not { } repository
         || !path[3].Equals("pullrequest", StringComparison.OrdinalIgnoreCase)
         || !int.TryParse(path[4], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
         || id <= 0)
      {
         return false;
      }

      pullRequest = new AzureDevOpsPullRequest(repository, id, repository.PullRequestUrl(id));
      return true;
   }
}
