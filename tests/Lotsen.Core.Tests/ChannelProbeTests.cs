using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Lotsen.Operations;
using Lotsen.Sessions;
using Lotsen.Tours;
using Microsoft.Extensions.DependencyInjection;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>Covers the probe that an unconfirmed session gets when it attaches a tour, the nonce that its questions carry until it acks, and the order of the probe and a later question.</summary>
[Trait("Category", "Roles")]
public sealed class ChannelProbeTests : IAsyncLifetime
{
   private const string CreateBody = $$"""{ "prUrl": "{{TestPlatform.PrUrl}}", "familiarity": "some" }""";

   private const string TourQuestion = """{ "text": "What does this tour cover?", "anchor": { "kind": "tour" } }""";

   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

   private ServerHarness _harness = null!;

   public async ValueTask InitializeAsync() => _harness = await ServerHarness.StartAsync();

   public async ValueTask DisposeAsync()
   {
      Writer.Hook = null;
      await _harness.DisposeAsync();
   }

   [Theory]
   [InlineData("create")]
   [InlineData("attach")]
   public async Task Probe_with_a_nonce_goes_to_an_unconfirmed_session_that_attaches_a_tour(string bind)
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);

      var id = bind == "create" ? await CreateAsync(session) : await AttachAsync(session, CreateUnbound());
      var probe = await session.Stream.NextAsync(Timeout);

      Assert.Equal(SessionEvents.Probe, probe.Type);
      Assert.Equal(["nonce"], probe.Data.AsObject().Select(property => property.Key));
      Assert.Matches("^[a-z2-7]{26}$", (string)probe.Data["nonce"]!);
      Assert.Equal("unconfirmed", await LevelAsync(id));
   }

   [Fact]
   public async Task Probe_goes_to_the_session_that_takes_a_tour_over()
   {
      await using var first = await AgentSessionConnection.OpenAsync(_harness);
      await using var second = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(first);
      var firstNonce = await first.ProbeAsync();

      await TakeOverAsync(second, id);
      var secondNonce = await second.ProbeAsync();
      var takenOver = await first.Stream.NextAsync(Timeout);

      Assert.NotEqual(firstNonce, secondNonce);
      Assert.Equal(SessionEvents.TourTakenOver, takenOver.Type);
   }

   /// <summary>A reconnected adapter opens a new session, which attaches the tour again and gets a probe of its own.</summary>
   [Fact]
   public async Task Probe_goes_to_the_new_session_that_attaches_a_tour_after_its_old_session_dropped()
   {
      var dropped = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(dropped);
      await dropped.ProbeAsync();
      await _harness.DropSessionAsync(dropped);
      await using var reconnected = await AgentSessionConnection.OpenAsync(_harness);

      await AttachAsync(reconnected, id);

      Assert.Matches("^[a-z2-7]{26}$", await reconnected.ProbeAsync());
   }

   [Theory]
   [InlineData("nonceOutstanding")]
   [InlineData("confirmed")]
   public async Task No_probe_goes_to_a_session_that_holds_its_nonce_or_is_confirmed(string state)
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      await using var other = await AgentSessionConnection.OpenAsync(_harness);
      await CreateAsync(session);
      var nonce = await session.ProbeAsync();
      if (state == "confirmed")
      {
         await AckAsync(session, nonce);
      }

      await CreateAsync(session);
      await AttachAsync(session, CreateUnbound());
      var taken = await CreateAsync(other);
      await TakeOverAsync(session, taken);
      var marker = await AskAsync(taken);

      var next = await session.Stream.NextAsync(Timeout);
      Assert.Equal((SessionEvents.Question, (string)marker["messages"]![0]!["id"]!), (next.Type, (string)next.Data["questionId"]!));
   }

   [Fact]
   public async Task Ack_of_the_probes_nonce_confirms_the_session()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(session);
      var nonce = await session.ProbeAsync();

      var acked = await AckAsync(session, nonce);

      Assert.True(JsonNode.DeepEquals(new JsonObject { ["sessionId"] = session.Id, ["level"] = "confirmed" }, acked), $"ack: {acked}");
      Assert.Equal("confirmed", await LevelAsync(id));
   }

   [Fact]
   public async Task Probe_nonce_rides_on_every_question_to_the_unconfirmed_session_and_on_none_after_the_ack()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var first = await CreateAsync(session);
      var second = await CreateAsync(session);
      var nonce = await session.ProbeAsync();

      await AskAsync(first);
      await AskAsync(second);
      var unconfirmed = new[] { await session.Stream.NextAsync(Timeout), await session.Stream.NextAsync(Timeout) };
      await AckAsync(session, nonce);
      await AskAsync(first);
      var confirmed = await session.Stream.NextAsync(Timeout);

      Assert.All(unconfirmed, sent => Assert.Equal((SessionEvents.Question, nonce), (sent.Type, (string?)sent.Data["nonce"])));
      Assert.Equal(SessionEvents.Question, confirmed.Type);
      Assert.False(confirmed.Data.AsObject().ContainsKey("nonce"), $"after the ack: {confirmed.Data}");
   }

   /// <summary>The attach delivers its probe in the hold that binds the tour, so a question that the reviewer asks after the attach follows the probe and carries its nonce.</summary>
   [Fact]
   public async Task Question_after_an_attach_follows_its_probe_with_the_same_nonce()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = CreateUnbound();

      await AttachAsync(session, id);
      var asked = await AskAsync(id);
      var nonce = await session.ProbeAsync();
      var question = await session.Stream.NextAsync(Timeout);

      Assert.Equal((SessionEvents.Question, (string)asked["messages"]![0]!["id"]!, nonce), (question.Type, (string)question.Data["questionId"]!, (string?)question.Data["nonce"]));
   }

   /// <summary>
   /// The session has no connection that reads its queue, which is full when the attach delivers its probe, so the delivery ends the session in the attach's own hold. The binding reads as none
   /// at once, and the settlement of the end publishes its release.
   /// </summary>
   [Fact]
   public async Task Probe_to_a_full_queue_ends_the_attaching_session_and_its_tour_is_released()
   {
      var agent = _harness.Sessions.TryOpen()!;
      var id = CreateUnbound();
      Assert.True(TourId.TryParse(id, out var tourId));
      Writer.Exclusive(locked =>
      {
         for (var i = 0; i < AgentSessions.EventCapacity; i++)
         {
            locked.Send(agent.Recipient.TakenOver(tourId));
         }
      });
      await using var stream = await EventStream.OpenAsync(_harness);

      var attached = _harness.Server.Services.GetRequiredService<SessionOperations>().AttachTour(Passes.Admit<Access.AnySession.Pass>("attach_tour", _harness.CallerOf(agent)), new AttachTourInput(tourId));
      var (endedAtOnce, holderAtOnce) = (agent.IsEnded, Writer.Exclusive(locked => locked.Holder(tourId)));
      Writer.SettleEnds();
      var bound = await stream.NextBesidesControlsAsync(Timeout);
      var released = await stream.NextBesidesControlsAsync(Timeout);

      Assert.Equal(tourId, attached.Value?.Id);
      Assert.True(endedAtOnce);
      Assert.Null(holderAtOnce);
      Assert.Equal((ServerEvents.TourBindingChanged.Name, agent.Id), (bound.Type, (string?)bound.Data["binding"]?["sessionId"]));
      Assert.Equal((ServerEvents.TourBindingChanged.Name, true), (released.Type, released.Data["binding"] is null));
      Assert.Null(await _harness.BindingOfAsync(id));
   }

   private TourWriter Writer => _harness.Server.Services.GetRequiredService<TourWriter>();

   private async Task<string> CreateAsync(AgentSessionConnection session) => (string)(await CallAsync(session.Request(_harness, "create_tour"), CreateBody))["id"]!;

   private string CreateUnbound() => (string)_harness.CreateUnboundTour(CreateBody)["id"]!;

   private async Task<string> AttachAsync(AgentSessionConnection session, string tourId) =>
      (string)(await CallAsync(session.Request(_harness, On("attach_tour", tourId)), "{}"))["id"]!;

   private async Task TakeOverAsync(AgentSessionConnection session, string tourId) =>
      await CallAsync(session.Request(_harness, On("attach_tour", tourId)), """{ "takeOver": true }""");

   private async Task<JsonNode> AckAsync(AgentSessionConnection session, string nonce) =>
      await CallAsync(session.Request(_harness, "ack"), $$"""{ "nonce": "{{nonce}}" }""");

   private async Task<JsonNode> AskAsync(string tourId) => await AskAsync(tourId, await _harness.UiCredentialAsync());

   private async Task<JsonNode> AskAsync(string tourId, string uiCredential) =>
      await CallAsync(new PreparedCall(On("ask_question", tourId), uiCredential), TourQuestion);

   private async Task<string> LevelAsync(string tourId) => (string)(await _harness.BindingOfAsync(tourId))!["level"]!;

   private async Task<JsonNode> CallAsync(PreparedCall call, string json)
   {
      using var response = await _harness.SendAsync(call, json);
      var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
      Assert.True(response.StatusCode == HttpStatusCode.OK, $"{call.Call.Name} returned {(int)response.StatusCode}: {body}");
      return SuccessBody.Output(body);
   }
}
