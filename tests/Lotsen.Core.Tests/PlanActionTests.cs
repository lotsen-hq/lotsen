using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Adapter;
using Lotsen.Operations;
using Lotsen.Sessions;
using Microsoft.Extensions.DependencyInjection;
using static Lotsen.Tests.OperationCall;
using static Lotsen.Tests.ThreadCalls;
using static Lotsen.Tests.TourActionTests;

namespace Lotsen.Tests;

/// <summary>
/// Covers the tour actions on the whole tour: a reviewer's request that Claude change the plan as the reviewer's words say, or put its stops in the reviewer's proposed order, which the server
/// checks against the latest plan, stores in a thread on the tour and sends to the bound session as a question event that names the action's kind.
/// </summary>
[Trait("Category", "TourActions")]
public sealed class PlanActionTests : IClassFixture<StopTourFixture>, IAsyncLifetime
{
   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

   private const string TourAnchor = TourActionTests.TourTarget;

   private readonly PinnedTourFixture _tour;

   public PlanActionTests(StopTourFixture fixture)
   {
      _tour = fixture.Tour;
   }

   private ServerHarness Harness => _tour.Harness;

   private AnswerCalls Answers => new(Harness);

   private ThreadCalls Calls => new(Harness);

   private TourWriter Writer => Harness.Server.Services.GetRequiredService<TourWriter>();

   public ValueTask InitializeAsync() => ValueTask.CompletedTask;

   public ValueTask DisposeAsync()
   {
      Writer.Hook = null;
      return ValueTask.CompletedTask;
   }

   private static string ReorderBody(IEnumerable<string> stopIds, string? text = null)
   {
      var ids = string.Join(", ", stopIds.Select(id => $"\"{id}\""));
      return text is null
         ? $$"""{ "action": { "kind": "reorder", "stopIds": [{{ids}}] } }"""
         : $$"""{ "action": { "kind": "reorder", "stopIds": [{{ids}}] }, "text": "{{text}}" }""";
   }

   private async Task<string[]> StopIdsAsync(string tourId) =>
      [.. (await Harness.CallAsync(On("read_state", tourId)))["plan"]!["stops"]!.AsArray().Select(stop => (string)stop!["id"]!)];

   private async Task SetPlanAsync(AgentSessionConnection session, string tourId, string stops) =>
      await Calls.CallAsync(Calls.Agent(session, On("set_plan", tourId)), $$"""{ "chapters": [{ "id": "code", "title": "Code" }], "stops": {{stops}} }""");

   [Fact]
   public async Task Reorder_carries_the_proposed_stop_order_and_reaches_the_bound_session_naming_its_kind()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      var nonce = await session.ProbeAsync();
      string[] proposed = [.. (await StopIdsAsync(tour.Id)).Reverse()];

      var thread = await Calls.CallAsync(await Calls.UiAsync(On("request_tour_action", tour.Id)), ReorderBody(proposed, "Read the later stop first."));
      var sent = await session.Stream.NextAsync(Timeout);

      var question = Assert.Single(thread["messages"]!.AsArray())!;
      var action = new JsonObject { ["kind"] = "reorder", ["stopIds"] = new JsonArray([.. proposed.Select(id => JsonValue.Create(id))]) };
      Assert.True(JsonNode.DeepEquals(action, thread["action"]), $"action: {thread["action"]}");
      Assert.True(JsonNode.DeepEquals(new JsonObject { ["kind"] = "tour" }, thread["anchor"]), $"anchor: {thread["anchor"]}");
      Assert.Equal(("pending", session.Id), ((string)RequestOf(thread)["state"]!, (string)RequestOf(thread)["sessionId"]!));
      Assert.True(JsonNode.DeepEquals(thread, Assert.Single(await Calls.ThreadsAsync(tour.Id))), "The stored thread differs from the action's result.");
      Assert.Equal(SessionEvents.Question, sent.Type);
      Assert.Equal(("reorder", nonce, "Read the later stop first."), ((string?)sent.Data["action"], (string?)sent.Data["nonce"], (string?)sent.Data["text"]));
      var meta = ChannelNotifications.ParamsFor(new SessionEvent(sent.Type, sent.Data.ToJsonString()))!["meta"]!;
      Assert.Equal((SessionEvents.Question, "reorder"), ((string)meta["event"]!, (string?)meta["action"]));
   }

   [Fact]
   public async Task Replan_carries_the_reviewers_words_and_reaches_the_bound_session_naming_its_kind()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await session.ProbeAsync();

      var thread = await Calls.CallAsync(await Calls.UiAsync(On("request_tour_action", tour.Id)), ActionBody("replan", TourAnchor, "Add a stop on the tests."));
      var sent = await session.Stream.NextAsync(Timeout);

      var question = thread["messages"]![0]!;
      Assert.True(JsonNode.DeepEquals(new JsonObject { ["kind"] = "replan" }, thread["action"]), $"action: {thread["action"]}");
      Assert.Equal("Add a stop on the tests.", (string)question["text"]!);
      Assert.Equal(("replan", "Add a stop on the tests."), ((string?)sent.Data["action"], (string?)sent.Data["text"]));
      var parameters = ChannelNotifications.ParamsFor(new SessionEvent(sent.Type, sent.Data.ToJsonString()))!;
      Assert.Equal(("replan", "Add a stop on the tests."), ((string?)parameters["meta"]!["action"], (string)parameters["content"]!));
   }

   /// <summary>A reorder without words gives Claude a sentence of the server's in place of the words, which sends Claude to fetch_question for the proposed order.</summary>
   [Fact]
   public async Task Reorder_without_words_reaches_claude_with_a_sentence_that_names_the_kind_and_fetch_question()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await session.ProbeAsync();

      await Calls.CallAsync(await Calls.UiAsync(On("request_tour_action", tour.Id)), ReorderBody((await StopIdsAsync(tour.Id)).Reverse()));
      var sent = await session.Stream.NextAsync(Timeout);
      var content = (string)ChannelNotifications.ParamsFor(new SessionEvent(sent.Type, sent.Data.ToJsonString()))!["content"]!;

      Assert.Contains("reorder", content, StringComparison.Ordinal);
      Assert.Contains("fetch_question", content, StringComparison.Ordinal);
      Assert.DoesNotContain("stop it is about", content, StringComparison.Ordinal);
   }

   [Fact]
   public async Task Fetch_question_and_read_state_return_the_proposed_order_of_a_reorder()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      string[] proposed = [.. (await StopIdsAsync(tour.Id)).Reverse()];
      var thread = await Calls.CallAsync(await Calls.UiAsync(On("request_tour_action", tour.Id)), ReorderBody(proposed));

      var fetched = await Calls.FetchAsync(session, tour.Id, QuestionIdOf(thread));
      var read = await Calls.CallAsync(Calls.Agent(session, On("read_state", tour.Id)));

      Assert.Equal(proposed, fetched["thread"]!["action"]!["stopIds"]!.AsArray().Select(id => (string)id!));
      Assert.Equal(proposed, read["threads"]![0]!["action"]!["stopIds"]!.AsArray().Select(id => (string)id!));
   }

   /// <summary>A set_plan that only reorders the same stops lands between the reorder and its re-send, so the proposal still names each stop once and goes to Claude again.</summary>
   [Fact]
   public async Task Resend_of_a_reorder_after_a_plan_of_the_same_stops_sends_it_again()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await session.ProbeAsync();
      var ids = await StopIdsAsync(tour.Id);
      var thread = await Calls.CallAsync(await Calls.UiAsync(On("request_tour_action", tour.Id)), ReorderBody([ids[1], ids[0]]));
      await session.Stream.NextAsync(Timeout);
      await SetPlanAsync(session, tour.Id, $$"""[{ "id": "{{ids[1]}}", "title": "Later", "kind": "inspect", "chapter": "code" }, { "id": "{{ids[0]}}", "title": "Keep the rename?", "kind": "question", "chapter": "code" }]""");

      var resent = await Calls.CallAsync(await Calls.UiAsync(On("resend_question", tour.Id, questionId: QuestionIdOf(thread))));
      var sent = await session.Stream.NextAsync(Timeout);

      Assert.Equal(("reorder", (string)RequestOf(resent, 0, 1)["id"]!), ((string?)sent.Data["action"], (string?)sent.Data["requestId"]));
   }
}
