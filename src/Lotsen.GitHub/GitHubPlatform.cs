using Lotsen.Credentials;
using Lotsen.Platforms;

namespace Lotsen.GitHub;

/// <summary>Provides the pull requests of repositories on github.com.</summary>
/// <remarks>
/// It recognizes, labels and compares pull request URLs and builds mention URLs without a network call. It neither resolves a pull request nor supplies a git credential, so
/// <see cref="ResolveAsync"/> and <see cref="GitCredential"/> throw <see cref="NotSupportedException"/>.
/// </remarks>
public sealed class GitHubPlatform : IPullRequestPlatform
{
   /// <summary>The platform ID that GitHub targets store.</summary>
   public const string PlatformId = "github";

   internal GitHubPlatform()
   {
   }

   public string Id => PlatformId;

   public string ExpectedForms => GitHubPullRequest.ExpectedForms;

   public IGitCredential GitCredential => throw new NotSupportedException("The GitHub platform supplies no git credential.");

   public Uri? Recognize(string text) => GitHubPullRequest.TryParse(text, out var pullRequest) ? pullRequest.Url : null;

   public bool IsSamePullRequest(Uri url, Uri other) =>
      GitHubPullRequest.TryParse(url.AbsoluteUri, out var pullRequest) && GitHubPullRequest.TryParse(other.AbsoluteUri, out var otherPullRequest) && pullRequest.IsSame(otherPullRequest);

   public string? Label(Uri url) => GitHubPullRequest.TryParse(url.AbsoluteUri, out var pullRequest) ? pullRequest.Label : null;

   /// <remarks>A work item is an issue in the repository of the tour's pull request.</remarks>
   public Uri? MentionUrl(Uri url, MentionKind kind, int id) => !GitHubPullRequest.TryParse(url.AbsoluteUri, out var pullRequest)
      ? null
      : kind == MentionKind.WorkItem ? pullRequest.IssueUrl(id) : pullRequest.PullRequestUrl(id);

   public Task<ResolvedPullRequest> ResolveAsync(Uri url, CancellationToken cancellationToken) => throw new NotSupportedException("The GitHub platform cannot resolve pull requests.");
}
