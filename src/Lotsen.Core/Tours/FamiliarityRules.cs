namespace Lotsen.Tours;

/// <summary>The rules of a change of a tour's familiarity level with <c>change_familiarity</c>.</summary>
internal static class FamiliarityRules
{
   /// <summary>Returns why a plan-first tour whose plan is not approved takes no change of its level, or null when the tour takes one.</summary>
   /// <remarks>A plan-first tour takes its level with the plan approval, so a change before it would leave a familiarity change beside the plan approval.</remarks>
   public static OperationError? CheckPlanFirst(Tour tour)
   {
      if (tour.PlanApproval != PlanApproval.Awaited)
      {
         return null;
      }

      var pick = tour.Plan is null
         ? "Claude has not set the plan yet. Pick the level in the plan proposal once Claude sets the plan, then approve the plan."
         : "Pick the level in the plan proposal, then approve the plan.";
      return OperationError.Invalid("tourId", $"this tour was started plan first, so its familiarity level goes to Claude with your approval of the plan. {pick}");
   }

   /// <summary>Returns why the tour takes no change to the level, or null when it takes one.</summary>
   /// <remarks>
   /// The tour's current level is refused unless the change asks Claude again for the latest change's rewrite. The thread table refuses a change while another familiarity change or the plan
   /// approval waits, because both are in <see cref="WaitGroup.Level"/>. Neither a cancel nor a drop reaches the session, so Claude may still carry out a canceled or unanswered change.
   /// </remarks>
   public static OperationError? Check(Tour tour, Familiarity level) =>
      tour.Familiarity == level && AskedAgain(tour, level) is null ? OperationError.Invalid("familiarity", "the tour is at this familiarity level already. Pick another level.") : null;

   /// <summary>Returns the state of the last question in the thread of the tour's latest familiarity change when a change to the tour's current level asks Claude again for it.</summary>
   /// <returns>Declined, canceled or unanswered, as <see cref="AskedAgain"/> decides, or null when a change to the current level asks nothing again.</returns>
   public static RequestState? AskAgain(Tour tour) => AskedAgain(tour, tour.Familiarity) is not null ? LastState(tour) : null;

   /// <summary>Returns the familiarity change that a change to <paramref name="level"/> reports.</summary>
   public static ReviewerDecision.FamiliarityChanged Decision(Tour tour, Familiarity level, string? stopId) =>
      ReviewerDecision.FamiliarityChanged.For(tour, AskedAgain(tour, level)?.From ?? tour.Familiarity, level, stopId);

   /// <summary>Returns the tour's latest familiarity change when a change to <paramref name="level"/> asks Claude again for that change's rewrite, or null when it is a new change.</summary>
   /// <remarks>
   /// The reviewer asks again at the tour's current level, the latest change's level, when no reply carried that change out: Claude declined the last question of its thread, or the reviewer
   /// canceled it, or it went unanswered. A follow-up in the thread is a question about the change, so the thread's last question decides, as it does for the waiting rule. The new decision keeps
   /// the latest change's earlier level and leaves the tour's level as it is.
   /// </remarks>
   private static ReviewerDecision.FamiliarityChanged? AskedAgain(Tour tour, Familiarity level)
   {
      var thread = tour.Threads.OfType<QuestionThread.Sent.Decided>().LastOrDefault(candidate => candidate.Decision is ReviewerDecision.FamiliarityChanged);
      if (thread is not { Decision: ReviewerDecision.FamiliarityChanged change, LastQuestion: { } last } || change.To != level || tour.Familiarity != level)
      {
         return null;
      }

      return StateOf(last) is RequestState.Declined or RequestState.Canceled or RequestState.Unanswered ? change : null;
   }

   private static RequestState? LastState(Tour tour) =>
      tour.Threads.OfType<QuestionThread.Sent.Decided>().LastOrDefault(candidate => candidate.Decision is ReviewerDecision.FamiliarityChanged)?.LastQuestion is { } last ? StateOf(last) : null;

   // A reply's resolution wins over the state of the last request, which a reply need not have resolved.
   private static RequestState StateOf(ThreadMessage.Question question) => question.Answered?.State ?? question.Requests[^1].State;
}
