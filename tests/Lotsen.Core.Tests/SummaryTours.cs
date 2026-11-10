using Lotsen.Git;
using Lotsen.Operations;
using Lotsen.Tours;

namespace Lotsen.Tests;

/// <summary>A tour state that the store accepts and that fills every part of a review summary: answers of each type and state, risk stops, threads in each state, skips and stale markers.</summary>
internal static class SummaryTours
{
   public const string DraftText = "Make the cap configurable.";
   public const string NoteText = "Check the rounding later.";
   public const string NoteThreadId = "t4";
   public const string OldQuestion = "Is the old rule kept?";

   private static readonly DateTimeOffset Now = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
   private static readonly CommitId Head = CommitId.Parse(new string('b', 40));

   /// <summary>Returns the tour with the sample plan, earlier versions, skips, answers and threads, its answers' batches going to the session with this ID.</summary>
   public static Tour Apply(Tour tour, string sessionId = "session1") => tour with
   {
      Plan = Plan(),
      Answers = Answers(sessionId),
      Threads = Threads(),
   };

   /// <summary>Returns a tour with no plan, answers or threads.</summary>
   public static Tour Empty() =>
      new(TourId.New(), new PullRequestTarget("test", new Uri("https://example.test/pr/1")), Familiarity.Some, Now, Now, new TourPin.Pinning(), null, TourThreads.Empty, []);

   /// <summary>Returns the sample plan, whose stop <c>s2</c> holds an earlier version and whose stops <c>s5</c> and <c>s6</c> are skipped.</summary>
   public static Plan Plan() => new(
      [new Chapter("a", "Pricing")],
      [
         new Stop.Written("s1", "What this PR changes", StopKind.Overview, "a", new StopContent("Discounts get a cap.", []), 1, []),
         new Stop.Written(
            "s2", "The new discount rule", StopKind.Inspect, "a",
            new StopContent("The cap applies last.", [Passage()], Checks: [new StopCheck("c1", "Is the cap right?"), new StopCheck("c2", "Should the cap be configurable?")]),
            2,
            [new StopVersion("The discount rule", StopKind.Inspect, new StopContent("The first rule.", [Passage()], Checks: [new StopCheck("c0", OldQuestion)]), Now)],
            StaleOn: 2),
         new Stop.Written("s3", "Rounding on stacked discounts", StopKind.Risk, "a", new StopContent("Rounding can drift.", [Passage()], Scenario: "Two discounts round twice."), 1, []),
         new Stop.Pending("s4", "Cache eviction under load", StopKind.Risk, "a"),
         new Stop.Written("s5", "Retry policy", StopKind.Question, "a", new StopContent("Retries are new.", [Passage()], Checks: [new StopCheck("c3", "How often should it retry?")]), 1, [], StaleOn: 3, Skipped: true),
         new Stop.Pending("s6", "How prices were computed", StopKind.Context, "a", Skipped: true),
      ],
      []);

   private static StopPassage Passage() => new(FileSide.Head, "src/Discounts.cs", 1, 2, PassageRole.Primary, Head, "a\nb\n");

   // In the order the checks were first answered, which is not plan order.
   private static IReadOnlyList<CheckAnswer> Answers(string sessionId)
   {
      var sent = new AnswerBatch("batch001", sessionId, Now);
      var received = sent with { ReceivedAt = Now };
      return
      [
         new CheckAnswer("s5", "c3", "Ask the team.", AnswerType.OpenQuestion, AnswerState.Sent, Now, sent),
         new CheckAnswer("s2", "c1", "The cap is right.", AnswerType.Decision, AnswerState.Received, Now, received),
         new CheckAnswer("gone", "c9", "Who owns this?", AnswerType.Concern, AnswerState.Sent, Now, sent),
         new CheckAnswer("s2", "c2", DraftText, AnswerType.Concern, AnswerState.Draft, Now),
         new CheckAnswer("s2", "c0", "Keep the old rule.", AnswerType.Decision, AnswerState.Sent, Now, sent, outdatedOn: 1),
      ];
   }

   private static TourThreads Threads() => ThreadFixtures.Threads(
      ThreadFixtures.Asked("t1", new ThreadAnchor.CodeRange(FileSide.Head, "src/Discounts.cs", 3, 5, Head, "a\nb\nc"), stopId: "s2", request: RequestState.Done, at: Now),
      ThreadFixtures.Asked("t2", new ThreadAnchor.StopText("s3", "scenario", "round twice", 1), QuestionThreadState.Resolved, "s3", request: RequestState.Done, at: Now),
      ThreadFixtures.Asked("t3", new ThreadAnchor.WholeTour(), action: new TourAction.Replan(), request: RequestState.Done, at: Now),
      ThreadFixtures.Note(NoteThreadId, new ThreadAnchor.WholeFile(FileSide.Head, "src/Rates.cs", Head), NoteText, "s1", at: Now),
      ThreadFixtures.Asked("t5", new ThreadAnchor.WholeStop("s4"), QuestionThreadState.Outdated, "s4", request: RequestState.Done, at: Now),
      ThreadFixtures.Started("t6", new ThreadAnchor.WholeTour(), "The rounding needs a test.", at: Now));
}
