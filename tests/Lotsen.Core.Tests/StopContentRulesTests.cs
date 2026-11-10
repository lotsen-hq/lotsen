using Lotsen.Tours;
using static Lotsen.Tests.TourValues;

namespace Lotsen.Tests;

/// <summary>Covers the rules of <see cref="StopContentRules"/>, one row per rule, on content values of each kind.</summary>
[Trait("Category", "Rules")]
public sealed class StopContentRulesTests
{
   private static readonly Dictionary<string, (StopKind Kind, StopContent Content, string Path, string Problem)> Broken = new()
   {
      ["empty headline"] = (StopKind.Inspect, Content() with { Headline = " " }, "headline", "a stop needs a headline: its main point, which the reviewer reads first."),
      ["null passage"] = (StopKind.Inspect, Content() with { Passages = [Passage(), null!] }, "passages[1]", "an element cannot be null."),
      ["null check"] = (StopKind.Inspect, Content() with { Checks = [null!] }, "checks[0]", "an element cannot be null."),
      ["null suggestion"] = (StopKind.Inspect, Content() with { Checks = [new StopCheck("c1", "Fine?", Suggestions: [null!])] }, "checks[0].suggestions[0]", "an element cannot be null."),
      ["null tag"] = (StopKind.Inspect, Content() with { Tags = ["a", null!] }, "tags[1]", "an element cannot be null."),
      ["empty check question"] = (StopKind.Inspect, Content() with { Checks = [new StopCheck("c1", " ")] }, "checks[0].question", "a check needs a question: the one decision that the reviewer makes."),
      ["empty check ID"] = (StopKind.Inspect, Content() with { Checks = [new StopCheck("", "Fine?")] }, "checks[0].id", "a check needs an ID."),
      ["repeated check ID"] = (StopKind.Inspect, Content() with { Checks = [new StopCheck("c1", "Fine?"), new StopCheck("c1", "Sure?")] }, "checks[1].id", "the check 'c1' appears twice. Give each check's ID once."),
      ["passage outside the repository"] = (StopKind.Inspect, Content() with { Passages = [Passage(path: "../a.cs")] }, "passages[0].path", "give a relative repository path with forward slashes and no empty, '.' or '..' segments."),
      ["passage before line 1"] = (StopKind.Inspect, Content() with { Passages = [Passage(startLine: 0)] }, "passages[0].startLine", "a line number starts at 1."),
      ["passage that ends before it starts"] = (StopKind.Inspect, Content() with { Passages = [Passage(startLine: 3, endLine: 2)] }, "passages[0].endLine", "the lines end at line 2, before the start line 3."),
      ["no primary passage"] = (
         StopKind.Inspect,
         Content() with { Passages = [Passage(PassageRole.Related)] },
         "passages",
         "exactly one passage must be primary, but 0 are. Make the passage the stop is about primary and the others related."),
      ["two primary passages"] = (
         StopKind.Context,
         Content(StopKind.Context) with { Passages = [Passage(), Passage()] },
         "passages",
         "exactly one passage must be primary, but 2 are. Make the passage the stop is about primary and the others related."),
      ["risk without a scenario"] = (StopKind.Risk, Content(StopKind.Risk) with { Scenario = null }, "scenario", "a risk stop needs a scenario: how the risk could happen."),
      ["question without a check"] = (StopKind.Question, Content(StopKind.Question) with { Checks = null }, "checks", "a question stop needs at least one check for the reviewer to answer."),
      ["inspect without a passage"] = (StopKind.Inspect, Content() with { Passages = [] }, "passages", "an inspect stop needs at least one passage. Only an overview may have none."),
      ["context without a passage"] = (StopKind.Context, Content(StopKind.Context) with { Passages = [] }, "passages", "a context stop needs at least one passage. Only an overview may have none."),
   };

   public static TheoryData<string> Rules => [.. Broken.Keys];

   public static TheoryData<string> Kinds => [.. Enum.GetNames<StopKind>()];

   [Theory]
   [MemberData(nameof(Rules))]
   public void Broken_rule_names_its_field_and_its_problem(string rule)
   {
      var (kind, content, path, problem) = Broken[rule];

      var fault = StopContentRules.Check(kind, content);

      Assert.NotNull(fault);
      Assert.Equal(path, fault.Path.ToString());
      Assert.Equal(problem, fault.Problem);
   }

   [Theory]
   [MemberData(nameof(Kinds))]
   public void Content_that_follows_the_rules_of_its_kind_has_no_fault(string kind) => Assert.Null(StopContentRules.Check(Enum.Parse<StopKind>(kind), Content(Enum.Parse<StopKind>(kind))));

   [Fact]
   public void Overview_may_point_at_no_passage() => Assert.Null(StopContentRules.Check(StopKind.Overview, Content(StopKind.Overview) with { Passages = [] }));
}
