using Lotsen.Operations;
using Lotsen.Tours;
using static Lotsen.Tests.TourValues;

namespace Lotsen.Tests;

/// <summary>Covers how <see cref="StopRules.ApplyPlanChange"/> names a plan rule that <c>put_stop</c>'s title, kind or chapter breaks: under the input's own field, or under its chapter.</summary>
[Trait("Category", "Rules")]
public sealed class PlanChangeTests
{
   private static readonly Tour Planned = Tour(new Plan([new Chapter("a", "A"), new Chapter("b", "B")], [Pending("s1"), Pending("s2"), Pending("s3", chapter: "b")], []));

   [Fact]
   public void Kind_that_breaks_a_rule_of_the_stop_is_refused_under_kind()
   {
      var error = StopRules.ApplyPlanChange(Planned, Input("s2") with { Kind = StopKind.Overview }).Error;

      Assert.Equal(("kind", "only the first stop can be an overview."), (error?.Field.ToString(), error?.Problem));
   }

   [Fact]
   public void Title_that_breaks_a_rule_of_the_stop_is_refused_under_title()
   {
      var error = StopRules.ApplyPlanChange(Planned, Input("s1") with { Title = " " }).Error;

      Assert.Equal(("title", "a stop title cannot be empty."), (error?.Field.ToString(), error?.Problem));
   }

   [Fact]
   public void Chapter_that_breaks_a_rule_of_another_chapter_is_refused_under_chapter()
   {
      var error = StopRules.ApplyPlanChange(Planned, Input("s3") with { Chapter = "a" }).Error;

      Assert.Equal("chapter", error?.Field.ToString());
      Assert.Equal("moving the stop to chapter 'a' breaks a rule of the plan. chapters[1]: chapter 'b' has no stops.", error?.Problem);
   }

   [Fact]
   public void Change_that_follows_the_rules_returns_the_changed_plan()
   {
      var change = StopRules.ApplyPlanChange(Planned, Input("s2") with { Chapter = "b", Title = "Moved" }).Value;

      Assert.NotNull(change);
      Assert.Equal(1, change.Index);
      Assert.Equal(("Moved", "b"), (change.Stop.Title, change.Stop.Chapter));
   }

   private static PutStopInput Input(string stopId) => new(Id, stopId, "The point.");
}
