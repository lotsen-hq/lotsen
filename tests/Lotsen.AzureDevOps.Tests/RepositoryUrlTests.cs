using Lotsen.AzureDevOps;
using Lotsen.Credentials;
using Lotsen.Platforms;

namespace Lotsen.Tests;

/// <summary>Covers which URLs count as Azure DevOps repositories, and how they are taken apart into a repository and a ref, normalized, labeled and mentioned.</summary>
[Trait("Category", "Api")]
public sealed class RepositoryUrlTests : IDisposable
{
   private readonly AzureDevOpsPlatform _platform = FakeAzureDevOpsRepository.PlatformAt(new Uri("http://127.0.0.1:9/"), new FixedAccessTokenProvider("token-one"));

   [Theory]
   [InlineData("https://dev.azure.com/contoso/shop/_git/web", "contoso", "https://dev.azure.com/contoso/shop/_git/web")]
   [InlineData("https://dev.azure.com/contoso/shop/_git/web/", "contoso", "https://dev.azure.com/contoso/shop/_git/web")]
   [InlineData("https://DEV.AZURE.COM/contoso/shop/_GIT/web", "contoso", "https://dev.azure.com/contoso/shop/_git/web")]
   [InlineData("https://dev.azure.com:443/contoso/shop/_git/web", "contoso", "https://dev.azure.com/contoso/shop/_git/web")]
   [InlineData("https://dev.azure.com/contoso/shop/_git/web?path=/README.md&_a=contents", "contoso", "https://dev.azure.com/contoso/shop/_git/web")]
   [InlineData("https://dev.azure.com/contoso/shop/_git/web#readme", "contoso", "https://dev.azure.com/contoso/shop/_git/web")]
   [InlineData("https://contoso.visualstudio.com/shop/_git/web", "contoso", "https://contoso.visualstudio.com/shop/_git/web")]
   [InlineData("https://Contoso.VisualStudio.com/DefaultCollection/shop/_git/web/", "contoso", "https://contoso.visualstudio.com/shop/_git/web")]
   [InlineData("git@ssh.dev.azure.com:v3/contoso/shop/web", "contoso", "https://dev.azure.com/contoso/shop/_git/web")]
   [InlineData("git@SSH.dev.azure.com:v3/contoso/shop/web", "contoso", "https://dev.azure.com/contoso/shop/_git/web")]
   [InlineData("ssh://git@ssh.dev.azure.com/v3/contoso/shop/web", "contoso", "https://dev.azure.com/contoso/shop/_git/web")]
   [InlineData("ssh://git@ssh.dev.azure.com:22/v3/contoso/shop/web", "contoso", "https://dev.azure.com/contoso/shop/_git/web")]
   [InlineData("contoso@vs-ssh.visualstudio.com:v3/contoso/shop/web", "contoso", "https://dev.azure.com/contoso/shop/_git/web")]
   [InlineData("Contoso@vs-ssh.visualstudio.com:v3/contoso/shop/web", "contoso", "https://dev.azure.com/contoso/shop/_git/web")]
   public void Repository_url_is_parsed_normalized_and_labeled_without_a_ref(string text, string organization, string normalized)
   {
      Assert.True(AzureDevOpsRepository.TryParse(text, out var repository, out var @ref));

      Assert.Null(@ref);
      Assert.Equal((organization, "shop", "web"), (repository.Organization, repository.Project, repository.Name));
      Assert.Equal(normalized, repository.Url.OriginalString);
      Assert.Equal("contoso/shop/web", repository.Label);
   }

   [Theory]
   [InlineData("https://dev.azure.com/contoso/My%20Shop/_git/web%20app", "https://dev.azure.com/contoso/My%20Shop/_git/web%20app")]
   [InlineData("https://dev.azure.com/contoso/My%20Shop/_git/web%2Dapp", "https://dev.azure.com/contoso/My%20Shop/_git/web-app")]
   [InlineData("git@ssh.dev.azure.com:v3/contoso/My%20Shop/web%20app", "https://dev.azure.com/contoso/My%20Shop/_git/web%20app")]
   [InlineData("https://dev.azure.com/My%20Org/shop/_git/web", "https://dev.azure.com/My%20Org/shop/_git/web")]
   public void Names_are_decoded_and_escaped_again_in_the_normalized_url(string text, string normalized)
   {
      Assert.True(AzureDevOpsRepository.TryParse(text, out var repository, out _));

      Assert.Equal(normalized, repository.Url.OriginalString);
   }

   /// <summary>Each name is decoded once, so a percent sign that the URL escapes stays part of the name.</summary>
   [Theory]
   [InlineData("https://dev.azure.com/%2541bc/%2541b/_git/%2541c")]
   [InlineData("git@ssh.dev.azure.com:v3/%2541bc/%2541b/%2541c")]
   [InlineData("%2541bc@vs-ssh.visualstudio.com:v3/%2541bc/%2541b/%2541c")]
   public void Escaped_percent_sign_stays_in_each_name(string text)
   {
      Assert.True(AzureDevOpsRepository.TryParse(text, out var repository, out _));

      Assert.Equal(("%41bc", "%41b", "%41c"), (repository.Organization, repository.Project, repository.Name));
      Assert.Equal("https://dev.azure.com/%2541bc/%2541b/_git/%2541c", repository.Url.OriginalString);
   }

   [Theory]
   [InlineData("?version=GBmain", "main")]
   [InlineData("/?version=GBmain", "main")]
   [InlineData("?version=GBfeature%2Fgreeting", "feature/greeting")]
   [InlineData("?version=GBfeature/greeting", "feature/greeting")]
   [InlineData("?path=/src&version=GTv1.2.0&_a=contents", "v1.2.0")]
   [InlineData("?version=GC0123456789ABCDEF0123456789abcdef01234567", "0123456789abcdef0123456789abcdef01234567")]
   [InlineData("?version=GBfix%231#readme", "fix#1")]
   public void Url_with_a_version_names_the_repository_and_the_ref(string suffix, string expected)
   {
      Assert.True(AzureDevOpsRepository.TryParse("https://dev.azure.com/contoso/shop/_git/web" + suffix, out var repository, out var @ref));

      Assert.Equal("https://dev.azure.com/contoso/shop/_git/web", repository.Url.OriginalString);
      Assert.Equal(expected, @ref);
   }

   [Theory]
   [InlineData(null)]
   [InlineData("")]
   [InlineData("dev.azure.com/contoso/shop/_git/web")]
   [InlineData("http://dev.azure.com/contoso/shop/_git/web")]
   [InlineData("https://dev.azure.com:8443/contoso/shop/_git/web")]
   [InlineData("https://contoso@dev.azure.com/contoso/shop/_git/web")]
   [InlineData("https://azure.com/contoso/shop/_git/web")]
   [InlineData("https://dev.azure.com.evil.example/contoso/shop/_git/web")]
   [InlineData("https://a.b.visualstudio.com/shop/_git/web")]
   [InlineData("https://visualstudio.com/shop/_git/web")]
   [InlineData("https://dev.azure.com/contoso")]
   [InlineData("https://dev.azure.com/contoso/shop")]
   [InlineData("https://dev.azure.com/contoso/shop/_git")]
   [InlineData("https://dev.azure.com/contoso/_git/web")]
   [InlineData("https://dev.azure.com/contoso/shop/_git/web/pullrequest/42")]
   [InlineData("https://dev.azure.com/contoso/shop/_git/web/commit/0123456789abcdef0123456789abcdef01234567")]
   [InlineData("https://dev.azure.com/contoso/shop/_git/web/branches")]
   [InlineData("https://dev.azure.com/contoso/shop/_build/web")]
   [InlineData("https://dev.azure.com/contoso/shop//_git/web")]
   [InlineData("https://dev.azure.com/contoso/a%2Fb/_git/web")]
   [InlineData("https://dev.azure.com/contoso/shop/_git/a%5Cb")]
   [InlineData("https://dev.azure.com/contoso/%20/_git/web")]
   [InlineData("https://dev.azure.com/contoso/%2E%2E/_git/web")]
   [InlineData("https://dev.azure.com/contoso/shop/_git/%2E")]
   [InlineData("https://dev.azure.com/contoso/shop/_git/web%0A")]
   [InlineData("https://dev.azure.com/contoso/DefaultCollection/shop/_git/web")]
   [InlineData("https://dev.azure.com/contoso/shop/_git/web?version=main")]
   [InlineData("https://dev.azure.com/contoso/shop/_git/web?version=GB")]
   [InlineData("https://dev.azure.com/contoso/shop/_git/web?version=GXmain")]
   [InlineData("https://dev.azure.com/contoso/shop/_git/web?version=GC0123456")]
   [InlineData("https://dev.azure.com/contoso/shop/_git/web?version=GCmain")]
   [InlineData("https://dev.azure.com/contoso/shop/_git/web?version=GB%20")]
   [InlineData("https://dev.azure.com/contoso/shop/_git/web?version=GBx%2F..%2F..%2Fheads%2Fmain")]
   [InlineData("https://dev.azure.com/contoso/shop/_git/web?version=GBmain..x")]
   [InlineData("https://dev.azure.com/contoso/shop/_git/web?version=GB.hidden")]
   [InlineData("https://dev.azure.com/contoso/shop/_git/web?version=GBv1.lock")]
   [InlineData("https://dev.azure.com/contoso/shop/_git/web?version=GBa~1")]
   [InlineData("https://dev.azure.com/contoso/shop/_git/web?version=GBa:b")]
   [InlineData("https://dev.azure.com/contoso/shop/_git/web?version=GBa%5Cb")]
   [InlineData("git@ssh.dev.azure.com:contoso/shop/web")]
   [InlineData("git@ssh.dev.azure.com:v2/contoso/shop/web")]
   [InlineData("git@ssh.dev.azure.com:v3/contoso/shop")]
   [InlineData("git@ssh.dev.azure.com:v3/contoso/shop/web/extra")]
   [InlineData("git@ssh.dev.azure.com:v3/contoso/shop/web/")]
   [InlineData("git@ssh.dev.azure.com:v3/contoso//web")]
   [InlineData("git@ssh.dev.azure.com:v3/contoso/../web")]
   [InlineData("git@ssh.dev.azure.com:v3/contoso/a%2Fb/web")]
   [InlineData("git@ssh.dev.azure.com:v3/contoso/shop/web\n")]
   [InlineData(" git@ssh.dev.azure.com:v3/contoso/shop/web")]
   [InlineData("other@ssh.dev.azure.com:v3/contoso/shop/web")]
   [InlineData("git@ssh.dev.azure.com.evil.example:v3/contoso/shop/web")]
   [InlineData("git@dev.azure.com:v3/contoso/shop/web")]
   [InlineData("ssh://git@ssh.dev.azure.com:2222/v3/contoso/shop/web")]
   [InlineData("ssh://git:secret@ssh.dev.azure.com/v3/contoso/shop/web")]
   [InlineData("ssh://ssh.dev.azure.com/v3/contoso/shop/web")]
   [InlineData("ssh://git@ssh.dev.azure.com/v3/contoso/shop/web?x=1")]
   [InlineData("ssh://git@ssh.dev.azure.com/v3/contoso/shop/web#x")]
   [InlineData("git@vs-ssh.visualstudio.com:v3/contoso/shop/web")]
   [InlineData("fabrikam@vs-ssh.visualstudio.com:v3/contoso/shop/web")]
   [InlineData("contoso@vs-ssh.visualstudio.com:contoso/shop/web")]
   public void Other_url_is_not_a_repository(string? text)
   {
      Assert.False(AzureDevOpsRepository.TryParse(text, out _, out _));
   }

   [Fact]
   public void Platform_recognizes_labels_and_mentions_a_repository_url()
   {
      var recognized = _platform.RecognizeRepository("https://dev.azure.com/contoso/My%20Shop/_git/web?version=GBrelease%2F2.0&_a=contents");

      Assert.Equal(new RecognizedRepository(new Uri("https://dev.azure.com/contoso/My%20Shop/_git/web"), "release/2.0"), recognized);
      Assert.Equal("contoso/My Shop/web", _platform.RepositoryLabel(recognized!.Url));
      Assert.Equal("https://dev.azure.com/contoso/My%20Shop/_workitems/edit/7", _platform.RepositoryMentionUrl(recognized.Url, MentionKind.WorkItem, 7)?.AbsoluteUri);
      Assert.Equal("https://dev.azure.com/contoso/My%20Shop/_git/web/pullrequest/7", _platform.RepositoryMentionUrl(recognized.Url, MentionKind.PullRequest, 7)?.AbsoluteUri);
   }

   [Fact]
   public void Mentions_of_a_repository_on_the_visualstudio_host_stay_on_that_host()
   {
      var url = _platform.RecognizeRepository("https://contoso.visualstudio.com/DefaultCollection/shop/_git/web")!.Url;

      Assert.Equal("https://contoso.visualstudio.com/shop/_workitems/edit/7", _platform.RepositoryMentionUrl(url, MentionKind.WorkItem, 7)?.AbsoluteUri);
      Assert.Equal("https://contoso.visualstudio.com/shop/_git/web/pullrequest/7", _platform.RepositoryMentionUrl(url, MentionKind.PullRequest, 7)?.AbsoluteUri);
   }

   [Fact]
   public void Platform_neither_labels_nor_mentions_a_url_with_a_version()
   {
      var url = new Uri("https://dev.azure.com/contoso/shop/_git/web?version=GBmain");

      Assert.Null(_platform.RepositoryLabel(url));
      Assert.Null(_platform.RepositoryMentionUrl(url, MentionKind.WorkItem, 7));
   }

   [Fact]
   public void Platform_names_the_repository_forms()
   {
      Assert.Equal(AzureDevOpsRepository.ExpectedForms, _platform.ExpectedRepositoryForms);
      Assert.StartsWith("https://dev.azure.com/{organization}/{project}/_git/{repository}", _platform.ExpectedRepositoryForms, StringComparison.Ordinal);
      Assert.Contains("?version=GB{branch}", _platform.ExpectedRepositoryForms, StringComparison.Ordinal);
      Assert.Contains("git@ssh.dev.azure.com:v3/{organization}/{project}/{repository}", _platform.ExpectedRepositoryForms, StringComparison.Ordinal);
   }

   public void Dispose() => _platform.Dispose();
}
