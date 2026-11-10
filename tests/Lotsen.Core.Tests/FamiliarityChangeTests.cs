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
/// Covers <c>change_familiarity</c>: the reviewer changes the tour's familiarity level, and one store update stores the level and a decided thread whose familiarityChanged decision names the old and new level and the
/// stops to rewrite, which reaches the bound session as a question event. Without a bound session nothing changes.
/// </summary>
[Trait("Category", "Versions")]
public sealed class FamiliarityChangeTests : IClassFixture<StopTourFixture>, IAsyncLifetime
{
   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

   // The stops by index: 0 to 3 inspect stops, written with content by PlannedAsync except stop 2, which stays pending.
   private const string Plan = """
      {
        "chapters": [{ "id": "code", "title": "Code" }],
        "stops": [
          { "title": "First", "kind": "inspect", "chapter": "code" },
          { "title": "Second", "kind": "inspect", "chapter": "code" },
          { "title": "Third", "kind": "inspect", "chapter": "code" },
          { "title": "Fourth", "kind": "inspect", "chapter": "code" },
          { "title": "Fifth", "kind": "inspect", "chapter": "code" }
        ]
      }
      """;

   private readonly PinnedTourFixture _tour;

   public FamiliarityChangeTests(StopTourFixture fixture)
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

   private static string ChangeBody(string level, string? stopId = null) =>
      stopId is null ? $$"""{ "familiarity": "{{level}}" }""" : $$"""{ "familiarity": "{{level}}", "stopId": "{{stopId}}" }""";

   private async Task<JsonNode> ChangeAsync(string tourId, string level, string? stopId = null) => await Calls.CallAsync(await Calls.UiAsync(On("change_familiarity", tourId)), ChangeBody(level, stopId));

   private async Task<HttpResponseMessage> TryChangeAsync(string tourId, string level, string? stopId = null) =>
      await Calls.SendAsync(await Calls.UiAsync(On("change_familiarity", tourId)), ChangeBody(level, stopId));

   private async Task<string> StoredLevelAsync(string tourId) => (string)(await Harness.CallAsync(On("read_state", tourId)))["familiarity"]!;

   private async Task ReplyAsync(AgentSessionConnection session, string tourId, string questionId, string resolution) =>
      await Calls.CallAsync(Calls.Agent(session, On("reply", tourId, questionId: questionId)), resolution == "declined" ? """{ "resolution": "declined" }""" : $$"""{ "resolution": "{{resolution}}", "text": "Noted." }""");

   private static string[] Ids(JsonNode? node) => [.. node!.AsArray().Select(id => (string)id!)];

   /// <summary>Creates a pinned tour at the level <c>some</c>, bound to the session, with <see cref="Plan"/>, and content on every stop but stop 2. Returns the tour's ID and its stops' IDs.</summary>
   private async Task<(string TourId, string[] StopIds)> PlannedAsync(AgentSessionConnection session)
   {
      var tourId = await Calls.CreateAsync(session);
      await Harness.PinsIdleAsync();
      var tour = await Calls.CallAsync(Calls.Agent(session, On("set_plan", tourId)), Plan);
      string[] stopIds = [.. tour["plan"]!["stops"]!.AsArray().Select(stop => (string)stop!["id"]!)];
      foreach (var stopId in stopIds.Where((_, index) => index != 2))
      {
         await Answers.PutStopAsync(session, tourId, stopId, "[]");
      }

      return (tourId, stopIds);
   }

   /// <summary>The UI asks for prose as HTML, so the change answers with its thread as the tour's prose view shows it, before any event brings the thread.</summary>
   [Fact]
   public async Task Change_answers_the_ui_with_its_thread_as_the_tour_shows_it_with_html()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var (tourId, stops) = await PlannedAsync(session);

      var changed = await Calls.CallAsync((await Calls.UiAsync(On("change_familiarity", tourId))).WithProseHtml(), ChangeBody("new", stops[0]));
      var tour = await Calls.CallAsync((await Calls.UiAsync(On("read_state", tourId))).WithProseHtml());

      var shown = tour["threads"]!.AsArray().Single(thread => (string)thread!["id"]! == (string)changed["thread"]!["id"]!);
      Assert.True(JsonNode.DeepEquals(shown, changed["thread"]), $"The change answered {changed["thread"]}, and the tour shows {shown}.");
   }

   [Fact]
   public async Task Change_with_words_stores_them_as_the_question_of_its_decision_and_fetch_question_returns_them()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var (tourId, stops) = await PlannedAsync(session);

      var changed = await Calls.CallAsync(await Calls.UiAsync(On("change_familiarity", tourId)), $$"""{ "familiarity": "new", "stopId": "{{stops[0]}}", "text": "Skip the glossary." }""");

      var fetched = await Calls.CallAsync(Calls.Agent(session, On("fetch_question", tourId, questionId: QuestionIdOf(changed["thread"]!))));
      var message = fetched["thread"]!["messages"]!.AsArray().Single()!;
      Assert.Equal(("Skip the glossary.", "familiarityChanged"), ((string?)message["text"], (string?)fetched["thread"]!["decision"]!["kind"]));
   }

   [Theory]
   [InlineData("   ")]
   [InlineData(@"\t\n")]
   public async Task Change_with_whitespace_words_stores_a_question_without_words(string words)
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var (tourId, stops) = await PlannedAsync(session);

      var changed = await Calls.CallAsync(await Calls.UiAsync(On("change_familiarity", tourId)), $$"""{ "familiarity": "new", "stopId": "{{stops[0]}}", "text": "{{words}}" }""");

      Assert.Equal("", (string?)changed["thread"]!["messages"]![0]!["text"]);
   }

   [Fact]
   public async Task Change_with_a_control_character_in_its_words_is_refused_and_keeps_the_level()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var (tourId, stops) = await PlannedAsync(session);

      using var response = await Calls.SendAsync(await Calls.UiAsync(On("change_familiarity", tourId)), $$"""{ "familiarity": "new", "stopId": "{{stops[0]}}", "text": "Bell \u0007" }""");

      Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
      Assert.Equal("some", await StoredLevelAsync(tourId));
      Assert.Empty(await Calls.ThreadsAsync(tourId));
   }

   [Fact]
   public async Task Change_stores_the_level_and_sends_a_familiarity_action_naming_the_old_and_new_level_to_the_bound_session()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var (tourId, stops) = await PlannedAsync(session);
      var nonce = await session.ProbeAsync();
      await using var stream = await EventStream.OpenAsync(Harness);

      var changed = await ChangeAsync(tourId, "new", stops[0]);
      var sent = await session.Stream.NextAsync(Timeout);
      var events = new[] { await stream.NextAsync(EventWait), await stream.NextAsync(EventWait) };

      Assert.Equal(("new", "new"), ((string)changed["familiarity"]!, await StoredLevelAsync(tourId)));
      var thread = changed["thread"]!;
      var question = Assert.Single(thread["messages"]!.AsArray())!;
      var action = new JsonObject { ["kind"] = "familiarityChanged", ["from"] = "some", ["to"] = "new", ["stopIds"] = new JsonArray(stops[1], stops[3], stops[4]) };
      Assert.True(JsonNode.DeepEquals(action, thread["decision"]), $"action: {thread["decision"]}");
      Assert.True(JsonNode.DeepEquals(new JsonObject { ["kind"] = "tour" }, thread["anchor"]), $"anchor: {thread["anchor"]}");
      Assert.Equal(("", stops[0]), ((string)question["text"]!, (string?)question["stopId"]));
      Assert.Equal(("pending", session.Id), ((string)RequestOf(thread)["state"]!, (string)RequestOf(thread)["sessionId"]!));
      Assert.True(JsonNode.DeepEquals(thread, Assert.Single(await Calls.ThreadsAsync(tourId))), "The stored thread differs from the change's result.");
      Assert.Equal(SessionEvents.Question, sent.Type);
      Assert.Equal(("familiarityChanged", nonce, QuestionIdOf(thread)), ((string?)sent.Data["decision"], (string?)sent.Data["nonce"], (string?)sent.Data["questionId"]));
      var parameters = ChannelNotifications.ParamsFor(new SessionEvent(sent.Type, sent.Data.ToJsonString()))!;
      Assert.Equal("familiarityChanged", (string?)parameters["meta"]!["decision"]);
      Assert.Contains("fetch_question", (string)parameters["content"]!, StringComparison.Ordinal);
      var levelEvent = Assert.Single(events, received => received.Type == ServerEvents.FamiliarityChanged.Name);
      var expected = new JsonObject { ["tourId"] = tourId, ["updatedAt"] = (string)changed["updatedAt"]!, ["familiarity"] = "new" };
      Assert.True(JsonNode.DeepEquals(expected, EventStream.WithoutRevision(levelEvent.Data)), $"familiarityChanged: {levelEvent.Data}");
      Assert.Single(events, received => received.Type == ServerEvents.ThreadCreated.Name);
   }

   /// <summary>
   /// The stops to rewrite are the stops after the stop under view, or every stop without one, that have content and that the reviewer did not skip. Stop 2 is pending, and stop 4 is skipped.
   /// </summary>
   [Theory]
   [InlineData(null, new[] { 0, 1, 3 })]
   [InlineData(0, new[] { 1, 3 })]
   [InlineData(1, new[] { 3 })]
   [InlineData(3, new int[0])]
   public async Task Change_asks_to_rewrite_the_stops_after_the_one_under_view_that_have_content_and_no_skip(int? viewed, int[] rewritten)
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var (tourId, stops) = await PlannedAsync(session);
      await Calls.CallAsync(await Calls.UiAsync(On("set_stop_skipped", tourId, stopId: stops[4])), """{ "skipped": true }""");

      var changed = await ChangeAsync(tourId, "familiar", viewed is { } index ? stops[index] : null);

      Assert.Equal(rewritten.Select(index => stops[index]), Ids(changed["thread"]!["decision"]!["stopIds"]));
   }

   [Fact]
   public async Task Fetch_question_and_read_state_return_the_levels_and_the_stops_of_a_familiarity_action()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var (tourId, stops) = await PlannedAsync(session);
      var changed = await ChangeAsync(tourId, "new", stops[3]);

      var fetched = await Calls.FetchAsync(session, tourId, QuestionIdOf(changed["thread"]!));
      var read = await Calls.CallAsync(Calls.Agent(session, On("read_state", tourId)));

      var action = new JsonObject { ["kind"] = "familiarityChanged", ["from"] = "some", ["to"] = "new", ["stopIds"] = new JsonArray(stops[4]) };
      Assert.True(JsonNode.DeepEquals(action, fetched["thread"]!["decision"]), $"fetched: {fetched["thread"]!["decision"]}");
      Assert.True(JsonNode.DeepEquals(action, read["threads"]![0]!["decision"]), $"read: {read["threads"]![0]!["decision"]}");
      Assert.Equal("new", (string)read["familiarity"]!);
   }

   [Fact]
   public async Task Change_on_a_tour_without_a_plan_stores_the_level_and_asks_to_rewrite_no_stop()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tourId = await Calls.CreateAsync(session);
      await Harness.PinsIdleAsync();

      var changed = await ChangeAsync(tourId, "familiar");

      Assert.Equal("familiar", await StoredLevelAsync(tourId));
      Assert.Empty(Ids(changed["thread"]!["decision"]!["stopIds"]));
   }

   [Theory]
   [InlineData("takenOverThenDropped")]
   [InlineData("dropped")]
   public async Task Change_on_a_tour_with_no_attached_session_is_refused_and_the_level_stays(string binding)
   {
      var session = await AgentSessionConnection.OpenAsync(Harness);
      var (tourId, _) = await PlannedAsync(session);
      if (binding == "takenOverThenDropped")
      {
         await using var other = await AgentSessionConnection.OpenAsync(Harness);
         await Calls.CallAsync(Calls.Agent(other, On("attach_tour", tourId)), """{ "takeOver": true }""");
         await Harness.DropSessionAsync(other);
         await session.DisposeAsync();
      }
      else
      {
         await Harness.DropSessionAsync(session);
      }

      using var response = await TryChangeAsync(tourId, "new");

      var error = await ErrorAsync(response, HttpStatusCode.Conflict, "noSessionAttached");
      Assert.Contains("no session can receive the familiarity change", (string)error["reason"]!, StringComparison.Ordinal);
      Assert.Equal("some", await StoredLevelAsync(tourId));
      Assert.Empty(await Calls.ThreadsAsync(tourId));
   }

   [Fact]
   public async Task Change_after_a_take_over_goes_to_the_new_session()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      await using var other = await AgentSessionConnection.OpenAsync(Harness);
      var (tourId, _) = await PlannedAsync(session);
      await Calls.CallAsync(Calls.Agent(other, On("attach_tour", tourId)), """{ "takeOver": true }""");

      var changed = await ChangeAsync(tourId, "new");

      Assert.Equal(other.Id, (string)RequestOf(changed["thread"]!)["sessionId"]!);
      await other.ProbeAsync();
      var sent = await other.Stream.NextAsync(Timeout);
      Assert.Equal(("familiarityChanged", QuestionIdOf(changed["thread"]!)), ((string?)sent.Data["decision"], (string?)sent.Data["questionId"]));
   }

   [Fact]
   public async Task Change_whose_stop_under_view_the_plan_dropped_is_refused_and_the_level_stays()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var (tourId, stops) = await PlannedAsync(session);
      await Calls.CallAsync(
         Calls.Agent(session, On("set_plan", tourId)),
         $$"""{ "chapters": [{ "id": "code", "title": "Code" }], "stops": [{ "id": "{{stops[1]}}", "title": "Second", "kind": "inspect", "chapter": "code" }] }""");

      using var response = await TryChangeAsync(tourId, "new", stops[0]);

      var error = await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("stopId", (string)error["field"]!);
      Assert.Equal("some", await StoredLevelAsync(tourId));
      Assert.Empty(await Calls.ThreadsAsync(tourId));
   }

   [Fact]
   public async Task Change_to_the_current_level_is_refused_and_stores_nothing()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var (tourId, _) = await PlannedAsync(session);

      using var response = await TryChangeAsync(tourId, "some");

      var error = await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("familiarity", (string)error["field"]!);
      Assert.Empty(await Calls.ThreadsAsync(tourId));
   }

   /// <summary>The older change got its reply, so the newer one could go. Each change is in the level's wait group, so a follow-up in the older thread would make a second one wait.</summary>
   [Fact]
   public async Task Follow_up_in_an_older_familiarity_change_while_a_newer_change_waits_is_refused()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var (tourId, _) = await PlannedAsync(session);
      var first = await ChangeAsync(tourId, "new");
      await ReplyAsync(session, tourId, QuestionIdOf(first["thread"]!), "done");
      var second = await ChangeAsync(tourId, "familiar");

      using var refused = await Calls.SendAsync(await Calls.UiAsync(On("ask_follow_up", tourId, threadId: ThreadIdOf(first["thread"]!))), MessageBody("Also the tests."));

      var error = await ErrorAsync(refused, HttpStatusCode.Conflict, "questionWaiting");
      Assert.Equal("threadId", (string)error["field"]!);
      Assert.Contains("Claude still writes stops at the familiarity level that you picked.", (string)error["reason"]!, StringComparison.Ordinal);
      Assert.DoesNotContain(ThreadIdOf(second["thread"]!), (string)error["reason"]!, StringComparison.Ordinal);
      var older = Assert.Single(await Calls.ThreadsAsync(tourId), thread => ThreadIdOf(thread!) == ThreadIdOf(first["thread"]!))!;
      Assert.Single(older["messages"]!.AsArray(), message => (string)message!["kind"]! == "question");
   }

   [Fact]
   public async Task Change_while_a_familiarity_action_waits_is_refused_until_the_action_is_canceled()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var (tourId, _) = await PlannedAsync(session);
      var first = await ChangeAsync(tourId, "new");

      using var refused = await TryChangeAsync(tourId, "familiar");
      var error = await ErrorAsync(refused, HttpStatusCode.Conflict, "questionWaiting");
      var levelWhileWaiting = await StoredLevelAsync(tourId);
      await Calls.CallAsync(await Calls.UiAsync(On("cancel_question", tourId, questionId: QuestionIdOf(first["thread"]!))));
      var second = await ChangeAsync(tourId, "familiar");

      Assert.Equal("familiarity", (string)error["field"]!);
      Assert.Equal("new", levelWhileWaiting);
      Assert.Equal(("new", "familiar"), ((string)second["thread"]!["decision"]!["from"]!, await StoredLevelAsync(tourId)));
      Assert.Equal(2, (await Calls.ThreadsAsync(tourId)).Count);
   }

   [Fact]
   public async Task Change_while_another_tour_action_waits_goes_to_claude()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var (tourId, _) = await PlannedAsync(session);
      await session.ProbeAsync();
      await Calls.CallAsync(await Calls.UiAsync(On("request_tour_action", tourId)), ActionBody("replan", """{ "kind": "tour" }""", "Fewer stops."));
      await session.Stream.NextAsync(Timeout);

      var changed = await ChangeAsync(tourId, "new");
      var sent = await session.Stream.NextAsync(Timeout);

      Assert.Equal("new", await StoredLevelAsync(tourId));
      Assert.Equal(("familiarityChanged", QuestionIdOf(changed["thread"]!)), ((string?)sent.Data["decision"], (string?)sent.Data["questionId"]));
      Assert.Equal(2, (await Calls.ThreadsAsync(tourId)).Count);
   }

   /// <summary>The reviewer viewed stop 0 at the first change and views stop 1 when asking again, so the action asks for the stops after stop 1 alone.</summary>
   [Theory]
   [InlineData("declined")]
   [InlineData("canceled")]
   [InlineData("unanswered")]
   public async Task Change_to_the_current_level_after_a_change_that_was_not_carried_out_asks_again_from_the_stop_under_view(string ended)
   {
      var session = await AgentSessionConnection.OpenAsync(Harness);
      await using var other = await AgentSessionConnection.OpenAsync(Harness);
      var (tourId, stops) = await PlannedAsync(session);
      await session.ProbeAsync();
      var first = await ChangeAsync(tourId, "new", stops[0]);
      await session.Stream.NextAsync(Timeout);
      var bound = session;
      if (ended == "declined")
      {
         await Calls.CallAsync(Calls.Agent(session, On("reply", tourId, questionId: QuestionIdOf(first["thread"]!))), """{ "resolution": "declined", "text": "Busy." }""");
      }
      else if (ended == "canceled")
      {
         await Calls.CallAsync(await Calls.UiAsync(On("cancel_question", tourId, questionId: QuestionIdOf(first["thread"]!))));
      }
      else
      {
         await Harness.DropSessionAsync(session);
         await Calls.CallAsync(Calls.Agent(other, On("attach_tour", tourId)));
         await other.ProbeAsync();
         bound = other;
      }

      var again = await ChangeAsync(tourId, "new", stops[1]);
      var sent = await bound.Stream.NextAsync(Timeout);

      var action = new JsonObject { ["kind"] = "familiarityChanged", ["from"] = "some", ["to"] = "new", ["stopIds"] = new JsonArray(stops[3], stops[4]) };
      Assert.True(JsonNode.DeepEquals(action, again["thread"]!["decision"]), $"action: {again["thread"]!["decision"]}");
      Assert.NotEqual(ThreadIdOf(first["thread"]!), ThreadIdOf(again["thread"]!));
      Assert.Equal(("pending", bound.Id), ((string)RequestOf(again["thread"]!)["state"]!, (string)RequestOf(again["thread"]!)["sessionId"]!));
      Assert.Equal(("familiarityChanged", QuestionIdOf(again["thread"]!)), ((string?)sent.Data["decision"], (string?)sent.Data["questionId"]));
      Assert.Equal(("new", "new"), ((string)again["familiarity"]!, await StoredLevelAsync(tourId)));
      Assert.Equal(2, (await Calls.ThreadsAsync(tourId)).Count);
      await session.DisposeAsync();
   }

   /// <summary>Both changes go to new, the earlier one declined and the later one, which asks again, carried out, so only the latest change decides.</summary>
   [Fact]
   public async Task Change_to_the_current_level_after_its_latest_change_was_carried_out_is_refused_though_an_earlier_one_to_it_was_declined()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var (tourId, _) = await PlannedAsync(session);
      var first = await ChangeAsync(tourId, "new");
      await ReplyAsync(session, tourId, QuestionIdOf(first["thread"]!), "declined");
      var again = await ChangeAsync(tourId, "new");
      await ReplyAsync(session, tourId, QuestionIdOf(again["thread"]!), "done");

      using var response = await TryChangeAsync(tourId, "new");

      var error = await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("familiarity", (string)error["field"]!);
      Assert.Equal(2, (await Calls.ThreadsAsync(tourId)).Count);
   }

   /// <summary>The changes go to new, carried out, to familiar, carried out, and to new again, declined, so only the latest change decides.</summary>
   [Fact]
   public async Task Change_to_the_current_level_after_its_latest_change_was_declined_asks_again_though_an_earlier_one_to_it_was_carried_out()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var (tourId, _) = await PlannedAsync(session);
      foreach (var (level, resolution) in new[] { ("new", "done"), ("familiar", "done"), ("new", "declined") })
      {
         var changed = await ChangeAsync(tourId, level);
         await ReplyAsync(session, tourId, QuestionIdOf(changed["thread"]!), resolution);
      }

      var again = await ChangeAsync(tourId, "new");

      Assert.Equal(("familiar", "new"), ((string)again["thread"]!["decision"]!["from"]!, (string)again["thread"]!["decision"]!["to"]!));
      Assert.Equal(4, (await Calls.ThreadsAsync(tourId)).Count);
   }

   /// <summary>Claude replies to the change, the reviewer follows up in its thread, and the follow-up's outcome decides, as a follow-up is a question about the change.</summary>
   [Theory]
   [InlineData("declined", "done", "refused")]
   [InlineData("declined", "declined", "askedAgain")]
   [InlineData("declined", "canceled", "askedAgain")]
   [InlineData("needsAnswer", "declined", "askedAgain")]
   [InlineData("needsAnswer", "pending", "waiting")]
   public async Task Change_to_the_current_level_after_a_follow_up_in_its_thread_follows_the_follow_up(string resolution, string followUp, string expected)
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var (tourId, _) = await PlannedAsync(session);
      var changed = await ChangeAsync(tourId, "new");
      await ReplyAsync(session, tourId, QuestionIdOf(changed["thread"]!), resolution);
      var followed = await Calls.CallAsync(await Calls.UiAsync(On("ask_follow_up", tourId, threadId: ThreadIdOf(changed["thread"]!))), """{ "text": "Please do it now." }""");
      var followUpId = QuestionIdOf(followed, followed["messages"]!.AsArray().Count - 1);
      if (followUp == "canceled")
      {
         await Calls.CallAsync(await Calls.UiAsync(On("cancel_question", tourId, questionId: followUpId)));
      }
      else if (followUp != "pending")
      {
         await ReplyAsync(session, tourId, followUpId, followUp);
      }

      using var response = await TryChangeAsync(tourId, "new");

      var status = expected switch { "askedAgain" => HttpStatusCode.OK, "waiting" => HttpStatusCode.Conflict, _ => HttpStatusCode.BadRequest };
      Assert.True(response.StatusCode == status, $"Expected {status}, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
      Assert.Equal(expected == "askedAgain" ? 2 : 1, (await Calls.ThreadsAsync(tourId)).Count);
   }

   [Fact]
   public async Task Change_to_the_current_level_while_its_change_waits_is_refused()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var (tourId, _) = await PlannedAsync(session);
      await ChangeAsync(tourId, "new");

      using var response = await TryChangeAsync(tourId, "new");

      var error = await ErrorAsync(response, HttpStatusCode.Conflict, "questionWaiting");
      Assert.Equal("familiarity", (string)error["field"]!);
      Assert.Single(await Calls.ThreadsAsync(tourId));
   }

   [Fact]
   public async Task Change_to_another_level_after_a_declined_change_changes_the_level_from_the_declined_one()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var (tourId, _) = await PlannedAsync(session);
      var first = await ChangeAsync(tourId, "new");
      await Calls.CallAsync(Calls.Agent(session, On("reply", tourId, questionId: QuestionIdOf(first["thread"]!))), """{ "resolution": "declined" }""");

      var second = await ChangeAsync(tourId, "familiar");

      Assert.Equal(("new", "familiar"), ((string)second["thread"]!["decision"]!["from"]!, (string)second["thread"]!["decision"]!["to"]!));
      Assert.Equal("familiar", await StoredLevelAsync(tourId));
   }

   [Theory]
   [InlineData("pending")]
   [InlineData("canceled")]
   public async Task Resend_of_a_familiarity_action_is_refused_and_names_how_to_ask_again(string state)
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var (tourId, _) = await PlannedAsync(session);
      var changed = await ChangeAsync(tourId, "new");
      if (state == "canceled")
      {
         await Calls.CallAsync(await Calls.UiAsync(On("cancel_question", tourId, questionId: QuestionIdOf(changed["thread"]!))));
      }

      var threads = await Calls.ThreadsAsync(tourId);

      using var response = await Calls.SendAsync(await Calls.UiAsync(On("resend_question", tourId, questionId: QuestionIdOf(changed["thread"]!))));

      var error = await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("decision.kind", (string)error["field"]!);
      Assert.Contains("open Re-plan… in the Plan panel's ⋯ menu", (string)error["reason"]!, StringComparison.Ordinal);
      Assert.Equal("new", await StoredLevelAsync(tourId));
      Assert.True(JsonNode.DeepEquals(threads, await Calls.ThreadsAsync(tourId)), "The refused re-send changed the threads.");
   }

   [Fact]
   public async Task Tour_list_shows_the_changed_level()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var (tourId, _) = await PlannedAsync(session);

      await ChangeAsync(tourId, "new");

      var listed = (await Harness.CallAsync("list_tours"))["tours"]!.AsArray().Single(tour => (string)tour!["id"]! == tourId)!;
      Assert.Equal("new", (string)listed["familiarity"]!);
   }
}
