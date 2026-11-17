using Lotsen.Credentials;
using Lotsen.Git;
using Lotsen.Platforms;
using Lotsen.Tours;

namespace Lotsen.Tests;

/// <summary>Covers which repository URLs the platform registry takes as platform-free, how it normalizes, labels and compares them, and which it leaves to a platform or refuses.</summary>
[Trait("Category", "Platform")]
public sealed class PlatformFreeRepositoriesTests
{
   private readonly PullRequestPlatforms _platforms = new([new TestPlatform()]);

   [Theory]
   [InlineData("https://git.example.com/team/shop.git", "https://git.example.com/team/shop.git")]
   [InlineData("https://Git.Example.COM/Team/Shop", "https://git.example.com/Team/Shop")]
   [InlineData("https://git.example.com/team/shop/", "https://git.example.com/team/shop")]
   [InlineData("https://git.example.com:443/shop", "https://git.example.com/shop")]
   [InlineData("https://git.example.com:8443/shop", "https://git.example.com:8443/shop")]
   [InlineData("http://127.0.0.1:8080/repo", "http://127.0.0.1:8080/repo")]
   public void Https_url_that_no_platform_recognizes_is_a_target_without_a_platform_at_its_normalized_url(string text, string normalized)
   {
      Assert.Equal(new RepositoryTarget(new Uri(normalized), null, null), _platforms.RecognizeRepository(text));
   }

   /// <summary>Each URL would need a credential, a protocol other than https, or a part that git does not clone from, or it is on a host whose platform does not recognize it.</summary>
   [Theory]
   [InlineData("http://git.example.com/shop")]
   [InlineData("git@git.example.com:team/shop.git")]
   [InlineData("ssh://git@git.example.com/team/shop.git")]
   [InlineData("https://user:s3cr3t@git.example.com/shop")]
   [InlineData("https://s3cr3t@git.example.com/shop")]
   [InlineData("https://git.example.com/shop?ref=main")]
   [InlineData("https://git.example.com/shop#readme")]
   [InlineData("https://git.example.com/")]
   [InlineData("https://git.example.com")]
   [InlineData("https://git.example.com./shop")]
   [InlineData("https://ｇｉｔ.example.com/shop")]
   [InlineData("file:///srv/git/shop")]
   [InlineData("/srv/git/shop")]
   [InlineData("shop")]
   [InlineData(TestPlatform.PrUrl)]
   [InlineData($"https://{TestPlatform.DefaultHost}/shop/settings/hooks")]
   public void Url_that_is_no_public_https_git_url_on_another_host_is_not_recognized(string text)
   {
      Assert.Null(_platforms.RecognizeRepository(text));
   }

   [Theory]
   [InlineData(null, "git.example.com/team/shop.git")]
   [InlineData("v1.2.0", "git.example.com/team/shop.git@v1.2.0")]
   public void Target_without_a_platform_is_labeled_by_its_host_and_path_with_its_ref(string? @ref, string label)
   {
      Assert.Equal(label, _platforms.Label(new RepositoryTarget(new Uri("https://git.example.com/team/shop.git"), @ref, null)));
   }

   [Fact]
   public void Target_on_a_non_default_port_keeps_the_port_in_its_label()
   {
      Assert.Equal("git.example.com:8443/shop", _platforms.Label(new RepositoryTarget(new Uri("https://git.example.com:8443/shop"), null, null)));
   }

   [Theory]
   [InlineData("https://git.example.com/team/shop/", true)]
   [InlineData("https://GIT.example.com/team/shop", true)]
   [InlineData("https://git.example.com/team/shop.git", false)]
   [InlineData("https://git.example.com/Team/shop", false)]
   [InlineData("https://git.example.com:8443/team/shop", false)]
   public void Targets_without_a_platform_name_the_same_repository_when_their_normalized_urls_are_equal(string other, bool same)
   {
      var wanted = _platforms.RecognizeRepository(other)!;

      Assert.Equal(same, _platforms.IsSameRepository(new RepositoryTarget(new Uri("https://git.example.com/team/shop"), "main", null), wanted));
   }

   [Fact]
   public void Target_without_a_platform_never_names_the_same_repository_as_a_platforms_target()
   {
      var url = new Uri("https://git.example.com/shop");

      Assert.False(_platforms.IsSameRepository(new RepositoryTarget(url, null, TestPlatform.DefaultId), new RepositoryTarget(url, null, null)));
      Assert.False(_platforms.IsSameRepository(new RepositoryTarget(url, null, null), new RepositoryTarget(url, null, TestPlatform.DefaultId)));
   }

   [Fact]
   public async Task Target_without_a_platform_gets_the_credential_that_has_no_answer_and_says_that_only_public_repositories_work()
   {
      var credential = _platforms.CredentialOf(new RepositoryTarget(new Uri("https://git.example.com/shop"), null, null))!;

      var refused = await Assert.ThrowsAsync<AccessTokenUnavailableException>(async () => await credential.AnswerAsync(TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));

      Assert.Equal(PlatformFreeRepositories.PrivateRefusal, refused.Reason);
   }
}
