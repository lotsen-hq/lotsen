using Lotsen.AzureDevOps;
using Lotsen.Credentials;
using Lotsen.Platforms;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lotsen.Tests;

/// <summary>Covers the web URLs that the Azure DevOps platform builds for the work items and pull requests that a tour's prose mentions.</summary>
[Trait("Category", "AzureDevOps")]
public sealed class MentionUrlTests : IDisposable
{
   private readonly AzureDevOpsPlatform _platform;

   public MentionUrlTests()
   {
      var tokens = new FixedAccessTokenProvider("token-one");
      _platform = new AzureDevOpsPlatform(new AzureDevOpsClient(tokens, new AzureDevOpsOptions(), NullLogger<AzureDevOpsClient>.Instance), tokens);
   }

   [Theory]
   [InlineData("https://dev.azure.com/contoso/shop/_git/web/pullrequest/42", "https://dev.azure.com/contoso/shop/_workitems/edit/1234")]
   [InlineData("https://contoso.visualstudio.com/shop/_git/web/pullrequest/42", "https://contoso.visualstudio.com/shop/_workitems/edit/1234")]
   [InlineData("https://Contoso.VisualStudio.com/DefaultCollection/shop/_git/web/pullrequest/42", "https://contoso.visualstudio.com/shop/_workitems/edit/1234")]
   [InlineData("https://dev.azure.com/My%20Org/My%20Shop/_git/web/pullrequest/42", "https://dev.azure.com/My%20Org/My%20Shop/_workitems/edit/1234")]
   public void Work_item_url_is_in_the_pull_requests_project(string pullRequest, string expected)
   {
      Assert.Equal(expected, _platform.MentionUrl(new Uri(pullRequest), MentionKind.WorkItem, 1234)?.AbsoluteUri);
   }

   [Theory]
   [InlineData("https://dev.azure.com/contoso/shop/_git/web/pullrequest/42", "https://dev.azure.com/contoso/shop/_git/web/pullrequest/7")]
   [InlineData("https://contoso.visualstudio.com/shop/_git/web/pullrequest/42", "https://contoso.visualstudio.com/shop/_git/web/pullrequest/7")]
   [InlineData("https://dev.azure.com/contoso/My%20Shop/_git/web%23app/pullrequest/42", "https://dev.azure.com/contoso/My%20Shop/_git/web%23app/pullrequest/7")]
   public void Pull_request_url_is_in_the_pull_requests_repository(string pullRequest, string expected)
   {
      Assert.Equal(expected, _platform.MentionUrl(new Uri(pullRequest), MentionKind.PullRequest, 7)?.AbsoluteUri);
   }

   public void Dispose() => _platform.Dispose();
}
