using System.Net;

namespace Lotsen.Tests;

/// <summary>Covers the checks that hold for both credentials, the server token with an agent session's credential and the UI credential.</summary>
[Trait("Category", "Security")]
public sealed class CredentialTests : IAsyncLifetime
{
   private readonly RecordingLoggerProvider _serverLog = new();

   private ServerHarness _harness = null!;

   public async ValueTask InitializeAsync() => _harness = await ServerHarness.StartAsync(configure: options => options with { LoggerProvider = _serverLog });

   public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

   [Theory]
   [InlineData("POST", "api/ops/list_tours")]
   [InlineData("GET", "api/caller")]
   [InlineData("GET", "api/events")]
   [InlineData("POST", "api/agent-session")]
   public async Task Wrong_ui_credential_is_refused(string method, string path)
   {
      var credential = await _harness.UiCredentialAsync();

      foreach (var wrong in new[] { credential + "x", credential[..^1], credential.Any(char.IsLower) ? credential.ToUpperInvariant() : credential.ToLowerInvariant() })
      {
         using var response = await _harness.SendAsync(ServerHarness.Request(new HttpMethod(method), path, wrong));

         Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
         Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
      }
   }

   [Theory]
   [InlineData("POST", "api/ops/list_tours")]
   [InlineData("GET", "api/caller")]
   [InlineData("POST", "api/ops/open_ui")]
   public async Task Session_credential_with_a_wrong_or_missing_token_is_refused(string method, string path)
   {
      await using var connection = await AgentSessionConnection.OpenAsync(_harness);

      using var wrongToken = await _harness.SendAsync(ServerHarness.Request(new HttpMethod(method), path, _harness.State.Token + "x", connection.Credential));
      var noToken = new HttpRequestMessage(new HttpMethod(method), path);
      noToken.Headers.Add(Lotsen.Sessions.AgentSessions.CredentialHeader, connection.Credential);
      using var missing = await _harness.SendAsync(noToken);
      using var wrongSession = await _harness.SendAsync(ServerHarness.Request(new HttpMethod(method), path, _harness.State.Token, connection.Credential + "x"));

      Assert.Equal(HttpStatusCode.Unauthorized, wrongToken.StatusCode);
      Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
      Assert.Equal(HttpStatusCode.Unauthorized, wrongSession.StatusCode);
   }

   [Fact]
   public async Task Session_credential_given_twice_is_refused()
   {
      await using var connection = await AgentSessionConnection.OpenAsync(_harness);
      var request = connection.Request(_harness, HttpMethod.Get, "api/caller");
      request.Headers.Add(Lotsen.Sessions.AgentSessions.CredentialHeader, connection.Credential);

      using var response = await _harness.SendAsync(request);

      Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
   }

   [Fact]
   public async Task Foreign_host_and_origin_are_refused_with_either_credential()
   {
      var uiCredential = await _harness.UiCredentialAsync();
      await using var connection = await AgentSessionConnection.OpenAsync(_harness);
      var requests = new (Func<HttpRequestMessage> Create, bool Answered)[]
      {
         (() => ServerHarness.Request(HttpMethod.Get, "api/caller", uiCredential), true),
         (() => connection.Request(_harness, HttpMethod.Get, "api/caller"), true),

         // An accepted session would hold its stream open, so the own origin goes untested for it.
         (() => AgentSessionConnection.OpenRequest(_harness, _harness.State.Token, _harness.Version), false),
      };

      foreach (var (create, answered) in requests)
      {
         var foreignHost = create();
         foreignHost.Headers.Host = "evil.example:" + _harness.Port;
         var foreignOrigin = create();
         foreignOrigin.Headers.TryAddWithoutValidation("Origin", "http://evil.example");
         var ownOrigin = create();
         ownOrigin.Headers.TryAddWithoutValidation("Origin", $"http://127.0.0.1:{_harness.Port}");

         using var hostResponse = await _harness.SendAsync(foreignHost);
         using var originResponse = await _harness.SendAsync(foreignOrigin);

         Assert.Equal(HttpStatusCode.MisdirectedRequest, hostResponse.StatusCode);
         Assert.Equal(HttpStatusCode.Forbidden, originResponse.StatusCode);
         if (answered)
         {
            using var accepted = await _harness.SendAsync(ownOrigin);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
         }
      }

      Assert.Equal(connection.Id, Assert.Single(_harness.Sessions.Current).Id);
   }

   /// <summary>Every credential crosses the wire in a request or a response here, accepted and refused, while the server logs every level.</summary>
   [Fact]
   public async Task No_credential_appears_in_the_servers_log()
   {
      var uiCredential = await _harness.UiCredentialAsync();
      var connection = await AgentSessionConnection.OpenAsync(_harness);
      var calls = new[]
      {
         new PreparedCall("list_tours", uiCredential).Request(),
         new PreparedCall("list_tours", uiCredential + "x").Request(),
         ServerHarness.Request(HttpMethod.Get, "api/caller", uiCredential, connection.Credential),
         connection.Request(_harness, "list_tours").Request(),
         connection.Request(_harness, HttpMethod.Get, "api/no-such-route"),
         new PreparedCall("list_tours", _harness.State.Token, connection.Credential + "x").Request(),
         new PreparedCall("list_tours", _harness.State.Token).Request(),
         AgentSessionConnection.OpenRequest(_harness, uiCredential, _harness.Version),
         AgentSessionConnection.OpenRequest(_harness, _harness.State.Token, _harness.Version with { Build = "another" }),
      };
      foreach (var request in calls)
      {
         using var response = await _harness.SendAsync(request);
      }

      var session = _harness.Sessions.Find(connection.Id)!;
      await connection.DisposeAsync();
      await Poll.EndedAsync(session, TimeSpan.FromSeconds(10), "The session did not end after its connection dropped.");
      using var afterDrop = await _harness.SendAsync(new PreparedCall("list_tours", _harness.State.Token, connection.Credential));
      await _harness.StopServerAsync();

      var key = connection.Credential[(connection.Credential.IndexOf('.') + 1)..];
      Assert.NotEmpty(_serverLog.Lines);
      Assert.Contains(_serverLog.Lines, line => line.Contains(connection.Id, StringComparison.Ordinal));
      foreach (var secret in new[] { _harness.State.Token, uiCredential, key })
      {
         Assert.DoesNotContain(_serverLog.Lines, line => line.Contains(secret, StringComparison.Ordinal));
      }
   }

   [Fact]
   public void Credentials_hide_their_values_from_tostring()
   {
      var credentials = Lotsen.Server.ServerCredentials.CreateRandom();

      Assert.DoesNotContain(credentials.ServerToken.Value, credentials.ToString());
      Assert.DoesNotContain(credentials.Ui.Value, credentials.ToString());
      Assert.DoesNotContain(credentials.Ui.Value, credentials.Ui.ToString());
      Assert.NotEqual(credentials.ServerToken.Value, credentials.Ui.Value);
   }
}
