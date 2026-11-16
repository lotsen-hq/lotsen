using Lotsen.Hosting;
using Lotsen.Platforms;
using Lotsen.Tours;
using Microsoft.Extensions.DependencyInjection;

namespace Lotsen.Tests;

/// <summary>Covers which repository URLs the platforms that the lotsen executable registers recognize, and what a repository of a platform without repositories gets.</summary>
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
      Assert.Equal("https://github.com/{owner}/{repository}, optionally ending in .git or followed by /tree/{ref}", _platforms.ExpectedRepositoryForms);
   }

   [Theory]
   [InlineData("https://github.com/octo-org/hello-world/tree/%20")]
   [InlineData("https://github.com/octo-org/hello-world/tree/x%2F..%2F..%2Fheads%2Fmain")]
   public void GitHub_repository_url_at_an_invalid_ref_is_not_recognized(string text)
   {
      Assert.Null(_platforms.RecognizeRepository(text));
   }

   [Theory]
   [InlineData("https://dev.azure.com/contoso/shop/_git/web")]
   [InlineData("https://contoso.visualstudio.com/shop/_git/web")]
   public void Azure_DevOps_repository_url_is_not_recognized(string text)
   {
      Assert.Null(_platforms.RecognizeRepository(text));
   }

   [Fact]
   public void Azure_DevOps_repository_target_reaches_no_platform_but_keeps_a_label()
   {
      var target = new RepositoryTarget(new Uri("https://dev.azure.com/contoso/shop/_git/web"), "main", "azureDevOps");

      var thrown = Assert.Throws<PlatformException>(() => _platforms.RepositoryOf(target));

      Assert.StartsWith("lotsen does not know the platform 'azureDevOps' of the repository https://dev.azure.com/contoso/shop/_git/web", thrown.Reason, StringComparison.Ordinal);
      Assert.Equal("https://dev.azure.com/contoso/shop/_git/web@main", _platforms.Label(target));
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
