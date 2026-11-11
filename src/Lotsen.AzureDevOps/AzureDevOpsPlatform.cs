using Lotsen.Credentials;
using Lotsen.Platforms;

namespace Lotsen.AzureDevOps;

/// <summary>Provides Azure DevOps pull requests, through the REST API as the user that the token provider signs in.</summary>
/// <remarks>Git receives the same token as the REST API, through <see cref="AzureDevOpsGitCredential"/>.</remarks>
public sealed class AzureDevOpsPlatform : IPullRequestPlatform, IDisposable
{
   /// <summary>The platform ID that Azure DevOps targets store.</summary>
   public const string PlatformId = "azureDevOps";

   private readonly AzureDevOpsClient _client;

   internal AzureDevOpsPlatform(AzureDevOpsClient client, IAccessTokenProvider accessTokens)
   {
      _client = client;
      GitCredential = new AzureDevOpsGitCredential(accessTokens);
   }

   public string Id => PlatformId;

   public string ExpectedForms => AzureDevOpsPullRequest.ExpectedForms;

   public IGitCredential GitCredential { get; }

   /// <summary>Returns a factory that builds the platform from the server's services, for the Azure DevOps service and the user's Azure CLI sign-in.</summary>
   public static Func<IServiceProvider, IPullRequestPlatform> Factory() => Factory(new AzureDevOpsOptions(), new AzureCliOptions(), accessTokens: null);

   /// <summary>Returns a factory that builds the platform from the server's services.</summary>
   /// <param name="options">How to call the REST API.</param>
   /// <param name="azureCli">How to run the Azure CLI when <paramref name="accessTokens"/> is null.</param>
   /// <param name="accessTokens">The token provider, or null for the user's Azure CLI sign-in.</param>
   internal static Func<IServiceProvider, IPullRequestPlatform> Factory(AzureDevOpsOptions options, AzureCliOptions azureCli, IAccessTokenProvider? accessTokens) => services =>
   {
      var tokens = accessTokens ?? new AzureCliTokenProvider(azureCli, services.GetRequiredService<TimeProvider>(), services.GetRequiredService<ILogger<AzureCliTokenProvider>>());
      return new AzureDevOpsPlatform(new AzureDevOpsClient(tokens, options, services.GetRequiredService<ILogger<AzureDevOpsClient>>()), tokens);
   };

   public Uri? Recognize(string text) => AzureDevOpsPullRequest.TryParse(text, out var pullRequest) ? pullRequest.Url : null;

   public bool IsSamePullRequest(Uri url, Uri other) =>
      AzureDevOpsPullRequest.TryParse(url.AbsoluteUri, out var pullRequest) && AzureDevOpsPullRequest.TryParse(other.AbsoluteUri, out var otherPullRequest) && pullRequest.IsSame(otherPullRequest);

   public string? Label(Uri url) => AzureDevOpsPullRequest.TryParse(url.AbsoluteUri, out var pullRequest) ? pullRequest.Label : null;

   public Uri? MentionUrl(Uri url, MentionKind kind, int id) => !AzureDevOpsPullRequest.TryParse(url.AbsoluteUri, out var pullRequest)
      ? null
      : kind == MentionKind.WorkItem ? pullRequest.WorkItemUrl(id) : pullRequest.PullRequestUrl(id);

   public async Task<ResolvedPullRequest> ResolveAsync(Uri url, CancellationToken cancellationToken)
   {
      if (!AzureDevOpsPullRequest.TryParse(url.AbsoluteUri, out var pullRequest))
      {
         throw new PlatformException($"'{url.AbsoluteUri}' is not an Azure DevOps pull request URL. Expected {AzureDevOpsPullRequest.ExpectedForms}.");
      }

      try
      {
         var metadata = await _client.GetPullRequestAsync(pullRequest, cancellationToken);
         var iterations = await _client.GetIterationsAsync(pullRequest, cancellationToken);
         if (iterations.Count == 0)
         {
            throw new PlatformException($"Azure DevOps lists no updates of the pull request {pullRequest.Url.AbsoluteUri}, so it has no commits to show.");
         }

         var latest = iterations[^1];
         return new ResolvedPullRequest(metadata.RemoteUrl, latest.SourceCommit, latest.TargetCommit, latest.Id, StateOf(metadata.Status));
      }
      catch (AzureDevOpsException exception)
      {
         // The client's message names the fix and holds no token, so it passes through unchanged.
         throw new PlatformException(exception.Message, exception);
      }
   }

   public void Dispose() => _client.Dispose();

   // A status that lotsen does not know, such as notSet, leaves the tour unflagged.
   private static PullRequestState StateOf(PullRequestStatus status) => status switch
   {
      PullRequestStatus.Completed => PullRequestState.Completed,
      PullRequestStatus.Abandoned => PullRequestState.Abandoned,
      _ => PullRequestState.Active,
   };
}
