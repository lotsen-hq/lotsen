using Lotsen.Hosting;
using Lotsen.Platforms;
using Lotsen.Tours;
using Microsoft.Extensions.DependencyInjection;

namespace Lotsen.Tests;

/// <summary>Covers which repository URLs the platforms that the lotsen executable registers recognize, and what a repository of a platform that lotsen does not know gets.</summary>
[Trait("Category", "Platform")]
public sealed class RegisteredPlatformsTests : IAsyncDisposable
{
   private readonly DirectoryInfo _dataDirectory = Directory.CreateTempSubdirectory("lotsen-tests-");
   private readonly ServiceProvider _services;
   private readonly IReadOnlyList<IPullRequestPlatform> _registered;
   private readonly PullRequestPlatforms _platforms;

   public RegisteredPlatformsTests()
   {
      _services = new ServiceCollection().AddLogging().AddSingleton(TimeProvider.System).AddSingleton(new DataDirectory(_dataDirectory.FullName)).BuildServiceProvider();
      _registered = [.. Program.Platforms.Select(factory => factory(_services))];
      _platforms = new PullRequestPlatforms(_registered);
   }

   [Fact]
   public void GitHub_repository_url_is_recognized_with_its_ref()
   {
      Assert.Equal(
         new RepositoryTarget(new Uri("https://github.com/octo-org/hello-world"), "main", "github"),
         _platforms.RecognizeRepository("https://github.com/octo-org/hello-world/tree/main"));
      Assert.Equal(
         new RepositoryTarget(new Uri("https://github.com/octo-org/hello-world"), null, "github"),
         _platforms.RecognizeRepository("git@github.com:octo-org/hello-world.git"));
   }

   [Fact]
   public void Azure_DevOps_repository_url_is_recognized_with_its_ref()
   {
      Assert.Equal(
         new RepositoryTarget(new Uri("https://dev.azure.com/contoso/shop/_git/web"), "main", "azureDevOps"),
         _platforms.RecognizeRepository("https://dev.azure.com/contoso/shop/_git/web?version=GBmain"));
      Assert.Equal(
         new RepositoryTarget(new Uri("https://contoso.visualstudio.com/shop/_git/web"), null, "azureDevOps"),
         _platforms.RecognizeRepository("https://contoso.visualstudio.com/shop/_git/web"));
      Assert.Equal(
         new RepositoryTarget(new Uri("https://dev.azure.com/contoso/shop/_git/web"), null, "azureDevOps"),
         _platforms.RecognizeRepository("git@ssh.dev.azure.com:v3/contoso/shop/web"));
   }

   [Fact]
   public void Expected_repository_forms_name_each_platform_in_registration_order()
   {
      Assert.Equal(
         "https://dev.azure.com/{organization}/{project}/_git/{repository} or https://{organization}.visualstudio.com/{project}/_git/{repository}, optionally followed by "
         + "?version=GB{branch}, ?version=GT{tag} or ?version=GC{commit}, or an SSH remote, git@ssh.dev.azure.com:v3/{organization}/{project}/{repository}, "
         + "ssh://git@ssh.dev.azure.com/v3/{organization}/{project}/{repository} or {organization}@vs-ssh.visualstudio.com:v3/{organization}/{project}/{repository}; or "
         + "https://github.com/{owner}/{repository}, optionally ending in .git or followed by /tree/{ref}, or an SSH remote, git@github.com:{owner}/{repository} or "
         + "ssh://git@github.com/{owner}/{repository}, optionally ending in .git",
         _platforms.ExpectedRepositoryForms);
   }

   [Theory]
   [InlineData("https://github.com/octo-org/hello-world/tree/%20")]
   [InlineData("https://github.com/octo-org/hello-world/tree/x%2F..%2F..%2Fheads%2Fmain")]
   public void GitHub_repository_url_at_an_invalid_ref_is_not_recognized(string text)
   {
      Assert.Null(_platforms.RecognizeRepository(text));
   }

   [Theory]
   [InlineData("https://dev.azure.com/contoso/shop/_git/web?version=GB%20")]
   [InlineData("https://dev.azure.com/contoso/shop/_git/web?version=GBx%2F..%2F..%2Fheads%2Fmain")]
   public void Azure_DevOps_repository_url_at_an_invalid_ref_is_not_recognized(string text)
   {
      Assert.Null(_platforms.RecognizeRepository(text));
   }

   [Fact]
   public void Repository_target_of_an_unknown_platform_reaches_no_platform_but_keeps_a_label()
   {
      var target = new RepositoryTarget(new Uri("https://gitlab.com/contoso/web"), "main", "gitlab");

      var thrown = Assert.Throws<PlatformException>(() => _platforms.RepositoryOf(target));

      Assert.StartsWith("lotsen does not know the platform 'gitlab' of the repository https://gitlab.com/contoso/web", thrown.Reason, StringComparison.Ordinal);
      Assert.Equal("https://gitlab.com/contoso/web@main", _platforms.Label(target));
   }

   [Fact]
   public void Azure_DevOps_repository_target_has_the_platforms_label_and_credential()
   {
      var target = new RepositoryTarget(new Uri("https://dev.azure.com/contoso/shop/_git/web"), "main", "azureDevOps");

      Assert.Equal("contoso/shop/web@main", _platforms.Label(target));
      Assert.Same(_platforms.Of(new PullRequestTarget("azureDevOps", new Uri("https://dev.azure.com/contoso/shop/_git/web/pullrequest/1"))).GitCredential, _platforms.CredentialOf(target));
   }

   public async ValueTask DisposeAsync()
   {
      foreach (var platform in _registered.OfType<IDisposable>())
      {
         platform.Dispose();
      }

      await _services.DisposeAsync();
      _dataDirectory.Delete(recursive: true);
   }
}
