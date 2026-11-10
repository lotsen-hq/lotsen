using Lotsen.Server;

namespace Lotsen.Tests;

/// <summary>Covers the commands that <see cref="SystemBrowserLauncher"/> tries for a URL, without a run of any of them.</summary>
[Trait("Category", "Security")]
public sealed class BrowserLauncherTests
{
   private const string Url = "http://localhost:5000/#code=abc-_1&tour=abcdefghijklmnop";
   private const string CmdUrl = "http://localhost:5000/#code=abc-_1^&tour=abcdefghijklmnop";

   [Fact]
   public void Wsl_gives_cmd_a_url_with_a_caret_before_each_ampersand_and_every_other_launcher_the_plain_url()
   {
      var candidates = SystemBrowserLauncher.Candidates(Url, browser: null, wsl: true).Select(candidate => (candidate.FileName, string.Join(' ', candidate.Arguments)));

      Assert.Equal(
         [("wslview", Url), ("cmd.exe", $"/c start {CmdUrl}"), ("/mnt/c/Windows/System32/cmd.exe", $"/c start {CmdUrl}"), ("xdg-open", Url)],
         candidates);
   }

   [Theory]
   [InlineData("http://localhost:5000/#code=a|b")]
   [InlineData("http://localhost:5000/#code=a^b")]
   [InlineData("http://localhost:5000/#code=a%b")]
   [InlineData("http://localhost:5000/#code=a b")]
   [InlineData("http://localhost:5000/#code=a\"b&tour=c")]
   public void Wsl_skips_cmd_for_a_url_with_another_metacharacter(string url)
   {
      var files = SystemBrowserLauncher.Candidates(url, browser: null, wsl: true).Select(candidate => candidate.FileName);

      Assert.Equal(["wslview", "xdg-open"], files);
   }

   [Fact]
   public void Browser_variable_gets_the_plain_url_and_is_the_only_candidate()
   {
      var candidate = Assert.Single(SystemBrowserLauncher.Candidates(Url, browser: "firefox", wsl: true));

      Assert.Equal(("firefox", Url), (candidate.FileName, Assert.Single(candidate.Arguments)));
   }

   [Fact]
   public void Linux_outside_wsl_gives_only_xdg_open_the_plain_url()
   {
      var candidate = Assert.Single(SystemBrowserLauncher.Candidates(Url, browser: null, wsl: false));

      Assert.Equal(("xdg-open", Url), (candidate.FileName, Assert.Single(candidate.Arguments)));
   }
}
