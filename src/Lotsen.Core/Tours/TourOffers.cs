using System.Text.Json.Serialization;
using Lotsen.Sessions;

namespace Lotsen.Tours;

/// <summary>Whether a control of the UI can act now: open, or closed with the refusal that its operation gives. The <c>state</c> property in JSON names the derived type.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "state")]
[JsonDerivedType(typeof(Open), "open")]
[JsonDerivedType(typeof(Closed), "closed")]
internal closed record Offer
{
   private Offer()
   {
   }

   /// <summary>The offer of a control that its operation would take now.</summary>
   public static Offer Opened { get; } = new Open();

   /// <summary>Returns the open offer, or the closed offer of the refusal.</summary>
   public static Offer Of(OperationError? refusal) => refusal is null ? Opened : new Closed(refusal.Code, refusal.Problem);

   /// <summary>A control that its operation would take now.</summary>
   internal sealed record Open : Offer;

   /// <summary>A control that its operation would refuse now.</summary>
   /// <param name="Code">The refusal's code.</param>
   /// <param name="Reason">The refusal's problem text, without the field it names, which the UI shows beside the disabled control.</param>
   internal sealed record Closed(OperationErrorCode Code, string Reason) : Offer;
}

/// <summary>
/// Whether each control of a tour can act now, from the tour's state and its bound session's ID, and the waiting threads by what each holds back. Only <see cref="Of"/> builds one, from the
/// rules that refuse the operations, so a closed offer carries the refusal that the tour's state gives its operation.
/// </summary>
/// <remarks>Each offer leaves out the binding check, which <c>TourControls</c> adds, and the input's own checks, such as the words of a replan.</remarks>
/// <param name="Ask">Whether the reviewer can ask a question.</param>
/// <param name="SendAnswers">Whether the reviewer can send the answers that the next send holds, which <see cref="CheckAnswer.ToSend"/> gives.</param>
/// <param name="Level">Whether the reviewer can change the familiarity level, and whether the tour's current level asks Claude again.</param>
/// <param name="ApprovePlan">Whether the reviewer can approve the plan.</param>
/// <param name="UpdateIteration">Whether the reviewer can update the tour to its available iteration.</param>
/// <param name="Replan">Whether the reviewer can request a replan.</param>
/// <param name="Reorder">Whether the reviewer can propose an order of the plan's stops.</param>
/// <param name="Stops">The tour actions on each stop of the plan, by stop ID.</param>
/// <param name="Summarize">Whether the reviewer can ask Claude to summarize the review.</param>
/// <param name="FollowUps">Whether the reviewer can request each follow-up action, by its kind: draftWorkItems, walkOpenQuestions and draftComments.</param>
/// <param name="Threads">What the reviewer can do in each thread, by thread ID.</param>
/// <param name="Pending">The waiting threads, by what each holds back.</param>
internal sealed record TourOffers(
   Offer Ask,
   Offer SendAnswers,
   LevelOffer Level,
   Offer ApprovePlan,
   Offer UpdateIteration,
   Offer Replan,
   Offer Reorder,
   IReadOnlyDictionary<string, StopOffers> Stops,
   Offer Summarize,
   IReadOnlyDictionary<string, Offer> FollowUps,
   IReadOnlyDictionary<string, ThreadOffers> Threads,
   PendingWork Pending)
{
   private static readonly ActionRequest.FollowUp[] FollowUpRequests =
      [new ActionRequest.FollowUp.DraftWorkItems(), new ActionRequest.FollowUp.WalkOpenQuestions(), new ActionRequest.FollowUp.DraftComments()];

   // The recipient of the probe commands, a session of a registry that nothing serves. A probe only checks, so nothing is ever sent to it.
   private static readonly Recipient Probe = new AgentSessions().TryOpen()!.Recipient;

   /// <summary>Returns the offers of the tour.</summary>
   /// <param name="tour">The tour.</param>
   /// <param name="boundSessionId">The bound session's ID, or null when no session holds the tour, which decides the sent answers that a send holds again.</param>
   /// <remarks>
   /// The tour actions run <see cref="ActionRules.Admit"/> on a probe request with valid words, then the thread table's <see cref="TourThreads.Check"/> on its command. The thread controls run
   /// the table on a probe command, whose words the table does not check. The reviewer decisions run the state checks of their operations and the table's <see cref="TourThreads.CheckWaits"/>.
   /// </remarks>
   public static TourOffers Of(Tour tour, string? boundSessionId)
   {
      var stops = tour.Plan?.Stops ?? [];
      return new TourOffers(
         Offer.Opened,
         Offer.Of(CheckAnswer.ToSend(tour, boundSessionId).Error),
         new LevelOffer(Offer.Of(FamiliarityRules.CheckPlanFirst(tour) ?? TourThreads.CheckWaits(tour, WaitGroup.Level, null, "familiarity")), FamiliarityRules.AskAgain(tour)),
         Offer.Of(DecisionRules.CheckApprovable(tour) ?? TourThreads.CheckWaits(tour, WaitGroup.Level, null, "familiarity")),
         Offer.Of(DecisionRules.CheckUpdatable(tour)),
         Action(tour, new ActionRequest.Replan(), "?"),
         Action(tour, new ActionRequest.Reorder([.. stops.Select(stop => stop.Id)])),
         stops.ToDictionary(
            stop => stop.Id,
            stop => new StopOffers(Action(tour, new ActionRequest.Explain(new StopTarget.WholeStop(stop.Id))), Action(tour, new ActionRequest.Revise(new StopTarget.WholeStop(stop.Id)), "?")),
            StringComparer.Ordinal),
         Action(tour, new ActionRequest.Summarize()),
         FollowUpRequests.ToDictionary(KindOf, request => Action(tour, request), StringComparer.Ordinal),
         tour.Threads.ToDictionary(thread => thread.Id, thread => ThreadOffersOf(tour, thread), StringComparer.Ordinal),
         PendingWork.Of(tour));
   }

   private static Offer Action(Tour tour, ActionRequest request, string text = "")
   {
      var admitted = ActionRules.Admit(tour, request, text);
      return Offer.Of(admitted.Value is { } action ? TourThreads.Check(tour, new ThreadCommand.Request(action, text, null, Probe)) : admitted.Error);
   }

   private static string KindOf(ActionRequest.FollowUp request) => request switch
   {
      ActionRequest.FollowUp.DraftWorkItems => TourAction.DraftWorkItemsKind,
      ActionRequest.FollowUp.WalkOpenQuestions => TourAction.WalkOpenQuestionsKind,
      ActionRequest.FollowUp.DraftComments => TourAction.DraftCommentsKind,
   };

   private static ThreadOffers ThreadOffersOf(Tour tour, QuestionThread thread)
   {
      // A note and a started thread without a follow-up have no question, so the table refuses a probe of the thread's ID as a question that the tour lacks.
      var questionId = (thread as QuestionThread.Sent)?.LastQuestion?.Id ?? thread.Id;
      return new ThreadOffers(
         Offer.Of(TourThreads.Check(tour, new ThreadCommand.Resend(questionId, Probe))),
         Offer.Of(TourThreads.Check(tour, new ThreadCommand.FollowUp(thread.Id, "", null, Probe))),
         Offer.Of(TourThreads.Check(tour, new ThreadCommand.Cancel(questionId))),
         Offer.Of(TourThreads.Check(tour, new ThreadCommand.SendNotes([thread.Id], Probe))));
   }
}

/// <summary>What the reviewer can do in one thread.</summary>
/// <param name="Resend">Whether the reviewer can send the thread's last question again.</param>
/// <param name="FollowUp">Whether the reviewer can follow up in the thread.</param>
/// <param name="Cancel">Whether the reviewer can cancel the pending request of the thread's last question.</param>
/// <param name="SendNote">Whether the reviewer can send the thread, a note, to the bound session.</param>
internal sealed record ThreadOffers(Offer Resend, Offer FollowUp, Offer Cancel, Offer SendNote);

/// <summary>Whether the reviewer can change the tour's familiarity level.</summary>
/// <param name="Change">Whether a change of the level goes to the tour now. A change to the tour's current level also needs <paramref name="AskAgain"/>.</param>
/// <param name="AskAgain">
/// The state of the last question in the thread of the latest familiarity change, declined, canceled or unanswered, when a change to the tour's current level asks Claude again for that change,
/// or null when it does not. JSON leaves it out when null.
/// </param>
internal sealed record LevelOffer(Offer Change, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RequestState? AskAgain);

/// <summary>The tour actions on one stop.</summary>
/// <param name="Explain">Whether the reviewer can ask Claude to explain the stop, or text in it, more deeply.</param>
/// <param name="Revise">Whether the reviewer can ask Claude to revise the stop.</param>
internal sealed record StopOffers(Offer Explain, Offer Revise);

/// <summary>The threads whose last question waits for Claude, by what each holds back, so the UI shows the work in progress where it applies.</summary>
/// <param name="Stops">The threads of the explain and revise tour actions on each stop, as a whole or text in it, by stop ID. A stop without one has no entry.</param>
/// <param name="Plan">The threads that hold back a replan and a reorder: the replans, the reorders and the reviewer decisions.</param>
/// <param name="Level">The thread of the familiarity change or the plan approval, or null when neither waits. JSON leaves it out when null.</param>
/// <param name="Iteration">The thread of the iteration update, or null when none waits. JSON leaves it out when null.</param>
/// <param name="Summary">The thread of each tour action of the summary page, by its kind: summarize and the follow-up actions. A kind without one has no entry.</param>
internal sealed record PendingWork(
   IReadOnlyDictionary<string, IReadOnlyList<string>> Stops,
   IReadOnlyList<string> Plan,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Level,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Iteration,
   IReadOnlyDictionary<string, string> Summary)
{
   internal static PendingWork Of(Tour tour)
   {
      var waiting = tour.Threads.OfType<QuestionThread.Sent>().Where(thread => thread.Waiting is not null).ToList();
      var onStops = waiting
         .Where(thread => thread is QuestionThread.Sent.Asked { Action: TourAction.Explain or TourAction.Revise })
         .Select(thread => (StopId: thread.Anchor switch { ThreadAnchor.WholeStop stop => stop.StopId, ThreadAnchor.StopText text => text.StopId, _ => null }, thread.Id))
         .Where(entry => entry.StopId is not null)
         .GroupBy(entry => entry.StopId!, StringComparer.Ordinal)
         .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)[.. group.Select(entry => entry.Id)], StringComparer.Ordinal);
      return new PendingWork(
         onStops,
         [.. waiting.Where(thread => TourThreads.WaitGroupOf(thread) is { } group && group.HoldsBack(WaitGroup.Plan)).Select(thread => thread.Id)],
         waiting.FirstOrDefault(thread => TourThreads.WaitGroupOf(thread) == WaitGroup.Level)?.Id,
         waiting.FirstOrDefault(thread => TourThreads.WaitGroupOf(thread) == WaitGroup.Iteration)?.Id,
         waiting
            .Where(thread => thread is QuestionThread.Sent.Asked { Action: { } action } && ActionRules.HandsOverReview(action))
            .GroupBy(thread => ((QuestionThread.Sent.Asked)thread).Action!.Kind, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Id, StringComparer.Ordinal));
   }
}
