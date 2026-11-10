using System.Text.Json.Serialization;

namespace Lotsen.Tours;

/// <summary>What the reviewer already did in the UI, reported to the bound session so Claude acts on it, as a decided thread stores it. The <c>kind</c> property in JSON names the derived type.</summary>
/// <remarks>
/// Each kind's factory builds one from what the same store update stores, and JSON reads one back, so no other code builds one. The rules of each kind are the columns of
/// <see cref="DecisionRules"/>. A decision is no request: the reviewer's change is stored whatever Claude replies.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(FamiliarityChanged), FamiliarityChangedKind)]
[JsonDerivedType(typeof(PlanApproved), PlanApprovedKind)]
[JsonDerivedType(typeof(IterationUpdated), IterationUpdatedKind)]
internal closed record ReviewerDecision
{
   public const string FamiliarityChangedKind = "familiarityChanged";
   public const string PlanApprovedKind = "planApproved";
   public const string IterationUpdatedKind = "iterationUpdated";

   private ReviewerDecision()
   {
   }

   /// <summary>The kind's name in JSON, which the question event names as its decision.</summary>
   [JsonIgnore]
   public abstract string Kind { get; }

   private static IReadOnlyList<string> StopIdsOf(IReadOnlyList<string>? stopIds, string kind) =>
      stopIds is not null && stopIds.All(id => id is not null) ? stopIds : throw StoredData.Invalid($"The {kind} decision needs its stop IDs, none of them null.");

   /// <summary>The reviewer's change of the tour's familiarity level, which asks Claude to rewrite the stops that the reviewer has not read yet at the new level.</summary>
   [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
   internal sealed record FamiliarityChanged : ReviewerDecision
   {
      /// <param name="from">The tour's familiarity level before the change.</param>
      /// <param name="to">The tour's familiarity level after the change.</param>
      /// <param name="stopIds">The IDs of the stops to rewrite, in plan order.</param>
      [JsonConstructor]
      private FamiliarityChanged(Familiarity from, Familiarity to, IReadOnlyList<string> stopIds)
      {
         From = from;
         To = to;
         StopIds = StopIdsOf(stopIds, FamiliarityChangedKind);
      }

      /// <summary>The tour's familiarity level before the change.</summary>
      public Familiarity From { get; }

      /// <summary>The tour's familiarity level after the change.</summary>
      public Familiarity To { get; }

      /// <summary>The IDs of the stops to rewrite, in plan order, as <see cref="For"/> picks them.</summary>
      public IReadOnlyList<string> StopIds { get; }

      [JsonIgnore]
      public override string Kind => FamiliarityChangedKind;

      /// <summary>Returns the decision that a change of the tour's level from <paramref name="from"/> to <paramref name="to"/> reports, with the stops to rewrite.</summary>
      /// <remarks>
      /// The stops to rewrite are those after the stop under view in plan order, or every stop when none is open, that have content and no skip. The reviewer has read the stops up to the one
      /// under view. A pending stop gets its first content at the new level, and a skipped stop is one that the reviewer does not read.
      /// </remarks>
      /// <param name="tour">The tour, whose plan the stops come from.</param>
      /// <param name="from">The level before the change.</param>
      /// <param name="to">The level after the change.</param>
      /// <param name="viewedStopId">The ID of the stop under view, which must be a stop of the tour's plan, or null when no stop is open.</param>
      public static FamiliarityChanged For(Tour tour, Familiarity from, Familiarity to, string? viewedStopId)
      {
         var stops = tour.Plan?.Stops ?? [];
         var after = viewedStopId is null ? stops : stops.SkipWhile(stop => stop.Id != viewedStopId).Skip(1);
         return new FamiliarityChanged(from, to, [.. after.Where(stop => stop is Stop.Written { Skipped: false }).Select(stop => stop.Id)]);
      }
   }

   /// <summary>The reviewer's approval of the plan of a plan-first tour, which asks Claude to write the plan's stops, with the reviewer's proposed order, skips and level.</summary>
   /// <remarks>A cancel or a lost request leaves the plan approved.</remarks>
   [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
   internal sealed record PlanApproved : ReviewerDecision
   {
      /// <param name="stopIds">The proposed order.</param>
      /// <param name="skippedStops">The skipped stops.</param>
      /// <param name="familiarity">The level.</param>
      [JsonConstructor]
      private PlanApproved(IReadOnlyList<string> stopIds, IReadOnlyList<string> skippedStops, Familiarity familiarity)
      {
         StopIds = StopIdsOf(stopIds, PlanApprovedKind);
         SkippedStops = StopIdsOf(skippedStops, PlanApprovedKind);
         Familiarity = familiarity;
      }

      /// <summary>The IDs of every stop of the plan, each once, in the order that the reviewer proposes, which is a request as a reorder's is.</summary>
      public IReadOnlyList<string> StopIds { get; }

      /// <summary>The IDs of the stops that the reviewer skipped, in plan order, as the tour held them after the approval.</summary>
      public IReadOnlyList<string> SkippedStops { get; }

      /// <summary>The tour's familiarity level after the approval.</summary>
      public Familiarity Familiarity { get; }

      [JsonIgnore]
      public override string Kind => PlanApprovedKind;

      /// <summary>Returns the decision that the approval of <paramref name="approved"/>'s plan reports, with its skips and level read from that tour.</summary>
      /// <param name="approved">The tour as the approval stores it, with the reviewer's skips and level.</param>
      /// <param name="order">The reviewer's proposed order, which names every stop of the plan once.</param>
      public static PlanApproved For(Tour approved, IReadOnlyList<string> order) => new(order, approved.Plan?.SkippedIds ?? [], approved.Familiarity);
   }

   /// <summary>The reviewer's update of the tour to a new iteration, which asks Claude to revise the stops that the update made stale.</summary>
   [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
   internal sealed record IterationUpdated : ReviewerDecision
   {
      /// <param name="iteration">The iteration's number.</param>
      /// <param name="stopIds">The stale stops.</param>
      [JsonConstructor]
      private IterationUpdated(int iteration, IReadOnlyList<string> stopIds)
      {
         Iteration = iteration;
         StopIds = StopIdsOf(stopIds, IterationUpdatedKind);
      }

      /// <summary>The number of the iteration that the update pinned the tour to.</summary>
      public int Iteration { get; }

      /// <summary>The IDs of the stops with a stale marker after the update, in plan order.</summary>
      public IReadOnlyList<string> StopIds { get; }

      [JsonIgnore]
      public override string Kind => IterationUpdatedKind;

      /// <summary>Returns the decision that an update to the iteration reports.</summary>
      /// <param name="iteration">The number of the iteration that the update pins the tour to.</param>
      /// <param name="staleStops">The IDs of the stops that the update made stale, in plan order.</param>
      public static IterationUpdated For(int iteration, IReadOnlyList<string> staleStops) => new(iteration, staleStops);
   }
}
