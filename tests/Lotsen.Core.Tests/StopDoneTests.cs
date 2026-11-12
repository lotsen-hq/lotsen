using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Git;
using Lotsen.Operations;
using Lotsen.Pinning;
using Lotsen.Sessions;
using Lotsen.Tours;
using static Lotsen.Tests.OperationCall;
using static Lotsen.Tests.ThreadCalls;

namespace Lotsen.Tests;

/// <summary>
/// Covers <c>set_stop_done</c>: the reviewer marks a version of a stop with content done or takes the mark back, the mark reaches the event stream but no session, a revision leaves it on
/// the earlier version, the stale marker and a <c>set_plan</c> that keeps the stop leave it as it is, and a <c>set_plan</c> that drops the stop drops it.
/// </summary>
[Trait("Category", "Reading")]
public sealed class StopDoneTests : IClassFixture<StopTourFixture>
{
   private readonly PinnedTourFixture _tour;

   public StopDoneTests(StopTourFixture fixture)
   {
      _tour = fixture.Tour;
   }

   private ServerHarness Harness => _tour.Harness;

   private AnswerCalls Answers => new(Harness);

   private ThreadCalls Calls => new(Harness);

   private static string Done(int? version) => $$"""{ "version": {{(version is { } number ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null")}} }""";

   private async Task<JsonNode> SetDoneAsync(string tourId, string stopId, int? version) => await Calls.CallAsync(await Calls.UiAsync(On("set_stop_done", tourId, stopId: stopId)), Done(version));

   private async Task<HttpResponseMessage> SendDoneAsync(string tourId, string stopId, int? version) =>
      await Calls.SendAsync(await Calls.UiAsync(On("set_stop_done", tourId, stopId: stopId)), Done(version));

   private async Task<JsonNode> StoredStopAsync(AnsweredTour tour) => TourJson.StopOf(await Harness.CallAsync(On("read_state", tour.Id)), tour.StopId);

   [Fact]
   public async Task Marking_the_current_version_done_and_taking_the_mark_back_are_stored_and_reach_the_event_stream()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await using var stream = await EventStream.OpenAsync(Harness);

      var done = await SetDoneAsync(tour.Id, tour.StopId, 1);
      var doneEvent = await stream.NextAsync(EventWait);
      var stored = await StoredStopAsync(tour);
      var cleared = await SetDoneAsync(tour.Id, tour.StopId, null);
      var clearedEvent = await stream.NextAsync(EventWait);

      Assert.Equal((tour.Id, tour.StopId, 1), ((string)done["tourId"]!, (string)done["stopId"]!, (int)done["doneVersion"]!));
      Assert.Equal(1, (int)stored["doneVersion"]!);
      Assert.Equal(ServerEvents.DoneVersionUpdated.Name, doneEvent.Type);
      Assert.True(JsonNode.DeepEquals(done, EventStream.WithoutRevision(doneEvent.Data)), $"The event differs from the response: {doneEvent.Data}");
      Assert.False(cleared.AsObject().ContainsKey("doneVersion"), $"The cleared mark is still in the response: {cleared}");
      Assert.Null((await StoredStopAsync(tour))["doneVersion"]);
      Assert.True(JsonNode.DeepEquals(cleared, EventStream.WithoutRevision(clearedEvent.Data)), $"The event differs from the response: {clearedEvent.Data}");
      Assert.True((DateTimeOffset)cleared["updatedAt"]! > (DateTimeOffset)done["updatedAt"]!, "Taking the mark back did not move the tour's update time.");
   }

   /// <summary>The marker skip is the stream's next event, so neither repeat published one.</summary>
   [Fact]
   public async Task Setting_the_mark_that_the_stop_has_changes_nothing_and_publishes_no_event()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      var first = await SetDoneAsync(tour.Id, tour.StopId, 1);
      await using var stream = await EventStream.OpenAsync(Harness);

      var again = await SetDoneAsync(tour.Id, tour.StopId, 1);
      await Calls.CallAsync(await Calls.UiAsync(On("set_stop_skipped", tour.Id, stopId: tour.StopId)), """{ "skipped": true }""");
      var next = await stream.NextAsync(EventWait);

      Assert.True(JsonNode.DeepEquals(first, again), $"The second mark differs from the first: {again}");
      Assert.Equal(ServerEvents.SkipsUpdated.Name, next.Type);
   }

   [Fact]
   public async Task A_revision_leaves_the_mark_on_the_earlier_version_and_a_mark_on_that_version_is_still_accepted()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await SetDoneAsync(tour.Id, tour.StopId, 1);

      await Answers.PutStopAsync(session, tour.Id, tour.StopId, AnswerCalls.Checks);
      var revised = await StoredStopAsync(tour);
      await SetDoneAsync(tour.Id, tour.StopId, 2);
      var late = await SetDoneAsync(tour.Id, tour.StopId, 1);

      Assert.Equal((2, 1), ((int)revised["version"]!, (int)revised["doneVersion"]!));
      Assert.Equal(1, (int)late["doneVersion"]!);
      Assert.Equal(1, (int)(await StoredStopAsync(tour))["doneVersion"]!);
   }

   [Fact]
   public async Task A_set_plan_that_keeps_the_stop_keeps_its_mark_and_one_that_drops_the_stop_drops_the_mark()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await SetDoneAsync(tour.Id, tour.StopId, 1);
      var plan = (await Harness.CallAsync(On("read_state", tour.Id)))["plan"]!["stops"]!.AsArray();
      var later = (string)plan[1]!["id"]!;

      await Calls.CallAsync(
         Calls.Agent(session, On("set_plan", tour.Id)),
         $$"""
         {
           "chapters": [{ "id": "renamed", "title": "Renamed" }],
           "stops": [{ "id": "{{later}}", "title": "Later", "kind": "inspect", "chapter": "renamed" }, { "id": "{{tour.StopId}}", "title": "Keep it?", "kind": "question", "chapter": "renamed" }]
         }
         """);
      var kept = await StoredStopAsync(tour);
      await Calls.CallAsync(
         Calls.Agent(session, On("set_plan", tour.Id)),
         $$"""{ "chapters": [{ "id": "code", "title": "Code" }], "stops": [{ "id": "{{later}}", "title": "Later", "kind": "inspect", "chapter": "code" }] }""");
      var dropped = await Harness.CallAsync(On("read_state", tour.Id));

      Assert.Equal(("Keep it?", 1), ((string)kept["title"]!, (int)kept["doneVersion"]!));
      Assert.Equal([later], dropped["plan"]!["stops"]!.AsArray().Select(stop => (string)stop!["id"]!));
      Assert.DoesNotContain("doneVersion", dropped.ToJsonString(), StringComparison.Ordinal);
   }

   /// <summary>The stop on b.txt goes stale on the new pin, whose head replaces line 9 of b.txt, as <see cref="Repin.Map"/> marks it.</summary>
   [Fact]
   public void The_stale_marker_leaves_the_mark_as_it_is()
   {
      var facts = IterationRows.Facts("b.txt").Changed("b.txt", IterationRows.Text(1, 10, (9, "nine")), new LineHunk(9, 1, 9, 1)).InDiff("b.txt").Build();
      var stop = IterationRows.Stop("s1", StopKind.Inspect, IterationRows.Passage("b.txt", 9, 9)) with { DoneVersion = 1 };

      var repin = Assert.IsType<Decision<Repin>.Decided>(Repin.Map(IterationRows.TourOf([stop]), IterationRows.Pin, IterationRows.NewPin, facts)).Value;

      var stale = Assert.IsType<Stop.Written>(Assert.Single(repin.Tour.Plan!.Stops));
      Assert.Equal((2, 1, 1), (stale.StaleOn, stale.Version, stale.DoneVersion));
   }

   [Fact]
   public async Task A_pending_stop_is_refused()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      var pending = (string)(await Harness.CallAsync(On("read_state", tour.Id)))["plan"]!["stops"]![1]!["id"]!;

      using var response = await SendDoneAsync(tour.Id, pending, 1);

      var error = await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("stopId", (string)error["field"]!);
   }

   [Fact]
   public async Task A_stop_that_the_plan_lacks_is_refused()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);

      using var response = await SendDoneAsync(tour.Id, "nosuchstop", 1);

      var error = await ErrorAsync(response, HttpStatusCode.NotFound, "stopNotFound");
      Assert.Equal("stopId", (string)error["field"]!);
   }

   [Theory]
   [InlineData(0)]
   [InlineData(-1)]
   [InlineData(2)]
   public async Task A_version_that_the_stop_lacks_is_refused(int version)
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);

      using var response = await SendDoneAsync(tour.Id, tour.StopId, version);

      var error = await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("version", (string)error["field"]!);
      Assert.Null((await StoredStopAsync(tour))["doneVersion"]);
   }

   [Fact]
   public async Task A_call_from_an_agent_session_is_refused()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);

      using var response = await Calls.SendAsync(Calls.Agent(session, On("set_stop_done", tour.Id, stopId: tour.StopId)), Done(1));

      await ErrorAsync(response, HttpStatusCode.Forbidden, "callerNotAllowed");
      Assert.Null((await StoredStopAsync(tour))["doneVersion"]);
   }

   /// <summary>The question asked after the mark is the session's next event, so the mark sent the session nothing.</summary>
   [Fact]
   public async Task The_agents_read_state_and_put_stop_result_leave_the_mark_out_and_the_mark_sends_the_session_no_event()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await session.ProbeAsync();

      await SetDoneAsync(tour.Id, tour.StopId, 1);
      var read = await Calls.CallAsync(Calls.Agent(session, On("read_state", tour.Id)));
      var marker = await Calls.AskAsync(tour.Id);
      var next = await session.Stream.NextAsync(EventWait);
      var written = await Answers.PutStopAsync(session, tour.Id, tour.StopId, AnswerCalls.Checks);

      Assert.Null(TourJson.StopOf(read, tour.StopId)["doneVersion"]);
      Assert.Equal((SessionEvents.Question, QuestionIdOf(marker)), (next.Type, (string)next.Data["questionId"]!));
      Assert.Null(written["doneVersion"]);
      Assert.Equal(1, (int)(await StoredStopAsync(tour))["doneVersion"]!);
   }
}
