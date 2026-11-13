using Lotsen.GitHub;

namespace Lotsen.Tests;

/// <summary>Covers which URLs count as GitHub pull requests, and how they are taken apart, normalized, labeled and compared.</summary>
[Trait("Category", "Api")]
public sealed class PullRequestUrlTests
{
   private readonly GitHubPlatform _platform = FakeGitHub.Offline();

   [Theory]
   [InlineData("https://github.com/octo-org/hello-world/pull/42")]
   [InlineData("https://github.com/octo-org/hello-world/pull/42/")]
   [InlineData("https://github.com/octo-org/hello-world/pull/0042")]
   [InlineData("http://github.com/octo-org/hello-world/pull/42")]
   [InlineData("https://www.github.com/octo-org/hello-world/pull/42")]
   [InlineData("https://GitHub.com/octo-org/hello-world/pull/42")]
   [InlineData("https://github.com:443/octo-org/hello-world/pull/42")]
   [InlineData("https://github.com/octo-org/hello-world/pull/42?w=1")]
   [InlineData("https://github.com/octo-org/hello-world/pull/42#issuecomment-123")]
   [InlineData("https://github.com/octo-org/hello-world/pull/42/files")]
   [InlineData("https://github.com/octo-org/hello-world/pull/42/files/")]
   [InlineData("https://github.com/octo-org/hello-world/pull/42/commits")]
   [InlineData("https://github.com/octo-org/hello-world/pull/42/checks")]
   [InlineData("https://github.com/octo-org/hello-world/pull/42/commits/0123456789abcdef0123456789abcdef01234567")]
   [InlineData("https://github.com/octo-org/hello-world/pull/42/files/0123456..89abcde")]
   [InlineData("https://github.com/octo-org/hello-world/pull/42/files#diff-0123abcd")]
   public void Pull_request_url_is_parsed_normalized_and_labeled(string text)
   {
      Assert.True(GitHubPullRequest.TryParse(text, out var pullRequest));

      Assert.Equal("octo-org", pullRequest.Owner);
      Assert.Equal("hello-world", pullRequest.Repository);
      Assert.Equal(42, pullRequest.Number);
      Assert.Equal("https://github.com/octo-org/hello-world/pull/42", pullRequest.Url.OriginalString);
      Assert.Equal("octo-org/hello-world #42", pullRequest.Label);
   }

   [Theory]
   [InlineData("https://github.com/Octo-Org/Hello-World/pull/7", "Octo-Org", "Hello-World", "https://github.com/Octo-Org/Hello-World/pull/7")]
   [InlineData("https://github.com/octo/.github/pull/7", "octo", ".github", "https://github.com/octo/.github/pull/7")]
   [InlineData("https://github.com/octo/my_repo.js/pull/7", "octo", "my_repo.js", "https://github.com/octo/my_repo.js/pull/7")]
   [InlineData("https://github.com/octo_acme/hello/pull/7", "octo_acme", "hello", "https://github.com/octo_acme/hello/pull/7")]
   [InlineData("https://github.com/o/r/pull/2147483647", "o", "r", "https://github.com/o/r/pull/2147483647")]
   public void Owner_and_repository_keep_their_letter_case_and_characters(string text, string owner, string repository, string normalized)
   {
      Assert.True(GitHubPullRequest.TryParse(text, out var pullRequest));

      Assert.Equal(owner, pullRequest.Owner);
      Assert.Equal(repository, pullRequest.Repository);
      Assert.Equal(normalized, pullRequest.Url.OriginalString);
   }

   [Theory]
   [InlineData(null)]
   [InlineData("")]
   [InlineData("github.com/octo/hello/pull/42")]
   [InlineData("ftp://github.com/octo/hello/pull/42")]
   [InlineData("https://github.com:8443/octo/hello/pull/42")]
   [InlineData("https://user@github.com/octo/hello/pull/42")]
   [InlineData("https://gist.github.com/octo/hello/pull/42")]
   [InlineData("https://api.github.com/octo/hello/pull/42")]
   [InlineData("https://github.example.com/octo/hello/pull/42")]
   [InlineData("https://github.com.evil.example/octo/hello/pull/42")]
   [InlineData("https://gitlab.com/octo/hello/pull/42")]
   [InlineData("https://github.com/octo/hello/pull/0")]
   [InlineData("https://github.com/octo/hello/pull/000")]
   [InlineData("https://github.com/octo/hello/pull/-1")]
   [InlineData("https://github.com/octo/hello/pull/+1")]
   [InlineData("https://github.com/octo/hello/pull/x")]
   [InlineData("https://github.com/octo/hello/pull/42x")]
   [InlineData("https://github.com/octo/hello/pull/99999999999")]
   [InlineData("https://github.com/octo/hello/pull/")]
   [InlineData("https://github.com/octo/hello/pull")]
   [InlineData("https://github.com/octo/hello/pulls/42")]
   [InlineData("https://github.com/octo/hello/issues/42")]
   [InlineData("https://github.com/octo/hello/commit/0123456789abcdef0123456789abcdef01234567")]
   [InlineData("https://github.com/octo/hello")]
   [InlineData("https://github.com/octo/pull/42")]
   [InlineData("https://github.com/octo//hello/pull/42")]
   [InlineData("https://github.com/octo/hello/pull/42/../../issues/42")]
   [InlineData("https://github.com/octo/a%2Fb/pull/42")]
   [InlineData("https://github.com/octo/hello%20world/pull/42")]
   [InlineData("https://github.com/octo/%2E%2E/pull/42")]
   public void Other_url_is_not_a_pull_request(string? text)
   {
      Assert.False(GitHubPullRequest.TryParse(text, out _));
   }

   [Theory]
   [InlineData("https://github.com/octo-org/hello-world/pull/42", "https://github.com/Octo-Org/Hello-World/pull/42", true)]
   [InlineData("https://github.com/octo-org/hello-world/pull/42", "https://github.com/OCTO-ORG/HELLO-WORLD/pull/42", true)]
   [InlineData("https://github.com/octo-org/hello-world/pull/42", "https://github.com/octo-org/hello-world/pull/43", false)]
   [InlineData("https://github.com/octo-org/hello-world/pull/42", "https://github.com/octo-org/hello-there/pull/42", false)]
   [InlineData("https://github.com/octo-org/hello-world/pull/42", "https://github.com/other-org/hello-world/pull/42", false)]
   public void Differently_cased_owner_and_repository_name_the_same_pull_request(string url, string other, bool same)
   {
      Assert.Equal(same, _platform.IsSamePullRequest(new Uri(url), new Uri(other)));
      Assert.Equal(same, _platform.IsSamePullRequest(new Uri(other), new Uri(url)));
   }

   [Fact]
   public void Platform_recognizes_and_labels_a_pull_request_url()
   {
      var url = _platform.Recognize("http://www.github.com/Octo-Org/Hello-World/pull/042/files?w=1#diff");

      Assert.Equal("https://github.com/Octo-Org/Hello-World/pull/42", url?.OriginalString);
      Assert.Equal("Octo-Org/Hello-World #42", _platform.Label(url!));
   }

   [Fact]
   public void Platform_refuses_an_issue_url_and_does_not_label_it()
   {
      Assert.Null(_platform.Recognize("https://github.com/octo/hello/issues/42"));
      Assert.Null(_platform.Label(new Uri("https://github.com/octo/hello/issues/42")));
      Assert.False(_platform.IsSamePullRequest(new Uri("https://github.com/octo/hello/issues/42"), new Uri("https://github.com/octo/hello/pull/42")));
   }

   [Fact]
   public void Platform_has_the_github_id_and_names_the_normalized_form()
   {
      Assert.Equal("github", _platform.Id);
      Assert.Equal(GitHubPlatform.PlatformId, _platform.Id);
      Assert.Contains("https://github.com/{owner}/{repository}/pull/{number}", _platform.ExpectedForms, StringComparison.Ordinal);
   }
}
