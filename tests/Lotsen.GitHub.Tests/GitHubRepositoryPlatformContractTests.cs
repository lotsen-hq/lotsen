using Lotsen.Credentials;
using Lotsen.Git;
using Lotsen.GitHub;
using Lotsen.Hosting;
using Lotsen.Platforms;

namespace Lotsen.Tests;

/// <summary>Covers the repository port's promises on <see cref="GitHubPlatform"/>, over a fake GitHub API.</summary>
[Trait("Category", "Platform")]
public sealed class GitHubRepositoryPlatformContractTests : RepositoryPlatformContractTests, IAsyncDisposable
{
   private readonly string _root = Directory.CreateTempSubdirectory("lotsen-tests-").FullName;
   private readonly DataDirectory _dataDirectory;
   private readonly FakeGitHubRepository _gitHub = new();
   private readonly HeldPort _refusing = HeldPort.Refusing();
   private GitHubPlatform _platform;

   public GitHubRepositoryPlatformContractTests()
   {
      _dataDirectory = new DataDirectory(Path.Combine(_root, "data"));
      _platform = FakeGitHub.PlatformAt(_gitHub.Server.BaseUrl, new FixedAccessTokenProvider("lotsen-test-token"), _dataDirectory);
   }

   protected override IRepositoryPlatform Platform => _platform;

   protected override IReadOnlyList<string> RepositoryForms =>
   [
      FakeGitHubRepository.Url,
      "https://github.com/octo-org/hello-world.git",
      "http://www.github.com/octo-org/hello-world/",
      "https://github.com/octo-org/hello-world?tab=readme-ov-file#install",
      "git@github.com:octo-org/hello-world.git",
      "ssh://git@github.com/octo-org/hello-world.git",
   ];

   protected override IReadOnlyList<string> OtherRepositories =>
      ["https://github.com/octo-org/goodbye-world", "https://github.com/octocat/hello-world"];

   protected override IReadOnlyList<string> SameRepositoryForms => ["https://github.com/Octo-Org/Hello-World"];

   protected override string PullRequestOfTheRepository => FakeGitHub.PrUrl;

   protected override string AtRef(string @ref) => $"{FakeGitHubRepository.Url}/tree/{@ref}";

   protected override void Arrange(string defaultBranch, IReadOnlyDictionary<string, CommitId> branches, IReadOnlyDictionary<string, CommitId> tags, IReadOnlyList<CommitId> commits)
   {
      _gitHub.Repository(defaultBranch);
      foreach (var (name, commit) in branches)
      {
         _gitHub.Branch(name, commit);
      }

      foreach (var (name, commit) in tags)
      {
         _gitHub.Tag(name, commit);
      }

      foreach (var commit in commits)
      {
         _gitHub.Commit(commit);
      }
   }

   protected override void Fail(ResolveFailure failure)
   {
      switch (failure)
      {
         case ResolveFailure.MissingRepository:
            _gitHub.Refuse(404, "Not Found");
            break;
         case ResolveFailure.RefusedToken:
            _gitHub.Refuse(401, "Bad credentials");
            break;
         case ResolveFailure.NoToken:
            _platform.Dispose();
            _platform = FakeGitHub.PlatformAt(_gitHub.Server.BaseUrl, new UnavailableAccessTokenProvider(), _dataDirectory);
            break;
         default:
            _platform.Dispose();
            _platform = FakeGitHub.PlatformAt(new Uri($"http://127.0.0.1:{_refusing.Port}/"), new FixedAccessTokenProvider("lotsen-test-token"), _dataDirectory);
            break;
      }
   }

   public async ValueTask DisposeAsync()
   {
      _platform.Dispose();
      _refusing.Dispose();
      await _gitHub.DisposeAsync();
      TestDirectories.Delete(_root, TimeSpan.FromSeconds(5));
   }

   private sealed class UnavailableAccessTokenProvider : IAccessTokenProvider
   {
      public ValueTask<AccessToken> GetTokenAsync(TimeSpan minimumValidity, CancellationToken cancellationToken) =>
         throw new AccessTokenUnavailableException("The GitHub CLI gave no token for github.com. Run 'gh auth login', then try again.");
   }
}
