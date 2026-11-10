using Lotsen.AzureDevOps;

namespace Lotsen.Tests;

/// <summary>Covers which URLs count as Azure DevOps pull requests, and how they are taken apart, normalized and labeled.</summary>
[Trait("Category", "Api")]
public sealed class PullRequestUrlTests
{
   [Theory]
   [InlineData("https://dev.azure.com/contoso/shop/_git/web/pullrequest/42", "contoso", "shop", "web", 42, "https://dev.azure.com/contoso/shop/_git/web/pullrequest/42")]
   [InlineData("https://DEV.AZURE.COM/contoso/shop/_GIT/web/PullRequest/42/", "contoso", "shop", "web", 42, "https://dev.azure.com/contoso/shop/_git/web/pullrequest/42")]
   [InlineData("https://dev.azure.com/contoso/shop/_git/web/pullrequest/42?_a=files&path=/x#top", "contoso", "shop", "web", 42, "https://dev.azure.com/contoso/shop/_git/web/pullrequest/42")]
   [InlineData("https://dev.azure.com/contoso/My%20Shop/_git/web%2Dapp/pullrequest/0042", "contoso", "My Shop", "web-app", 42, "https://dev.azure.com/contoso/My%20Shop/_git/web-app/pullrequest/42")]
   [InlineData("https://contoso.visualstudio.com/shop/_git/web/pullrequest/7", "contoso", "shop", "web", 7, "https://contoso.visualstudio.com/shop/_git/web/pullrequest/7")]
   [InlineData("https://Contoso.VisualStudio.com/DefaultCollection/shop/_git/web/pullrequest/7", "contoso", "shop", "web", 7, "https://contoso.visualstudio.com/shop/_git/web/pullrequest/7")]
   public void Pull_request_url_is_parsed_normalized_and_labeled(string text, string organization, string project, string repository, int id, string normalized)
   {
      Assert.True(AzureDevOpsPullRequest.TryParse(text, out var pullRequest));

      Assert.Equal(organization, pullRequest.Organization);
      Assert.Equal(project, pullRequest.Project);
      Assert.Equal(repository, pullRequest.Repository);
      Assert.Equal(id, pullRequest.PullRequestId);
      Assert.Equal(normalized, pullRequest.Url.OriginalString);
      Assert.Equal($"{organization}/{project}/{repository} #{id}", pullRequest.Label);
   }

   [Theory]
   [InlineData(null)]
   [InlineData("")]
   [InlineData("dev.azure.com/contoso/shop/_git/web/pullrequest/42")]
   [InlineData("http://dev.azure.com/contoso/shop/_git/web/pullrequest/42")]
   [InlineData("https://dev.azure.com:8443/contoso/shop/_git/web/pullrequest/42")]
   [InlineData("https://user@dev.azure.com/contoso/shop/_git/web/pullrequest/42")]
   [InlineData("https://azure.com/contoso/shop/_git/web/pullrequest/42")]
   [InlineData("https://evil.example/contoso/shop/_git/web/pullrequest/42")]
   [InlineData("https://dev.azure.com.evil.example/contoso/shop/_git/web/pullrequest/42")]
   [InlineData("https://a.b.visualstudio.com/shop/_git/web/pullrequest/42")]
   [InlineData("https://visualstudio.com/shop/_git/web/pullrequest/42")]
   [InlineData("https://dev.azure.com/contoso/shop/_git/web/pullrequest/0")]
   [InlineData("https://dev.azure.com/contoso/shop/_git/web/pullrequest/-1")]
   [InlineData("https://dev.azure.com/contoso/shop/_git/web/pullrequest/+1")]
   [InlineData("https://dev.azure.com/contoso/shop/_git/web/pullrequest/99999999999")]
   [InlineData("https://dev.azure.com/contoso/shop/_git/web/pullrequest/42/files")]
   [InlineData("https://dev.azure.com/contoso/shop/_git/web/pullrequests")]
   [InlineData("https://dev.azure.com/contoso/shop/_git/web")]
   [InlineData("https://dev.azure.com/contoso/_git/web/pullrequest/42")]
   [InlineData("https://dev.azure.com/contoso/shop//_git/web/pullrequest/42")]
   [InlineData("https://dev.azure.com/contoso/a%2Fb/_git/web/pullrequest/42")]
   [InlineData("https://dev.azure.com/contoso/%20/_git/web/pullrequest/42")]
   [InlineData("https://dev.azure.com/contoso/DefaultCollection/shop/_git/web/pullrequest/42")]
   public void Other_url_is_not_a_pull_request(string? text)
   {
      Assert.False(AzureDevOpsPullRequest.TryParse(text, out _));
   }
}
