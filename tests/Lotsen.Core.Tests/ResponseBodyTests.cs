using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Operations;
using static Lotsen.Tests.OperationCall;
using static Lotsen.Tests.ThreadCalls;

namespace Lotsen.Tests;

/// <summary>
/// Covers the body of an operation's response: the envelope of a success with the tour's revision and, for the UI, the tour's controls, and the bare refusal. The controls' closed offers
/// carry the refusals that the operations give.
/// </summary>
[Trait("Category", "Api")]
public sealed class ResponseBodyTests : IAsyncLifetime
{
   private ServerHarness _harness = null!;
   private ThreadCalls _calls = null!;

   public async ValueTask InitializeAsync()
   {
      _harness = await ServerHarness.StartAsync();
      _calls = new ThreadCalls(_harness);
   }

   public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

   [Fact]
   public async Task Ui_write_on_a_tour_answers_its_output_with_the_stored_revision_and_the_controls_at_it()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);

      var (status, body) = await SendAsync(await _calls.UiAsync(On("ask_question", id)), NoteBody("Why?"));

      Assert.Equal(HttpStatusCode.OK, status);
      Assert.Equal(["output", "revision", "controls"], body.AsObject().Select(property => property.Key));
      Assert.Equal("Why?", (string?)body["output"]!["messages"]![0]!["text"]);
      Assert.Equal(Revision(id), (long)body["revision"]!);
      Assert.Equal((id, Revision(id)), ((string)body["controls"]!["tourId"]!, (long)body["controls"]!["revision"]!));
   }

   [Fact]
   public async Task Read_state_answers_the_tours_revision_and_only_the_ui_gets_the_controls()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);

      var (_, ui) = await SendAsync(await _calls.UiAsync(On("read_state", id)));
      var (_, agent) = await SendAsync(_calls.Agent(session, On("read_state", id)));

      Assert.Equal(Revision(id), (long)ui["revision"]!);
      Assert.NotNull(ui["controls"]);
      Assert.Equal(Revision(id), (long)agent["revision"]!);
      Assert.False(agent.AsObject().ContainsKey("controls"), $"The agent's body carries controls: {agent}");
   }

   [Fact]
   public async Task Tour_list_answers_neither_a_revision_nor_controls()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      await _calls.CreateAsync(session);

      var (_, body) = await SendAsync(await _calls.UiAsync("list_tours"));

      Assert.Equal(["output"], body.AsObject().Select(property => property.Key));
   }

   [Fact]
   public async Task Refusal_is_the_bare_error()
   {
      var (status, body) = await SendAsync(await _calls.UiAsync(On("read_state", "aaaaaaaaaaaaaaaa")));

      Assert.Equal(HttpStatusCode.NotFound, status);
      Assert.Equal("tourNotFound", (string?)body["code"]);
      Assert.False(body.AsObject().ContainsKey("output"), $"The refusal carries an output: {body}");
   }

   /// <summary>The tour has no plan, so its state closes the replan.</summary>
   [Fact]
   public async Task Closed_replan_carries_the_refusal_of_request_tour_action()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);

      var (_, read) = await SendAsync(await _calls.UiAsync(On("read_state", id)));
      using var refused = await _calls.SendAsync(await _calls.UiAsync(On("request_tour_action", id)), """{ "action": { "kind": "replan" }, "text": "Fewer stops." }""");

      var error = await ErrorAsync(refused, HttpStatusCode.BadRequest, "invalidInput");
      AssertCarries(error, read["controls"]!["offers"]!["replan"]!);
   }

   /// <summary>The session that held the tour dropped, so the missing session closes the ask.</summary>
   [Fact]
   public async Task Closed_ask_without_a_session_carries_the_refusal_of_ask_question()
   {
      var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      await _harness.DropSessionAsync(session);

      var (_, read) = await SendAsync(await _calls.UiAsync(On("read_state", id)));
      using var refused = await _calls.SendAsync(await _calls.UiAsync(On("ask_question", id)), NoteBody("Why?"));

      var error = await ErrorAsync(refused, HttpStatusCode.Conflict, "noSessionAttached");
      AssertCarries(error, read["controls"]!["offers"]!["ask"]!);
   }

   private static void AssertCarries(JsonNode error, JsonNode offer) =>
      Assert.Equal(("closed", (string?)error["code"], (string?)error["problem"]), ((string?)offer["state"], (string?)offer["code"], (string?)offer["reason"]));

   private long Revision(string tourId) => _harness.StoredTour(Lotsen.Tours.TourId.TryParse(tourId, out var id) ? id : throw new InvalidOperationException("The tour ID does not parse."))!.Revision;

   private async Task<(HttpStatusCode Status, JsonNode Body)> SendAsync(PreparedCall call, string? json = null)
   {
      using var response = await _calls.SendAsync(call, json);
      return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!);
   }
}
