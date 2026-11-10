namespace Lotsen.Tours;

/// <summary>The rules of the reviewer decisions, one column per member, over every kind, in the form of <see cref="ActionRules"/>.</summary>
internal static class DecisionRules
{
   // The last sentences of every decision's rule, served to Claude.
   private const string ResolveRule = "Resolve the decision's question with one short reply. A declined reply leaves the decision made: the tour keeps the new level, the approval or the new pin.";

   private static readonly FieldPath DecisionField = "decision";

   /// <summary>Returns why a decided thread's opening question cannot go to the tour again, or null when it can.</summary>
   /// <remarks>
   /// A re-send stores nothing, so it fits only while the tour still holds what the decision reports. A familiarity change never goes again, because the reviewer asks again with
   /// <c>change_familiarity</c> at the same level, which picks the stops to rewrite from the stop under view then. A plan approval goes again until Claude writes a stop, because a re-send after
   /// that could undo a later change of the plan, and only while the tour holds the approval's level, skips and stops. An iteration update goes again while the tour stays on its iteration and one
   /// of its stops still has its stale marker, because Claude revises each stale stop against that pin.
   /// </remarks>
   public static OperationError? CheckResend(Tour tour, QuestionThread.Sent.Decided thread) => thread.Decision switch
   {
      ReviewerDecision.FamiliarityChanged => OperationError.Invalid(
         DecisionField.Then("kind"),
         "a familiarity change does not go again. To ask Claude again, open Re-plan… in the Plan panel's ⋯ menu, or Change familiarity… while the tour has no plan, and send it at this level. Claude then rewrites the stops after the one you view."),
      ReviewerDecision.PlanApproved approval => CheckApprovalAgain(tour, approval),
      ReviewerDecision.IterationUpdated update => CheckUpdateAgain(tour, update),
   };

   /// <summary>Returns the group of which at most one thread waits at a time. Every decision waits alone.</summary>
   /// <remarks>
   /// A familiarity change and a plan approval share a group, because Claude writes stops at the level that each sets. A thread's last question decides whether it waits, so a follow-up in it
   /// counts, because Claude can do the work again for it.
   /// </remarks>
   public static WaitGroup WaitsAlone(ReviewerDecision decision) => decision switch
   {
      ReviewerDecision.FamiliarityChanged => WaitGroup.Level,
      ReviewerDecision.PlanApproved => WaitGroup.Level,
      ReviewerDecision.IterationUpdated => WaitGroup.Iteration,
   };

   /// <summary>Returns what the decision asks of Claude and how to carry it out, as <c>fetch_question</c> serves it for each question of the thread.</summary>
   /// <remarks>A rule stands alone, as <see cref="ActionRules.HowToAct"/> says.</remarks>
   public static string HowToAct(ReviewerDecision decision) => decision switch
   {
      ReviewerDecision.FamiliarityChanged => $"The decision \"familiarityChanged\" says that the reviewer changed the tour's familiarity level: rewrite the stops that the reviewer has not read yet at the new level. Its from and to name the earlier and the new level, and its stopIds name the stops to rewrite: those after the stop the reviewer views, or every stop, that have content and no skip. The reviewer can decide again at the tour's current level after you declined the last change or the last follow-up in its thread, or after it was canceled or unanswered. That decision keeps the earlier from, and its stopIds start after the stop the reviewer views now. Act on it the same way. The reviewer's words, when there are any, say what else to change. Act on familiarityChanged in this order. Read the new level with read_state, and write at it. {ActionRules.DepthRule} A context stop is background that the reviewer needs before the changed code: few or no context stops at familiar, only for code that the change depends on but does not show at some, and before the changed code, on the surrounding code, its conventions and its vocabulary, at new. Where the new level asks for more or fewer context stops, add or drop them with set_plan, give every other stop the id it has, and keep each skipped stop. Then revise with put_stop each stop of stopIds that the plan still has, so each gets a new version, and reply. Apply the reviewer's words too, with set_plan where they differ from the plan, and say in the reply where you did not follow them and why. Write the stops that have no content yet at the new level after the reply. Leave the stops before the one the reviewer views as they are: the reviewer has read them, and can ask for a revision of one. {ResolveRule}",
      ReviewerDecision.PlanApproved => $"The decision \"planApproved\" says that the reviewer approved the plan of a plan-first tour: write its stops. Its stopIds hold the reviewer's proposed order of the plan's stops, its skippedStops the stops that the reviewer skips, and its familiarity the level to write at, which the tour holds now. The reviewer's words, when there are any, say what to change or keep in mind. Act on planApproved in this order. Where the reviewer's order or words differ from the plan, apply them with set_plan: give every stop the id it has, and keep each skipped stop. Then write the stops with put_stop at the decision's familiarity, in plan order, and reply once they are written. {ActionRules.DepthRule} {ActionRules.ProposedOrderRule} Say in the reply where your plan differs from the reviewer's order or words and why. read_state marks each stop that the reviewer skipped with skipped true: a skip is the reviewer's reading state, and no event tells you of it. {ResolveRule}",
      ReviewerDecision.IterationUpdated => $"The decision \"iterationUpdated\" says that the reviewer updated the tour to a new iteration of the pull request, which its iteration names: revise the stops that the update made stale. Its stopIds name the stale stops: those with a passage or link that lotsen could not move. In read_state, each of them has staleOn, and all of its content, every passage and link included, still points at the iteration before the one its staleOn names. Act on iterationUpdated with put_stop on each stop of stopIds against the tour's new pin. Read the pin with read_state, and read the code again at its commits, the head side at pin.head and the base side at pin.mergeBase, because the code that the stop points at changed or moved. Revise each stale stop so that its passages and links point at the new pin, which clears its staleOn, and reply once each is written. A file can move to a path that put_stop refuses, such as one with a backslash, so point such a passage at the file's base side, or leave it out. Leave the other stops as they are: their passages and links moved with the update. {ActionRules.DepthRule} {ResolveRule}",
   };

   /// <summary>Returns why the tour's plan takes no approval now, from the tour's state alone, or null when the plan waits for one.</summary>
   public static OperationError? CheckApprovable(Tour tour) => tour.PlanWaits
      ? null
      : OperationError.Invalid("tourId", tour.PlanApproval switch
      {
         PlanApproval.NotNeeded => "this tour was not started plan first, so its plan does not wait for an approval.",
         PlanApproval.Given => "the plan is approved already. Ask Claude for a change with Re-plan… or Reorder… in the Plan panel's ⋯ menu.",
         _ => "this tour has no plan yet, so there is nothing to approve. Wait until Claude sets the plan.",
      });

   /// <summary>Returns why the tour takes no iteration update now, or null when it takes one.</summary>
   /// <remarks>
   /// The thread table refuses the update's report while an earlier one waits. This check refuses an update that would leave no stop stale too, and before git runs, so it checks the waiting
   /// rule itself.
   /// </remarks>
   public static OperationError? CheckUpdatable(Tour tour)
   {
      if (tour.AvailableIteration is null)
      {
         return OperationError.NoAvailableIteration();
      }

      return TourThreads.CheckWaits(tour, WaitGroup.Iteration, null, "tourId");
   }

   private static OperationError? CheckApprovalAgain(Tour tour, ReviewerDecision.PlanApproved approval)
   {
      if (tour.Plan?.Stops.Any(stop => stop is Stop.Written) == true)
      {
         return OperationError.Invalid(
            DecisionField.Then("kind"),
            "the approval does not go again, because Claude has written stops of the approved plan. Follow up in the approval's thread, or request a tour action from the Plan panel.");
      }

      if (tour.Familiarity != approval.Familiarity)
      {
         return OperationError.Invalid(DecisionField.Then("familiarity"), "the approval does not go again, because the tour's familiarity level changed since. Follow up in the approval's thread instead.");
      }

      if (!(tour.Plan?.SkippedIds ?? []).ToHashSet(StringComparer.Ordinal).SetEquals(approval.SkippedStops))
      {
         return OperationError.Invalid(DecisionField.Then("skippedStops"), "the approval does not go again, because the skipped stops changed since. Follow up in the approval's thread instead.");
      }

      return PlanRules.CheckOrder(tour.Plan, approval.StopIds) is null
         ? null
         : OperationError.Invalid(DecisionField.Then("stopIds"), "the approval does not go again, because Claude changed the plan's stops since. Follow up in the approval's thread instead.");
   }

   private static OperationError? CheckUpdateAgain(Tour tour, ReviewerDecision.IterationUpdated update)
   {
      if (tour.Pin is not TourPin.Pinned { Iteration: var pinned } || pinned != update.Iteration)
      {
         return OperationError.Invalid(
            DecisionField.Then("iteration"),
            $"the move to update {update.Iteration} does not go again, because the tour moved to another update since. Follow up in the thread of the latest move instead.");
      }

      return update.StopIds.Any(id => tour.FindStop(id) is Stop.Written { StaleOn: not null })
         ? null
         : OperationError.Invalid(
            DecisionField.Then("stopIds"),
            "the move does not go again, because its stops are no longer stale: Claude revised each of them, or the plan dropped them. Follow up in the move's thread instead.");
   }
}
