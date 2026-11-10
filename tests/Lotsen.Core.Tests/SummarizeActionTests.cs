using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Adapter;
using Lotsen.Operations;
using Lotsen.Sessions;
using Lotsen.Tours;
using static Lotsen.Tests.OperationCall;
using static Lotsen.Tests.ThreadCalls;
using static Lotsen.Tests.TourActionTests;

namespace Lotsen.Tests;

/// <summary>
/// Covers the summarize tour action: the reviewer's request from the summary page that Claude write a synthesis of the review, which the server stores in a thread on the tour and sends to the bound
/// session as a question event that names the action's kind. One waits at a time.
/// </summary>
[Trait("Category", "TourActions")]
public sealed class SummarizeActionTests : IClassFixture<StopTourFixture>
{
   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

   private const string TourAnchor = TourActionTests.TourTarget;

   private const string DraftText = "DRAFT-ANSWER-WORDS";

   private const string NoteText = "NOTE-WORDS";

   private readonly PinnedTourFixture _tour;

   public SummarizeActionTests(StopTourFixture fixture)
   {
      _tour = fixture.Tour;
   }

   private ServerHarness Harness => _tour.Harness;

   private AnswerCalls Answers => new(Harness);

   private ThreadCalls Calls => new(Harness);

   private const string FollowUpBody = """{ "text": "Say more about the rounding." }""";

   // A summarize whose question Claude answered with a request for the reviewer's answer, so its thread takes a follow-up and nothing waits in it.
   private async Task<JsonNode> AnsweredSummarizeAsync(AgentSessionConnection session, string tourId)
   {
      var thread = await Calls.CallAsync(await Calls.UiAsync(On("request_tour_action", tourId)), ActionBody(TourAction.SummarizeKind, TourAnchor));
      await ReplyAsync(session, tourId, QuestionIdOf(thread), "needsAnswer");
      return thread;
   }

   private async Task ReplyAsync(AgentSessionConnection session, string tourId, string questionId, string resolution) =>
      await Calls.CallAsync(Calls.Agent(session, On("reply", tourId, questionId: questionId)), $$"""{ "resolution": "{{resolution}}", "text": "Which part?" }""");

   private static JsonNode ThreadOf(JsonArray threads, string threadId) => threads.Single(thread => (string)thread!["id"]! == threadId)!;

   [Fact]
   public async Task Summarize_is_stored_on_the_tour_anchor_and_reaches_the_bound_session_naming_its_kind()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await session.ProbeAsync();

      var thread = await Calls.CallAsync(await Calls.UiAsync(On("request_tour_action", tour.Id)), ActionBody(TourAction.SummarizeKind, TourAnchor, "Focus on the rounding."));
      var sent = await session.Stream.NextAsync(Timeout);

      var question = Assert.Single(thread["messages"]!.AsArray())!;
      Assert.True(JsonNode.DeepEquals(new JsonObject { ["kind"] = "summarize" }, thread["action"]), $"action: {thread["action"]}");
      Assert.True(JsonNode.DeepEquals(new JsonObject { ["kind"] = "tour" }, thread["anchor"]), $"anchor: {thread["anchor"]}");
      Assert.Equal(("pending", session.Id), ((string)RequestOf(thread)["state"]!, (string)RequestOf(thread)["sessionId"]!));
      Assert.Equal(SessionEvents.Question, sent.Type);
      Assert.Equal(("summarize", "Focus on the rounding."), ((string?)sent.Data["action"], (string?)sent.Data["text"]));
      var meta = ChannelNotifications.ParamsFor(new SessionEvent(sent.Type, sent.Data.ToJsonString()))!["meta"]!;
      Assert.Equal((SessionEvents.Question, "summarize"), ((string)meta["event"]!, (string?)meta["action"]));
   }

   [Fact]
   public async Task Summarize_without_a_bound_session_is_refused_and_stores_nothing()
   {
      var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await Harness.DropSessionAsync(session);

      using var response = await Calls.SendAsync(await Calls.UiAsync(On("request_tour_action", tour.Id)), ActionBody(TourAction.SummarizeKind, TourAnchor));

      await ErrorAsync(response, HttpStatusCode.Conflict, "noSessionAttached");
      Assert.Empty(await Calls.ThreadsAsync(tour.Id));
   }

   /// <summary>A replan does not wait behind a summarize, because a summarize changes no stop.</summary>
   [Fact]
   public async Task Second_summarize_while_one_waits_is_refused_and_one_goes_again_once_claude_replied()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      var first = await Calls.CallAsync(await Calls.UiAsync(On("request_tour_action", tour.Id)), ActionBody(TourAction.SummarizeKind, TourAnchor));

      using var second = await Calls.SendAsync(await Calls.UiAsync(On("request_tour_action", tour.Id)), ActionBody(TourAction.SummarizeKind, TourAnchor));
      var error = await ErrorAsync(second, HttpStatusCode.Conflict, "questionWaiting");
      var replan = await Calls.CallAsync(await Calls.UiAsync(On("request_tour_action", tour.Id)), ActionBody("replan", TourAnchor, "Add a stop on the tests."));
      await Calls.CallAsync(Calls.Agent(session, On("reply", tour.Id, questionId: QuestionIdOf(first))), """{ "resolution": "done", "text": "Wrote the synthesis." }""");
      var third = await Calls.CallAsync(await Calls.UiAsync(On("request_tour_action", tour.Id)), ActionBody(TourAction.SummarizeKind, TourAnchor));

      Assert.Contains("Claude still summarizes the review.", (string)error["reason"]!, StringComparison.Ordinal);
      Assert.Equal("replan", (string)replan["action"]!["kind"]!);
      Assert.Equal("summarize", (string)third["action"]!["kind"]!);
   }

   [Fact]
   public async Task Resend_of_the_waiting_summarize_goes_and_a_resend_of_a_canceled_one_while_another_waits_is_refused()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      var first = await Calls.CallAsync(await Calls.UiAsync(On("request_tour_action", tour.Id)), ActionBody(TourAction.SummarizeKind, TourAnchor));

      var resent = await Calls.CallAsync(await Calls.UiAsync(On("resend_question", tour.Id, questionId: QuestionIdOf(first))));
      await Calls.CallAsync(await Calls.UiAsync(On("cancel_question", tour.Id, questionId: QuestionIdOf(first))));
      var second = await Calls.CallAsync(await Calls.UiAsync(On("request_tour_action", tour.Id)), ActionBody(TourAction.SummarizeKind, TourAnchor));
      using var refused = await Calls.SendAsync(await Calls.UiAsync(On("resend_question", tour.Id, questionId: QuestionIdOf(first))));

      Assert.Equal(2, resent["messages"]![0]!["requests"]!.AsArray().Count);
      var error = await ErrorAsync(refused, HttpStatusCode.Conflict, "questionWaiting");
      Assert.Contains("Claude still summarizes the review.", (string)error["reason"]!, StringComparison.Ordinal);
   }

   /// <summary>
   /// A follow-up asks Claude about a summarize, so it can bring another synthesis. The last question of each summarize thread decides whether that thread waits, see
   /// <see cref="ActionRules.WaitsAlone"/>.
   /// </summary>
   [Fact]
   public async Task Follow_up_in_an_answered_summarize_thread_goes_while_no_other_summarize_thread_has_a_waiting_last_question()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      var first = await AnsweredSummarizeAsync(session, tour.Id);

      var followed = await Calls.CallAsync(await Calls.UiAsync(On("ask_follow_up", tour.Id, threadId: ThreadIdOf(first))), FollowUpBody);

      Assert.Equal("pending", (string)RequestOf(followed, message: 2)["state"]!);
   }

   [Fact]
   public async Task Follow_up_in_an_answered_summarize_thread_is_refused_while_the_first_question_of_another_summarize_thread_waits()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      var first = await AnsweredSummarizeAsync(session, tour.Id);
      var second = await Calls.CallAsync(await Calls.UiAsync(On("request_tour_action", tour.Id)), ActionBody(TourAction.SummarizeKind, TourAnchor));

      using var refused = await Calls.SendAsync(await Calls.UiAsync(On("ask_follow_up", tour.Id, threadId: ThreadIdOf(first))), FollowUpBody);

      var error = await ErrorAsync(refused, HttpStatusCode.Conflict, "questionWaiting");
      Assert.Equal("threadId", (string)error["field"]!);
      Assert.Contains("Claude still summarizes the review.", (string)error["reason"]!, StringComparison.Ordinal);
      Assert.Equal(2, ThreadOf(await Calls.ThreadsAsync(tour.Id), ThreadIdOf(first))["messages"]!.AsArray().Count);
   }

   [Fact]
   public async Task Follow_up_and_summarize_are_refused_while_the_last_question_of_another_summarize_thread_is_a_waiting_follow_up()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      var first = await AnsweredSummarizeAsync(session, tour.Id);
      var second = await AnsweredSummarizeAsync(session, tour.Id);
      await Calls.CallAsync(await Calls.UiAsync(On("ask_follow_up", tour.Id, threadId: ThreadIdOf(second))), FollowUpBody);

      using var followUp = await Calls.SendAsync(await Calls.UiAsync(On("ask_follow_up", tour.Id, threadId: ThreadIdOf(first))), FollowUpBody);
      using var summarize = await Calls.SendAsync(await Calls.UiAsync(On("request_tour_action", tour.Id)), ActionBody(TourAction.SummarizeKind, TourAnchor));

      Assert.Contains("Claude still summarizes the review.", (string)(await ErrorAsync(followUp, HttpStatusCode.Conflict, "questionWaiting"))["reason"]!, StringComparison.Ordinal);
      Assert.Contains("Claude still summarizes the review.", (string)(await ErrorAsync(summarize, HttpStatusCode.Conflict, "questionWaiting"))["reason"]!, StringComparison.Ordinal);
   }

   [Fact]
   public async Task Resend_of_a_follow_up_in_a_summarize_thread_is_refused_while_the_first_question_of_another_summarize_thread_waits()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      var first = await AnsweredSummarizeAsync(session, tour.Id);
      var followed = await Calls.CallAsync(await Calls.UiAsync(On("ask_follow_up", tour.Id, threadId: ThreadIdOf(first))), FollowUpBody);
      await Calls.CallAsync(await Calls.UiAsync(On("cancel_question", tour.Id, questionId: QuestionIdOf(followed, message: 2))));
      var second = await Calls.CallAsync(await Calls.UiAsync(On("request_tour_action", tour.Id)), ActionBody(TourAction.SummarizeKind, TourAnchor));

      using var refused = await Calls.SendAsync(await Calls.UiAsync(On("resend_question", tour.Id, questionId: QuestionIdOf(followed, message: 2))));

      var error = await ErrorAsync(refused, HttpStatusCode.Conflict, "questionWaiting");
      Assert.Equal("questionId", (string)error["field"]!);
      Assert.Contains("Claude still summarizes the review.", (string)error["reason"]!, StringComparison.Ordinal);
   }

   /// <summary>The thread's own waiting follow-up is the one that waits, so it does not count against itself.</summary>
   [Fact]
   public async Task Resend_of_the_waiting_follow_up_in_a_summarize_thread_goes()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      var first = await AnsweredSummarizeAsync(session, tour.Id);
      var followed = await Calls.CallAsync(await Calls.UiAsync(On("ask_follow_up", tour.Id, threadId: ThreadIdOf(first))), FollowUpBody);

      var resent = await Calls.CallAsync(await Calls.UiAsync(On("resend_question", tour.Id, questionId: QuestionIdOf(followed, message: 2))));

      Assert.Equal(("canceled", "pending"), ((string)RequestOf(resent, message: 2)["state"]!, (string)RequestOf(resent, message: 2, index: 1)["state"]!));
   }

   /// <summary>Only summarize threads count, so a follow-up about a replan and a question in a thread without an action go while a summarize waits.</summary>
   [Fact]
   public async Task Follow_up_in_a_thread_of_another_kind_goes_while_a_summarize_waits()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      var replan = await Calls.CallAsync(await Calls.UiAsync(On("request_tour_action", tour.Id)), ActionBody("replan", TourAnchor, "Add a stop on the tests."));
      await ReplyAsync(session, tour.Id, QuestionIdOf(replan), "needsAnswer");
      var asked = await Calls.AskAsync(tour.Id);
      await ReplyAsync(session, tour.Id, QuestionIdOf(asked), "done");
      await Calls.CallAsync(await Calls.UiAsync(On("request_tour_action", tour.Id)), ActionBody(TourAction.SummarizeKind, TourAnchor));

      var afterReplan = await Calls.CallAsync(await Calls.UiAsync(On("ask_follow_up", tour.Id, threadId: ThreadIdOf(replan))), FollowUpBody);
      var afterQuestion = await Calls.CallAsync(await Calls.UiAsync(On("ask_follow_up", tour.Id, threadId: ThreadIdOf(asked))), FollowUpBody);

      Assert.All([afterReplan, afterQuestion], thread => Assert.Equal("pending", (string)RequestOf(thread, message: 2)["state"]!));
   }

   /// <summary>The leak paths of the action: the question event that reaches the session and what <c>fetch_question</c> returns, with a draft answer and a note on the tour.</summary>
   [Fact]
   public async Task Summarize_question_event_and_fetch_question_hold_no_draft_answer_and_no_note()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await session.ProbeAsync();
      await Answers.AnswerAsync(tour, 0, DraftText);
      await Calls.SaveNoteAsync(tour.Id, NoteText);

      var thread = await Calls.CallAsync(await Calls.UiAsync(On("request_tour_action", tour.Id)), ActionBody(TourAction.SummarizeKind, TourAnchor));
      var sent = await session.Stream.NextAsync(Timeout);
      var channel = ChannelNotifications.ParamsFor(new SessionEvent(sent.Type, sent.Data.ToJsonString()))!.ToJsonString();
      var fetched = (await Calls.FetchAsync(session, tour.Id, QuestionIdOf(thread))).ToJsonString();

      Assert.All([sent.Data.ToJsonString(), channel, fetched], json =>
      {
         Assert.DoesNotContain(DraftText, json, StringComparison.Ordinal);
         Assert.DoesNotContain(NoteText, json, StringComparison.Ordinal);
      });
      Assert.Contains("summarize", channel, StringComparison.Ordinal);
   }
}
