using Lotsen.Git;
using Lotsen.Sessions;
using Lotsen.Tours;
using static Lotsen.Tests.ThreadFixtures;
using static Lotsen.Tests.TourValues;

namespace Lotsen.Tests;

/// <summary>Covers <see cref="TourOffers.Of"/>, one row per rule, on tour values: each offer against the refusal of the operation that the control calls.</summary>
/// <remarks>
/// The planned tour holds the written stop <c>s1</c> and the pending stop <c>s2</c>, at level some. An offer row names the offer, the operation's own check on the same tour, and a part of the
/// refusal's problem, or null for an open offer. A closed offer carries the code and the problem of that refusal.
/// </remarks>
[Trait("Category", "TourActions")]
public sealed class TourOffersTests
{
   private static readonly Recipient Bound = new AgentSessions().TryOpen()!.Recipient;
   private static readonly AnswerBatch Batch = new("b1", "session1", Now);

   private static readonly Dictionary<string, (Tour Tour, Func<TourOffers, Offer> Offer, Func<Tour, OperationError?> Operation, string? Problem)> Offers = new()
   {
      ["replan without a plan"] = (Tour(), offers => offers.Replan, Requested(new ActionRequest.Replan(), "Fewer stops."), "no plan to replan"),
      ["replan with a plan"] = (Planned(), offers => offers.Replan, Requested(new ActionRequest.Replan(), "Fewer stops."), null),
      ["replan while a replan waits"] = (With(Planned(), Acting(new TourAction.Replan())), offers => offers.Replan, Requested(new ActionRequest.Replan(), "Fewer stops."), "re-plans the tour as you asked"),
      ["replan while a reorder waits"] = (With(Planned(), Acting(new TourAction.Reorder(["s1", "s2"]))), offers => offers.Replan, Requested(new ActionRequest.Replan(), "Fewer stops."), "reorders the plan as you asked"),
      ["replan while a familiarity change waits"] = (With(Planned(), Deciding(LevelChange)), offers => offers.Replan, Requested(new ActionRequest.Replan(), "Fewer stops."), "writes stops at the familiarity level"),
      ["replan while a plan approval waits"] = (With(Planned(), Deciding(Approval)), offers => offers.Replan, Requested(new ActionRequest.Replan(), "Fewer stops."), "writes the stops of the plan that you approved"),
      ["replan while an iteration update waits"] = (With(Planned(), Deciding(_ => Update)), offers => offers.Replan, Requested(new ActionRequest.Replan(), "Fewer stops."), "revises the stops that the last update"),
      ["replan while a summarize waits"] = (With(Planned(), Acting(new TourAction.Summarize())), offers => offers.Replan, Requested(new ActionRequest.Replan(), "Fewer stops."), null),
      ["reorder without a plan"] = (Tour(), offers => offers.Reorder, Requested(new ActionRequest.Reorder([])), "no plan to reorder"),
      ["reorder with a plan"] = (Planned(), offers => offers.Reorder, Requested(Order), null),
      ["reorder while a replan waits"] = (With(Planned(), Acting(new TourAction.Replan())), offers => offers.Reorder, Requested(Order), "re-plans the tour as you asked"),
      ["reorder while a familiarity change waits"] = (With(Planned(), Deciding(LevelChange)), offers => offers.Reorder, Requested(Order), "writes stops at the familiarity level"),
      ["reorder while an iteration update waits"] = (With(Planned(), Deciding(_ => Update)), offers => offers.Reorder, Requested(Order), "revises the stops that the last update"),

      ["explain on a pending stop"] = (Planned(), offers => offers.Stops["s2"].Explain, Requested(new ActionRequest.Explain(new StopTarget.WholeStop("s2"))), "stop 's2' has no content yet"),
      ["explain on a written stop"] = (Planned(), offers => offers.Stops["s1"].Explain, Requested(new ActionRequest.Explain(new StopTarget.WholeStop("s1"))), null),
      ["revise of a pending stop"] = (Planned(), offers => offers.Stops["s2"].Revise, Requested(new ActionRequest.Revise(new StopTarget.WholeStop("s2")), "Shorter."), "stop 's2' has no content yet"),
      ["revise of a written stop"] = (Planned(), offers => offers.Stops["s1"].Revise, Requested(new ActionRequest.Revise(new StopTarget.WholeStop("s1")), "Shorter."), null),

      ["summarize without a plan"] = (Tour(), offers => offers.Summarize, Requested(new ActionRequest.Summarize()), "no review to summarize"),
      ["summarize with a plan"] = (Planned(), offers => offers.Summarize, Requested(new ActionRequest.Summarize()), null),
      ["summarize while a summarize waits"] = (With(Planned(), Acting(new TourAction.Summarize())), offers => offers.Summarize, Requested(new ActionRequest.Summarize()), "summarizes the review"),

      ["draftWorkItems without a sent decision"] = (Planned(), FollowUp(TourAction.DraftWorkItemsKind), Requested(new ActionRequest.FollowUp.DraftWorkItems()), ActionRules.NoDecisionSent),
      ["draftWorkItems with a sent decision"] = (Answered(AnswerType.Decision), FollowUp(TourAction.DraftWorkItemsKind), Requested(new ActionRequest.FollowUp.DraftWorkItems()), null),
      ["draftWorkItems while another waits"] = (
         With(Answered(AnswerType.Decision), Acting(new TourAction.FollowUp.DraftWorkItems([]))),
         FollowUp(TourAction.DraftWorkItemsKind),
         Requested(new ActionRequest.FollowUp.DraftWorkItems()),
         "proposes work items for the decisions"),
      ["walkOpenQuestions without a sent open question"] = (Planned(), FollowUp(TourAction.WalkOpenQuestionsKind), Requested(new ActionRequest.FollowUp.WalkOpenQuestions()), ActionRules.NoOpenQuestionSent),
      ["walkOpenQuestions with a sent open question"] = (Answered(AnswerType.OpenQuestion), FollowUp(TourAction.WalkOpenQuestionsKind), Requested(new ActionRequest.FollowUp.WalkOpenQuestions()), null),
      ["walkOpenQuestions while another waits"] = (
         With(Answered(AnswerType.OpenQuestion), Acting(new TourAction.FollowUp.WalkOpenQuestions([]))),
         FollowUp(TourAction.WalkOpenQuestionsKind),
         Requested(new ActionRequest.FollowUp.WalkOpenQuestions()),
         "walks through the open questions"),
      ["draftComments without a sent concern or a written risk"] = (Planned(), FollowUp(TourAction.DraftCommentsKind), Requested(new ActionRequest.FollowUp.DraftComments()), ActionRules.NoConcernSent),
      ["draftComments with a sent concern"] = (Answered(AnswerType.Concern), FollowUp(TourAction.DraftCommentsKind), Requested(new ActionRequest.FollowUp.DraftComments()), null),
      ["draftComments while another waits"] = (
         With(Answered(AnswerType.Concern), Acting(new TourAction.FollowUp.DraftComments([], []))),
         FollowUp(TourAction.DraftCommentsKind),
         Requested(new ActionRequest.FollowUp.DraftComments()),
         "drafts comments from the concerns"),

      ["level change of a plan-first tour without a plan"] = (Tour() with { PlanApproval = PlanApproval.Awaited }, offers => offers.Level.Change, LevelChanged, "Claude has not set the plan yet"),
      ["level change of a plan-first tour before the approval"] = (Planned() with { PlanApproval = PlanApproval.Awaited }, offers => offers.Level.Change, LevelChanged, "this tour was started plan first, so its familiarity level goes to Claude with your approval of the plan. Pick the level in the plan proposal"),
      ["level change of a tour after its approval"] = (Planned() with { PlanApproval = PlanApproval.Given }, offers => offers.Level.Change, LevelChanged, null),
      ["level change while a familiarity change waits"] = (With(Planned(), Deciding(LevelChange)), offers => offers.Level.Change, LevelChanged, "writes stops at the familiarity level"),
      ["level change while a plan approval waits"] = (With(Planned(), Deciding(Approval)), offers => offers.Level.Change, LevelChanged, "writes the stops of the plan that you approved"),
      ["level change while a replan waits"] = (With(Planned(), Acting(new TourAction.Replan())), offers => offers.Level.Change, LevelChanged, null),

      ["approval of a tour not started plan first"] = (Planned(), offers => offers.ApprovePlan, Approved, "this tour was not started plan first"),
      ["approval of an approved plan"] = (Planned() with { PlanApproval = PlanApproval.Given }, offers => offers.ApprovePlan, Approved, "the plan is approved already"),
      ["approval without a plan"] = (Tour() with { PlanApproval = PlanApproval.Awaited }, offers => offers.ApprovePlan, Approved, "this tour has no plan yet"),
      ["approval of a plan that waits"] = (Planned() with { PlanApproval = PlanApproval.Awaited }, offers => offers.ApprovePlan, Approved, null),

      ["update without an available iteration"] = (Planned(), offers => offers.UpdateIteration, Updated, "this tour has no newer update"),
      ["update to the available iteration"] = (Updatable(), offers => offers.UpdateIteration, Updated, null),
      ["update while an earlier update waits"] = (With(Updatable(), Deciding(_ => Update)), offers => offers.UpdateIteration, Updated, "revises the stops that the last update"),

      ["resend of a note"] = (With(Planned(), Note("t1", new ThreadAnchor.WholeTour())), ThreadOffer(offers => offers.Resend), Resent("t1"), "has no question 't1'"),
      ["follow-up in a note"] = (With(Planned(), Note("t1", new ThreadAnchor.WholeTour())), ThreadOffer(offers => offers.FollowUp), FollowedUp, "note"),
      ["cancel in a note"] = (With(Planned(), Note("t1", new ThreadAnchor.WholeTour())), ThreadOffer(offers => offers.Cancel), Canceled("t1"), "has no question 't1'"),
      ["send of a note"] = (With(Planned(), Note("t1", new ThreadAnchor.WholeTour())), ThreadOffer(offers => offers.SendNote), SentNote, null),
      ["resend of a waiting question"] = (With(Planned(), Asked("t1", new ThreadAnchor.WholeTour())), ThreadOffer(offers => offers.Resend), Resent("q-t1"), null),
      ["follow-up while the question waits"] = (With(Planned(), Asked("t1", new ThreadAnchor.WholeTour())), ThreadOffer(offers => offers.FollowUp), FollowedUp, "still waits"),
      ["cancel of a waiting question"] = (With(Planned(), Asked("t1", new ThreadAnchor.WholeTour())), ThreadOffer(offers => offers.Cancel), Canceled("q-t1"), null),
      ["send of a sent thread"] = (With(Planned(), Asked("t1", new ThreadAnchor.WholeTour())), ThreadOffer(offers => offers.SendNote), SentNote, "sent"),
      ["resend of an answered question"] = (With(Planned(), Answer()), ThreadOffer(offers => offers.Resend), Resent("q-t1"), "answered"),
      ["follow-up after an answer"] = (With(Planned(), Answer()), ThreadOffer(offers => offers.FollowUp), FollowedUp, null),
      ["cancel of an answered question"] = (With(Planned(), Answer()), ThreadOffer(offers => offers.Cancel), Canceled("q-t1"), "answered"),
      ["resend of a canceled familiarity change"] = (With(Planned(), Deciding(LevelChange, RequestState.Canceled)), ThreadOffer(offers => offers.Resend), Resent("q-t1"), "does not go again"),
      ["follow-up after a canceled familiarity change"] = (With(Planned(), Deciding(LevelChange, RequestState.Canceled)), ThreadOffer(offers => offers.FollowUp), FollowedUp, null),
      ["cancel of a canceled familiarity change"] = (With(Planned(), Deciding(LevelChange, RequestState.Canceled)), ThreadOffer(offers => offers.Cancel), Canceled("q-t1"), "does not wait for an answer"),
      ["follow-up in a replan thread while a familiarity change waits"] = (
         With(Planned(), _ => Asked("t1", new ThreadAnchor.WholeTour(), action: new TourAction.Replan(), request: RequestState.Done), tour => Decided("t2", LevelChange(tour))),
         ThreadOffer(offers => offers.FollowUp),
         FollowedUp,
         null),
      ["resend of a canceled reorder while a replan waits"] = (
         With(
            Planned(),
            _ => Asked("t1", new ThreadAnchor.WholeTour(), action: new TourAction.Reorder(["s1", "s2"]), request: RequestState.Canceled),
            _ => Asked("t2", new ThreadAnchor.WholeTour(), action: new TourAction.Replan())),
         ThreadOffer(offers => offers.Resend), Resent("q-t1"), "re-plans the tour as you asked"),
      ["follow-up in a familiarity change thread while a newer change waits"] = (
         With(Planned(), tour => Decided("t1", LevelChange(tour), request: RequestState.Done), tour => Decided("t2", LevelChange(tour))),
         ThreadOffer(offers => offers.FollowUp),
         FollowedUp,
         "writes stops at the familiarity level"),
      ["resend in a started thread"] = (With(Planned(), Started("t1", new ThreadAnchor.WholeTour(), "Look here.")), ThreadOffer(offers => offers.Resend), Resent("t1"), "has no question 't1'"),
      ["follow-up in a started thread"] = (With(Planned(), Started("t1", new ThreadAnchor.WholeTour(), "Look here.")), ThreadOffer(offers => offers.FollowUp), FollowedUp, null),
   };

   // Each row names the bound session's ID, or null for a tour that no session holds, and whether a send holds an answer.
   private static readonly Dictionary<string, (Tour Tour, string? BoundSessionId, bool Sends)> AnswerSends = new()
   {
      ["no answer"] = (Asking(), "session2", false),
      ["a draft"] = (Asking(Draft()), "session2", true),
      ["a draft with no session bound"] = (Asking(Draft()), null, true),
      ["a draft to a check that the stop's content lacks"] = (Asking(Draft(checkId: "c9")), "session2", false),
      ["a draft to a pending stop"] = (Asking(Draft("s2")), "session2", false),
      ["an outdated draft"] = (Asking(Draft().OutdatedAt(1)), "session2", false),
      ["a received answer"] = (Asking(Draft().SentIn(Batch).AsReceived(Now)), "session2", false),
      ["an answer sent to the bound session"] = (Asking(Draft().SentIn(Batch)), "session1", false),
      ["an answer sent to a session that is not bound"] = (Asking(Draft().SentIn(Batch)), "session2", true),
      ["an answer sent with no session bound"] = (Asking(Draft().SentIn(Batch)), null, true),
      ["a received answer and a draft"] = (Asking(Draft().SentIn(Batch).AsReceived(Now), Draft(checkId: "c2")), "session2", true),
   };

   private static readonly Dictionary<string, (Tour Tour, RequestState? AskAgain)> AsksAgain = new()
   {
      ["no familiarity change"] = (Planned(), null),
      ["the latest change to the level was declined"] = (AtNew(RequestState.Declined), RequestState.Declined),
      ["the latest change to the level was canceled"] = (AtNew(RequestState.Canceled), RequestState.Canceled),
      ["the latest change to the level went unanswered"] = (AtNew(RequestState.Unanswered), RequestState.Unanswered),
      ["the latest change to the level was done"] = (AtNew(RequestState.Done), null),
      ["the latest change to the level waits"] = (AtNew(RequestState.Pending), null),
      ["the latest change went to another level"] = (AtNew(RequestState.Declined) with { Familiarity = Familiarity.Familiar }, null),
   };

   private static readonly Dictionary<string, (Tour Tour, string Pending)> Pendings = new()
   {
      ["nothing waits"] = (Planned(), "stops: ; plan: ; level: ; iteration: ; summary: "),
      ["an explain of a whole stop and a revise of its text"] = (
         With(Planned(), Acting(new TourAction.Explain(1), "t1", new ThreadAnchor.WholeStop("s1")), Acting(new TourAction.Revise(), "t2", new ThreadAnchor.StopText("s1", "headline", "point", 1))),
         "stops: s1=t1,t2; plan: ; level: ; iteration: ; summary: "),
      ["an answered explain"] = (With(Planned(), Asked("t1", new ThreadAnchor.WholeStop("s1"), action: new TourAction.Explain(1), request: RequestState.Done)), "stops: ; plan: ; level: ; iteration: ; summary: "),
      ["a replan and a reorder"] = (With(Planned(), Acting(new TourAction.Replan()), Acting(new TourAction.Reorder(["s1", "s2"]), "t2")), "stops: ; plan: t1,t2; level: ; iteration: ; summary: "),
      ["a familiarity change"] = (With(Planned(), Deciding(LevelChange)), "stops: ; plan: t1; level: t1; iteration: ; summary: "),
      ["a plan approval"] = (With(Planned(), Deciding(Approval)), "stops: ; plan: t1; level: t1; iteration: ; summary: "),
      ["an iteration update"] = (With(Planned(), Deciding(_ => Update)), "stops: ; plan: t1; level: ; iteration: t1; summary: "),
      ["a summarize and each follow-up"] = (
         With(
            Planned(),
            Acting(new TourAction.Summarize()),
            Acting(new TourAction.FollowUp.DraftWorkItems([]), "t2"),
            Acting(new TourAction.FollowUp.WalkOpenQuestions([]), "t3"),
            Acting(new TourAction.FollowUp.DraftComments([], []), "t4")),
         "stops: ; plan: ; level: ; iteration: ; summary: draftComments=t4,draftWorkItems=t2,summarize=t1,walkOpenQuestions=t3"),
   };

   public static TheoryData<string> OfferRows => [.. Offers.Keys];

   public static TheoryData<string> AskAgainRows => [.. AsksAgain.Keys];

   public static TheoryData<string> AnswerSendRows => [.. AnswerSends.Keys];

   public static TheoryData<string> PendingRows => [.. Pendings.Keys];

   private static ActionRequest Order => new ActionRequest.Reorder(["s2", "s1"]);

   private static ReviewerDecision Update => ReviewerDecision.IterationUpdated.For(2, ["s1"]);

   [Theory]
   [MemberData(nameof(OfferRows))]
   public void Offer_row(string row)
   {
      var (tour, select, operation, problem) = Offers[row];

      var offer = select(TourOffers.Of(tour, null));

      var refused = operation(tour);
      if (problem is null)
      {
         Assert.True(refused is null, refused?.Reason);
         Assert.IsType<Offer.Open>(offer);
         return;
      }

      Assert.NotNull(refused);
      var closed = Assert.IsType<Offer.Closed>(offer);
      Assert.Equal((refused.Code, refused.Problem), (closed.Code, closed.Reason));
      Assert.Contains(problem, closed.Reason, StringComparison.Ordinal);
   }

   /// <summary>The offer of <c>send_answers</c>, which depends on the bound session, because a send holds again a sent answer that another session never received.</summary>
   [Theory]
   [MemberData(nameof(AnswerSendRows))]
   public void Answer_send_row(string row)
   {
      var (tour, boundSessionId, sends) = AnswerSends[row];

      var offer = TourOffers.Of(tour, boundSessionId).SendAnswers;

      if (sends)
      {
         Assert.IsType<Offer.Open>(offer);
         return;
      }

      var closed = Assert.IsType<Offer.Closed>(offer);
      Assert.Equal(OperationErrorCode.NoAnswersToSend, closed.Code);
      Assert.StartsWith("this tour has no answer to send", closed.Reason, StringComparison.Ordinal);
   }

   [Theory]
   [MemberData(nameof(AskAgainRows))]
   public void Ask_again_row(string row)
   {
      var (tour, askAgain) = AsksAgain[row];

      Assert.Equal(askAgain, TourOffers.Of(tour, null).Level.AskAgain);
   }

   [Theory]
   [MemberData(nameof(PendingRows))]
   public void Pending_row(string row)
   {
      var (tour, pending) = Pendings[row];

      Assert.Equal(pending, Short(TourOffers.Of(tour, null).Pending));
   }

   [Fact]
   public void Offers_name_each_stop_of_the_plan_and_each_thread_of_the_tour()
   {
      var offers = TourOffers.Of(With(Planned(), Asked("t1", new ThreadAnchor.WholeTour()), Note("t2", new ThreadAnchor.WholeTour())), null);

      Assert.Equal(["s1", "s2"], offers.Stops.Keys.Order());
      Assert.Equal(["t1", "t2"], offers.Threads.Keys.Order());
      Assert.Equal([TourAction.DraftCommentsKind, TourAction.DraftWorkItemsKind, TourAction.WalkOpenQuestionsKind], offers.FollowUps.Keys.Order());
   }

   private static Tour Planned() => Tour(Plan(Written("s1"), Pending("s2")));

   // The planned tour with a question stop s1, whose checks are c1 and c2, and the answers.
   private static Tour Asking(params CheckAnswer[] answers) =>
      Tour(Plan(Written("s1", StopKind.Question, Content(StopKind.Question) with { Checks = [new("c1", "Fine?"), new("c2", "Tested?")] }), Pending("s2"))) with { Answers = answers };

   private static CheckAnswer Draft(string stopId = "s1", string checkId = "c1") => new(stopId, checkId, "Yes.", AnswerType.Decision, AnswerState.Draft, Now);

   private static Tour Answered(AnswerType type) => Planned() with { Answers = [new CheckAnswer("s1", "c1", "Sent.", type, AnswerState.Sent, Now, Batch)] };

   private static Tour Updatable() =>
      Planned() with { AvailableIteration = new AvailableIteration(Pinned with { Head = CommitId.Parse(new string('c', 40)), Iteration = 2 }, ["s1"], Now) };

   // The planned tour at level new, whose one familiarity change from some to new ended in the state.
   private static Tour AtNew(RequestState state) => With(Planned(), Deciding(LevelChange, state)) with { Familiarity = Familiarity.New };

   private static Tour With(Tour tour, params QuestionThread[] threads) => tour with { Threads = Threads(threads) };

   private static Tour With(Tour tour, params Func<Tour, QuestionThread>[] threads) => tour with { Threads = Threads([.. threads.Select(thread => thread(tour))]) };

   private static Func<Tour, QuestionThread> Acting(TourAction action, string id = "t1", ThreadAnchor? anchor = null) => _ => Asked(id, anchor ?? new ThreadAnchor.WholeTour(), action: action);

   private static Func<Tour, QuestionThread> Deciding(Func<Tour, ReviewerDecision> decision, RequestState request = RequestState.Pending) => tour => Decided("t1", decision(tour), request: request);

   private static Func<Tour, QuestionThread> Answer() => _ => Asked("t1", new ThreadAnchor.WholeTour(), request: RequestState.Done);

   private static ReviewerDecision LevelChange(Tour tour) => ReviewerDecision.FamiliarityChanged.For(tour, Familiarity.Some, Familiarity.New, null);

   private static ReviewerDecision Approval(Tour tour) => ReviewerDecision.PlanApproved.For(tour, ["s1", "s2"]);

   private static Func<TourOffers, Offer> FollowUp(string kind) => offers => offers.FollowUps[kind];

   private static Func<TourOffers, Offer> ThreadOffer(Func<ThreadOffers, Offer> select) => offers => select(offers.Threads["t1"]);

   // The refusal of request_tour_action: the action's rules, then the thread table's.
   private static Func<Tour, OperationError?> Requested(ActionRequest request, string text = "") => tour =>
   {
      var admitted = ActionRules.Admit(tour, request, text);
      return admitted.Value is { } action ? TourThreads.Check(tour, new ThreadCommand.Request(action, text, null, Bound)) : admitted.Error;
   };

   // The refusal of change_familiarity to another level, in the operation's order.
   private static OperationError? LevelChanged(Tour tour) =>
      FamiliarityRules.CheckPlanFirst(tour)
      ?? TourThreads.Check(tour, new ThreadCommand.Report(FamiliarityRules.Decision(tour, Familiarity.New, null), "", null, Bound, "familiarity"))
      ?? FamiliarityRules.Check(tour, Familiarity.New);

   private static OperationError? Approved(Tour tour) =>
      DecisionRules.CheckApprovable(tour) ?? TourThreads.Check(tour, new ThreadCommand.Report(Approval(tour), "", null, Bound, "familiarity"));

   private static OperationError? Updated(Tour tour) =>
      DecisionRules.CheckUpdatable(tour) ?? TourThreads.Check(tour, new ThreadCommand.Report(Update, "", null, Bound, "tourId"));

   private static Func<Tour, OperationError?> Resent(string questionId) => tour => TourThreads.Check(tour, new ThreadCommand.Resend(questionId, Bound));

   private static OperationError? FollowedUp(Tour tour) => TourThreads.Check(tour, new ThreadCommand.FollowUp("t1", "And?", null, Bound));

   private static Func<Tour, OperationError?> Canceled(string questionId) => tour => TourThreads.Check(tour, new ThreadCommand.Cancel(questionId));

   private static OperationError? SentNote(Tour tour) => TourThreads.Check(tour, new ThreadCommand.SendNotes(["t1"], Bound));

   private static string Short(PendingWork pending) =>
      $"stops: {string.Join(",", pending.Stops.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => $"{entry.Key}={string.Join(",", entry.Value)}"))}; "
      + $"plan: {string.Join(",", pending.Plan)}; level: {pending.Level}; iteration: {pending.Iteration}; "
      + $"summary: {string.Join(",", pending.Summary.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => $"{entry.Key}={entry.Value}"))}";
}
