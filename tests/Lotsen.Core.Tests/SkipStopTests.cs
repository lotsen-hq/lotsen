using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Operations;
using Lotsen.Sessions;
using static Lotsen.Tests.OperationCall;
using static Lotsen.Tests.ThreadCalls;

namespace Lotsen.Tests;

/// <summary>
/// Covers <c>set_stop_skipped</c>: the reviewer skips and unskips stops of the plan as reading state stored on the tour, with or without a bound session, the skips reach the event stream and
/// read_state but no session, and a stop that set_plan drops takes its skip with it.
/// </summary>
[Trait("Category", "Reading")]
public sealed class SkipStopTests : IAsyncLifetime
{
   private const string Plan = """
      {
        "chapters": [{ "id": "code", "title": "Code" }],
        "stops": [{ "title": "First", "kind": "inspect", "chapter": "code" }, { "title": "Second", "kind": "inspect", "chapter": "code" }, { "title": "Third", "kind": "inspect", "chapter": "code" }]
      }
      """;

   private const string Skip = """{ "skipped": true }""";

   private const string Unskip = """{ "skipped": false }""";

   private ServerHarness _harness = null!;

   public async ValueTask InitializeAsync() => _harness = await ServerHarness.StartAsync();

   public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

   private ThreadCalls Calls => new(_harness);

   private static string[] Ids(JsonNode node) => [.. node.AsArray().Select(id => (string)id!)];

   private async Task<(string TourId, string[] StopIds)> PlannedAsync(AgentSessionConnection session)
   {
      var tourId = await Calls.CreateAsync(session);
      var tour = await Calls.CallAsync(Calls.Agent(session, On("set_plan", tourId)), Plan);
      return (tourId, [.. tour["plan"]!["stops"]!.AsArray().Select(stop => (string)stop!["id"]!)]);
   }

   private async Task<JsonNode> SetSkippedAsync(string tourId, string stopId, string body) => await Calls.CallAsync(await Calls.UiAsync(On("set_stop_skipped", tourId, stopId: stopId)), body);

   private async Task<string[]> StoredSkipsAsync(string tourId) => TourJson.SkippedIn(await _harness.CallAsync(On("read_state", tourId)));

   [Fact]
   public async Task Skip_and_unskip_with_no_session_attached_are_stored_on_the_tour_and_reach_the_event_stream()
   {
      var session = await AgentSessionConnection.OpenAsync(_harness);
      var (tourId, stops) = await PlannedAsync(session);
      await _harness.DropSessionAsync(session);
      await using var stream = await EventStream.OpenAsync(_harness);

      var skipped = await SetSkippedAsync(tourId, stops[1], Skip);
      var skippedEvent = await stream.NextAsync(EventWait);
      var stored = await StoredSkipsAsync(tourId);
      var unskipped = await SetSkippedAsync(tourId, stops[1], Unskip);
      var unskippedEvent = await stream.NextAsync(EventWait);

      Assert.Null(await _harness.BindingOfAsync(tourId));
      Assert.Equal(tourId, (string)skipped["tourId"]!);
      Assert.Equal([stops[1]], Ids(skipped["stopIds"]!));
      Assert.Equal([stops[1]], stored);
      Assert.Equal(ServerEvents.SkipsUpdated.Name, skippedEvent.Type);
      Assert.True(JsonNode.DeepEquals(skipped, EventStream.WithoutRevision(skippedEvent.Data)), $"The event differs from the response: {skippedEvent.Data}");
      Assert.Empty(Ids(unskipped["stopIds"]!));
      Assert.Empty(await StoredSkipsAsync(tourId));
      Assert.True(JsonNode.DeepEquals(unskipped, EventStream.WithoutRevision(unskippedEvent.Data)), $"The event differs from the response: {unskippedEvent.Data}");
      Assert.True((DateTimeOffset)unskipped["updatedAt"]! > (DateTimeOffset)skipped["updatedAt"]!, "Unskipping did not move the tour's update time.");
      Assert.Equal((string)unskipped["updatedAt"]!, (string)(await _harness.CallAsync(On("read_state", tourId)))["updatedAt"]!);
   }

   [Fact]
   public async Task Skips_keep_plan_order_whatever_order_the_reviewer_skips_in()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var (tourId, stops) = await PlannedAsync(session);

      await SetSkippedAsync(tourId, stops[2], Skip);
      var both = await SetSkippedAsync(tourId, stops[0], Skip);

      Assert.Equal([stops[0], stops[2]], Ids(both["stopIds"]!));
      Assert.Equal([stops[0], stops[2]], await StoredSkipsAsync(tourId));
   }

   /// <summary>The marker skip is the stream's next event, so neither repeat published one.</summary>
   [Fact]
   public async Task Skip_of_a_skipped_stop_and_unskip_of_a_stop_not_skipped_change_nothing_and_publish_no_event()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var (tourId, stops) = await PlannedAsync(session);
      var first = await SetSkippedAsync(tourId, stops[0], Skip);
      await using var stream = await EventStream.OpenAsync(_harness);

      var again = await SetSkippedAsync(tourId, stops[0], Skip);
      var notSkipped = await SetSkippedAsync(tourId, stops[1], Unskip);
      var marker = await SetSkippedAsync(tourId, stops[2], Skip);
      var next = await stream.NextAsync(EventWait);

      Assert.True(JsonNode.DeepEquals(first, again), $"The second skip differs from the first: {again}");
      Assert.True(JsonNode.DeepEquals(first, notSkipped), $"The unskip of a stop not skipped changed the skips: {notSkipped}");
      Assert.True(JsonNode.DeepEquals(marker, EventStream.WithoutRevision(next.Data)), $"The next event is not the marker's: {next.Type} {next.Data}");
   }

   /// <summary>The question asked after the skip is the session's next event, so the skip sent the session nothing.</summary>
   [Fact]
   public async Task Skip_shows_in_read_state_for_the_bound_session_and_sends_it_no_event()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var (tourId, stops) = await PlannedAsync(session);
      await session.ProbeAsync();

      await SetSkippedAsync(tourId, stops[1], Skip);
      var read = await Calls.CallAsync(Calls.Agent(session, On("read_state", tourId)));
      var marker = await Calls.AskAsync(tourId);
      var next = await session.Stream.NextAsync(EventWait);

      Assert.Equal([stops[1]], TourJson.SkippedIn(read));
      Assert.Equal((SessionEvents.Question, QuestionIdOf(marker)), (next.Type, (string)next.Data["questionId"]!));
   }

   [Fact]
   public async Task Set_plan_drops_the_skip_of_a_stop_it_leaves_out_and_keeps_a_kept_stops_skip_in_the_new_order()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var (tourId, stops) = await PlannedAsync(session);
      await SetSkippedAsync(tourId, stops[0], Skip);
      await SetSkippedAsync(tourId, stops[1], Skip);
      var replan = $$"""
         {
           "chapters": [{ "id": "code", "title": "Code" }],
           "stops": [{ "id": "{{stops[2]}}", "title": "Third", "kind": "inspect", "chapter": "code" }, { "id": "{{stops[1]}}", "title": "Second", "kind": "inspect", "chapter": "code" }]
         }
         """;

      var replanned = await Calls.CallAsync(Calls.Agent(session, On("set_plan", tourId)), replan);
      var again = await Calls.CallAsync(
         Calls.Agent(session, On("set_plan", tourId)),
         replan.Replace("\"Third\"", "\"Third again\"", StringComparison.Ordinal));

      Assert.Equal([stops[1]], TourJson.SkippedIn(replanned));
      Assert.Equal([stops[1]], TourJson.SkippedIn(again));
      Assert.Equal([stops[1]], await StoredSkipsAsync(tourId));
   }

   [Fact]
   public async Task Set_plan_that_puts_skipped_stops_in_another_order_stores_the_skips_in_that_order()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var (tourId, stops) = await PlannedAsync(session);
      await SetSkippedAsync(tourId, stops[0], Skip);
      await SetSkippedAsync(tourId, stops[2], Skip);
      var reversed = string.Join(", ", stops.Reverse().Select(id => $$"""{ "id": "{{id}}", "title": "A stop", "kind": "inspect", "chapter": "code" }"""));

      await Calls.CallAsync(Calls.Agent(session, On("set_plan", tourId)), $$"""{ "chapters": [{ "id": "code", "title": "Code" }], "stops": [{{reversed}}] }""");

      Assert.Equal([stops[2], stops[0]], await StoredSkipsAsync(tourId));
   }

   [Fact]
   public async Task Skip_of_a_stop_that_the_plan_lacks_is_refused()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var (tourId, _) = await PlannedAsync(session);

      using var response = await Calls.SendAsync(await Calls.UiAsync(On("set_stop_skipped", tourId, stopId: "nosuchstop")), Skip);

      var error = await ErrorAsync(response, HttpStatusCode.NotFound, "stopNotFound");
      Assert.Equal("stopId", (string)error["field"]!);
      Assert.Empty(await StoredSkipsAsync(tourId));
   }

   [Fact]
   public async Task Skip_on_a_tour_without_a_plan_is_refused()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var tourId = await Calls.CreateAsync(session);

      using var response = await Calls.SendAsync(await Calls.UiAsync(On("set_stop_skipped", tourId, stopId: "nosuchstop")), Skip);

      await ErrorAsync(response, HttpStatusCode.NotFound, "stopNotFound");
   }

   [Fact]
   public async Task Skip_on_an_unknown_tour_is_not_found()
   {
      using var response = await Calls.SendAsync(await Calls.UiAsync(On("set_stop_skipped", "aaaaaaaaaaaaaaaa", stopId: "s1")), Skip);

      await ErrorAsync(response, HttpStatusCode.NotFound, "tourNotFound");
   }

   [Theory]
   [InlineData("{}")]
   [InlineData("""{ "skipped": "yes" }""")]
   [InlineData("""{ "skipped": true, "extra": 1 }""")]
   public async Task Skip_with_a_missing_or_malformed_value_is_refused(string body)
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var (tourId, stops) = await PlannedAsync(session);

      using var response = await Calls.SendAsync(await Calls.UiAsync(On("set_stop_skipped", tourId, stopId: stops[0])), body);

      await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Empty(await StoredSkipsAsync(tourId));
   }

   [Fact]
   public void Set_stop_skipped_is_an_operation_of_the_ui_and_no_agent_tool()
   {
      var operation = OperationCatalog.Get("set_stop_skipped");

      Assert.False(operation.IsAgentTool);
      Assert.DoesNotContain(OperationCatalog.AgentTools, tool => tool.Name == "set_stop_skipped");
   }
}
