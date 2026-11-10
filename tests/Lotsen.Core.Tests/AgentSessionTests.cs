using System.Net;
using System.Text.Json;
using Lotsen.Hosting;
using Lotsen.Json;
using Lotsen.Operations;
using Lotsen.Sessions;
using Lotsen.Tours;

namespace Lotsen.Tests;

/// <summary>Covers the two kinds of caller: agent sessions, each held open by one connection, and the UI with its own credential.</summary>
[Trait("Category", "Roles")]
public sealed class AgentSessionTests : IAsyncLifetime
{
   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

   private ServerHarness _harness = null!;

   public async ValueTask InitializeAsync() => _harness = await ServerHarness.StartAsync();

   public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

   [Fact]
   public async Task Connection_registers_a_session_with_an_id_that_ends_when_the_connection_drops()
   {
      var connection = await AgentSessionConnection.OpenAsync(_harness);
      var session = Assert.Single(_harness.Sessions.Current);
      var caller = await _harness.WhoAsync(_harness.State.Token, connection.Credential);

      await connection.DisposeAsync();
      await Poll.EndedAsync(session, Timeout, "The session did not end after its connection dropped.");
      var afterDrop = await _harness.WhoAsync(_harness.State.Token, connection.Credential);

      Assert.False(string.IsNullOrEmpty(connection.Id));
      Assert.Equal(connection.Id, session.Id);
      Assert.Equal("agent", (string)caller.Caller!["kind"]!);
      Assert.Equal(connection.Id, (string)caller.Caller!["sessionId"]!);
      Assert.Empty(_harness.Sessions.Current);
      Assert.Null(_harness.Sessions.Find(connection.Id));
      Assert.Equal(HttpStatusCode.Unauthorized, afterDrop.Status);
   }

   [Fact]
   public async Task Each_connection_gets_its_own_session()
   {
      await using var first = await AgentSessionConnection.OpenAsync(_harness);
      await using var second = await AgentSessionConnection.OpenAsync(_harness);

      var firstCaller = await _harness.WhoAsync(_harness.State.Token, first.Credential);
      var secondCaller = await _harness.WhoAsync(_harness.State.Token, second.Credential);

      Assert.NotEqual(first.Id, second.Id);
      Assert.Equal(first.Id, (string)firstCaller.Caller!["sessionId"]!);
      Assert.Equal(second.Id, (string)secondCaller.Caller!["sessionId"]!);
      Assert.Equal(new[] { first.Id, second.Id }.Order(), _harness.Sessions.Current.Select(session => session.Id).Order());
   }

   [Fact]
   public async Task Ui_credential_cannot_open_an_agent_session()
   {
      var credential = await _harness.UiCredentialAsync();

      using var response = await _harness.SendAsync(AgentSessionConnection.OpenRequest(_harness, credential, _harness.Version));

      Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
      Assert.Empty(_harness.Sessions.Current);
   }

   [Fact]
   public async Task Request_with_the_ui_credential_is_the_ui()
   {
      var credential = await _harness.UiCredentialAsync();

      var caller = await _harness.WhoAsync(credential);

      Assert.Equal(HttpStatusCode.OK, caller.Status);
      Assert.Equal("ui", (string)caller.Caller!["kind"]!);
      Assert.Null(caller.Caller!["sessionId"]);
   }

   [Fact]
   public async Task Ui_credential_with_a_sessions_credential_is_refused()
   {
      var credential = await _harness.UiCredentialAsync();
      await using var connection = await AgentSessionConnection.OpenAsync(_harness);

      var caller = await _harness.WhoAsync(credential, connection.Credential);

      Assert.Equal(HttpStatusCode.Forbidden, caller.Status);
   }

   [Fact]
   public async Task Request_with_the_server_token_alone_is_the_cli()
   {
      var caller = await _harness.WhoAsync(_harness.State.Token);

      Assert.Equal("cli", (string)caller.Caller!["kind"]!);
      Assert.Null(caller.Caller!["sessionId"]);
   }

   /// <summary>The session ID is no secret, since later events and the UI name it, so neither it alone nor it with another session's key names the session.</summary>
   [Fact]
   public async Task Session_id_without_its_own_key_is_refused()
   {
      await using var first = await AgentSessionConnection.OpenAsync(_harness);
      await using var second = await AgentSessionConnection.OpenAsync(_harness);
      var secondKey = second.Credential[(second.Credential.IndexOf('.') + 1)..];

      var idAlone = await _harness.WhoAsync(_harness.State.Token, first.Id);
      var otherKey = await _harness.WhoAsync(_harness.State.Token, $"{first.Id}.{secondKey}");
      var empty = await _harness.WhoAsync(_harness.State.Token, "");

      Assert.Equal(HttpStatusCode.Unauthorized, idAlone.Status);
      Assert.Equal(HttpStatusCode.Unauthorized, otherKey.Status);
      Assert.Equal(HttpStatusCode.Unauthorized, empty.Status);
   }

   [Fact]
   public async Task Session_credential_without_the_server_token_is_refused()
   {
      await using var connection = await AgentSessionConnection.OpenAsync(_harness);

      var withoutToken = await _harness.WhoAsync(connection.Credential);
      var withUiCredential = await _harness.WhoAsync(await _harness.UiCredentialAsync(), connection.Credential);

      Assert.Equal(HttpStatusCode.Unauthorized, withoutToken.Status);
      Assert.Equal(HttpStatusCode.Forbidden, withUiCredential.Status);
   }

   /// <summary>A tool call's body goes to the operation, and nothing in it names the caller.</summary>
   [Fact]
   public async Task Tool_call_body_cannot_name_another_session()
   {
      await using var first = await AgentSessionConnection.OpenAsync(_harness);
      await using var second = await AgentSessionConnection.OpenAsync(_harness);

      var request = first.Request(_harness, "create_tour");
      using var response = await _harness.SendAsync(request, $$"""{ "prUrl": "{{TestPlatform.PrUrl}}", "familiarity": "some", "sessionId": "{{second.Id}}" }""");

      Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
      var error = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
      Assert.Equal("sessionId", (string)error["field"]!);
      Assert.Empty((await _harness.CallAsync("list_tours"))["tours"]!.AsArray());
   }

   [Theory]
   [Trait("Category", "Versions")]
   [InlineData(false)]
   [InlineData(true)]
   public async Task Session_of_another_build_is_refused_with_the_restart_named(bool protocolDiffers)
   {
      var other = protocolDiffers ? _harness.Version with { Protocol = _harness.Version.Protocol + 1 } : _harness.Version with { Build = "0.0.0 (another build)" };

      using var response = await _harness.SendAsync(AgentSessionConnection.OpenRequest(_harness, _harness.State.Token, other));
      var reason = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), LotsenJsonContext.Default.OperationError)!.Reason;

      Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
      Assert.Contains("another build", reason);
      Assert.Contains($"`{LotsenCommand.Of(other).With("restart")}`", reason, StringComparison.Ordinal);
      Assert.DoesNotMatch(ServerMessageTests.RestartOfServe(), reason);
      Assert.Empty(_harness.Sessions.Current);
   }

   [Fact]
   public async Task Agent_session_cannot_open_another()
   {
      await using var connection = await AgentSessionConnection.OpenAsync(_harness);
      var request = AgentSessionConnection.OpenRequest(_harness, _harness.State.Token, _harness.Version);
      request.Headers.Add(AgentSessions.CredentialHeader, connection.Credential);

      using var response = await _harness.SendAsync(request);

      Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
      Assert.Equal(connection.Id, Assert.Single(_harness.Sessions.Current).Id);
   }

   [Fact]
   public async Task Agent_session_cannot_open_the_event_stream()
   {
      await using var connection = await AgentSessionConnection.OpenAsync(_harness);

      using var response = await _harness.SendAsync(connection.Request(_harness, HttpMethod.Get, "api/events"));
      var reason = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

      Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
      Assert.StartsWith("text/plain", response.Content.Headers.ContentType?.MediaType);
      Assert.Contains("agent session", reason);
   }

   [Fact]
   public async Task Session_ends_when_the_server_stops()
   {
      await using var connection = await AgentSessionConnection.OpenAsync(_harness);
      var session = Assert.Single(_harness.Sessions.Current);

      await _harness.StopServerAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);

      Assert.True(session.IsEnded);
      Assert.True(await connection.Stream.ReadToEndAsync(Timeout), "The session's stream stayed open after the server stopped.");
   }

   [Fact]
   public async Task Event_sent_to_one_session_reaches_only_that_session()
   {
      await using var first = await AgentSessionConnection.OpenAsync(_harness);
      await using var second = await AgentSessionConnection.OpenAsync(_harness);

      var (toFirst, toSecond) = (TourId.New(), TourId.New());
      _harness.Writer.Exclusive(locked =>
      {
         locked.Send(_harness.Sessions.Find(first.Id)!.Recipient.TakenOver(toFirst));
         locked.Send(_harness.Sessions.Find(second.Id)!.Recipient.TakenOver(toSecond));
      });

      var atFirst = await first.Stream.NextAsync(Timeout);
      var atSecond = await second.Stream.NextAsync(Timeout);
      Assert.Equal((SessionEvents.TourTakenOver, toFirst.Value), (atFirst.Type, (string)atFirst.Data["tourId"]!));
      Assert.Equal((SessionEvents.TourTakenOver, toSecond.Value), (atSecond.Type, (string)atSecond.Data["tourId"]!));
   }

   /// <summary>Requests race the drop of their session's connection, so each one is identified as that session or refused, and never as anyone else.</summary>
   [Fact]
   public async Task Request_that_races_the_drop_is_the_session_or_refused()
   {
      var connection = await AgentSessionConnection.OpenAsync(_harness);
      var session = _harness.Sessions.Find(connection.Id)!;

      var calls = Enumerable.Range(0, 40).Select(_ => Task.Run(() => _harness.WhoAsync(_harness.State.Token, connection.Credential))).ToList();
      await connection.DisposeAsync();
      var results = await Task.WhenAll(calls);
      await Poll.EndedAsync(session, Timeout, "The session did not end after its connection dropped.");
      var afterEnd = await _harness.WhoAsync(_harness.State.Token, connection.Credential);

      Assert.All(results, result => Assert.True(
         result.Status == HttpStatusCode.Unauthorized || (result.Status == HttpStatusCode.OK && (string)result.Caller!["sessionId"]! == connection.Id),
         $"{result.Status} {result.Caller}"));
      Assert.Equal(HttpStatusCode.Unauthorized, afterEnd.Status);
   }
}

/// <summary>Covers the registry of agent sessions on its own: how a session ends and how its ledger learns of the end.</summary>
[Trait("Category", "Roles")]
public sealed class AgentSessionRegistryTests
{
   /// <summary>The session holds a tour, so its end waits for the ledger, which hands it out once however often the session ends.</summary>
   [Fact]
   public void Ended_session_is_no_longer_found_and_its_end_is_recorded_once()
   {
      var sessions = new AgentSessions();
      var ledger = sessions.ClaimLedger();
      var session = sessions.TryOpen()!;
      var tour = TourId.New();
      ledger.BindNew(tour, session.Recipient);

      session.End();
      session.End();

      Assert.True(session.IsEnded);
      Assert.True(session.WhenEnded.IsCompletedSuccessfully);
      Assert.Null(sessions.Find(session.Id));
      Assert.Null(sessions.Authenticate(session.Credential));
      Assert.Empty(sessions.Current);
      Assert.True(session.Events.Completion.IsCompleted);
      var end = ledger.TakeEnd();
      Assert.Equal(session.Id, end?.SessionId);
      Assert.Equal([tour], end!.Released);
      Assert.Null(ledger.TakeEnd());
      Assert.False(ledger.Deliver(session.Recipient.TakenOver(tour)));
   }

   [Fact]
   public void Session_that_falls_behind_is_ended_without_blocking_the_sender()
   {
      var sessions = new AgentSessions();
      var ledger = sessions.ClaimLedger();
      var slow = sessions.TryOpen()!;
      var other = sessions.TryOpen()!;

      var sent = Enumerable.Range(0, AgentSessions.EventCapacity + 1).Select(_ => ledger.Deliver(slow.Recipient.TakenOver(TourId.New()))).ToList();

      Assert.Equal(AgentSessions.EventCapacity, sent.Count(accepted => accepted));
      Assert.False(sent[^1]);
      Assert.True(slow.IsEnded);
      Assert.False(other.IsEnded);
      Assert.Equal(other.Id, Assert.Single(sessions.Current).Id);
   }

   [Fact]
   public void Credential_authenticates_its_own_session_only()
   {
      var sessions = new AgentSessions();
      var first = sessions.TryOpen()!;
      var second = sessions.TryOpen()!;

      Assert.Same(first, sessions.Authenticate(first.Credential));
      Assert.Same(second, sessions.Authenticate(second.Credential));
      Assert.Null(sessions.Authenticate(first.Id));
      Assert.Null(sessions.Authenticate(first.Credential + "x"));
      Assert.Null(sessions.Authenticate($"{second.Id}.{first.Credential[(first.Credential.IndexOf('.') + 1)..]}"));
      Assert.DoesNotContain(first.Credential, first.ToString());
   }
}
