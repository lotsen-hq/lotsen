using System.Text.Json;
using Lotsen.Git;
using Lotsen.Json;
using Lotsen.Operations;
using Lotsen.Sessions;
using Lotsen.Tours;
using static Lotsen.Tests.ThreadFixtures;
using static Lotsen.Tests.TourValues;

namespace Lotsen.Tests;

/// <summary>Covers <see cref="TourControls.Of"/>: the tour's offers with the binding refusal of each control's operation, at the tour's revision.</summary>
/// <remarks>
/// The open tour leaves every control open by its state: it has the written stop <c>s1</c>, a sent answer of each type, an available iteration, the note <c>t1</c>, the answered thread <c>t2</c>,
/// the canceled thread <c>t3</c> and the waiting thread <c>t4</c>. The approving tour is that tour started plan first, with its plan waiting.
/// </remarks>
[Trait("Category", "TourActions")]
public sealed class TourControlsTests
{
   private static readonly AnswerBatch Batch = new("b1", "session1", Now);

   private static readonly Dictionary<string, (Func<Tour> Tour, Func<TourOffers, Offer> Control, string Operation)> Bound = new()
   {
      ["ask"] = (Open, offers => offers.Ask, "ask_question"),
      ["send answers"] = (Open, offers => offers.SendAnswers, "send_answers"),
      ["level change"] = (Open, offers => offers.Level.Change, "change_familiarity"),
      ["plan approval"] = (Approving, offers => offers.ApprovePlan, "approve_plan"),
      ["iteration update"] = (Open, offers => offers.UpdateIteration, "update_iteration"),
      ["replan"] = (Open, offers => offers.Replan, "request_tour_action"),
      ["reorder"] = (Open, offers => offers.Reorder, "request_tour_action"),
      ["explain"] = (Open, offers => offers.Stops["s1"].Explain, "request_tour_action"),
      ["revise"] = (Open, offers => offers.Stops["s1"].Revise, "request_tour_action"),
      ["summarize"] = (Open, offers => offers.Summarize, "request_tour_action"),
      ["draftWorkItems"] = (Open, offers => offers.FollowUps[TourAction.DraftWorkItemsKind], "request_tour_action"),
      ["walkOpenQuestions"] = (Open, offers => offers.FollowUps[TourAction.WalkOpenQuestionsKind], "request_tour_action"),
      ["draftComments"] = (Open, offers => offers.FollowUps[TourAction.DraftCommentsKind], "request_tour_action"),
      ["resend"] = (Open, offers => offers.Threads["t3"].Resend, "resend_question"),
      ["follow-up"] = (Open, offers => offers.Threads["t2"].FollowUp, "ask_follow_up"),
      ["send note"] = (Open, offers => offers.Threads["t1"].SendNote, "send_notes"),
   };

   private static readonly Recipient Holder = new AgentSessions().TryOpen()!.Recipient;

   public static TheoryData<string> BoundRows => [.. Bound.Keys];

   /// <summary>Each row's control is open by the tour's state, so the missing session alone closes it.</summary>
   [Theory]
   [MemberData(nameof(BoundRows))]
   public void Control_of_an_operation_to_the_bound_session_is_closed_without_a_holder(string row)
   {
      var (tour, control, operation) = Bound[row];
      var built = tour();

      var offer = control(TourControls.Of(built, holder: null).Offers);

      Assert.IsType<Offer.Open>(control(TourOffers.Of(built, null)));
      var closed = Assert.IsType<Offer.Closed>(offer);
      var refusal = OperationCatalog.Get(operation).Access.RefusesReviewer(built.Id, null);
      Assert.NotNull(refusal);
      Assert.Equal((OperationErrorCode.NoSessionAttached, refusal.Problem), (closed.Code, closed.Reason));
      Assert.StartsWith("this tour is attached to no Claude Code session", closed.Reason, StringComparison.Ordinal);
      Assert.Contains($"to attach tour {built.Id}, then try again.", closed.Reason, StringComparison.Ordinal);
   }

   [Fact]
   public void Cancel_stays_open_without_a_holder()
   {
      var offers = TourControls.Of(Open(), holder: null).Offers;

      Assert.IsType<Offer.Open>(offers.Threads["t4"].Cancel);
   }

   [Fact]
   public void Control_that_the_state_closes_keeps_the_states_refusal_without_a_holder()
   {
      var tour = Tour();

      var offers = TourControls.Of(tour, holder: null).Offers;

      Assert.Equal(TourOffers.Of(tour, null).Replan, offers.Replan);
      Assert.Contains("no plan to replan", Assert.IsType<Offer.Closed>(offers.Replan).Reason, StringComparison.Ordinal);
   }

   [Fact]
   public void Send_answers_is_closed_by_the_state_when_every_answer_went_to_the_holder()
   {
      var tour = Open() with { Answers = [.. Open().Answers.Select(answer => answer.SentIn(Batch with { SessionId = Holder.Id }))] };

      var closed = Assert.IsType<Offer.Closed>(TourControls.Of(tour, Holder).Offers.SendAnswers);

      Assert.Equal(OperationErrorCode.NoAnswersToSend, closed.Code);
      Assert.Equal(OperationErrorCode.NoAnswersToSend, Assert.IsType<Offer.Closed>(TourControls.Of(Tour(Plan(Written("s1"))), holder: null).Offers.SendAnswers).Code);
   }

   [Fact]
   public void Controls_with_a_holder_are_the_tours_offers_at_its_revision()
   {
      var tour = Open() with { Revision = 7 };

      var controls = TourControls.Of(tour, Holder);

      Assert.Equal((tour.Id, 7L), (controls.TourId, controls.Revision));
      Assert.Equal(Json(TourOffers.Of(tour, Holder.Id)), Json(controls.Offers));
   }

   private static Tour Open() => Tour(Plan(Written("s1", StopKind.Question, Content(StopKind.Question) with { Checks = [new("c1", "Fine?"), new("c2", "Tested?"), new("c3", "Safe?")] }))) with
   {
      Answers =
      [
         new CheckAnswer("s1", "c1", "Decided.", AnswerType.Decision, AnswerState.Sent, Now, Batch),
         new CheckAnswer("s1", "c2", "Open.", AnswerType.OpenQuestion, AnswerState.Sent, Now, Batch),
         new CheckAnswer("s1", "c3", "Worried.", AnswerType.Concern, AnswerState.Sent, Now, Batch),
      ],
      AvailableIteration = new AvailableIteration(Pinned with { Head = CommitId.Parse(new string('c', 40)), Iteration = 2 }, ["s1"], Now),
      Threads = Threads(
         Note("t1", new ThreadAnchor.WholeTour()),
         Asked("t2", new ThreadAnchor.WholeTour(), request: RequestState.Done),
         Asked("t3", new ThreadAnchor.WholeTour(), request: RequestState.Canceled),
         Asked("t4", new ThreadAnchor.WholeTour())),
   };

   private static Tour Approving() => Open() with { PlanApproval = PlanApproval.Awaited };

   private static string Json(TourOffers offers) => JsonSerializer.Serialize(offers, LotsenJsonContext.Default.TourOffers);
}
