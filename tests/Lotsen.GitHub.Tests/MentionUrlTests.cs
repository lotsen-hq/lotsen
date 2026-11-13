using Lotsen.GitHub;
using Lotsen.Platforms;

namespace Lotsen.Tests;

/// <summary>Covers the web URLs that the GitHub platform builds for the issues and pull requests that a tour's prose mentions.</summary>
[Trait("Category", "GitHub")]
public sealed class MentionUrlTests
{
   private readonly GitHubPlatform _platform = FakeGitHub.Offline();

   [Theory]
   [InlineData("https://github.com/octo-org/hello-world/pull/42", "https://github.com/octo-org/hello-world/issues/1234")]
   [InlineData("https://github.com/Octo-Org/Hello-World/pull/42", "https://github.com/Octo-Org/Hello-World/issues/1234")]
   [InlineData("https://github.com/octo/.github/pull/42", "https://github.com/octo/.github/issues/1234")]
   public void Work_item_url_is_an_issue_in_the_pull_requests_repository(string pullRequest, string expected)
   {
      Assert.Equal(expected, _platform.MentionUrl(new Uri(pullRequest), MentionKind.WorkItem, 1234)?.AbsoluteUri);
   }

   [Theory]
   [InlineData("https://github.com/octo-org/hello-world/pull/42", "https://github.com/octo-org/hello-world/pull/7")]
   [InlineData("https://github.com/Octo-Org/Hello-World/pull/42", "https://github.com/Octo-Org/Hello-World/pull/7")]
   public void Pull_request_url_is_in_the_pull_requests_repository(string pullRequest, string expected)
   {
      Assert.Equal(expected, _platform.MentionUrl(new Uri(pullRequest), MentionKind.PullRequest, 7)?.AbsoluteUri);
   }

   [Theory]
   [InlineData(MentionKind.WorkItem)]
   [InlineData(MentionKind.PullRequest)]
   public void Url_of_another_platform_has_no_mention_url(MentionKind kind)
   {
      Assert.Null(_platform.MentionUrl(new Uri("https://dev.azure.com/contoso/shop/_git/web/pullrequest/42"), kind, 7));
   }
}
