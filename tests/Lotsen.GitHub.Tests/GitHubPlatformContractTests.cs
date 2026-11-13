using Lotsen.Credentials;
using Lotsen.Git;
using Lotsen.GitHub;
using Lotsen.Hosting;
using Lotsen.Platforms;

namespace Lotsen.Tests;

/// <summary>Covers the port's promises on <see cref="GitHubPlatform"/>, over a fake GitHub API, with its iteration records in a temporary data directory.</summary>
[Trait("Category", "Platform")]
public sealed class GitHubPlatformContractTests : PlatformContractTests, IAsyncDisposable
{
   private readonly string _root = Directory.CreateTempSubdirectory("lotsen-tests-").FullName;
   private readonly DataDirectory _dataDirectory;
   private readonly FakeGitHub _gitHub = new();
   private readonly HeldPort _refusing = HeldPort.Refusing();
   private GitHubPlatform _platform;

   public GitHubPlatformContractTests()
   {
      _dataDirectory = new DataDirectory(Path.Combine(_root, "data"));
      _platform = _gitHub.Platform(new FixedAccessTokenProvider("lotsen-test-token"), _dataDirectory);
   }

   protected override IPullRequestPlatform Platform => _platform;

   protected override IReadOnlyList<string> PullRequestForms =>
      [FakeGitHub.PrUrl, "http://www.github.com/octo-org/hello-world/pull/042/files?w=1#diff", "https://github.com/octo-org/hello-world/pull/42/commits/" + new string('1', 40)];

   protected override IReadOnlyList<string> SamePullRequestInOtherForms => ["https://github.com/Octo-Org/Hello-World/pull/42", "https://github.com/OCTO-ORG/hello-world/pull/42/files"];

   protected override IReadOnlyList<string> OtherPullRequests =>
   [
      "https://github.com/octo-org/hello-world/pull/43",
      "https://github.com/octo-org/goodbye-world/pull/42",
      "https://github.com/octocat/hello-world/pull/42",
   ];

   protected override void Push(CommitId source, CommitId target, PullRequestState state = PullRequestState.Active)
   {
      var (gitHubState, merged) = state switch
      {
         PullRequestState.Completed => ("closed", true),
         PullRequestState.Abandoned => ("closed", false),
         _ => ("open", false),
      };
      _gitHub.PullRequest(source, target, gitHubState, merged);
   }

   protected override void Fail(ResolveFailure failure)
   {
      switch (failure)
      {
         case ResolveFailure.MissingPullRequest:
            _gitHub.Refuse(404, "Not Found");
            break;
         case ResolveFailure.RefusedToken:
            _gitHub.Refuse(401, "Bad credentials");
            break;
         case ResolveFailure.NoToken:
            _platform.Dispose();
            _platform = _gitHub.Platform(new UnavailableAccessTokenProvider(), _dataDirectory);
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
