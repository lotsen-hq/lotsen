using Lotsen.Credentials;
using Lotsen.Git;
using Lotsen.Platforms;

namespace Lotsen.Tests;

/// <summary>Covers the repository port's promises on <see cref="TestPlatform"/>.</summary>
[Trait("Category", "Platform")]
public sealed class TestRepositoryPlatformContractTests : RepositoryPlatformContractTests
{
   private static readonly Uri Remote = new("https://platform.test/shop.git");

   private readonly TestPlatform _platform = new();

   protected override IRepositoryPlatform Platform => _platform;

   protected override IReadOnlyList<string> RepositoryForms =>
      [$"https://{TestPlatform.DefaultHost}/shop", "https://PLATFORM.TEST/shop/", "https://platform.test/shop.git", "https://platform.test/shop?tab=readme"];

   protected override IReadOnlyList<string> OtherRepositories => [$"https://{TestPlatform.DefaultHost}/web"];

   protected override IReadOnlyList<string> SameRepositoryForms => [$"https://{TestPlatform.DefaultHost}/SHOP"];

   protected override string PullRequestOfTheRepository => TestPlatform.PrUrl;

   protected override string AtRef(string @ref) => $"https://{TestPlatform.DefaultHost}/shop/tree/{@ref}";

   protected override void Arrange(string defaultBranch, IReadOnlyDictionary<string, CommitId> branches, IReadOnlyDictionary<string, CommitId> tags, IReadOnlyList<CommitId> commits) =>
      _platform.Repository(Remote, branches[defaultBranch], defaultBranch, branches, tags, commits);

   protected override void Fail(ResolveFailure failure) => _platform.FailRepository(failure switch
   {
      ResolveFailure.NoToken => new AccessTokenUnavailableException(TestGitCredential.Hint),
      ResolveFailure.RefusedToken => new PlatformException($"The test platform refused the token. {TestGitCredential.Hint}"),
      ResolveFailure.MissingRepository => new PlatformException("The test platform has no such repository."),
      _ => new PlatformException("The test platform could not be reached."),
   });
}
