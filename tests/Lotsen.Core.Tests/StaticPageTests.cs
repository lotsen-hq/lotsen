using System.Net;
using System.Text.RegularExpressions;

namespace Lotsen.Tests;

/// <summary>Covers how the server serves the UI files: without the token, and with a strict content security policy.</summary>
[Trait("Category", "Security")]
public sealed partial class StaticPageTests : IAsyncLifetime
{
   private ServerHarness _harness = null!;

   public async ValueTask InitializeAsync() => _harness = await ServerHarness.StartAsync();

   public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

   [Theory]
   [InlineData("", "text/html")]
   [InlineData("index.html", "text/html")]
   [InlineData("popout.html", "text/html")]
   [InlineData("app.js", "text/javascript")]
   [InlineData("app.css", "text/css")]
   public async Task Ui_files_are_served_without_the_token(string path, string mediaType)
   {
      using var response = await _harness.SendAsync(_harness.Request(HttpMethod.Get, path));

      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
      Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
      Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
   }

   [Fact]
   public async Task Missing_file_is_not_found()
   {
      using var response = await _harness.SendAsync(_harness.Request(HttpMethod.Get, "no-such-file.js"));

      Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
   }

   [Theory]
   [InlineData("")]
   [InlineData("popout.html")]
   public async Task Each_page_load_gets_a_new_style_nonce_in_the_header_and_the_page(string path)
   {
      var (firstPolicy, firstHtml) = await LoadPageAsync(path);
      var (secondPolicy, secondHtml) = await LoadPageAsync(path);

      var firstNonce = StyleNonce(firstPolicy);
      var secondNonce = StyleNonce(secondPolicy);

      Assert.NotEqual(firstNonce, secondNonce);
      Assert.Contains($"content=\"{firstNonce}\"", firstHtml);
      Assert.Contains($"content=\"{secondNonce}\"", secondHtml);
      Assert.DoesNotContain(firstNonce, secondHtml);
   }

   [Fact]
   public async Task Page_policy_allows_only_own_scripts_and_styles()
   {
      var (policy, _) = await LoadPageAsync();
      var directives = policy.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToDictionary(directive => directive.Split(' ')[0], directive => directive);

      Assert.Equal("default-src 'self'", directives["default-src"]);
      Assert.Equal("script-src 'self'", directives["script-src"]);
      Assert.Matches(@"^style-src 'self' 'nonce-[A-Za-z0-9+/_=-]{22,}'$", directives["style-src"]);
      Assert.Equal("object-src 'none'", directives["object-src"]);
      Assert.Equal("base-uri 'none'", directives["base-uri"]);
      Assert.Equal("frame-ancestors 'none'", directives["frame-ancestors"]);
      Assert.Equal("form-action 'none'", directives["form-action"]);
   }

   [Fact]
   public async Task Page_is_never_cached()
   {
      using var response = await _harness.SendAsync(_harness.Request(HttpMethod.Get, ""));

      Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
      Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
   }

   private async Task<(string Policy, string Html)> LoadPageAsync(string path = "")
   {
      using var response = await _harness.SendAsync(_harness.Request(HttpMethod.Get, path));
      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
      var policy = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
      return (policy, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
   }

   private static string StyleNonce(string policy)
   {
      var match = StyleNoncePattern().Match(policy);
      Assert.True(match.Success, $"The policy has no style nonce: {policy}");
      return match.Groups["nonce"].Value;
   }

   [GeneratedRegex(@"style-src [^;]*'nonce-(?<nonce>[^']+)'")]
   private static partial Regex StyleNoncePattern();
}
