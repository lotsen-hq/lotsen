using System.ComponentModel;
using System.Text.Json.Serialization;
using Lotsen.Git;
using Lotsen.Pinning;
using Lotsen.Sessions;
using Lotsen.Tours;

namespace Lotsen.Operations;

/// <summary>The input of <c>change_familiarity</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ChangeFamiliarityInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId,
   [property: Description("The tour's new familiarity level: familiar, some or new. The tour's current level asks Claude again for the rewrite when the last question in the thread of the latest change to it, the change or a follow-up, was declined, canceled or unanswered.")]
   Familiarity Familiarity,
   [property: Description("The ID of the stop that the reviewer views. Claude rewrites the stops after it. Omit it when no stop is open, and Claude rewrites every stop.")]
   string? StopId = null,
   [property: Description("The reviewer's words for Claude about the change as plain text, with no control characters but tabs and line breaks. Omit them, or leave them empty, for a change without words.")]
   string? Text = null);

/// <summary>A tour's familiarity level as one write stored it, with the tour's update time after that write, so the UI orders it against the events and loads in flight.</summary>
/// <param name="TourId">The tour.</param>
/// <param name="UpdatedAt">The tour's update time after the write.</param>
/// <param name="Familiarity">The tour's familiarity level after the write.</param>
internal sealed record UpdatedFamiliarity(TourId TourId, DateTimeOffset UpdatedAt, Familiarity Familiarity);

/// <summary>The output of <c>change_familiarity</c>: the tour's new level and the decided thread of the familiarity change that the same write stored.</summary>
/// <param name="TourId">The tour.</param>
/// <param name="UpdatedAt">The tour's update time after the write.</param>
/// <param name="Familiarity">The tour's familiarity level after the write.</param>
/// <param name="Thread">The new decided thread on the tour, which holds the <see cref="ReviewerDecision.FamiliarityChanged"/> decision.</param>
internal sealed record ChangedFamiliarity(TourId TourId, DateTimeOffset UpdatedAt, Familiarity Familiarity, QuestionThread Thread);

/// <summary>The input of <c>approve_plan</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ApprovePlanInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId,
   [property: Description("The IDs of every stop of the plan, each once, in the order that the reviewer proposes.")]
   IReadOnlyList<string> StopIds,
   [property: Description("The IDs of the plan's stops that the reviewer skips, in any order, or an empty list for none. They become the tour's skipped stops.")]
   IReadOnlyList<string> SkippedStops,
   [property: Description("The familiarity level for Claude to write the stops at: familiar, some or new. It becomes the tour's level.")]
   Familiarity Familiarity,
   [property: Description("The reviewer's words for Claude about the plan as plain text, with no control characters but tabs and line breaks. Omit them, or leave them empty, for an approval without words.")]
   string? Text = null);

/// <summary>A tour's plan approval as one write stored it, with the tour's update time after that write, so the UI orders it against the events and loads in flight.</summary>
/// <param name="TourId">The tour.</param>
/// <param name="UpdatedAt">The tour's update time after the write.</param>
/// <param name="PlanApproval">The tour's plan approval after the write.</param>
internal sealed record UpdatedPlanApproval(TourId TourId, DateTimeOffset UpdatedAt, PlanApproval PlanApproval);

/// <summary>The output of <c>approve_plan</c>: what the approval stored on the tour, and the decided thread of the plan approval that the same write stored.</summary>
/// <param name="TourId">The tour.</param>
/// <param name="UpdatedAt">The tour's update time after the write.</param>
/// <param name="PlanApproval">The tour's plan approval after the write, which is given.</param>
/// <param name="Familiarity">The tour's familiarity level after the write.</param>
/// <param name="SkippedStops">The tour's skipped stops after the write, in plan order.</param>
/// <param name="Thread">The new decided thread on the tour, which holds the <see cref="ReviewerDecision.PlanApproved"/> decision.</param>
internal sealed record ApprovedPlan(TourId TourId, DateTimeOffset UpdatedAt, PlanApproval PlanApproval, Familiarity Familiarity, IReadOnlyList<string> SkippedStops, QuestionThread Thread);

/// <summary>The input of <c>update_iteration</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record UpdateIterationInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId,
   [property: Description("The pin of the available iteration that the reviewer's notice showed. The update is refused when the tour's available iteration is another one.")]
   PinInput Pin);

/// <summary>The pinned commits of an iteration as an operation's input names them, which <see cref="ToPin"/> turns into the stored form.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PinInput(
   [property: Description("The iteration's merge base, as a full commit ID.")]
   CommitId MergeBase,
   [property: Description("The iteration's head, as a full commit ID.")]
   CommitId Head,
   [property: Description("The pull request's number of the iteration, from 1.")]
   int Iteration,
   [property: Description("The URL of the repository that the commits come from, absolute.")]
   Uri RemoteUrl)
{
   /// <summary>Returns the pin as a tour stores it.</summary>
   public TourPin.Pinned ToPin() => new(MergeBase, Head, Iteration, RemoteUrl);
}

/// <summary>The output of <c>update_iteration</c>: the tour's new pin and stale stops, and the decided thread of the iteration update that the same write stored.</summary>
/// <param name="TourId">The tour.</param>
/// <param name="UpdatedAt">The tour's update time after the write.</param>
/// <param name="Pin">The tour's pin after the write, on the new iteration.</param>
/// <param name="StaleStops">The IDs of the stops with a stale marker after the write, in plan order.</param>
/// <param name="Thread">
/// The new decided thread on the tour, which holds the <see cref="ReviewerDecision.IterationUpdated"/> decision, or null when no stop is stale, so no decision went. JSON leaves it out when null.
/// </param>
internal sealed record UpdatedIteration(
   TourId TourId,
   DateTimeOffset UpdatedAt,
   TourPin Pin,
   IReadOnlyList<string> StaleStops,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] QuestionThread? Thread = null);

/// <summary>
/// The reviewer's decisions that only Claude acts on, so each needs a bound session and stores a decided thread that reports the decision in the same write: <c>change_familiarity</c>,
/// <c>approve_plan</c> and <c>update_iteration</c>.
/// </summary>
internal sealed class ReviewerDecisions
{
   private readonly TourWriter _writer;
   private readonly IterationScheduler _scheduler;
   private readonly IterationLifecycle _lifecycle;

   public ReviewerDecisions(TourWriter writer, IterationScheduler scheduler, IterationLifecycle lifecycle)
   {
      _writer = writer;
      _scheduler = scheduler;
      _lifecycle = lifecycle;
   }

   /// <summary>
   /// Stores the UI's new familiarity level on the tour together with a decided thread that reports the familiarity change to the tour's bound session, with the reviewer's optional words as the
   /// thread's question. Sends the session the question, and returns both.
   /// </summary>
   /// <remarks>
   /// One write stores the level and the thread, so neither lands without the other. A tour with no bound session is refused, and so is a plan-first tour whose plan is not approved, a change
   /// while another thread of <see cref="WaitGroup.Level"/> waits, and each change that <see cref="FamiliarityRules.Check"/> refuses.
   /// </remarks>
   public OperationResult<ChangedFamiliarity> ChangeFamiliarity(Access.ReviewerToSession.Pass pass, ChangeFamiliarityInput input)
   {
      _scheduler.Nudge(input.TourId, Nudge.ReviewerAction);
      return _writer.Write(pass, input.TourId, edit =>
      {
         var latest = edit.Latest;
         if (FamiliarityRules.CheckPlanFirst(latest) is { } planFirst)
         {
            return planFirst;
         }

         // The report's waiting refusal comes before the level checks, because a change that waits makes the level the tour's already.
         var text = string.IsNullOrWhiteSpace(input.Text) ? "" : input.Text;
         var reported = edit.Threads.Report(FamiliarityRules.Decision(latest, input.Familiarity, input.StopId), text, input.StopId, "familiarity", latest with { Familiarity = input.Familiarity });
         if ((reported.Error ?? FamiliarityRules.Check(latest, input.Familiarity) ?? AnchorChecks.CheckViewedStop(latest, input.StopId) ?? CheckWords(input.Text, "familiarity change")) is { } refused)
         {
            return refused;
         }

         return edit.Store(reported, (stored, written) =>
         {
            if (stored.Before.Familiarity != stored.Tour.Familiarity)
            {
               stored.Publish(ServerEvents.ForFamiliarityChanged(stored.Tour));
            }

            return new ChangedFamiliarity(stored.Tour.Id, stored.Tour.UpdatedAt, stored.Tour.Familiarity, written.Thread!);
         });
      });
   }

   /// <summary>
   /// Approves the plan of a plan-first tour in the reviewer's name: stores the reviewer's skips and level, lifts the plan's wait, and stores a decided thread that reports the plan approval to
   /// the tour's bound session. Sends the session the question, and returns what the approval stored with the thread.
   /// </summary>
   /// <remarks>
   /// One write stores the approval, the skips, the level and the thread, so none lands without the others. A tour with no bound session is refused, and so is a tour whose plan does not wait,
   /// an order that is not each stop of the latest plan once, and a skip of a stop that the plan lacks. A waiting tour action does not refuse it,
   /// because no stop has content yet for that action to change.
   /// </remarks>
   public OperationResult<ApprovedPlan> ApprovePlan(Access.ReviewerToSession.Pass pass, ApprovePlanInput input)
   {
      _scheduler.Nudge(input.TourId, Nudge.ReviewerAction);
      return _writer.Write(pass, input.TourId, edit =>
      {
         var latest = edit.Latest;
         if (CheckApproval(latest, input) is { } refused)
         {
            return refused;
         }

         // The check refuses a tour without a plan that waits.
         var plan = latest.Plan!;
         var skipped = input.SkippedStops.ToHashSet(StringComparer.Ordinal);
         var skippedPlan = plan with { Stops = [.. plan.Stops.Select(stop => stop.Skipped == skipped.Contains(stop.Id) ? stop : stop with { Skipped = !stop.Skipped })] };
         var next = latest with { PlanApproval = PlanApproval.Given, Plan = skippedPlan, Familiarity = input.Familiarity };
         var text = string.IsNullOrWhiteSpace(input.Text) ? "" : input.Text;
         var reported = edit.Threads.Report(ReviewerDecision.PlanApproved.For(next, input.StopIds), text, null, "familiarity", next);
         return edit.Store(reported, (stored, thread) =>
         {
            var written = stored.Tour;
            stored.Publish(ServerEvents.ForPlanApproved(written));
            if (!written.Plan!.SkippedIds.SequenceEqual(stored.Before.Plan!.SkippedIds))
            {
               stored.Publish(ServerEvents.ForSkipsUpdated(new UpdatedSkips(written.Id, written.UpdatedAt, written.Plan.SkippedIds)));
            }

            if (written.Familiarity != stored.Before.Familiarity)
            {
               stored.Publish(ServerEvents.ForFamiliarityChanged(written));
            }

            return new ApprovedPlan(written.Id, written.UpdatedAt, written.PlanApproval, written.Familiarity, written.Plan.SkippedIds, thread.Thread!);
         });
      });
   }

   /// <inheritdoc cref="IterationLifecycle.UpdateAsync"/>
   public Task<OperationResult<UpdatedIteration>> UpdateIterationAsync(Access.ReviewerToSession.Pass pass, UpdateIterationInput input, CancellationToken cancellationToken) =>
      _lifecycle.UpdateAsync(pass, input, cancellationToken);

   // Returns why the plan cannot be approved with the input, or null when it can.
   private static OperationError? CheckApproval(Tour tour, ApprovePlanInput input)
   {
      if (DecisionRules.CheckApprovable(tour) is { } notWaiting)
      {
         return notWaiting;
      }

      if (PlanRules.CheckOrder(tour.Plan, input.StopIds) is { } order)
      {
         return OperationError.Invalid(order.Under("stopIds"));
      }

      for (var i = 0; i < input.SkippedStops.Count; i++)
      {
         // The serializer checks nullability on properties but not on collection elements.
         if (input.SkippedStops[i] is not { } id)
         {
            return OperationError.Invalid(FieldPath.Root.Then("skippedStops").At(i), "a stop ID cannot be null.");
         }

         if (tour.Plan!.Stops.All(stop => stop.Id != id))
         {
            return OperationError.StopNotShown(id);
         }
      }

      return CheckWords(input.Text, "approval");
   }

   // Returns why the reviewer's optional words break a limit, or null when they keep the limits or are absent.
   private static OperationError? CheckWords(string? text, string noun) =>
      text is { Length: > 0 } && ReviewerWords.Limit(text, noun) is { } limit ? OperationError.Invalid("text", limit) : null;
}
