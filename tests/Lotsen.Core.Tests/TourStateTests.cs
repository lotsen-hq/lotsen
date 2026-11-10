using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Operations;
using Lotsen.Sessions;
using Microsoft.Extensions.DependencyInjection;
using static Lotsen.Tests.OperationCall;
using static Lotsen.Tests.ThreadCalls;

namespace Lotsen.Tests;

/// <summary>
/// Covers the tour states that the tour list shows, as <see cref="Lotsen.Tours.TourStates"/> derives them from the plan, the reading progress that <c>record_reading_progress</c> stores and the archive
/// that <c>set_tour_archived</c> sets, and the <c>tourStateChanged</c> events that carry each change.
/// </summary>
[Trait("Category", "TourStates")]
public sealed class TourStateTests : IClassFixture<StopTourFixture>, IAsyncLifetime
{
   private const string TwoStops = """
      {
        "chapters": [{ "id": "code", "title": "Code" }],
        "stops": [{ "title": "First", "kind": "inspect", "chapter": "code" }, { "title": "Second", "kind": "inspect", "chapter": "code" }]
      }
      """;

   private readonly PinnedTourFixture _tour;

   public TourStateTests(StopTourFixture fixture)
   {
      _tour = fixture.Tour;
   }

   private ServerHarness Harness => _tour.Harness;

   private ThreadCalls Calls => new(Harness);

   private TourWriter Writer => Harness.Server.Services.GetRequiredService<TourWriter>();

   public ValueTask InitializeAsync() => ValueTask.CompletedTask;

   public ValueTask DisposeAsync()
   {
      Writer.Hook = null;
      return ValueTask.CompletedTask;
   }

   private static string StopBody(string stopId) => $$"""{ "stopId": "{{stopId}}" }""";

   private const string SummaryBody = """{ "summary": true }""";

   private async Task<string> CreatePinnedAsync(AgentSessionConnection session)
   {
      var tourId = await Calls.CreateAsync(session);
      await Harness.PinsIdleAsync();
      return tourId;
   }

   private async Task<string[]> SetPlanAsync(AgentSessionConnection session, string tourId, string plan)
   {
      var tour = await Calls.CallAsync(Calls.Agent(session, On("set_plan", tourId)), plan);
      return [.. tour["plan"]!["stops"]!.AsArray().Select(stop => (string)stop!["id"]!)];
   }

   private async Task PutStopAsync(AgentSessionConnection session, string tourId, string stopId) =>
      await Calls.CallAsync(Calls.Agent(session, On("put_stop", tourId, stopId: stopId)), $$"""{ "headline": "A headline.", "passages": [{{AnswerCalls.Passage}}] }""");

   private async Task<JsonNode> RecordAsync(string tourId, string body) => await Calls.CallAsync(await Calls.UiAsync(On("record_reading_progress", tourId)), body);

   private async Task<JsonNode> ArchiveAsync(string tourId, bool archived) => await Calls.CallAsync(await Calls.UiAsync(On("set_tour_archived", tourId)), $$"""{ "archived": {{(archived ? "true" : "false")}} }""");

   private async Task<JsonNode> RowAsync(string tourId) =>
      (await Calls.CallAsync(await Calls.UiAsync("list_tours")))["tours"]!.AsArray().Single(row => (string)row!["id"]! == tourId)!;

   private async Task<string> StateAsync(string tourId) => (string)(await RowAsync(tourId))["state"]!;

   private async Task<JsonNode> ReadAsync(string tourId) => await Harness.CallAsync(On("read_state", tourId));

   // Skips every event until one of the type arrives, so a test reads the state events of its own tour among the plan and stop events.
   private static async Task<JsonNode> NextOfAsync(EventStream stream, string type, string tourId)
   {
      while (true)
      {
         var next = await stream.NextAsync(EventWait);
         if (next.Type == type && (string?)next.Data["tourId"] == tourId)
         {
            return next.Data;
         }
      }
   }

   [Fact]
   public async Task Tour_moves_through_generating_ready_in_progress_and_finished_as_stops_are_written_a_stop_opens_and_the_summary_opens()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tourId = await CreatePinnedAsync(session);
      await using var stream = await EventStream.OpenAsync(Harness);
      var states = new List<string> { await StateAsync(tourId) };

      var stops = await SetPlanAsync(session, tourId, TwoStops);
      states.Add(await StateAsync(tourId));
      await PutStopAsync(session, tourId, stops[0]);
      states.Add(await StateAsync(tourId));
      await PutStopAsync(session, tourId, stops[1]);
      var ready = await NextOfAsync(stream, ServerEvents.TourStateChanged.Name, tourId);
      states.Add(await StateAsync(tourId));
      var opened = await RecordAsync(tourId, StopBody(stops[0]));
      var inProgress = await NextOfAsync(stream, ServerEvents.TourStateChanged.Name, tourId);
      states.Add(await StateAsync(tourId));
      var summary = await RecordAsync(tourId, SummaryBody);
      var finished = await NextOfAsync(stream, ServerEvents.TourStateChanged.Name, tourId);
      states.Add(await StateAsync(tourId));

      Assert.Equal(["generating", "generating", "generating", "ready", "inProgress", "finished"], states);
      Assert.Equal("ready", (string)ready["state"]!);
      Assert.Equal("inProgress", (string)opened["state"]!);
      Assert.True(JsonNode.DeepEquals(new JsonObject { ["tourId"] = tourId, ["updatedAt"] = opened["updatedAt"]!.DeepClone(), ["state"] = "inProgress", ["pullRequestState"] = "active" }, EventStream.WithoutRevision(inProgress)), $"The event differs from the write: {inProgress}");
      Assert.Equal("finished", (string)summary["state"]!);
      Assert.Equal("finished", (string)finished["state"]!);
      Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{ "finished": true }"""), summary["progress"]), $"The summary's progress is {summary["progress"]}.");
      Assert.Equal((string)finished["updatedAt"]!, (string)(await RowAsync(tourId))["updatedAt"]!);
   }

   /// <summary>A set_plan that adds a pending stop is what would make an unopened tour generating again.</summary>
   [Fact]
   public async Task Tour_whose_stop_the_reviewer_opened_stays_in_progress_while_Claude_adds_and_writes_new_stops()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tourId = await CreatePinnedAsync(session);
      var stops = await SetPlanAsync(session, tourId, TwoStops);
      await PutStopAsync(session, tourId, stops[0]);
      await RecordAsync(tourId, StopBody(stops[0]));
      await using var stream = await EventStream.OpenAsync(Harness);

      var more = await SetPlanAsync(session, tourId, $$"""
         {
           "chapters": [{ "id": "code", "title": "Code" }],
           "stops": [{ "id": "{{stops[0]}}", "title": "First", "kind": "inspect", "chapter": "code" }, { "id": "{{stops[1]}}", "title": "Second", "kind": "inspect", "chapter": "code" },
             { "title": "Third", "kind": "inspect", "chapter": "code" }]
         }
         """);
      var afterPlan = await StateAsync(tourId);
      await PutStopAsync(session, tourId, more[1]);
      await PutStopAsync(session, tourId, more[2]);
      var marker = await ArchiveAsync(tourId, archived: true);
      var next = await NextOfAsync(stream, ServerEvents.TourStateChanged.Name, tourId);

      Assert.Equal("inProgress", afterPlan);
      Assert.Equal("archived", (string)next["state"]!);
      Assert.Equal((string)marker["updatedAt"]!, (string)next["updatedAt"]!);
   }

   [Fact]
   public async Task Finished_tour_stays_finished_when_the_reviewer_opens_a_stop_again_and_when_Claude_adds_a_pending_stop()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tourId = await CreatePinnedAsync(session);
      var stops = await SetPlanAsync(session, tourId, TwoStops);
      await RecordAsync(tourId, SummaryBody);

      var reopened = await RecordAsync(tourId, StopBody(stops[1]));
      await SetPlanAsync(session, tourId, $$"""
         {
           "chapters": [{ "id": "code", "title": "Code" }],
           "stops": [{ "id": "{{stops[1]}}", "title": "Second", "kind": "inspect", "chapter": "code" }, { "title": "New", "kind": "inspect", "chapter": "code" }]
         }
         """);

      Assert.Equal("finished", (string)reopened["state"]!);
      Assert.True(JsonNode.DeepEquals(JsonNode.Parse($$"""{ "lastStop": "{{stops[1]}}", "finished": true }"""), reopened["progress"]), $"The progress is {reopened["progress"]}.");
      Assert.Equal("finished", await StateAsync(tourId));
   }

   /// <summary>The stop write waits after its passage checks, so the reviewer opens the first stop before the last pending stop gets its content.</summary>
   [Fact]
   public async Task Stop_opened_while_Claude_writes_the_last_pending_stop_leaves_the_tour_in_progress_and_never_ready()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tourId = await CreatePinnedAsync(session);
      var stops = await SetPlanAsync(session, tourId, TwoStops);
      await PutStopAsync(session, tourId, stops[0]);
      var checkedStop = new TaskCompletionSource();
      var release = new TaskCompletionSource();
      Writer.Hook = WriterHooks.AfterGit(async _ =>
      {
         checkedStop.TrySetResult();
         await release.Task;
      }, "put_stop");
      await using var stream = await EventStream.OpenAsync(Harness);

      var writing = PutStopAsync(session, tourId, stops[1]);

      // A put_stop refused before the hook ends the wait with its own error.
      await await Task.WhenAny(checkedStop.Task, writing).WaitAsync(EventWait, TestContext.Current.CancellationToken);
      Assert.True(checkedStop.Task.IsCompleted, "put_stop ended without reaching the hook.");
      Writer.Hook = null;
      await RecordAsync(tourId, StopBody(stops[0]));
      release.SetResult();
      await writing;
      var first = await NextOfAsync(stream, ServerEvents.TourStateChanged.Name, tourId);
      var marker = await RecordAsync(tourId, SummaryBody);
      var second = await NextOfAsync(stream, ServerEvents.TourStateChanged.Name, tourId);

      Assert.Equal("inProgress", (string)first["state"]!);
      Assert.Equal("finished", (string)second["state"]!);
      Assert.Equal((string)marker["updatedAt"]!, (string)second["updatedAt"]!);
   }

   [Fact]
   public async Task Archive_and_unarchive_with_no_session_attached_set_the_archived_state_and_return_the_tour_to_its_derived_state()
   {
      var session = await AgentSessionConnection.OpenAsync(Harness);
      var tourId = await CreatePinnedAsync(session);
      var stops = await SetPlanAsync(session, tourId, TwoStops);
      await RecordAsync(tourId, StopBody(stops[1]));
      await Harness.DropSessionAsync(session);
      await using var stream = await EventStream.OpenAsync(Harness);

      var archived = await ArchiveAsync(tourId, archived: true);
      var archivedEvent = await NextOfAsync(stream, ServerEvents.TourStateChanged.Name, tourId);
      var archivedRow = await RowAsync(tourId);
      var stored = await ReadAsync(tourId);
      var unarchived = await ArchiveAsync(tourId, archived: false);
      var unarchivedEvent = await NextOfAsync(stream, ServerEvents.TourStateChanged.Name, tourId);

      Assert.Null(await Harness.BindingOfAsync(tourId));
      Assert.Equal("archived", (string)archived["state"]!);
      Assert.True(JsonNode.DeepEquals(archived, EventStream.WithoutRevision(archivedEvent)), $"The event differs from the response: {archivedEvent}");
      Assert.Equal("archived", (string)archivedRow["state"]!);
      Assert.True((bool)stored["archived"]!);
      Assert.Equal("inProgress", (string)unarchived["state"]!);
      Assert.True(JsonNode.DeepEquals(unarchived, EventStream.WithoutRevision(unarchivedEvent)), $"The event differs from the response: {unarchivedEvent}");
      Assert.Equal("inProgress", await StateAsync(tourId));
      Assert.Null((await ReadAsync(tourId))["archived"]);
   }

   /// <summary>The marker archive's event is the stream's next state event, so neither repeat published one.</summary>
   [Fact]
   public async Task Recording_the_place_held_already_and_archiving_an_archived_tour_store_nothing_and_publish_no_event()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tourId = await CreatePinnedAsync(session);
      var stops = await SetPlanAsync(session, tourId, TwoStops);
      var first = await RecordAsync(tourId, StopBody(stops[0]));
      await ArchiveAsync(tourId, archived: true);
      var archivedAt = (string)(await ReadAsync(tourId))["updatedAt"]!;
      await using var stream = await EventStream.OpenAsync(Harness);

      var again = await RecordAsync(tourId, StopBody(stops[0]));
      var archivedAgain = await ArchiveAsync(tourId, archived: true);
      var marker = await ArchiveAsync(tourId, archived: false);
      var next = await stream.NextAsync(EventWait);

      Assert.Equal(first["progress"]!.ToJsonString(), again["progress"]!.ToJsonString());
      Assert.Equal(archivedAt, (string)again["updatedAt"]!);
      Assert.Equal(archivedAt, (string)archivedAgain["updatedAt"]!);
      Assert.Equal(ServerEvents.TourStateChanged.Name, next.Type);
      Assert.True(JsonNode.DeepEquals(marker, EventStream.WithoutRevision(next.Data)), $"The next event is not the marker's: {next.Data}");
   }

   /// <summary>A move to another stop keeps the tour in progress, so it publishes no state event, and the marker archive's event comes next.</summary>
   [Fact]
   public async Task Move_to_another_stop_stores_the_last_stop_and_publishes_no_state_event()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tourId = await CreatePinnedAsync(session);
      var stops = await SetPlanAsync(session, tourId, TwoStops);
      var first = await RecordAsync(tourId, StopBody(stops[0]));
      await using var stream = await EventStream.OpenAsync(Harness);

      var moved = await RecordAsync(tourId, StopBody(stops[1]));
      var marker = await ArchiveAsync(tourId, archived: true);
      var next = await stream.NextAsync(EventWait);

      Assert.True((DateTimeOffset)moved["updatedAt"]! > (DateTimeOffset)first["updatedAt"]!, "The move did not move the tour's update time.");
      Assert.True(JsonNode.DeepEquals(JsonNode.Parse($$"""{ "lastStop": "{{stops[1]}}", "finished": false }"""), (await ReadAsync(tourId))["progress"]), "read_state lacks the move.");
      Assert.True(JsonNode.DeepEquals(marker, EventStream.WithoutRevision(next.Data)), $"The next event is not the marker's: {next.Type} {next.Data}");
   }

   [Fact]
   public void Progress_and_archive_are_operations_of_the_ui_and_no_agent_tools()
   {
      Assert.All(["record_reading_progress", "set_tour_archived"], name =>
      {
         Assert.False(OperationCatalog.Get(name).IsAgentTool, $"{name} is offered as an agent tool.");
         Assert.DoesNotContain(OperationCatalog.AgentTools, tool => tool.Name == name);
      });
   }

   [Theory]
   [InlineData("""{ "stopId": "nosuchstop" }""", HttpStatusCode.NotFound, "stopNotFound")]
   [InlineData("""{ }""", HttpStatusCode.BadRequest, "invalidInput")]
   [InlineData("""{ "stopId": "STOP", "summary": true }""", HttpStatusCode.BadRequest, "invalidInput")]
   [InlineData("""{ "summary": false }""", HttpStatusCode.BadRequest, "invalidInput")]
   public async Task Progress_that_names_no_stop_of_the_plan_or_both_places_is_refused(string body, HttpStatusCode status, string code)
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tourId = await CreatePinnedAsync(session);
      var stops = await SetPlanAsync(session, tourId, TwoStops);

      using var response = await Calls.SendAsync(await Calls.UiAsync(On("record_reading_progress", tourId)), body.Replace("STOP", stops[0], StringComparison.Ordinal));

      await ErrorAsync(response, status, code);
      Assert.Null((await ReadAsync(tourId))["progress"]);
   }

   [Fact]
   public async Task Progress_on_a_stop_of_a_tour_without_a_plan_is_refused_and_the_summary_is_recorded()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tourId = await CreatePinnedAsync(session);

      using var response = await Calls.SendAsync(await Calls.UiAsync(On("record_reading_progress", tourId)), StopBody("nosuchstop"));
      var summary = await RecordAsync(tourId, SummaryBody);

      await ErrorAsync(response, HttpStatusCode.NotFound, "stopNotFound");
      Assert.Equal("finished", (string)summary["state"]!);
   }
}
