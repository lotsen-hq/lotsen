using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Adapter;
using Lotsen.Operations;
using Lotsen.Sessions;
using Lotsen.Tours;
using Microsoft.Extensions.DependencyInjection;
using static Lotsen.Tests.OperationCall;
using static Lotsen.Tests.ThreadCalls;

namespace Lotsen.Tests;

/// <summary>
/// Covers the tour actions: a reviewer's request that Claude explain a stop deeper or revise it, which the server stores as a question with its action in a thread on the stop, and sends to the
/// bound session as a question event that names the action's kind.
/// </summary>
[Trait("Category", "TourActions")]
public sealed class TourActionTests : IClassFixture<StopTourFixture>, IAsyncLifetime
{
   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

   private readonly PinnedTourFixture _tour;

   public TourActionTests(StopTourFixture fixture)
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

   /// <summary>Returns the body of a request of the kind: an explain or revise names <paramref name="target"/> as its target, and every other kind applies to the tour, so it names none.</summary>
   public static string ActionBody(string kind, string? target = null, string? text = null)
   {
      var action = target is null || target == TourTarget ? $$"""{ "kind": "{{kind}}" }""" : $$"""{ "kind": "{{kind}}", "target": {{target}} }""";
      return text is null ? $$"""{ "action": {{action}} }""" : $$"""{ "action": {{action}}, "text": "{{text}}" }""";
   }

   /// <summary>The tour as a request's target, which a kind that applies to the tour omits.</summary>
   public const string TourTarget = """{ "kind": "tour" }""";

   public static string StopAnchor(string stopId) => $$"""{ "kind": "stop", "stopId": "{{stopId}}" }""";

   public static string TextAnchor(string stopId, string field = "headline", string text = "headline") => $$"""{ "kind": "stopText", "stopId": "{{stopId}}", "field": "{{field}}", "text": "{{text}}" }""";

   [Theory]
   [InlineData("explain")]
   [InlineData("revise")]
   public async Task Action_on_a_stop_is_stored_as_a_question_with_its_kind_and_reaches_the_bound_session_naming_the_kind(string kind)
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      var nonce = await session.ProbeAsync();
      await using var stream = await EventStream.OpenAsync(Harness);

      var thread = await Calls.CallAsync(await Calls.UiAsync(On("request_tour_action", tour.Id)), ActionBody(kind, StopAnchor(tour.StopId), "Go deeper on the rounding."));
      var sent = await session.Stream.NextAsync(Timeout);
      var created = await stream.NextAsync(Timeout);

      var question = Assert.Single(thread["messages"]!.AsArray())!;
      var request = Assert.Single(question["requests"]!.AsArray())!;
      Assert.True(JsonNode.DeepEquals(new JsonObject { ["kind"] = kind, ["stopVersion"] = 1 }, thread["action"]), $"action: {thread["action"]}");
      Assert.Equal(("question", "Go deeper on the rounding."), ((string)question["kind"]!, (string)question["text"]!));
      Assert.Equal(("pending", session.Id), ((string)request["state"]!, (string)request["sessionId"]!));
      Assert.True(JsonNode.DeepEquals(new JsonObject { ["kind"] = "stop", ["stopId"] = tour.StopId }, thread["anchor"]), $"anchor: {thread["anchor"]}");
      Assert.True(JsonNode.DeepEquals(thread, Assert.Single(await Calls.ThreadsAsync(tour.Id))), "The stored thread differs from the action's result.");
      Assert.Equal(ServerEvents.ThreadCreated.Name, created.Type);
      Assert.Equal(SessionEvents.Question, sent.Type);
      Assert.Equal((kind, nonce, "Go deeper on the rounding."), ((string?)sent.Data["action"], (string?)sent.Data["nonce"], (string?)sent.Data["text"]));
      var meta = ChannelNotifications.ParamsFor(new SessionEvent(sent.Type, sent.Data.ToJsonString()))!["meta"]!;
      Assert.Equal((SessionEvents.Question, kind, (string)question["id"]!), ((string)meta["event"]!, (string?)meta["action"], (string)meta["questionId"]!));
   }

   [Fact]
   public async Task Action_is_resolved_by_the_reply_of_the_session_and_fetch_question_returns_it_with_its_kind()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      var thread = await Calls.CallAsync(await Calls.UiAsync(On("request_tour_action", tour.Id)), ActionBody("revise", TextAnchor(tour.StopId), "Shorter, please."));
      var questionId = QuestionIdOf(thread);

      var fetched = await Calls.FetchAsync(session, tour.Id, questionId);
      await Answers.PutStopAsync(session, tour.Id, tour.StopId, AnswerCalls.Checks);
      var replied = await Calls.CallAsync(Calls.Agent(session, On("reply", tour.Id, questionId: questionId)), """{ "resolution": "done", "text": "Revised the headline." }""");

      Assert.Equal("revise", (string)fetched["thread"]!["action"]!["kind"]!);
      Assert.Equal(tour.StopId, (string)fetched["stop"]!["id"]!);
      Assert.Equal("done", (string)RequestOf(replied)["state"]!);
      Assert.Equal(("reply", "Revised the headline."), ((string)replied["messages"]![1]!["kind"]!, (string)replied["messages"]![1]!["text"]!));
   }

   /// <summary>Claude carries out an explain on the headline by changing the headline, which leaves the action's anchor on version 1 while its request waits.</summary>
   [Fact]
   public async Task Explain_on_stop_text_whose_field_its_revision_changes_keeps_its_thread_open_with_the_reply_on_the_earlier_version()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      var thread = await Calls.CallAsync(await Calls.UiAsync(On("request_tour_action", tour.Id)), ActionBody("explain", TextAnchor(tour.StopId)));
      var questionId = QuestionIdOf(thread);
      await Calls.FetchAsync(session, tour.Id, questionId);
      await Calls.CallAsync(
         Calls.Agent(session, On("put_stop", tour.Id, stopId: tour.StopId)),
         $$"""{ "headline": "A headline, explained.", "passages": [{ "side": "head", "path": "src/modified.txt", "startLine": 2, "endLine": 2, "role": "primary" }], "checks": {{AnswerCalls.Checks}} }""");
      var revised = Assert.Single(await Calls.ThreadsAsync(tour.Id))!;

      var replied = await Calls.CallAsync(Calls.Agent(session, On("reply", tour.Id, questionId: questionId)), """{ "resolution": "done", "text": "Explained the headline." }""");

      Assert.Equal(("open", 1), ((string)revised["state"]!, (int)revised["anchor"]!["version"]!));
      Assert.Equal(("open", 1, "Explained the headline."), ((string)replied["state"]!, (int)replied["anchor"]!["version"]!, (string)replied["messages"]![1]!["text"]!));
   }

   /// <summary>An action without words stores empty text, and its event gives Claude a sentence of the server's that names the kind in place of the words.</summary>
   [Fact]
   public async Task Action_without_words_stores_empty_text_and_its_channel_content_names_the_kind()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await session.ProbeAsync();

      var thread = await Calls.CallAsync(await Calls.UiAsync(On("request_tour_action", tour.Id)), ActionBody("explain", TextAnchor(tour.StopId), " "));
      var sent = await session.Stream.NextAsync(Timeout);
      var parameters = ChannelNotifications.ParamsFor(new SessionEvent(sent.Type, sent.Data.ToJsonString()))!;

      Assert.Equal("", (string)thread["messages"]![0]!["text"]!);
      var anchor = new JsonObject { ["kind"] = "stopText", ["stopId"] = tour.StopId, ["field"] = "headline", ["text"] = "headline", ["version"] = 1 };
      Assert.True(JsonNode.DeepEquals(anchor, thread["anchor"]), $"anchor: {thread["anchor"]}");
      Assert.True(JsonNode.DeepEquals(new JsonObject { ["kind"] = "explain" }, thread["action"]), $"action: {thread["action"]}");
      Assert.Contains("explain", (string)parameters["content"]!, StringComparison.Ordinal);
      Assert.Contains("fetch_question", (string)parameters["content"]!, StringComparison.Ordinal);
   }

   /// <summary>A decision is a report, so its event names it as a decision and its content says that the reviewer decided, not that the reviewer requests.</summary>
   [Fact]
   public void Channel_notification_of_a_decision_without_words_names_the_decision_and_no_action()
   {
      var parameters = ChannelNotifications.ParamsFor(SessionEvents.ForQuestion(new QuestionEvent(TourId.New(), "t1", "q1", "r1", "", Decision: ReviewerDecision.PlanApprovedKind)))!;

      Assert.Equal(ReviewerDecision.PlanApprovedKind, (string?)parameters["meta"]!["decision"]);
      Assert.False(parameters["meta"]!.AsObject().ContainsKey("action"), $"meta: {parameters["meta"]}");
      Assert.StartsWith("The reviewer made a decision for you to act on: planApproved", (string)parameters["content"]!, StringComparison.Ordinal);
   }

   [Fact]
   public void Channel_notification_of_a_question_without_an_action_has_no_action_attribute()
   {
      var parameters = ChannelNotifications.ParamsFor(SessionEvents.ForQuestion(new QuestionEvent(TourId.New(), "t1", "q1", "r1", "Why?")))!;

      Assert.False(parameters["meta"]!.AsObject().ContainsKey("action"), $"meta: {parameters["meta"]}");
   }

   [Theory]
   [InlineData("takenOverThenDropped")]
   [InlineData("dropped")]
   public async Task Action_on_a_tour_with_no_attached_session_is_refused_and_stores_nothing(string binding)
   {
      var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      if (binding == "takenOverThenDropped")
      {
         await using var other = await AgentSessionConnection.OpenAsync(Harness);
         await Calls.CallAsync(Calls.Agent(other, On("attach_tour", tour.Id)), """{ "takeOver": true }""");
         await Harness.DropSessionAsync(other);
         await session.DisposeAsync();
      }
      else
      {
         await Harness.DropSessionAsync(session);
      }

      using var response = await Calls.SendAsync(await Calls.UiAsync(On("request_tour_action", tour.Id)), ActionBody("explain", StopAnchor(tour.StopId)));

      var error = await ErrorAsync(response, HttpStatusCode.Conflict, "noSessionAttached");
      Assert.Contains("no session can receive the tour action", (string)error["reason"]!, StringComparison.Ordinal);
      Assert.Empty(await Calls.ThreadsAsync(tour.Id));
   }

   /// <summary>The request's type holds no anchor, no decision kind and no follow-up items, so each fails to read, before the tour and its binding are checked.</summary>
   [Theory]
   [InlineData("""{ "action": { "kind": "explain" } }""", "action.target", "missing required property.")]
   [InlineData("""{ "action": { "kind": "explain", "target": { "kind": "tour" } } }""", "action.target.kind", "expected one of stop, stopText.")]
   [InlineData("""{ "action": { "kind": "replan" }, "anchor": { "kind": "tour" }, "text": "Fewer stops." }""", "anchor", "'anchor'")]
   [InlineData("""{ "action": { "kind": "summarize", "target": { "kind": "stop", "stopId": "s1" } } }""", "action.target", "'target'")]
   [InlineData("""{ "action": { "kind": "familiarityChanged", "from": "some", "to": "new", "stopIds": [] } }""", "action.kind", "expected one of explain, revise,")]
   [InlineData("""{ "action": { "kind": "planApproved", "stopIds": [], "skippedStops": [], "familiarity": "some" } }""", "action.kind", "expected one of explain, revise,")]
   [InlineData("""{ "action": { "kind": "draftComments", "concerns": [], "risks": [] } }""", "action.concerns", "'concerns'")]
   public async Task Request_that_names_what_its_type_cannot_hold_fails_to_read_and_stores_nothing(string body, string field, string named)
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);

      using var response = await Calls.SendAsync(await Calls.UiAsync(On("request_tour_action", tour.Id)), body);

      var error = await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal(field, (string?)error["field"]);
      Assert.Contains(named, (string)error["reason"]!, StringComparison.Ordinal);
      Assert.Empty(await Calls.ThreadsAsync(tour.Id));
   }

   [Fact]
   public async Task Action_on_a_stop_with_its_current_version_stores_that_version()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);

      var thread = await Calls.CallAsync(
         await Calls.UiAsync(On("request_tour_action", tour.Id)), $$"""{ "action": { "kind": "revise", "target": { "kind": "stop", "stopId": "{{tour.StopId}}", "version": 1 } }, "text": "Shorter, please." }""");

      Assert.Equal(1, (int)thread["action"]!["stopVersion"]!);
   }

   [Fact]
   public async Task Action_with_words_over_the_limit_is_refused_and_stores_nothing()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);

      using var response = await Calls.SendAsync(
         await Calls.UiAsync(On("request_tour_action", tour.Id)), ActionBody("revise", StopAnchor(tour.StopId), new string('a', ReviewerWords.MaxLength + 1)));

      var error = await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("text", (string)error["field"]!);
      Assert.Empty(await Calls.ThreadsAsync(tour.Id));
   }

   [Fact]
   public async Task Revise_whose_words_are_only_whitespace_is_refused_and_stores_nothing()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);

      using var response = await Calls.SendAsync(await Calls.UiAsync(On("request_tour_action", tour.Id)), ActionBody("revise", StopAnchor(tour.StopId), "  "));

      var error = await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("text", (string)error["field"]!);
      Assert.Empty(await Calls.ThreadsAsync(tour.Id));
   }

   /// <summary>A follow-up is the reviewer's question about the action, so it carries no action of its own, and its event names none.</summary>
   [Fact]
   public async Task Follow_up_in_an_action_thread_is_a_question_without_an_action()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await session.ProbeAsync();
      var thread = await Calls.CallAsync(await Calls.UiAsync(On("request_tour_action", tour.Id)), ActionBody("revise", StopAnchor(tour.StopId), "Shorter, please."));
      await session.Stream.NextAsync(Timeout);
      await Calls.CallAsync(Calls.Agent(session, On("reply", tour.Id, questionId: QuestionIdOf(thread))), """{ "resolution": "needsAnswer", "text": "Which part?" }""");

      var followed = await Calls.CallAsync(await Calls.UiAsync(On("ask_follow_up", tour.Id, threadId: ThreadIdOf(thread))), MessageBody("The rounding."));
      var sent = await session.Stream.NextAsync(Timeout);

      Assert.Null(followed["messages"]![2]!["action"]);
      Assert.Equal("revise", (string)followed["action"]!["kind"]!);
      Assert.Null(sent.Data["action"]);
   }

   [Theory]
   [InlineData("explain")]
   [InlineData("revise")]
   public async Task Resend_of_an_action_on_a_stop_before_any_revision_sends_it_again_with_its_kind(string kind)
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await session.ProbeAsync();
      var thread = await Calls.CallAsync(await Calls.UiAsync(On("request_tour_action", tour.Id)), ActionBody(kind, StopAnchor(tour.StopId), "Go deeper on the rounding."));
      await session.Stream.NextAsync(Timeout);

      var resent = await Calls.CallAsync(await Calls.UiAsync(On("resend_question", tour.Id, questionId: QuestionIdOf(thread))));
      var sent = await session.Stream.NextAsync(Timeout);

      Assert.Equal(("canceled", "pending"), ((string)RequestOf(resent, 0, 0)["state"]!, (string)RequestOf(resent, 0, 1)["state"]!));
      Assert.Equal((kind, (string)RequestOf(resent, 0, 1)["id"]!), ((string?)sent.Data["action"], (string?)sent.Data["requestId"]));
   }

   /// <summary>A revision that keeps the headline lands between the action on its text and the re-send, so the anchor moves to version 2 and the re-send's check passes.</summary>
   [Fact]
   public async Task Resend_of_an_action_on_stop_text_whose_field_a_revision_kept_sends_it_again()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await session.ProbeAsync();
      var thread = await Calls.CallAsync(await Calls.UiAsync(On("request_tour_action", tour.Id)), ActionBody("explain", TextAnchor(tour.StopId)));
      await session.Stream.NextAsync(Timeout);
      await Answers.PutStopAsync(session, tour.Id, tour.StopId, AnswerCalls.Checks);

      var resent = await Calls.CallAsync(await Calls.UiAsync(On("resend_question", tour.Id, questionId: QuestionIdOf(thread))));
      var sent = await session.Stream.NextAsync(Timeout);

      Assert.Equal(2, (int)resent["anchor"]!["version"]!);
      Assert.Equal(("explain", (string)RequestOf(resent, 0, 1)["id"]!), ((string?)sent.Data["action"], (string?)sent.Data["requestId"]));
   }

   /// <summary>A question checks nothing against the plan on a re-send, so a question about a stop that the plan dropped still goes to Claude again.</summary>
   [Fact]
   public async Task Resend_of_a_question_on_a_stop_that_the_plan_dropped_sends_it_again()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await session.ProbeAsync();
      var thread = await Calls.CallAsync(await Calls.UiAsync(On("ask_question", tour.Id)), StartBody("Why this stop?", StopAnchor(tour.StopId)));
      await session.Stream.NextAsync(Timeout);
      await Calls.CallAsync(Calls.Agent(session, On("set_plan", tour.Id)), """{ "chapters": [{ "id": "code", "title": "Code" }], "stops": [{ "title": "Other", "kind": "inspect", "chapter": "code" }] }""");

      var resent = await Calls.CallAsync(await Calls.UiAsync(On("resend_question", tour.Id, questionId: QuestionIdOf(thread))));
      var sent = await session.Stream.NextAsync(Timeout);

      Assert.Equal((string)RequestOf(resent, 0, 1)["id"]!, (string?)sent.Data["requestId"]);
   }

   // The thread is as it was, and the session's next event is the question asked after the refusal.
   private async Task AssertNothingSentAsync(AgentSessionConnection session, string tourId, JsonNode thread)
   {
      Assert.True(JsonNode.DeepEquals(thread, Assert.Single(await Calls.ThreadsAsync(tourId))), "The refused re-send changed the thread.");
      var asked = await Calls.AskAsync(tourId);
      Assert.Equal(QuestionIdOf(asked), (string?)(await session.Stream.NextAsync(Timeout)).Data["questionId"]);
   }

   [Fact]
   public async Task Agent_read_state_shows_the_action_of_a_question()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await Calls.CallAsync(await Calls.UiAsync(On("request_tour_action", tour.Id)), ActionBody("revise", StopAnchor(tour.StopId), "Shorter, please."));

      var read = await Calls.CallAsync(Calls.Agent(session, On("read_state", tour.Id)));

      var thread = read["threads"]![0]!;
      Assert.Equal(("revise", "Shorter, please."), ((string)thread["action"]!["kind"]!, (string)thread["messages"]![0]!["text"]!));
   }
}
