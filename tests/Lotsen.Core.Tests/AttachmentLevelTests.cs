using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Lotsen.Operations;
using Lotsen.Sessions;
using Lotsen.Tours;
using Microsoft.Extensions.DependencyInjection;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>Covers a binding's attachment level, unconfirmed until the session acks its nonce and confirmed after, and the refusals of ack.</summary>
[Trait("Category", "Roles")]
public sealed class AttachmentLevelTests : IAsyncLifetime
{
   private const string CreateBody = $$"""{ "prUrl": "{{TestPlatform.PrUrl}}", "familiarity": "some" }""";

   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

   private ServerHarness _harness = null!;

   public async ValueTask InitializeAsync() => _harness = await ServerHarness.StartAsync();

   public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

   [Fact]
   public async Task Tour_list_and_creation_event_show_the_level_of_the_bound_session_and_the_tour_itself_holds_no_binding()
   {
      const string level = "unconfirmed";
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      await using var stream = await EventStream.OpenAsync(_harness);

      var created = await CallAsync(session.Request(_harness, "create_tour"), CreateBody);
      var id = (string)created["id"]!;
      var createdEvent = await stream.NextAsync(Timeout);
      var read = await _harness.CallAsync(On("read_state", id));
      var listed = await _harness.CallAsync("list_tours");

      var expected = new JsonObject { ["sessionId"] = session.Id, ["level"] = level };
      Assert.True(JsonNode.DeepEquals(expected, createdEvent.Data["binding"]), $"tourCreated: {createdEvent.Data}");
      Assert.False(created.AsObject().ContainsKey("binding"), $"create_tour: {created}");
      Assert.False(read.AsObject().ContainsKey("binding"), $"read_state: {read}");
      Assert.True(JsonNode.DeepEquals(expected, listed["tours"]![0]!["binding"]), $"list_tours: {listed}");
   }

   /// <summary>Each session gets its own nonce with its own probe.</summary>
   [Fact]
   public async Task Each_session_gets_its_own_nonce()
   {
      await using var first = await AgentSessionConnection.OpenAsync(_harness);
      await using var second = await AgentSessionConnection.OpenAsync(_harness);

      await CreateAsync(first);
      await CreateAsync(second);
      var firstNonce = await first.ProbeAsync();
      var secondNonce = await second.ProbeAsync();

      Assert.NotEqual(firstNonce, secondNonce);
   }

   [Fact]
   public async Task Ack_with_the_right_nonce_confirms_the_session_and_each_of_its_tours()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var asked = await CreateAsync(session);
      var other = await CreateAsync(session);
      var nonce = await session.ProbeAsync();
      await using var stream = await EventStream.OpenAsync(_harness);

      var acked = await CallAsync(AckRequest(session), $$"""{ "nonce": "{{nonce}}" }""");
      var changes = new[] { await stream.NextBesidesControlsAsync(Timeout), await stream.NextBesidesControlsAsync(Timeout) };
      var again = await CallAsync(AckRequest(session), $$"""{ "nonce": "{{nonce}}" }""");
      var marker = _harness.CreateUnboundTour(CreateBody);

      var confirmed = new JsonObject { ["sessionId"] = session.Id, ["level"] = "confirmed" };
      Assert.True(JsonNode.DeepEquals(confirmed, acked), $"ack: {acked}");
      Assert.True(JsonNode.DeepEquals(confirmed, again), $"second ack: {again}");
      Assert.All(changes, change => Assert.Equal(ServerEvents.TourBindingChanged.Name, change.Type));
      Assert.Equal(new[] { asked, other }.Order(), changes.Select(change => (string)change.Data["tourId"]!).Order());
      Assert.All(changes, change => Assert.True(JsonNode.DeepEquals(confirmed, change.Data["binding"]), $"event: {change.Data}"));
      Assert.Equal(("tourCreated", (string)marker["id"]!), await NextTypeAndIdAsync(stream));
      foreach (var id in new[] { asked, other })
      {
         Assert.True(JsonNode.DeepEquals(confirmed, await _harness.BindingOfAsync(id)), $"tour {id}");
      }
   }

   [Theory]
   [InlineData("wrong")]
   [InlineData("prefix")]
   [InlineData("noneIssued")]
   [InlineData("anotherSessions")]
   public async Task Ack_with_a_nonce_that_the_session_did_not_receive_is_refused_and_confirms_nothing(string presented)
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      await using var other = await AgentSessionConnection.OpenAsync(_harness);
      await using var unbound = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(session);
      await CreateAsync(other);
      var otherNonce = await other.ProbeAsync();
      var nonce = await session.ProbeAsync();
      var (acking, value) = presented switch
      {
         "wrong" => (session, new string('a', 26)),
         "prefix" => (session, nonce[..25]),
         "noneIssued" => (unbound, otherNonce),
         _ => (session, otherNonce),
      };
      await using var stream = await EventStream.OpenAsync(_harness);

      using var response = await SendAsync(AckRequest(acking), $$"""{ "nonce": "{{value}}" }""");
      var marker = _harness.CreateUnboundTour(CreateBody);

      var error = await ErrorAsync(response, HttpStatusCode.Conflict, "wrongNonce");
      Assert.Equal("nonce", (string)error["field"]!);
      Assert.Equal("unconfirmed", (string)(await _harness.BindingOfAsync(id))!["level"]!);
      Assert.Equal(("tourCreated", (string)marker["id"]!), await NextTypeAndIdAsync(stream));
      var byAcking = (string)(await CallAsync(acking.Request(_harness, "create_tour"), CreateBody))["id"]!;
      Assert.Equal("unconfirmed", (string)(await _harness.BindingOfAsync(byAcking))!["level"]!);
   }

   [Theory]
   [InlineData("ui")]
   [InlineData("cli")]
   public async Task Ack_from_any_caller_but_an_agent_session_is_refused(string caller)
   {
      var credential = caller == "ui" ? await _harness.UiCredentialAsync() : _harness.State.Token;

      using var response = await SendAsync(new PreparedCall("ack", credential), """{ "nonce": "abc" }""");

      var error = await ErrorAsync(response, HttpStatusCode.Forbidden, "callerNotAllowed");
      Assert.Null(error["field"]);
   }

   /// <summary>A session that ended after it received its nonce stays unconfirmed, so its end leaves no confirmation behind.</summary>
   [Fact]
   public void Ack_of_a_session_that_ended_is_refused()
   {
      var session = _harness.Sessions.TryOpen()!;
      _harness.Writer.Exclusive(locked => locked.Send(session.Recipient.Probe(TourId.New())));
      Assert.True(session.Events.TryRead(out var sent));
      var nonce = (string)JsonNode.Parse(sent.Data)!["nonce"]!;
      var pass = Passes.Admit<Access.AnySession.Pass>("ack", _harness.CallerOf(session));
      session.End();

      var result = SessionOperations.Ack(pass, new AckInput(nonce));

      Assert.Equal(OperationErrorCode.CallerNotAllowed, result.Error?.Code);
   }

   /// <summary>Confirmation belongs to the session, so it outlasts the take-over of the session's tour and returns with the session when it takes the tour again.</summary>
   [Fact]
   public async Task Take_over_moves_the_level_with_the_binding_and_the_confirmation_stays_with_its_session()
   {
      await using var first = await AgentSessionConnection.OpenAsync(_harness);
      await using var second = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(first);
      await CallAsync(AckRequest(first), $$"""{ "nonce": "{{await first.ProbeAsync()}}" }""");
      await using var stream = await EventStream.OpenAsync(_harness);

      await CallAsync(second.Request(_harness, On("attach_tour", id)), """{ "takeOver": true }""");
      var toSecond = await NextBindingChangeAsync(stream);
      await CallAsync(first.Request(_harness, On("attach_tour", id)), """{ "takeOver": true }""");
      var toFirst = await NextBindingChangeAsync(stream);

      Assert.True(JsonNode.DeepEquals(new JsonObject { ["sessionId"] = second.Id, ["level"] = "unconfirmed" }, toSecond), $"to the second session: {toSecond}");
      Assert.True(JsonNode.DeepEquals(new JsonObject { ["sessionId"] = first.Id, ["level"] = "confirmed" }, toFirst), $"back to the first session: {toFirst}");
   }

   /// <summary>The ack's event follows the event of the binding it changes, and the drop's release follows the ack's.</summary>
   [Fact]
   public async Task Binding_events_reach_the_ui_in_the_order_of_attach_ack_and_drop()
   {
      var asker = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(asker);
      var nonce = await asker.ProbeAsync();
      await using var other = await AgentSessionConnection.OpenAsync(_harness);
      var otherTour = await CreateAsync(other);
      await using var stream = await EventStream.OpenAsync(_harness);
      var serverSession = _harness.Sessions.Find(asker.Id)!;

      await CallAsync(asker.Request(_harness, On("attach_tour", otherTour)), """{ "takeOver": true }""");
      await CallAsync(AckRequest(asker), $$"""{ "nonce": "{{nonce}}" }""");
      await asker.DisposeAsync();
      await Poll.EndedAsync(serverSession, Timeout, "The session did not end after its connection dropped.");
      var changes = new List<JsonNode>();
      while (changes.Count < 5)
      {
         var next = await stream.NextAsync(Timeout);
         if (next.Type == ServerEvents.TourBindingChanged.Name)
         {
            changes.Add(next.Data);
         }
      }

      static string Describe(JsonNode change) => $"{(string)change["tourId"]!}:{(string?)change["binding"]?["level"] ?? "none"}";
      Assert.Equal($"{otherTour}:unconfirmed", Describe(changes[0]));
      Assert.Equal(new[] { $"{id}:confirmed", $"{otherTour}:confirmed" }.Order(), changes.Skip(1).Take(2).Select(Describe).Order());
      Assert.Equal(new[] { $"{id}:none", $"{otherTour}:none" }.Order(), changes.Skip(3).Select(Describe).Order());
   }

   private SessionOperations SessionOperations => _harness.Server.Services.GetRequiredService<SessionOperations>();

   private PreparedCall AckRequest(AgentSessionConnection session) => session.Request(_harness, "ack");

   private async Task<string> CreateAsync(AgentSessionConnection session) => (string)(await CallAsync(session.Request(_harness, "create_tour"), CreateBody))["id"]!;

   private Task<HttpResponseMessage> SendAsync(PreparedCall call, string? json = null) => _harness.SendAsync(call, json);

   private async Task<JsonNode> CallAsync(PreparedCall call, string? json = null)
   {
      using var response = await SendAsync(call, json);
      var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
      Assert.True(response.IsSuccessStatusCode, $"{call.Call.Name} returned {(int)response.StatusCode}: {body}");
      return SuccessBody.Output(body);
   }

   private static async Task<JsonNode> ErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
   {
      var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
      Assert.True(response.StatusCode == status, $"Expected {(int)status}, got {(int)response.StatusCode}: {body}");
      var error = JsonNode.Parse(body)!;
      Assert.Equal(code, (string)error["code"]!);
      return error;
   }

   private static async Task<JsonNode?> NextBindingChangeAsync(EventStream stream)
   {
      while (true)
      {
         var next = await stream.NextAsync(Timeout);
         if (next.Type == ServerEvents.TourBindingChanged.Name)
         {
            return next.Data["binding"];
         }
      }
   }

   private static async Task<(string Type, string? Id)> NextTypeAndIdAsync(EventStream stream)
   {
      var next = await stream.NextBesidesControlsAsync(Timeout);
      return (next.Type, (string?)next.Data["id"]);
   }
}
