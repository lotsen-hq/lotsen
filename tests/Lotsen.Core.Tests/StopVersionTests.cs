using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Operations;
using Lotsen.Server;
using Lotsen.Sessions;
using Microsoft.Extensions.DependencyInjection;
using static Lotsen.Tests.OperationCall;
using static Lotsen.Tests.ThreadCalls;

namespace Lotsen.Tests;

/// <summary>
/// Covers the versions of a stop: a write over content keeps the replaced content as an earlier version, and settles the answers and the stop text threads that the replaced version held.
/// </summary>
[Trait("Category", "Versions")]
public sealed class StopVersionTests : IClassFixture<StopTourFixture>, IAsyncLifetime
{
   private const string Passage = """{ "side": "head", "path": "src/modified.txt", "startLine": 2, "endLine": 2, "role": "primary" }""";

   private readonly PinnedTourFixture _tour;

   public StopVersionTests(StopTourFixture fixture)
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

   [Fact]
   public async Task Writing_a_ready_stop_again_keeps_the_earlier_content_as_version_1_and_stores_the_new_content_as_version_2()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      var first = (await ReadAsync(tour.Id))["plan"]!["stops"]![0]!;

      var second = await PutAsync(session, tour, """{ "headline": "The second headline.", "how": "It *sorts*." }""");

      Assert.Equal((1, "ready"), ((int)first["version"]!, (string)first["status"]!));
      Assert.Equal((2, "revised"), ((int)second["version"]!, (string)second["status"]!));
      var agentRead = await Calls.CallAsync(Calls.Agent(session, On("read_state", tour.Id)));
      Assert.True(JsonNode.DeepEquals(second, agentRead["plan"]!["stops"]![0]), $"read_state differs from put_stop's result: {agentRead}");
      var read = await ReadAsync(tour.Id);
      var earlier = Assert.Single(TourJson.EarlierOf(read, tour.StopId))!;
      Assert.Equal(("Keep the rename?", "question"), ((string)earlier["title"]!, (string)earlier["kind"]!));
      Assert.True(JsonNode.DeepEquals(first["content"], earlier["content"]), $"The earlier version differs from the content it replaced: {earlier}");
      Assert.Equal("src/modified.txt", (string)earlier["content"]!["passages"]![0]!["path"]!);

      var ui = await ReadAsTheUiAsync(tour.Id);
      var html = TourJson.EarlierOf(ui, tour.StopId)[0]!["content"]!["html"]!;
      Assert.Equal(("<p>A headline.</p>\n", "<p>Keep the digit?</p>\n"), ((string)html["headline"]!, (string)html["checks"]![0]!["question"]!));
      Assert.Equal("<p>It <em>sorts</em>.</p>\n", (string)ui["plan"]!["stops"]![0]!["content"]!["html"]!["how"]!);
   }

   /// <summary>Claude wrote every earlier version, so an agent session's read leaves them out and shows only each stop's current version number.</summary>
   [Fact]
   public async Task Agent_read_shows_the_current_version_number_without_the_earlier_versions()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await PutAsync(session, tour, """{ "headline": "The second headline." }""");
      await PutAsync(session, tour, """{ "headline": "The third headline." }""");

      var agent = await Calls.CallAsync(Calls.Agent(session, On("read_state", tour.Id)));

      Assert.Equal((3, "revised"), ((int)agent["plan"]!["stops"]![0]!["version"]!, (string)agent["plan"]!["stops"]![0]!["status"]!));
      Assert.Empty(TourJson.EarlierOf(agent, tour.StopId));
      Assert.Equal(["A headline.", "The second headline."], TourJson.EarlierOf(await ReadAsync(tour.Id), tour.StopId).Select(version => (string)version!["content"]!["headline"]!));
   }

   [Fact]
   public async Task Answers_on_a_kept_check_keep_their_state_and_answers_on_a_dropped_or_reidentified_check_become_outdated_on_the_replaced_version()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await ThreeChecksAsync(session);
      await Answers.AnswerAsync(tour, 0, "Kept.");
      await Answers.AnswerAsync(tour, 1, "Rewritten.");
      await Answers.SendAsync(tour.Id);
      await Answers.AnswerAsync(tour, 2, "Dropped.");

      var revised = await PutAsync(session, tour, $$"""{ "headline": "A headline.", "checks": [{ "id": "{{tour.CheckIds[0]}}", "question": "A, reworded?" }, { "question": "B?" }] }""");

      var newCheck = (string)revised["content"]!["checks"]![1]!["id"]!;
      Assert.DoesNotContain(newCheck, tour.CheckIds);
      Assert.Equal(
         [(tour.CheckIds[0], "sent", null), (tour.CheckIds[1], "sent", 1), (tour.CheckIds[2], "draft", 1)],
         (await Answers.AnswersAsync(tour.Id)).Select(answer => ((string)answer!["checkId"]!, (string)answer["state"]!, (int?)answer["outdatedOn"])));
      var agent = await Calls.CallAsync(Calls.Agent(session, On("read_state", tour.Id)));
      Assert.Equal([(tour.CheckIds[0], null), (tour.CheckIds[1], 1)], agent["answers"]!.AsArray().Select(answer => ((string)answer!["checkId"]!, (int?)answer["outdatedOn"])));

      await Calls.CallAsync(await Calls.UiAsync(On("answer_check", tour.Id, stopId: tour.StopId, checkId: newCheck)), """{ "text": "New." }""");
      var sent = await Answers.SendAsync(tour.Id);

      Assert.Equal([newCheck], sent["answers"]!.AsArray().Select(answer => (string)answer!["checkId"]!));
   }

   /// <summary>A sent answer whose session lost the tour awaits the next send, as <see cref="Lotsen.Tours.CheckAnswer.AwaitsSending"/> describes, unless a revision made it outdated.</summary>
   [Fact]
   public async Task Send_after_a_take_over_leaves_out_an_outdated_answer_whose_batch_went_to_the_old_session()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await Answers.AnswerAsync(tour, 0, "Kept.");
      await Answers.AnswerAsync(tour, 1, "Dropped.");
      await Answers.SendAsync(tour.Id);
      await Answers.PutStopAsync(session, tour.Id, tour.StopId, $$"""[{ "id": "{{tour.CheckIds[0]}}", "question": "Keep the digit?" }]""");
      await using var second = await AgentSessionConnection.OpenAsync(Harness);
      await Calls.CallAsync(Calls.Agent(second, On("attach_tour", tour.Id)), """{ "takeOver": true }""");

      var sent = await Answers.SendAsync(tour.Id);

      Assert.Equal([tour.CheckIds[0]], sent["answers"]!.AsArray().Select(answer => (string)answer!["checkId"]!));
      Assert.Equal(session.Id, (string)(await Answers.AnswersAsync(tour.Id))[1]!["batch"]!["sessionId"]!);
   }

   /// <summary>The send precedes the revision, so the fetch shows the dropped check's answer with the question of the version that it stays on.</summary>
   [Fact]
   public async Task Fetch_after_a_revision_shows_an_outdated_answer_with_its_version_and_the_question_it_answered()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await Answers.AnswerAsync(tour, 0, "Kept.");
      await Answers.AnswerAsync(tour, 1, "Dropped.");
      var batchId = AnswerCalls.BatchIdOf(await Answers.SendAsync(tour.Id));
      await Answers.PutStopAsync(session, tour.Id, tour.StopId, $$"""[{ "id": "{{tour.CheckIds[0]}}", "question": "Keep the digit, reworded?" }]""");

      var fetched = await Answers.FetchAsync(session, tour.Id, batchId);

      Assert.Equal(
         [(tour.CheckIds[0], "Keep the digit, reworded?", null), (tour.CheckIds[1], "Is line one still right?", 1)],
         fetched["answers"]!.AsArray().Select(answer => ((string)answer!["checkId"]!, (string)answer["question"]!, (int?)answer["outdatedOn"])));
   }

   /// <summary>An outdated answer stays on the version it was given, so the reviewer reads it there and no write reaches it.</summary>
   [Fact]
   public async Task Outdated_answer_cannot_be_answered_again_retyped_or_cleared()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await Answers.AnswerAsync(tour, 1, "Dropped.");
      await Answers.PutStopAsync(session, tour.Id, tour.StopId, $$"""[{ "id": "{{tour.CheckIds[0]}}", "question": "Keep the digit?" }]""");
      var before = await Answers.AnswersAsync(tour.Id);

      using var answered = await Calls.SendAsync(await Calls.UiAsync(On("answer_check", tour.Id, stopId: tour.StopId, checkId: tour.CheckIds[1])), """{ "text": "Again." }""");
      using var retyped = await Calls.SendAsync(await Calls.UiAsync(On("set_answer_type", tour.Id, stopId: tour.StopId, checkId: tour.CheckIds[1])), """{ "type": "concern" }""");
      using var cleared = await Calls.SendAsync(await Calls.UiAsync(On("clear_answer", tour.Id, stopId: tour.StopId, checkId: tour.CheckIds[1])));

      foreach (var response in new[] { answered, retyped, cleared })
      {
         await ErrorAsync(response, HttpStatusCode.NotFound, "checkNotFound");
      }

      Assert.True(JsonNode.DeepEquals(before, await Answers.AnswersAsync(tour.Id)), "A refused write changed the answers.");
   }

   [Fact]
   public async Task Check_text_anchor_follows_its_check_id_through_a_reorder()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await ThreeChecksAsync(session, recommendation: "Yes.");
      var question = await AskAsync(tour, $"checks[{tour.CheckIds[0]}].question");
      var recommendation = await AskAsync(tour, $"checks[{tour.CheckIds[0]}].recommendation");
      var other = await AskAsync(tour, $"checks[{tour.CheckIds[1]}].question");

      await PutAsync(session, tour, $$"""
         {
           "headline": "A headline.",
           "checks": [{ "id": "{{tour.CheckIds[2]}}", "question": "C?" }, { "id": "{{tour.CheckIds[1]}}", "question": "B?" }, { "id": "{{tour.CheckIds[0]}}", "question": "A?", "recommendation": "Yes." }]
         }
         """);

      var threads = await Calls.ThreadsAsync(tour.Id);
      Assert.Equal(
         [($"checks[{tour.CheckIds[0]}].question", "open", 2), ($"checks[{tour.CheckIds[0]}].recommendation", "open", 2), ($"checks[{tour.CheckIds[1]}].question", "open", 2)],
         threads.Select(thread => ((string)thread!["anchor"]!["field"]!, (string)thread["state"]!, (int)thread["anchor"]!["version"]!)));
      Assert.Equal([ThreadIdOf(question), ThreadIdOf(recommendation), ThreadIdOf(other)], threads.Select(thread => ThreadIdOf(thread!)));
   }

   /// <summary>A stop text anchor names a check's prose by the check's ID, so a field that names a check by its index is not one the stop has.</summary>
   [Fact]
   public async Task Stop_text_anchor_that_names_a_check_by_its_index_is_refused()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);

      using var response = await Calls.SendAsync(await Calls.UiAsync(On("ask_question", tour.Id)), StopTextBody(tour, "checks[0].question"));

      Assert.Equal("anchor.field", (string?)(await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput"))["field"]);
   }

   [Fact]
   public async Task First_write_publishes_stop_put_and_a_revision_publishes_stop_revised_with_the_replaced_version_and_what_it_settled()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var created = await Calls.CallAsync(Calls.Agent(session, "create_tour"), CreateBody);
      await Harness.PinsIdleAsync();
      var tourId = (string)created["id"]!;
      var plan = await Calls.CallAsync(Calls.Agent(session, On("set_plan", tourId)), AnswerCalls.Plan);
      var stopId = (string)plan["plan"]!["stops"]![0]!["id"]!;
      JsonNode first;
      (string Type, JsonNode Data) put;
      await using (var firstStream = await EventStream.OpenAsync(Harness))
      {
         first = await Answers.PutStopAsync(session, tourId, stopId, AnswerCalls.Checks);
         put = await firstStream.NextAsync(EventWait);
      }

      var tour = new AnsweredTour(tourId, stopId, [.. first["content"]!["checks"]!.AsArray().Select(check => (string)check!["id"]!)]);
      await Calls.AckAsync(session, await session.ProbeAsync());
      await Answers.AnswerAsync(tour, 1, "Dropped.");
      var thread = await AskAsync(tour, $"checks[{tour.CheckIds[1]}].question");
      await session.Stream.NextAsync(EventWait);
      await ReplyAsync(session, tour, thread);
      await using var stream = await EventStream.OpenAsync(Harness);

      var revised = await Answers.PutStopAsync(session, tourId, stopId, $$"""[{ "id": "{{tour.CheckIds[0]}}", "question": "Keep the digit?" }]""");
      var revision = await stream.NextAsync(EventWait);
      var read = await ReadAsTheUiAsync(tourId);
      var marker = await Calls.AskAsync(tourId, "Marker.");
      var next = await session.Stream.NextAsync(EventWait);

      Assert.Equal((ServerEvents.StopPut.Name, 1), (put.Type, (int)put.Data["stop"]!["version"]!));
      Assert.Equal(ServerEvents.StopRevised.Name, revision.Type);
      Assert.Equal((tourId, (string)read["updatedAt"]!), ((string)revision.Data["tourId"]!, (string)revision.Data["updatedAt"]!));
      Assert.True(JsonNode.DeepEquals(read["plan"]!["stops"]![0], revision.Data["stop"]), $"The event's stop differs from the stored one: {revision.Data}");
      Assert.True(JsonNode.DeepEquals(read["answers"], revision.Data["answers"]), $"The event's answers differ from the stored ones: {revision.Data}");
      Assert.True(JsonNode.DeepEquals(read["threads"], revision.Data["threads"]), $"The event's threads differ from the stored ones: {revision.Data}");
      Assert.Equal(("outdated", 1), ((string)revision.Data["threads"]![0]!["state"]!, (int)revision.Data["threads"]![0]!["anchor"]!["version"]!));
      Assert.Equal(ThreadIdOf(thread), ThreadIdOf(revision.Data["threads"]![0]!));
      Assert.Equal(2, (int)revised["version"]!);
      Assert.Equal((SessionEvents.Question, QuestionIdOf(marker)), (next.Type, (string)next.Data["questionId"]!));
   }

   [Fact]
   public async Task Ask_about_text_of_a_replaced_version_is_refused_and_an_ask_without_a_version_takes_the_current_one()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await ThreeChecksAsync(session, how: "How one.");
      await PutAsync(session, tour, """{ "headline": "A headline.", "how": "How two." }""");

      using var stale = await Calls.SendAsync(await Calls.UiAsync(On("ask_question", tour.Id)), StopTextBody(tour, "how", version: 1));
      var current = await Calls.CallAsync(await Calls.UiAsync(On("ask_question", tour.Id)), StopTextBody(tour, "how", version: 2));
      var unversioned = await Calls.CallAsync(await Calls.UiAsync(On("ask_question", tour.Id)), StopTextBody(tour, "how"));

      Assert.Equal("anchor.version", (string?)(await ErrorAsync(stale, HttpStatusCode.BadRequest, "invalidInput"))["field"]);
      Assert.Equal([2, 2], new[] { current, unversioned }.Select(thread => (int)thread["anchor"]!["version"]!));
   }

   /// <summary>The answer is stored while put_stop checks its passages, so the revision that the store's update applies afterwards finds the answer and makes it outdated.</summary>
   [Fact]
   public async Task Answer_stored_while_a_revision_that_drops_its_check_is_checked_becomes_outdated()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      Writer.Hook = WriterHooks.AfterGit(async _ =>
      {
         Writer.Hook = null;
         await Answers.AnswerAsync(tour, 0, "Kept.");
         await Answers.AnswerAsync(tour, 1, "Dropped.");
      }, "put_stop");

      await Answers.PutStopAsync(session, tour.Id, tour.StopId, $$"""[{ "id": "{{tour.CheckIds[0]}}", "question": "Keep the digit?" }]""");

      Assert.Equal(
         [(tour.CheckIds[0], null), (tour.CheckIds[1], 1)],
         (await Answers.AnswersAsync(tour.Id)).Select(answer => ((string)answer!["checkId"]!, (int?)answer["outdatedOn"])));
   }

   /// <summary>The revision lands while the ask checks its anchor, so the store's update finds a newer version than the one the reviewer selected in.</summary>
   [Fact]
   public async Task Ask_about_stop_text_while_a_revision_lands_is_refused_and_stores_no_thread()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await ThreeChecksAsync(session, how: "How one.");
      Writer.Hook = WriterHooks.AfterGit(async _ =>
      {
         Writer.Hook = null;
         await PutAsync(session, tour, """{ "headline": "A headline.", "how": "How two." }""");
      }, "ask_question");

      using var response = await Calls.SendAsync(await Calls.UiAsync(On("ask_question", tour.Id)), StopTextBody(tour, "how", version: 1));

      Assert.Equal("anchor.version", (string?)(await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput"))["field"]);
      Assert.Empty(await Calls.ThreadsAsync(tour.Id));
   }

   [Fact]
   public async Task Set_plan_keeps_a_kept_stops_version_and_earlier_versions_and_drops_those_of_a_stop_it_leaves_out()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      var plan = (await ReadAsync(tour.Id))["plan"]!;
      var later = (string)plan["stops"]![1]!["id"]!;
      await PutAsync(session, tour, """{ "headline": "The second headline." }""");
      await Calls.CallAsync(Calls.Agent(session, On("put_stop", tour.Id, stopId: later)), $$"""{ "headline": "Later.", "passages": [{{Passage}}] }""");
      await Calls.CallAsync(Calls.Agent(session, On("put_stop", tour.Id, stopId: later)), $$"""{ "headline": "Later, again.", "passages": [{{Passage}}] }""");

      var replanned = await Calls.CallAsync(Calls.Agent(session, On("set_plan", tour.Id)), $$"""
         { "chapters": [{ "id": "code", "title": "Code" }], "stops": [{ "id": "{{tour.StopId}}", "title": "Renamed", "kind": "question", "chapter": "code" }] }
         """);

      Assert.Equal((2, "revised"), ((int)replanned["plan"]!["stops"]![0]!["version"]!, (string)replanned["plan"]!["stops"]![0]!["status"]!));
      var read = await ReadAsync(tour.Id);
      Assert.Equal([tour.StopId], read["plan"]!["stops"]!.AsArray().Select(stop => (string)stop!["id"]!));
      Assert.Single(TourJson.EarlierOf(read, tour.StopId));
   }

   /// <summary>The store keeps every version, outdated answer and thread version, so a restart reads them back.</summary>
   [Fact]
   public async Task Versions_outdated_answers_and_thread_versions_round_trip_through_the_store()
   {
      string tourId;
      await using (var session = await AgentSessionConnection.OpenAsync(Harness))
      {
         var tour = await ThreeChecksAsync(session, how: "How one.");
         tourId = tour.Id;
         await Answers.AnswerAsync(tour, 2, "Dropped.");
         var thread = await AskAsync(tour, "how");
         await Calls.CallAsync(await Calls.UiAsync(On("cancel_question", tour.Id, questionId: QuestionIdOf(thread))));
         await PutAsync(session, tour, """{ "headline": "A headline.", "how": "How two." }""");
         await Harness.DropSessionAsync(session);
      }

      var before = await ReadAsync(tourId);

      await _tour.RestartServerAsync();

      Assert.True(JsonNode.DeepEquals(before, await ReadAsync(tourId)), "The tour differs after the restart.");
   }

   private static string StopTextBody(AnsweredTour tour, string field, int? version = null) =>
      $$"""{ "text": "Why this?", "anchor": { "kind": "stopText", "stopId": "{{tour.StopId}}", "field": "{{field}}", "text": "this"{{(version is { } number ? $", \"version\": {number}" : "")}} } }""";

   // Creates a tour bound to the session whose question stop's first version holds the checks A?, B? and C?, with the optional prose fields.
   private async Task<AnsweredTour> ThreeChecksAsync(AgentSessionConnection session, string? how = null, string? why = null, string? recommendation = null)
   {
      var created = await Calls.CallAsync(Calls.Agent(session, "create_tour"), CreateBody);
      await Harness.PinsIdleAsync();
      var tourId = (string)created["id"]!;
      var plan = await Calls.CallAsync(Calls.Agent(session, On("set_plan", tourId)), AnswerCalls.Plan);
      var tour = new AnsweredTour(tourId, (string)plan["plan"]!["stops"]![0]!["id"]!, []);
      var fields = string.Concat(new[] { ("how", how), ("why", why) }.Where(field => field.Item2 is not null).Select(field => $", \"{field.Item1}\": \"{field.Item2}\""));
      var first = recommendation is null ? "{ \"question\": \"A?\" }" : $"{{ \"question\": \"A?\", \"recommendation\": \"{recommendation}\" }}";
      var stop = await PutAsync(session, tour, $$"""{ "headline": "A headline."{{fields}}, "checks": [{{first}}, { "question": "B?" }, { "question": "C?" }] }""");
      return tour with { CheckIds = [.. stop["content"]!["checks"]!.AsArray().Select(check => (string)check!["id"]!)] };
   }

   [Fact]
   public async Task Opening_a_version_stores_it_as_seen_and_leaves_the_status_revised_from_version_2()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      Assert.Null((await ReadStopAsync(tour))["seenVersion"]);

      var first = await OpenVersionAsync(tour, 1);
      await PutAsync(session, tour, """{ "headline": "The second headline." }""");
      var unseen = await ReadStopAsync(tour);
      var second = await OpenVersionAsync(tour, 2);
      var seen = await ReadStopAsync(tour);

      Assert.Equal(1, (int)first["seenVersion"]!);
      Assert.Equal((2, "revised", 1), ((int)unseen["version"]!, (string)unseen["status"]!, (int)unseen["seenVersion"]!));
      Assert.Equal(2, (int)second["seenVersion"]!);
      Assert.Equal((2, "revised", 2), ((int)seen["version"]!, (string)seen["status"]!, (int)seen["seenVersion"]!));
   }

   [Fact]
   public async Task Opening_an_earlier_version_leaves_the_seen_version_where_it_was()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await PutAsync(session, tour, """{ "headline": "The second headline." }""");
      await OpenVersionAsync(tour, 2);

      var reopened = await OpenVersionAsync(tour, 1);

      Assert.Equal(2, (int)reopened["seenVersion"]!);
      Assert.Equal(2, (int)(await ReadStopAsync(tour))["seenVersion"]!);
   }

   [Theory]
   [InlineData(2)]
   [InlineData(0)]
   [InlineData(-1)]
   public async Task A_version_that_the_stop_does_not_have_records_nothing_and_still_moves_the_place(int version)
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);

      var recorded = await OpenVersionAsync(tour, version);

      Assert.Equal(tour.StopId, (string)recorded["progress"]!["lastStop"]!);
      Assert.Null(recorded["seenVersion"]);
      Assert.Null((await ReadStopAsync(tour))["seenVersion"]);
   }

   [Fact]
   public async Task A_version_for_the_summary_page_records_nothing()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);

      var recorded = await Calls.CallAsync(await Calls.UiAsync(On("record_reading_progress", tour.Id)), """{ "summary": true, "version": 1 }""");

      Assert.Null(recorded["seenVersion"]);
      Assert.Null((await ReadStopAsync(tour))["seenVersion"]);
   }

   [Fact]
   public async Task A_version_for_a_pending_stop_records_nothing()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tourId = await Calls.CreateAsync(session);
      var plan = await Calls.CallAsync(Calls.Agent(session, On("set_plan", tourId)), """{ "chapters": [{ "id": "code", "title": "Code" }], "stops": [{ "title": "First", "kind": "inspect", "chapter": "code" }] }""");
      var stopId = (string)plan["plan"]!["stops"]![0]!["id"]!;

      var recorded = await Calls.CallAsync(await Calls.UiAsync(On("record_reading_progress", tourId)), $$"""{ "stopId": "{{stopId}}", "version": 1 }""");

      Assert.Equal(stopId, (string)recorded["progress"]!["lastStop"]!);
      Assert.Null(recorded["seenVersion"]);
   }

   [Fact]
   public async Task The_seen_version_survives_a_revision_and_a_new_plan()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await OpenVersionAsync(tour, 1);

      await PutAsync(session, tour, """{ "headline": "The second headline." }""");
      var revised = await ReadStopAsync(tour);
      await Calls.CallAsync(
         Calls.Agent(session, On("set_plan", tour.Id)),
         $$"""{ "chapters": [{ "id": "renamed", "title": "Renamed" }], "stops": [{ "id": "{{tour.StopId}}", "title": "Keep the rename?", "kind": "question", "chapter": "renamed" }] }""");
      var replanned = await ReadStopAsync(tour);

      Assert.Equal((2, 1), ((int)revised["version"]!, (int)revised["seenVersion"]!));
      Assert.Equal((2, 1), ((int)replanned["version"]!, (int)replanned["seenVersion"]!));
   }

   /// <summary>The seen version is the reviewer's own reading state, so no agent session's read or write result shows it.</summary>
   [Fact]
   public async Task An_agent_session_never_gets_the_seen_version()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await OpenVersionAsync(tour, 1);

      var written = await PutAsync(session, tour, """{ "headline": "The second headline." }""");
      var agentRead = await Calls.CallAsync(Calls.Agent(session, On("read_state", tour.Id)));
      var stored = await ReadStopAsync(tour);

      Assert.Null(written["seenVersion"]);
      Assert.Null(agentRead["plan"]!["stops"]![0]!["seenVersion"]);
      Assert.Equal(1, (int)stored["seenVersion"]!);
   }

   [Fact]
   public async Task A_change_of_the_seen_version_on_the_same_place_is_stored_and_reaches_the_event_stream_once()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await Calls.CallAsync(await Calls.UiAsync(On("record_reading_progress", tour.Id)), $$"""{ "stopId": "{{tour.StopId}}" }""");
      var before = await ReadAsync(tour.Id);
      await using var stream = await EventStream.OpenAsync(Harness);

      var opened = await OpenVersionAsync(tour, 1);
      var seenEvent = await stream.NextAsync(EventWait);
      await OpenVersionAsync(tour, 1);
      await Calls.CallAsync(await Calls.UiAsync(On("set_stop_skipped", tour.Id, stopId: tour.StopId)), """{ "skipped": true }""");
      var nextEvent = await stream.NextAsync(EventWait);

      Assert.True((DateTimeOffset)opened["updatedAt"]! > (DateTimeOffset)before["updatedAt"]!, "Seeing a new version did not move the tour's update time.");
      Assert.Equal(ServerEvents.SeenVersionUpdated.Name, seenEvent.Type);
      var expected = new JsonObject { ["tourId"] = tour.Id, ["updatedAt"] = (string)opened["updatedAt"]!, ["stopId"] = tour.StopId, ["seenVersion"] = 1 };
      Assert.True(JsonNode.DeepEquals(expected, EventStream.WithoutRevision(seenEvent.Data)), $"The event differs from the write: {seenEvent.Data}");
      Assert.Equal(ServerEvents.SkipsUpdated.Name, nextEvent.Type);
   }

   // Writes the stop's content from the fields, with the one passage, and returns the stored stop.
   private async Task<JsonNode> PutAsync(AgentSessionConnection session, AnsweredTour tour, string fields)
   {
      var body = JsonNode.Parse(fields)!.AsObject();
      body["passages"] = JsonNode.Parse($"[{Passage}]");
      if (!body.ContainsKey("checks"))
      {
         body["checks"] = JsonNode.Parse("""[{ "question": "Keep the digit?" }]""");
      }

      return await Calls.CallAsync(Calls.Agent(session, On("put_stop", tour.Id, stopId: tour.StopId)), body.ToJsonString());
   }

   private async Task<JsonNode> AskAsync(AnsweredTour tour, string field) => await Calls.CallAsync(await Calls.UiAsync(On("ask_question", tour.Id)), StopTextBody(tour, field));

   private async Task<JsonNode> ReplyAsync(AgentSessionConnection session, AnsweredTour tour, JsonNode thread) =>
      await Calls.CallAsync(Calls.Agent(session, On("reply", tour.Id, questionId: QuestionIdOf(thread))), """{ "resolution": "done", "text": "Noted." }""");

   private async Task<JsonNode> OpenVersionAsync(AnsweredTour tour, int version) =>
      await Calls.CallAsync(await Calls.UiAsync(On("record_reading_progress", tour.Id)), $$"""{ "stopId": "{{tour.StopId}}", "version": {{version}} }""");

   private async Task<JsonNode> ReadStopAsync(AnsweredTour tour) => (await ReadAsync(tour.Id))["plan"]!["stops"]![0]!;

   private Task<JsonNode> ReadAsync(string tourId) => Harness.CallAsync(On("read_state", tourId));

   private async Task<JsonNode> ReadAsTheUiAsync(string tourId)
   {
      var request = (await Calls.UiAsync(On("read_state", tourId))).WithProseHtml();
      return await Calls.CallAsync(request);
   }
}
