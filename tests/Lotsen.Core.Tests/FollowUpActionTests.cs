using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Adapter;
using Lotsen.Operations;
using Lotsen.Sessions;
using Lotsen.Tours;
using Microsoft.Extensions.DependencyInjection;
using static Lotsen.Tests.OperationCall;
using static Lotsen.Tests.ThreadCalls;
using static Lotsen.Tests.TourActionTests;

namespace Lotsen.Tests;

/// <summary>
/// Covers the follow-up actions that the reviewer requests from the summary page: <c>draftWorkItems</c>, <c>walkOpenQuestions</c> and <c>draftComments</c>. Each takes the tour and carries the
/// group of the review summary that the server collected from an agent session's summary when it stored the request.
/// </summary>
/// <remarks>Each test stores the summary's state through the store, from <see cref="SummaryTours"/>, on a tour that the test's session created and holds.</remarks>
[Trait("Category", "TourActions")]
public sealed class FollowUpActionTests : IAsyncLifetime
{
   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

   private const string TourAnchor = TourActionTests.TourTarget;

   private const string RiskId = "r1";

   private ServerHarness _harness = null!;
   private ThreadCalls _calls = null!;

   public async ValueTask InitializeAsync()
   {
      _harness = await ServerHarness.StartAsync();
      _calls = new ThreadCalls(_harness);
   }

   public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

   public static TheoryData<string> Kinds => [TourAction.DraftWorkItemsKind, TourAction.WalkOpenQuestionsKind, TourAction.DraftCommentsKind];

   [Fact]
   public async Task Draft_work_items_is_stored_on_the_tour_anchor_with_the_sent_decisions_and_reaches_the_session_naming_its_kind()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await SeededTourAsync(session);
      await session.ProbeAsync();

      var thread = await RequestAsync(id, TourAction.DraftWorkItemsKind, "Put them on the board.");
      var sent = await session.Stream.NextAsync(Timeout);

      var expected = JsonNode.Parse("""
         {
           "kind": "draftWorkItems",
           "decisions": [{ "stopId": "s2", "stopTitle": "The new discount rule", "checkId": "c1", "question": "Is the cap right?", "text": "The cap is right.", "type": "decision" }]
         }
         """);
      Assert.True(JsonNode.DeepEquals(expected, thread["action"]), $"action: {thread["action"]}");
      Assert.True(JsonNode.DeepEquals(JsonNode.Parse(TourAnchor), thread["anchor"]), $"anchor: {thread["anchor"]}");
      Assert.Equal(("pending", session.Id), ((string)RequestOf(thread)["state"]!, (string)RequestOf(thread)["sessionId"]!));
      Assert.Equal(SessionEvents.Question, sent.Type);
      Assert.Equal((TourAction.DraftWorkItemsKind, "Put them on the board."), ((string?)sent.Data["action"], (string?)sent.Data["text"]));
   }

   [Fact]
   public async Task Walk_open_questions_is_stored_with_the_sent_open_questions()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await SeededTourAsync(session);

      var thread = await RequestAsync(id, TourAction.WalkOpenQuestionsKind);

      var expected = JsonNode.Parse("""
         {
           "kind": "walkOpenQuestions",
           "openQuestions": [{ "stopId": "s5", "stopTitle": "Retry policy", "checkId": "c3", "question": "How often should it retry?", "text": "Ask the team.", "type": "openQuestion" }]
         }
         """);
      Assert.True(JsonNode.DeepEquals(expected, thread["action"]), $"action: {thread["action"]}");
   }

   /// <summary>An answer whose stop the plan dropped is listed with what is known, as the summary lists it, and the action omits the pending risk stop.</summary>
   [Fact]
   public async Task Draft_comments_is_stored_with_the_sent_concerns_and_the_risk_stops_that_claude_wrote()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await SeededTourAsync(session);

      var thread = await RequestAsync(id, TourAction.DraftCommentsKind);

      var expected = JsonNode.Parse("""
         {
           "kind": "draftComments",
           "concerns": [{ "stopId": "gone", "checkId": "c9", "text": "Who owns this?", "type": "concern" }],
           "risks": [{ "stopId": "s3", "title": "Rounding on stacked discounts", "scenario": "Two discounts round twice." }]
         }
         """);
      Assert.True(JsonNode.DeepEquals(expected, thread["action"]), $"action: {thread["action"]}");
   }

   /// <summary>The tour holds one sent concern, so the action goes whichever risk stops it hands. The cases pin the rule of <see cref="ActionRules"/> for a draftComments action's risk stops.</summary>
   [Theory]
   [InlineData(true, false, false, true)]
   [InlineData(false, false, false, false)]
   [InlineData(false, false, true, false)]
   [InlineData(true, false, true, false)]
   [InlineData(true, true, false, true)]
   [InlineData(true, true, true, false)]
   public async Task Draft_comments_hands_a_risk_stop_only_when_claude_wrote_it_and_the_reviewer_did_not_skip_it(bool written, bool stale, bool skipped, bool handed)
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      Seed(id, tour => SummaryTours.Apply(tour, session.Id) with
      {
         Plan = PlanWithRisk(written, stale, skipped),
         Answers = [.. SummaryTours.Apply(tour, session.Id).Answers.Where(answer => answer.Type == AnswerType.Concern && answer.State != AnswerState.Draft)],
         Threads = TourThreads.Empty,
      });

      var thread = await RequestAsync(id, TourAction.DraftCommentsKind);

      var action = thread["action"]!;
      string[] risks = handed ? [RiskId] : [];
      Assert.Equal(risks, action["risks"]!.AsArray().Select(risk => (string)risk!["stopId"]!));
      Assert.Equal(["c9"], action["concerns"]!.AsArray().Select(concern => (string)concern!["checkId"]!));
   }

   /// <summary>The summary still lists the risk stop, because the page lists every risk stop of the plan under Concerns.</summary>
   [Theory]
   [InlineData(false, false)]
   [InlineData(false, true)]
   [InlineData(true, true)]
   public async Task Draft_comments_without_a_sent_concern_is_refused_when_its_only_risk_stop_is_pending_or_skipped_as_the_summary_says(bool written, bool skipped)
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      Seed(id, tour => SummaryTours.Apply(tour, session.Id) with
      {
         Plan = PlanWithRisk(written, stale: false, skipped),
         Answers = [],
         Threads = TourThreads.Empty,
      });

      using var response = await _calls.SendAsync(await _calls.UiAsync(On("request_tour_action", id)), ActionBody(TourAction.DraftCommentsKind, TourAnchor));
      var summary = await _calls.CallAsync(await _calls.UiAsync(On("read_summary", id)));

      var error = await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal($"action: {ActionRules.NoConcernSent}", (string)error["reason"]!);
      Assert.Equal(ActionRules.NoConcernSent, (string)summary["followUps"]![TourAction.DraftCommentsKind]!);
      Assert.Equal([RiskId], summary["risks"]!.AsArray().Select(risk => (string)risk!["stopId"]!));
      Assert.Empty(await _calls.ThreadsAsync(id));
   }

   [Theory]
   [MemberData(nameof(Kinds))]
   public async Task Follow_up_without_a_bound_session_is_refused_and_stores_nothing(string kind)
   {
      var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await SeededTourAsync(session);
      await _harness.DropSessionAsync(session);

      using var response = await _calls.SendAsync(await _calls.UiAsync(On("request_tour_action", id)), ActionBody(kind, TourAnchor));

      await ErrorAsync(response, HttpStatusCode.Conflict, "noSessionAttached");
      Assert.DoesNotContain(await _calls.ThreadsAsync(id), thread => (string?)thread!["action"]?["kind"] == kind);
   }

   /// <summary>The summary page disables each action with the reason that the summary returns, which is the refusal's reason.</summary>
   [Theory]
   [InlineData(TourAction.DraftWorkItemsKind, "No decision has been sent to Claude yet.")]
   [InlineData(TourAction.WalkOpenQuestionsKind, "No open question has been sent to Claude yet.")]
   [InlineData(TourAction.DraftCommentsKind, "No concern has been sent to Claude yet, and every risk stop is still unwritten or skipped.")]
   public async Task Follow_up_whose_group_holds_only_drafts_is_refused_with_what_is_missing_as_the_summary_says(string kind, string missing)
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      Seed(id, tour => SummaryTours.Apply(tour, session.Id) with
      {
         Plan = SummaryTours.Plan() with { Stops = [.. SummaryTours.Plan().Stops.Where(stop => stop.Kind != StopKind.Risk)] },
         Answers =
         [
            new CheckAnswer("s2", "c1", "A draft decision.", AnswerType.Decision, AnswerState.Draft, DateTimeOffset.UnixEpoch),
            new CheckAnswer("s2", "c2", "A draft concern.", AnswerType.Concern, AnswerState.Draft, DateTimeOffset.UnixEpoch),
            new CheckAnswer("s5", "c3", "A draft open question.", AnswerType.OpenQuestion, AnswerState.Draft, DateTimeOffset.UnixEpoch),
         ],
         Threads = TourThreads.Empty,
      });

      using var response = await _calls.SendAsync(await _calls.UiAsync(On("request_tour_action", id)), ActionBody(kind, TourAnchor));
      var summary = await _calls.CallAsync(await _calls.UiAsync(On("read_summary", id)));

      var error = await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("action", (string)error["field"]!);
      Assert.StartsWith($"action: {missing}", (string)error["reason"]!, StringComparison.Ordinal);
      Assert.Equal($"action: {(string)summary["followUps"]![kind]!}", (string)error["reason"]!);
      Assert.Empty(await _calls.ThreadsAsync(id));
   }

   /// <summary>A risk stop that Claude wrote is a concern of the summary, so it alone gives draftComments its group, and the action omits the pending risk stop.</summary>
   [Fact]
   public async Task Draft_comments_goes_with_risk_stops_alone_and_the_summary_names_no_reason_for_it()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      Seed(id, tour => SummaryTours.Apply(tour, session.Id) with { Answers = [], Threads = TourThreads.Empty });

      var summary = await _calls.CallAsync(await _calls.UiAsync(On("read_summary", id)));
      var thread = await RequestAsync(id, TourAction.DraftCommentsKind);

      Assert.Null(summary["followUps"]![TourAction.DraftCommentsKind]);
      Assert.NotNull(summary["followUps"]![TourAction.DraftWorkItemsKind]);
      Assert.Equal(["s3"], thread["action"]!["risks"]!.AsArray().Select(risk => (string)risk!["stopId"]!));
      Assert.Empty(thread["action"]!["concerns"]!.AsArray());
   }

   [Theory]
   [InlineData(TourAction.DraftWorkItemsKind, "decisions")]
   [InlineData(TourAction.WalkOpenQuestionsKind, "openQuestions")]
   [InlineData(TourAction.DraftCommentsKind, "concerns")]
   [InlineData(TourAction.DraftCommentsKind, "risks")]
   public async Task Follow_up_that_gives_its_items_is_refused_and_stores_nothing(string kind, string field)
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await SeededTourAsync(session);
      var threadsBefore = (await _calls.ThreadsAsync(id)).Count;
      var body = new JsonObject { ["action"] = new JsonObject { ["kind"] = kind, [field] = new JsonArray() } }.ToJsonString();

      using var response = await _calls.SendAsync(await _calls.UiAsync(On("request_tour_action", id)), body);

      var error = await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal($"action.{field}", (string)error["field"]!);
      Assert.Equal(threadsBefore, (await _calls.ThreadsAsync(id)).Count);
   }

   /// <summary>One of each kind waits at a time, and the kinds do not wait for each other, for a summarize or for a replan, because each acts on another part of the review.</summary>
   [Fact]
   public async Task Second_follow_up_of_a_kind_is_refused_while_one_waits_and_the_other_kinds_go()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await SeededTourAsync(session);
      var first = await RequestAsync(id, TourAction.DraftWorkItemsKind);

      using var second = await _calls.SendAsync(await _calls.UiAsync(On("request_tour_action", id)), ActionBody(TourAction.DraftWorkItemsKind, TourAnchor));
      var error = await ErrorAsync(second, HttpStatusCode.Conflict, "questionWaiting");
      var others = new[] { TourAction.WalkOpenQuestionsKind, TourAction.DraftCommentsKind, TourAction.SummarizeKind };
      var started = new List<JsonNode>();
      foreach (var kind in others)
      {
         started.Add(await RequestAsync(id, kind));
      }

      var replan = await _calls.CallAsync(await _calls.UiAsync(On("request_tour_action", id)), ActionBody(TourAction.ReplanKind, TourAnchor, "Add a stop on the tests."));
      await ReplyAsync(session, id, QuestionIdOf(first), "done");
      var third = await RequestAsync(id, TourAction.DraftWorkItemsKind);

      Assert.Equal("action", (string)error["field"]!);
      Assert.Contains("Claude still proposes work items for the decisions.", (string)error["reason"]!, StringComparison.Ordinal);
      Assert.Equal(others, started.Select(thread => (string)thread["action"]!["kind"]!));
      Assert.Equal(TourAction.ReplanKind, (string)replan["action"]!["kind"]!);
      Assert.Equal("pending", (string)RequestOf(third)["state"]!);
   }

   /// <summary>A follow-up asks Claude about the action, so it can bring the same work again, and a thread's last question decides whether the thread waits.</summary>
   [Fact]
   public async Task Follow_up_and_resend_in_an_answered_thread_of_a_kind_are_refused_while_another_thread_of_that_kind_waits()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await SeededTourAsync(session);
      var first = await RequestAsync(id, TourAction.WalkOpenQuestionsKind);
      await ReplyAsync(session, id, QuestionIdOf(first), "needsAnswer");
      var followed = await _calls.CallAsync(await _calls.UiAsync(On("ask_follow_up", id, threadId: ThreadIdOf(first))), """{ "text": "Start with the retries." }""");
      await _calls.CallAsync(await _calls.UiAsync(On("cancel_question", id, questionId: QuestionIdOf(followed, message: 2))));
      var second = await RequestAsync(id, TourAction.WalkOpenQuestionsKind);
      var otherKind = await RequestAsync(id, TourAction.DraftWorkItemsKind);
      await ReplyAsync(session, id, QuestionIdOf(otherKind), "needsAnswer");

      using var resend = await _calls.SendAsync(await _calls.UiAsync(On("resend_question", id, questionId: QuestionIdOf(followed, message: 2))));
      var otherFollowUp = await _calls.CallAsync(await _calls.UiAsync(On("ask_follow_up", id, threadId: ThreadIdOf(otherKind))), """{ "text": "Use the team's board." }""");

      var error = await ErrorAsync(resend, HttpStatusCode.Conflict, "questionWaiting");
      Assert.Equal("questionId", (string)error["field"]!);
      Assert.Contains("Claude still walks through the open questions.", (string)error["reason"]!, StringComparison.Ordinal);
      Assert.Equal("pending", (string)RequestOf(otherFollowUp, message: 2)["state"]!);
   }

   /// <summary>
   /// The reviewer changed the decision after the request, which makes it a draft again with new words. The re-send carries the decision as Claude could read it at the request, and none of the
   /// reviewer's new words, though the summary now holds no sent decision.
   /// </summary>
   [Fact]
   public async Task Resend_carries_the_stored_items_after_the_answer_was_edited_back_to_a_draft()
   {
      const string EditedText = "EDITED-DRAFT-WORDS";
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await SeededTourAsync(session);
      await session.ProbeAsync();
      var thread = await RequestAsync(id, TourAction.DraftWorkItemsKind);
      await session.Stream.NextAsync(Timeout);
      await _calls.CallAsync(await _calls.UiAsync(On("answer_check", id, stopId: "s2", checkId: "c1")), $$"""{ "text": "{{EditedText}}" }""");

      var resent = await _calls.CallAsync(await _calls.UiAsync(On("resend_question", id, questionId: QuestionIdOf(thread))));
      var sent = await session.Stream.NextAsync(Timeout);
      var fetched = await _calls.FetchAsync(session, id, QuestionIdOf(thread));
      var state = await _calls.CallAsync(_calls.Agent(session, On("read_state", id)));

      Assert.Equal("pending", (string)RequestOf(resent, index: 1)["state"]!);
      Assert.Equal("The cap is right.", (string)fetched["thread"]!["action"]!["decisions"]![0]!["text"]!);
      Assert.All([sent.Data.ToJsonString(), fetched.ToJsonString(), state.ToJsonString()], json => Assert.DoesNotContain(EditedText, json, StringComparison.Ordinal));
   }

   /// <summary>
   /// The leak paths of a follow-up action: its items, the question event and its channel notification, what <c>fetch_question</c> returns and the thread in an agent session's
   /// <c>read_state</c>, with a draft answer of the kind's own type and a note on the tour.
   /// </summary>
   [Theory]
   [InlineData(TourAction.DraftWorkItemsKind, nameof(AnswerType.Decision))]
   [InlineData(TourAction.WalkOpenQuestionsKind, nameof(AnswerType.OpenQuestion))]
   [InlineData(TourAction.DraftCommentsKind, nameof(AnswerType.Concern))]
   public async Task Follow_up_items_question_event_fetch_question_and_read_state_hold_no_draft_answer_and_no_note(string kind, string type)
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      Seed(id, tour =>
      {
         var seeded = SummaryTours.Apply(tour, session.Id);
         return seeded with { Answers = [.. seeded.Answers.Select(answer => answer.State == AnswerState.Draft ? answer.Revised(answer.Text, Enum.Parse<AnswerType>(type), answer.SavedAt) : answer)] };
      });
      await session.ProbeAsync();

      var thread = await RequestAsync(id, kind);
      var sent = await session.Stream.NextAsync(Timeout);
      var channel = ChannelNotifications.ParamsFor(new SessionEvent(sent.Type, sent.Data.ToJsonString()))!.ToJsonString();
      var fetched = (await _calls.FetchAsync(session, id, QuestionIdOf(thread))).ToJsonString();
      var state = (await _calls.CallAsync(_calls.Agent(session, On("read_state", id)))).ToJsonString();

      Assert.All([thread["action"]!.ToJsonString(), sent.Data.ToJsonString(), channel, fetched, state], json =>
      {
         Assert.DoesNotContain(SummaryTours.DraftText, json, StringComparison.Ordinal);
         Assert.DoesNotContain(SummaryTours.NoteText, json, StringComparison.Ordinal);
      });
      Assert.Contains(kind, channel, StringComparison.Ordinal);
   }

   private async Task<JsonNode> RequestAsync(string tourId, string kind, string? text = null) =>
      await _calls.CallAsync(await _calls.UiAsync(On("request_tour_action", tourId)), ActionBody(kind, TourAnchor, text));

   private async Task ReplyAsync(AgentSessionConnection session, string tourId, string questionId, string resolution) =>
      await _calls.CallAsync(_calls.Agent(session, On("reply", tourId, questionId: questionId)), $$"""{ "resolution": "{{resolution}}", "text": "Which part?" }""");

   // An overview and one risk stop, which Claude wrote or not, which a stale marker marks or not, and which the reviewer skipped or not.
   private static Plan PlanWithRisk(bool written, bool stale, bool skipped)
   {
      var overview = SummaryTours.Plan().Stops[0];
      Stop risk = written
         ? new Stop.Written(
            RiskId, "Rounding on stacked discounts", StopKind.Risk, "a",
            new StopContent("Rounding can drift.", [new StopPassage(FileSide.Head, "src/a.cs", 1, 1, PassageRole.Primary, TourValues.Head, "a\n")], Scenario: "Two discounts round twice."), 1, [],
            StaleOn: stale ? 2 : null, Skipped: skipped)
         : new Stop.Pending(RiskId, "Rounding on stacked discounts", StopKind.Risk, "a", Skipped: skipped);
      return SummaryTours.Plan() with { Stops = [overview, risk] };
   }

   // A tour that the session created and holds, with the summary's sample state.
   private async Task<string> SeededTourAsync(AgentSessionConnection session)
   {
      var id = await _calls.CreateAsync(session);
      Seed(id, tour => SummaryTours.Apply(tour, session.Id));
      return id;
   }

   private void Seed(string tourId, Func<Tour, Tour> change)
   {
      Assert.True(TourId.TryParse(tourId, out var id), $"The tour ID {tourId} does not parse.");
      _harness.ChangeStored(id, change);
   }
}
