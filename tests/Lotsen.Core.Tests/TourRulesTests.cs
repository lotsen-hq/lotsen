using Lotsen.Tours;
using static Lotsen.Tests.TourValues;

namespace Lotsen.Tests;

/// <summary>Covers the rules of <see cref="TourRules"/> that span the tour, one row per rule, and <see cref="ReviewMarkRules"/>.</summary>
[Trait("Category", "Rules")]
public sealed class TourRulesTests
{
   private static readonly AnswerBatch Batch = new("b1", "session1", Now);

   private static readonly Dictionary<string, (Tour Tour, string Path, string Problem)> Broken = new()
   {
      ["plan rule"] = (Tour(Plan(Pending("s1"), Pending("s1"))), "plan.stops[1].id", "another stop already has the ID 's1'."),
      ["content rule of a stop"] = (Tour(Plan(Written("s1", content: Content() with { Headline = "" }))), "plan.stops[0].content.headline", "a stop needs a headline: its main point, which the reviewer reads first."),
      ["content rule under the stop's kind"] = (Tour(Plan(Written("s1", StopKind.Risk, Content()))), "plan.stops[0].content.scenario", "a risk stop needs a scenario: how the risk could happen."),
      ["check ID of two stops"] = (
         Tour(Plan(Written("s1", StopKind.Question), Written("s2", StopKind.Question))),
         "plan.stops[1].content.checks[0].id",
         "another check of the tour already has the ID 'c1'."),
      ["versions that do not add up"] = (Tour(Plan(Written("s1") with { Version = 2 })), "plan.stops[0].earlier", "stop 's1' is at version 2, so it holds 1 earlier versions, not 0."),
      ["null earlier version"] = (Tour(Plan(Written("s1") with { Version = 2, Earlier = [null!] })), "plan.stops[0].earlier[0]", "an element cannot be null."),
      ["content rule of an earlier version under its own kind"] = (
         Tour(Plan(Written("s1") with { Version = 2, Earlier = [new StopVersion("Old", StopKind.Risk, Content(), Now)] })),
         "plan.stops[0].earlier[0].content.scenario",
         "a risk stop needs a scenario: how the risk could happen."),
      ["null answer"] = (Tour() with { Answers = [null!] }, "answers[0]", "an element cannot be null."),
      ["two answers to one check"] = (
         Tour() with { Answers = [Answer("s1", "c1"), Answer("s1", "c1")] },
         "answers[1]",
         "another answer already answers check 'c1' of stop 's1'."),
      ["stale marker before iteration 1"] = (Tour(Plan(Written("s1") with { StaleOn = 0 })), "plan.stops[0].staleOn", "an iteration counts from 1."),
      ["available iteration on a tour that is pinning"] = (
         Tour() with { Pin = new TourPin.Pinning(), AvailableIteration = new AvailableIteration(Pinned with { Iteration = 2 }, [], Now) },
         "availableIteration",
         "only a pinned tour has an available iteration."),
      ["available iteration on a tour whose pin failed"] = (
         Tour() with { Pin = new TourPin.Failed("No."), AvailableIteration = new AvailableIteration(Pinned with { Iteration = 2 }, [], Now) },
         "availableIteration",
         "only a pinned tour has an available iteration."),
      ["null stale stop of the available iteration"] = (
         Tour() with { AvailableIteration = new AvailableIteration(Pinned with { Iteration = 2 }, [null!], Now) },
         "availableIteration.staleStops[0]",
         "an element cannot be null."),
   };

   public static TheoryData<string> Rules => [.. Broken.Keys];

   [Theory]
   [MemberData(nameof(Rules))]
   public void Broken_rule_names_its_field_and_its_problem(string rule)
   {
      var (tour, path, problem) = Broken[rule];

      var fault = TourRules.Check(tour);

      Assert.NotNull(fault);
      Assert.Equal(path, fault.Path.ToString());
      Assert.Equal(problem, fault.Problem);
   }

   [Fact]
   public void Tour_that_follows_every_rule_has_no_fault()
   {
      var revised = Written("s3", StopKind.Risk) with { StaleOn = 2, Version = 2, Earlier = [new StopVersion("Was an inspect", StopKind.Inspect, Content(), Now)] };
      var tour = Tour(Plan(Pending("s1", StopKind.Overview) with { Skipped = true }, Written("s2", StopKind.Question), revised)) with
      {
         Answers = [Answer("s2", "c1"), Answer("gone", "c1").SentIn(Batch)],
         AvailableIteration = new AvailableIteration(Pinned with { Iteration = 2 }, ["s3"], Now),
         Progress = new ReadingProgress(false, "gone"),
      };

      Assert.Null(TourRules.Check(tour));
   }

   [Theory]
   [InlineData("null", "reviewMarks[1]", "an element cannot be null.")]
   [InlineData("../a.cs", "reviewMarks[1].path", "give a relative repository path with forward slashes and no empty, '.' or '..' segments.")]
   [InlineData("src/a.cs", "reviewMarks[1].path", "another review mark already names 'src/a.cs'.")]
   public void Broken_review_mark_names_its_field_and_its_problem(string second, string path, string problem)
   {
      var fault = ReviewMarkRules.Check([new ReviewMark("src/a.cs", null), second == "null" ? null! : new ReviewMark(second, null)]);

      Assert.Equal((path, problem), (fault?.Path.ToString(), fault?.Problem));
   }

   [Fact]
   public void Review_marks_on_distinct_paths_have_no_fault() => Assert.Null(ReviewMarkRules.Check([new ReviewMark("src/a.cs", null), new ReviewMark("src/b.cs", new string('c', 40))]));

   private static CheckAnswer Answer(string stopId, string checkId) => new(stopId, checkId, "Yes.", AnswerType.Decision, AnswerState.Draft, Now);
}
