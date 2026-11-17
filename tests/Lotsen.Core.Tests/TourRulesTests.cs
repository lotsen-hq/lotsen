using Lotsen.Platforms;
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
      ["repository tour without a purpose"] = (RepositoryTour() with { Purpose = null }, "purpose", "a repository tour needs a purpose: orient, prepare or evaluate."),
      ["repository tour without a title"] = (RepositoryTour() with { Title = null }, "title", "a repository tour needs a title."),
      ["repository tour with a blank title"] = (RepositoryTour() with { Title = " " }, "title", "a repository tour needs a title."),
      ["title with a line break"] = (RepositoryTour() with { Title = "Two\nlines" }, "title", "a title is one line of plain text, without line breaks, tabs or other control characters."),
      ["title over the limit"] = (RepositoryTour() with { Title = new string('t', TourRules.MaxTitleLength + 1) }, "title", $"a title holds at most {TourRules.MaxTitleLength} characters, and this one holds {TourRules.MaxTitleLength + 1}."),
      ["repository tour with blank words"] = (RepositoryTour() with { Words = " " }, "words", "words are null when the reviewer gave none, never empty."),
      ["dive origin with an empty stop ID"] = (RepositoryTour() with { DiveOrigin = new DiveOrigin(OtherId, OtherId, "") }, "diveOrigin.stopId", "a dive's stop ID is null for a dive from the whole tour, never empty."),
      ["repository tour pinned to an iteration"] = (RepositoryTour() with { Pin = Pinned }, "pin", "a repository tour is pinned to a snapshot of one commit, not to an iteration of a pull request."),
      ["repository tour with a pull request state"] = (RepositoryTour() with { PullRequestState = PullRequestState.Completed }, "pullRequestState", "only a pull request's tour has a pull request state."),
      ["pull request tour with a purpose"] = (Tour() with { Purpose = TourPurpose.Evaluate }, "purpose", "only a repository tour has a purpose."),
      ["pull request tour with a purpose and a risk stop"] = (Tour(Plan(Written("s1", StopKind.Risk))) with { Purpose = TourPurpose.Orient }, "purpose", "only a repository tour has a purpose."),
      ["pull request tour with words"] = (Tour() with { Words = "Focus on the cache." }, "words", "only a repository tour has words."),
      ["pull request tour with a title"] = (Tour() with { Title = "Pricing" }, "title", "only a repository tour has a title."),
      ["pull request tour with a dive origin"] = (Tour() with { DiveOrigin = new DiveOrigin(OtherId, OtherId) }, "diveOrigin", "only a repository tour is a dive."),
      ["pull request tour pinned to a snapshot"] = (Tour() with { Pin = Snapshot }, "pin", "a pull request's tour is pinned to an iteration of the pull request, not to a snapshot."),
      ["gotchas in a pull request's tour"] = (
         Tour(Plan(Written("s1", content: Content() with { Gotchas = "Mind the cache." }))),
         "plan.stops[0].content.gotchas",
         "only a repository tour's stops take gotchas. Leave gotchas out."),
      ["gotchas on a risk stop of a pull request's tour"] = (
         Tour(Plan(Written("s1", StopKind.Risk, Content(StopKind.Risk) with { Gotchas = "Mind the cache." }))),
         "plan.stops[0].content.gotchas",
         "only a repository tour's stops take gotchas. Leave gotchas out."),
      ["try it in an earlier version in a pull request's tour"] = (
         Tour(Plan(Written("s1") with { Version = 2, Earlier = [new StopVersion("Old", StopKind.Context, Content(StopKind.Context) with { TryIt = "Run it." }, Now)] })),
         "plan.stops[0].earlier[0].content.tryIt",
         "only a repository tour's stops take tryIt. Leave tryIt out."),
      ["passage at the base of a repository tour"] = (
         RepositoryTour(Plan(Written("s1", content: Content() with { Passages = [Passage(), Passage(PassageRole.Related) with { Side = FileSide.Base }] }))),
         "plan.stops[0].content.passages[1].side",
         "a repository tour has only the head side, the one commit of its snapshot. Point the passage at head."),
      ["file mark in a repository tour"] = (
         RepositoryTour(Plan() with { FileMarks = [new FileMark("README.md", FileMarkKind.Generated, "A tool writes it.")] }),
         "plan.fileMarks",
         "a repository tour has no changed files to mark. Leave fileMarks out."),
      ["risk stop in an orient tour"] = (
         RepositoryTour(Plan(Pending("s1", StopKind.Risk))),
         "plan.stops[0].kind",
         "an orient tour has no risk stops. A risk stop belongs in a prepare or an evaluate tour, so make it a context or an inspect stop."),
      ["question stop in a prepare tour"] = (
         RepositoryTour(Plan(Written("s1", StopKind.Question))) with { Purpose = TourPurpose.Prepare },
         "plan.stops[0].kind",
         "a prepare tour has no question stops. A question stop belongs in an evaluate tour, so make it a context or an inspect stop."),
      ["risk version in an orient tour"] = (
         RepositoryTour(Plan(Written("s1") with { Version = 2, Earlier = [new StopVersion("Old", StopKind.Risk, Content(StopKind.Risk), Now)] })),
         "plan.stops[0].earlier[0].kind",
         "an orient tour has no risk stops. A risk stop belongs in a prepare or an evaluate tour, so make it a context or an inspect stop."),
      ["check in an orient tour"] = (
         RepositoryTour(Plan(Written("s1", content: Content() with { Checks = [new StopCheck("c1", "Fine?")] }))),
         "plan.stops[0].content.checks",
         "an orient tour asks no checks. Only an evaluate tour does, so leave checks out."),
      ["check in an earlier version of a prepare tour"] = (
         RepositoryTour(Plan(Written("s1") with { Version = 2, Earlier = [new StopVersion("Old", StopKind.Context, Content(StopKind.Context) with { Checks = [new StopCheck("c1", "Fine?")] }, Now)] })) with { Purpose = TourPurpose.Prepare },
         "plan.stops[0].earlier[0].content.checks",
         "a prepare tour asks no checks. Only an evaluate tour does, so leave checks out."),
   };

   private static TourId OtherId => TourId.TryParse("qrstuvwxyzabcdef", out var id) ? id : throw new InvalidOperationException("The ID does not parse.");

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

   [Fact]
   public void Repository_tour_that_follows_every_rule_has_no_fault()
   {
      var tour = RepositoryTour(Plan(Written("s1", content: Content() with { Gotchas = "Mind the cache.", TryIt = "Run it." }), Written("s2", StopKind.Context, Content(StopKind.Context) with { TryIt = "Run it." }))) with
      {
         Target = new RepositoryTarget(new Uri("https://example.test/repo"), "release/2.0", "test"),
         Purpose = TourPurpose.Evaluate,
         Words = "Is the cache safe under load?",
         Title = new string('t', TourRules.MaxTitleLength),
         DiveOrigin = new DiveOrigin(OtherId, OtherId, "s4"),
      };

      Assert.Null(TourRules.Check(tour));
   }

   /// <summary>Each row is a kind that its purpose allows beside the overview, context and inspect stops that every purpose allows, with checks only in an evaluate tour.</summary>
   [Theory]
   [InlineData("Orient", "Inspect", false)]
   [InlineData("Prepare", "Risk", false)]
   [InlineData("Evaluate", "Risk", true)]
   [InlineData("Evaluate", "Question", true)]
   public void Repository_tour_holds_the_kinds_and_checks_that_its_purpose_allows(string purpose, string kind, bool checks)
   {
      var kinds = new[] { StopKind.Overview, StopKind.Context, StopKind.Inspect, Enum.Parse<StopKind>(kind) };
      var stops = kinds.Select((of, i) => Written($"s{i}", of, checks ? Content(of) with { Checks = [new StopCheck($"c{i}", "Fine?")] } : Content(of)));

      Assert.Null(TourRules.Check(RepositoryTour(Plan([.. stops])) with { Purpose = Enum.Parse<TourPurpose>(purpose) }));
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
