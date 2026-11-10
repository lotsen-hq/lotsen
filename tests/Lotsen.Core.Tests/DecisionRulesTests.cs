using Lotsen.Tours;
using static Lotsen.Tests.TourValues;

namespace Lotsen.Tests;

/// <summary>Covers the columns of <see cref="DecisionRules"/> and the factories of <see cref="ReviewerDecision"/>, one row per rule, on tour values.</summary>
/// <remarks>
/// The tour waits for its plan's approval, with the pending stops <c>s1</c> and <c>s2</c>, <c>s2</c> skipped, at level some, pinned to iteration 1. A refused row names the field and a part of the
/// problem, and a row without them goes again.
/// </remarks>
[Trait("Category", "TourActions")]
public sealed class DecisionRulesTests
{
   private static readonly Dictionary<string, (Tour Tour, Func<Tour, ReviewerDecision> Decision, string? Field, string? Problem)> Resends = new()
   {
      ["a familiarity change never goes again"] = (Approving(), tour => ReviewerDecision.FamiliarityChanged.For(tour, Familiarity.Some, Familiarity.New, null), "decision.kind", "a familiarity change does not go again"),
      ["an approval goes again while the tour holds its stops, skips and level"] = (Approving(), Approval, null, null),
      ["an approval once Claude wrote a stop"] = (Approving() with { Plan = Plan(Written("s1"), Pending("s2") with { Skipped = true }) }, Approval, "decision.kind", "Claude has written stops of the approved plan"),
      ["an approval after the level changed"] = (Approving(), tour => ReviewerDecision.PlanApproved.For(tour with { Familiarity = Familiarity.New }, ["s1", "s2"]), "decision.familiarity", "the tour's familiarity level changed since"),
      ["an approval after the skips changed"] = (Approving(), tour => ReviewerDecision.PlanApproved.For(tour with { Plan = Plan(Pending("s1"), Pending("s2")) }, ["s1", "s2"]), "decision.skippedStops", "the skipped stops changed since"),
      ["an approval after the plan's stops changed"] = (Approving(), tour => ReviewerDecision.PlanApproved.For(tour, ["s1", "s3"]), "decision.stopIds", "Claude changed the plan's stops since"),
      ["an iteration update goes again while a stop of it is stale"] = (Stale(), _ => ReviewerDecision.IterationUpdated.For(1, ["s1"]), null, null),
      ["an iteration update after the tour moved to another iteration"] = (Stale(), _ => ReviewerDecision.IterationUpdated.For(2, ["s1"]), "decision.iteration", "the tour moved to another update since"),
      ["an iteration update whose stops are no longer stale"] = (Approving(), _ => ReviewerDecision.IterationUpdated.For(1, ["s1"]), "decision.stopIds", "its stops are no longer stale"),
   };

   public static TheoryData<string> ResendRows => [.. Resends.Keys];

   [Theory]
   [MemberData(nameof(ResendRows))]
   public void Resend_row(string row)
   {
      var (tour, decision, field, problem) = Resends[row];
      var thread = (QuestionThread.Sent.Decided)ThreadFixtures.Decided("t1", decision(tour), request: RequestState.Canceled);

      var refused = DecisionRules.CheckResend(tour, thread);

      if (field is null)
      {
         Assert.Null(refused);
         return;
      }

      Assert.NotNull(refused);
      Assert.Equal(field, refused.Field?.ToString());
      Assert.Contains(problem!, refused.Problem, StringComparison.Ordinal);
   }

   [Fact]
   public void Waits_alone_puts_a_level_change_and_the_plan_approval_in_one_group_and_the_iteration_update_in_its_own()
   {
      var tour = Approving();

      Assert.Equal(WaitGroup.Level, DecisionRules.WaitsAlone(ReviewerDecision.FamiliarityChanged.For(tour, Familiarity.Some, Familiarity.New, null)));
      Assert.Equal(WaitGroup.Level, DecisionRules.WaitsAlone(Approval(tour)));
      Assert.Equal(WaitGroup.Iteration, DecisionRules.WaitsAlone(ReviewerDecision.IterationUpdated.For(1, ["s1"])));
   }

   /// <summary>The plan holds the written stops s1, s3 and s4, s3 skipped, and the pending stop s2.</summary>
   [Theory]
   [InlineData(null, new[] { "s1", "s4" })]
   [InlineData("s1", new[] { "s4" })]
   [InlineData("s4", new string[0])]
   public void Familiarity_change_names_the_written_unskipped_stops_after_the_viewed_one(string? viewed, string[] rewritten)
   {
      var tour = Tour(Plan(Written("s1"), Pending("s2"), Written("s3") with { Skipped = true }, Written("s4")));

      var change = ReviewerDecision.FamiliarityChanged.For(tour, Familiarity.Some, Familiarity.New, viewed);

      Assert.Equal((Familiarity.Some, Familiarity.New), (change.From, change.To));
      Assert.Equal(rewritten, change.StopIds);
   }

   [Fact]
   public void Plan_approval_reads_its_skips_and_level_from_the_approved_tour()
   {
      var approval = ReviewerDecision.PlanApproved.For(Approving() with { Familiarity = Familiarity.Familiar }, ["s2", "s1"]);

      Assert.Equal(["s2", "s1"], approval.StopIds);
      Assert.Equal(["s2"], approval.SkippedStops);
      Assert.Equal(Familiarity.Familiar, approval.Familiarity);
   }

   private static Tour Approving() => Tour(Plan(Pending("s1"), Pending("s2") with { Skipped = true })) with { PlanApproval = PlanApproval.Given };

   private static Tour Stale() => Tour(Plan(Written("s1") with { StaleOn = 1 }, Pending("s2") with { Skipped = true }));

   private static ReviewerDecision Approval(Tour tour) => ReviewerDecision.PlanApproved.For(tour, ["s1", "s2"]);
}
