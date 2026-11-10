using Lotsen.AzureDevOps;
using Lotsen.Credentials;
using Lotsen.Git;
using Lotsen.Platforms;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lotsen.Tests;

/// <summary>Covers the port's promises on <see cref="AzureDevOpsPlatform"/>, over a fake Azure DevOps server that numbers its iterations from 1.</summary>
[Trait("Category", "Platform")]
public sealed class AzureDevOpsPlatformContractTests : PlatformContractTests, IAsyncDisposable
{
   private static readonly Uri Remote = new("https://dev.azure.com/contoso/shop/_git/web");

   private readonly FakeAzureDevOps _azureDevOps = new();
   private readonly List<(CommitId Source, CommitId Target)> _iterations = [];
   private readonly HeldPort _refusing = HeldPort.Refusing();
   private AzureDevOpsPlatform _platform;

   public AzureDevOpsPlatformContractTests()
   {
      _platform = PlatformAt(_azureDevOps.Server.BaseUrl, new FixedAccessTokenProvider("lotsen-test-token"));
   }

   protected override IPullRequestPlatform Platform => _platform;

   protected override IReadOnlyList<string> PullRequestForms =>
      [FakeAzureDevOps.PrUrl, "https://DEV.AZURE.COM/contoso/shop/_GIT/web/PullRequest/042/", "https://dev.azure.com/contoso/shop/_git/web/pullrequest/42?_a=files"];

   protected override IReadOnlyList<string> SamePullRequestInOtherForms =>
   [
      "https://contoso.visualstudio.com/shop/_git/web/pullrequest/42",
      "https://dev.azure.com/Contoso/SHOP/_git/Web/pullrequest/42",
      "https://CONTOSO.visualstudio.com/DefaultCollection/Shop/_git/WEB/pullrequest/42",
   ];

   protected override IReadOnlyList<string> OtherPullRequests =>
   [
      "https://dev.azure.com/contoso/shop/_git/web/pullrequest/43",
      "https://dev.azure.com/contoso/shop/_git/api/pullrequest/42",
      "https://dev.azure.com/contoso/store/_git/web/pullrequest/42",
      "https://dev.azure.com/fabrikam/shop/_git/web/pullrequest/42",
      "https://fabrikam.visualstudio.com/shop/_git/web/pullrequest/42",
   ];

   protected override void Push(CommitId source, CommitId target, PullRequestState state = PullRequestState.Active)
   {
      _iterations.Add((source, target));
      _azureDevOps.PullRequest(Remote, [.. _iterations]);
      _azureDevOps.Status(state switch
      {
         PullRequestState.Completed => "completed",
         PullRequestState.Abandoned => "abandoned",
         _ => "active",
      });
   }

   protected override void Fail(ResolveFailure failure)
   {
      switch (failure)
      {
         case ResolveFailure.MissingPullRequest:
            _azureDevOps.Refuse(404, "The pull request was not found.");
            break;
         case ResolveFailure.RefusedToken:
            _azureDevOps.Refuse(401, "The token was refused.");
            break;
         case ResolveFailure.NoToken:
            _platform.Dispose();
            _platform = PlatformAt(_azureDevOps.Server.BaseUrl, new UnavailableAccessTokenProvider());
            break;
         default:
            _platform.Dispose();
            _platform = PlatformAt(new Uri($"http://127.0.0.1:{_refusing.Port}/"), new FixedAccessTokenProvider("lotsen-test-token"));
            break;
      }
   }

   /// <summary>The adapter reads only the status and the remote URL of the pull request, so the properties that it does not read may be missing.</summary>
   [Fact]
   public async Task Pull_request_without_the_properties_that_the_adapter_does_not_read_resolves()
   {
      var (source, target) = (CommitId.Parse(new string('1', 40)), CommitId.Parse(new string('a', 40)));
      Push(source, target);
      _azureDevOps.Omit("pullRequestId", "sourceRefName", "targetRefName", "lastMergeSourceCommit", "lastMergeTargetCommit");

      var resolved = await Platform.ResolveAsync(Platform.Recognize(FakeAzureDevOps.PrUrl)!, TestContext.Current.CancellationToken);

      Assert.Equal(new ResolvedPullRequest(Remote, source, target, 1, PullRequestState.Active), resolved);
   }

   public async ValueTask DisposeAsync()
   {
      _platform.Dispose();
      _refusing.Dispose();
      await _azureDevOps.DisposeAsync();
   }

   private static AzureDevOpsPlatform PlatformAt(Uri server, IAccessTokenProvider tokens) =>
      new(new AzureDevOpsClient(tokens, new AzureDevOpsOptions { ServerOverride = server }, NullLogger<AzureDevOpsClient>.Instance), tokens);

   private sealed class UnavailableAccessTokenProvider : IAccessTokenProvider
   {
      public ValueTask<AccessToken> GetTokenAsync(TimeSpan minimumValidity, CancellationToken cancellationToken) =>
         throw new AccessTokenUnavailableException("The Azure CLI is not signed in. Run 'az login', then try again.");
   }
}
