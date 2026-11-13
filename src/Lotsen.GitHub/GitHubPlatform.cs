using Lotsen.Credentials;
using Lotsen.Hosting;
using Lotsen.Platforms;

namespace Lotsen.GitHub;

/// <summary>Provides the pull requests of repositories on github.com, through the REST API as the user of the GitHub CLI's sign-in.</summary>
/// <remarks>
/// GitHub does not number a pull request's iterations, so <see cref="GitHubIterationStore"/> counts them. Git receives the token provider's token through <see cref="GitHubGitCredential"/>.
/// </remarks>
public sealed class GitHubPlatform : IPullRequestPlatform, IDisposable
{
   /// <summary>The platform ID that GitHub targets store.</summary>
   public const string PlatformId = "github";

   private readonly GitHubClient _client;
   private readonly GitHubIterationStore _iterations;

   internal GitHubPlatform(GitHubClient client, GitHubIterationStore iterations, IAccessTokenProvider accessTokens)
   {
      _client = client;
      _iterations = iterations;
      GitCredential = new GitHubGitCredential(accessTokens);
   }

   public string Id => PlatformId;

   public string ExpectedForms => GitHubPullRequest.ExpectedForms;

   public IGitCredential GitCredential { get; }

   /// <summary>Returns a factory that builds the platform from the server's services, for github.com and the user's GitHub CLI sign-in.</summary>
   public static Func<IServiceProvider, IPullRequestPlatform> Factory() => Factory(new GitHubOptions(), new GhCliOptions(), accessTokens: null);

   /// <summary>Returns a factory that builds the platform from the server's services, which hold the data directory for the iteration records.</summary>
   /// <param name="options">How to call the REST API.</param>
   /// <param name="ghCli">How to run the GitHub CLI when <paramref name="accessTokens"/> is null.</param>
   /// <param name="accessTokens">The token provider, or null for the user's GitHub CLI sign-in.</param>
   internal static Func<IServiceProvider, IPullRequestPlatform> Factory(GitHubOptions options, GhCliOptions ghCli, IAccessTokenProvider? accessTokens) => services =>
   {
      var tokens = accessTokens ?? new GhCliTokenProvider(ghCli, services.GetRequiredService<ILogger<GhCliTokenProvider>>());
      return new GitHubPlatform(
         new GitHubClient(tokens, options, services.GetRequiredService<ILogger<GitHubClient>>()),
         new GitHubIterationStore(services.GetRequiredService<DataDirectory>(), services.GetRequiredService<ILogger<GitHubIterationStore>>()),
         tokens);
   };

   public Uri? Recognize(string text) => GitHubPullRequest.TryParse(text, out var pullRequest) ? pullRequest.Url : null;

   public bool IsSamePullRequest(Uri url, Uri other) =>
      GitHubPullRequest.TryParse(url.AbsoluteUri, out var pullRequest) && GitHubPullRequest.TryParse(other.AbsoluteUri, out var otherPullRequest) && pullRequest.IsSame(otherPullRequest);

   public string? Label(Uri url) => GitHubPullRequest.TryParse(url.AbsoluteUri, out var pullRequest) ? pullRequest.Label : null;

   /// <remarks>A work item is an issue in the repository of the tour's pull request.</remarks>
   public Uri? MentionUrl(Uri url, MentionKind kind, int id) => !GitHubPullRequest.TryParse(url.AbsoluteUri, out var pullRequest)
      ? null
      : kind == MentionKind.WorkItem ? pullRequest.IssueUrl(id) : pullRequest.PullRequestUrl(id);

   /// <remarks>
   /// The remote is the base repository's, also for a fork's pull request, which relies on GitHub serving the fork's head commit by its ID there.
   /// That is unverified until a tour of a live fork pull request pins.
   /// The iteration number comes from the pull request's iteration record.
   /// </remarks>
   public async Task<ResolvedPullRequest> ResolveAsync(Uri url, CancellationToken cancellationToken)
   {
      if (!GitHubPullRequest.TryParse(url.AbsoluteUri, out var pullRequest))
      {
         throw new PlatformException($"'{url.AbsoluteUri}' is not a GitHub pull request URL. Expected {GitHubPullRequest.ExpectedForms}.");
      }

      var details = await _client.GetPullRequestAsync(pullRequest, cancellationToken);
      int iteration;
      try
      {
         iteration = _iterations.IterationNumber(pullRequest, details.Head);
      }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
      {
         throw new PlatformException(
            $"lotsen could not store the iteration of the pull request {pullRequest.Url.AbsoluteUri} in its data directory: {exception.Message} Check that you can read and write that file, then try again.",
            exception);
      }

      return new ResolvedPullRequest(details.RemoteUrl, details.Head, details.Base, iteration, StateOf(details));
   }

   public void Dispose() => _client.Dispose();

   // A draft is open, and a state that lotsen does not know leaves the tour unflagged.
   private static PullRequestState StateOf(GitHubPullRequestDetails details) => details.State switch
   {
      "closed" when details.Merged => PullRequestState.Completed,
      "closed" => PullRequestState.Abandoned,
      _ => PullRequestState.Active,
   };
}
