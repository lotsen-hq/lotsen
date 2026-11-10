using Lotsen.Credentials;
using Lotsen.Git;
using Lotsen.Platforms;

namespace Lotsen.Tests;

/// <summary>Covers the port's promises on <see cref="TestPlatform"/>, which numbers its iterations as a platform without native numbers does.</summary>
[Trait("Category", "Platform")]
public sealed class TestPlatformContractTests : PlatformContractTests
{
   private static readonly Uri Remote = new("https://platform.test/shop.git");

   private readonly TestPlatform _platform = new();

   protected override IPullRequestPlatform Platform => _platform;

   protected override IReadOnlyList<string> PullRequestForms => [TestPlatform.PrUrl, "https://PLATFORM.TEST/shop/pull/042/", "https://platform.test/shop/pull/42?tab=files"];

   protected override IReadOnlyList<string> SamePullRequestInOtherForms => [$"https://{TestPlatform.DefaultHost}/SHOP/pull/42"];

   protected override IReadOnlyList<string> OtherPullRequests => [$"https://{TestPlatform.DefaultHost}/shop/pull/43", $"https://{TestPlatform.DefaultHost}/web/pull/42"];

   protected override void Push(CommitId source, CommitId target, PullRequestState state = PullRequestState.Active) => _platform.Push(Remote, source, target, state);

   protected override void Fail(ResolveFailure failure)
   {
      switch (failure)
      {
         case ResolveFailure.NoToken:
            _platform.Answer(_ => Task.FromException<ResolvedPullRequest>(new AccessTokenUnavailableException(TestGitCredential.Hint)));
            break;
         case ResolveFailure.RefusedToken:
            _platform.Fail($"The test platform refused the token. {TestGitCredential.Hint}");
            break;
         case ResolveFailure.MissingPullRequest:
            _platform.Fail("The test platform has no such pull request.");
            break;
         default:
            _platform.Fail("The test platform could not be reached.");
            break;
      }
   }
}
