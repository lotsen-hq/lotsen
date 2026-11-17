using Lotsen.AzureDevOps;
using Lotsen.Credentials;
using Lotsen.Git;
using Lotsen.Platforms;

namespace Lotsen.Tests;

/// <summary>Covers the repository port's promises on <see cref="AzureDevOpsPlatform"/>, over a fake Azure DevOps API.</summary>
[Trait("Category", "Platform")]
public sealed class AzureDevOpsRepositoryPlatformContractTests : RepositoryPlatformContractTests, IAsyncDisposable
{
   private readonly FakeAzureDevOpsRepository _azureDevOps = new();
   private readonly HeldPort _refusing = HeldPort.Refusing();
   private AzureDevOpsPlatform _platform;

   public AzureDevOpsRepositoryPlatformContractTests()
   {
      _platform = FakeAzureDevOpsRepository.PlatformAt(_azureDevOps.Server.BaseUrl, new FixedAccessTokenProvider("lotsen-test-token"));
   }

   protected override IRepositoryPlatform Platform => _platform;

   protected override IReadOnlyList<string> RepositoryForms =>
   [
      FakeAzureDevOpsRepository.Url,
      "https://dev.azure.com/contoso/shop/_git/web/",
      "https://DEV.AZURE.COM/contoso/shop/_GIT/web?path=/README.md&_a=contents#top",
      "https://dev.azure.com/contoso/sh%6Fp/_git/web",
      "git@ssh.dev.azure.com:v3/contoso/shop/web",
      "ssh://git@ssh.dev.azure.com/v3/contoso/shop/web",
      "contoso@vs-ssh.visualstudio.com:v3/contoso/shop/web",
   ];

   protected override IReadOnlyList<string> OtherRepositories =>
   [
      "https://dev.azure.com/contoso/shop/_git/api",
      "https://dev.azure.com/contoso/store/_git/web",
      "https://dev.azure.com/fabrikam/shop/_git/web",
      "https://fabrikam.visualstudio.com/shop/_git/web",
   ];

   protected override IReadOnlyList<string> SameRepositoryForms =>
   [
      "https://contoso.visualstudio.com/shop/_git/web",
      "https://Contoso.VisualStudio.com/DefaultCollection/Shop/_git/WEB",
      "https://dev.azure.com/Contoso/SHOP/_git/Web",
   ];

   protected override string PullRequestOfTheRepository => FakeAzureDevOps.PrUrl;

   protected override string AtRef(string @ref) => $"{FakeAzureDevOpsRepository.Url}?version=GB{@ref}";

   protected override void Arrange(string defaultBranch, IReadOnlyDictionary<string, CommitId> branches, IReadOnlyDictionary<string, CommitId> tags, IReadOnlyList<CommitId> commits)
   {
      _azureDevOps.Repository(defaultBranch);
      foreach (var (name, commit) in branches)
      {
         _azureDevOps.Branch(name, commit);
      }

      foreach (var (name, commit) in tags)
      {
         _azureDevOps.Tag(name, commit);
      }

      foreach (var commit in commits)
      {
         _azureDevOps.Commit(commit);
      }
   }

   protected override void Fail(ResolveFailure failure)
   {
      switch (failure)
      {
         case ResolveFailure.MissingRepository:
            _azureDevOps.Refuse(404, "The repository was not found.");
            break;
         case ResolveFailure.RefusedToken:
            _azureDevOps.Refuse(401, "The token was refused.");
            break;
         case ResolveFailure.NoToken:
            _platform.Dispose();
            _platform = FakeAzureDevOpsRepository.PlatformAt(_azureDevOps.Server.BaseUrl, new UnavailableAccessTokenProvider());
            break;
         default:
            _platform.Dispose();
            _platform = FakeAzureDevOpsRepository.PlatformAt(new Uri($"http://127.0.0.1:{_refusing.Port}/"), new FixedAccessTokenProvider("lotsen-test-token"));
            break;
      }
   }

   public async ValueTask DisposeAsync()
   {
      _platform.Dispose();
      _refusing.Dispose();
      await _azureDevOps.DisposeAsync();
   }

   private sealed class UnavailableAccessTokenProvider : IAccessTokenProvider
   {
      public ValueTask<AccessToken> GetTokenAsync(TimeSpan minimumValidity, CancellationToken cancellationToken) =>
         throw new AccessTokenUnavailableException("The Azure CLI is not signed in. Run 'az login', then try again.");
   }
}
