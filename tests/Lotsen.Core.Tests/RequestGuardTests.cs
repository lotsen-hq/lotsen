using System.Net;
using Lotsen.Operations;

namespace Lotsen.Tests;

/// <summary>Covers the token, Host and Origin checks that the server applies to every request.</summary>
[Trait("Category", "Security")]
public sealed class RequestGuardTests : IAsyncLifetime
{
   private ServerHarness _harness = null!;

   public async ValueTask InitializeAsync() => _harness = await ServerHarness.StartAsync();

   public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

   [Theory]
   [InlineData("POST", "api/ops/open_ui")]
   [InlineData("GET", "api/events")]
   [InlineData("POST", "api/ops/list_tours")]
   [InlineData("GET", "api/no-such-route")]
   [InlineData("POST", "API/ops/open_ui")]
   public async Task Api_request_without_a_token_is_refused(string method, string path)
   {
      using var response = await _harness.SendAsync(_harness.Request(new HttpMethod(method), path));

      Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
      Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
   }

   [Theory]
   [InlineData("Bearer wrong-token")]
   [InlineData("Basic dXNlcjpwYXNz")]
   [InlineData("Bearer")]
   [InlineData("")]
   public async Task Api_request_with_a_wrong_credential_is_refused(string authorization)
   {
      var request = _harness.Request(OperationCatalog.OpenUi.Name);
      request.Headers.TryAddWithoutValidation("Authorization", authorization);

      using var response = await _harness.SendAsync(request);

      Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
   }

   [Fact]
   public async Task Api_request_with_the_token_is_accepted()
   {
      using var response = await _harness.SendAsync(_harness.Request(OperationCatalog.OpenUi.Name, withToken: true));

      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
   }

   [Fact]
   public async Task Unknown_api_route_with_the_token_is_not_found()
   {
      using var response = await _harness.SendAsync(_harness.Request(HttpMethod.Get, "api/no-such-route", withToken: true));

      Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
   }

   [Theory]
   [InlineData("evil.example:{port}")]
   [InlineData("evil.example")]
   [InlineData("127.0.0.1")]
   [InlineData("localhost")]
   [InlineData("127.0.0.1:1")]
   [InlineData("127.0.0.2:{port}")]
   [InlineData("[::1]:{port}")]
   [InlineData("localhost.:{port}")]
   [InlineData("127.0.0.1.evil.example:{port}")]
   public async Task Foreign_host_is_refused_on_pages_and_api(string host)
   {
      foreach (var (path, withToken) in new[] { ("", false), ("api/ops/open_ui", true) })
      {
         var request = _harness.Request(path.Length == 0 ? HttpMethod.Get : HttpMethod.Post, path, withToken);
         request.Headers.Host = host.Replace("{port}", _harness.Port.ToString());

         using var response = await _harness.SendAsync(request);

         Assert.Equal(HttpStatusCode.MisdirectedRequest, response.StatusCode);
      }
   }

   [Theory]
   [InlineData("127.0.0.1:{port}")]
   [InlineData("localhost:{port}")]
   [InlineData("LOCALHOST:{port}")]
   public async Task Loopback_host_is_accepted(string host)
   {
      var request = _harness.Request(OperationCatalog.OpenUi.Name, withToken: true);
      request.Headers.Host = host.Replace("{port}", _harness.Port.ToString());

      using var response = await _harness.SendAsync(request);

      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
   }

   [Theory]
   [InlineData("http://evil.example")]
   [InlineData("null")]
   [InlineData("http://127.0.0.1:1")]
   [InlineData("https://127.0.0.1:{port}")]
   [InlineData("http://localhost:{port}")]
   [InlineData("http://127.0.0.1:{port}/")]
   [InlineData("")]
   public async Task Foreign_origin_is_refused_on_pages_and_api(string origin)
   {
      foreach (var (path, withToken) in new[] { ("", false), ("api/ops/open_ui", true), ("api/ui-credential", false), ("api/agent-session", true) })
      {
         var request = _harness.Request(path.Length == 0 ? HttpMethod.Get : HttpMethod.Post, path, withToken);
         request.Headers.TryAddWithoutValidation("Origin", origin.Replace("{port}", _harness.Port.ToString()));

         using var response = await _harness.SendAsync(request);

         Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
      }
   }

   [Theory]
   [InlineData("127.0.0.1:{port}", "http://127.0.0.1:{port}")]
   [InlineData("localhost:{port}", "http://localhost:{port}")]
   public async Task Own_origin_is_accepted(string host, string origin)
   {
      var request = _harness.Request(OperationCatalog.OpenUi.Name, withToken: true);
      request.Headers.Host = host.Replace("{port}", _harness.Port.ToString());
      request.Headers.TryAddWithoutValidation("Origin", origin.Replace("{port}", _harness.Port.ToString()));

      using var response = await _harness.SendAsync(request);

      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
   }

   [Theory]
   [InlineData("GET", "", false)]
   [InlineData("POST", "api/ops/open_ui", true)]
   [InlineData("POST", "api/ops/open_ui", false)]
   public async Task Responses_set_no_cookies_and_no_cors_headers(string method, string path, bool withToken)
   {
      var request = _harness.Request(new HttpMethod(method), path, withToken);
      request.Headers.TryAddWithoutValidation("Origin", $"http://127.0.0.1:{_harness.Port}");

      using var response = await _harness.SendAsync(request);

      Assert.False(response.Headers.Contains("Set-Cookie"));
      Assert.DoesNotContain(response.Headers, header => header.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
   }

   [Fact]
   public async Task Cors_preflight_from_a_foreign_origin_is_refused()
   {
      var request = _harness.Request(HttpMethod.Options, "api/ops/open_ui");
      request.Headers.TryAddWithoutValidation("Origin", "http://evil.example");
      request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
      request.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", "authorization");

      using var response = await _harness.SendAsync(request);

      Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
      Assert.DoesNotContain(response.Headers, header => header.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
   }
}
