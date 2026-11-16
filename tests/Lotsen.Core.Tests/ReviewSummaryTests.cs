using System.Text.Json;
using Lotsen.Json;
using Lotsen.Operations;
using Lotsen.Tours;

namespace Lotsen.Tests;

/// <summary>Covers how a review summary is built from a tour's state, as the UI sees the tour.</summary>
[Trait("Category", "Summary")]
public sealed class ReviewSummaryTests
{
   private static readonly Tour Sample = SummaryTours.Apply(SummaryTours.Empty());

   [Fact]
   public void Answers_are_grouped_by_their_own_type_with_their_stop_check_question_and_state()
   {
      var answers = ReviewSummary.Of(Sample, Sample).Answers;

      Assert.Equal([new SummaryAnswer("s2", "The new discount rule", "c1", "Is the cap right?", "The cap is right.", AnswerType.Decision, AnswerState.Received)], answers.Decisions);
      Assert.Equal(
         [
            new SummaryAnswer("s2", "The new discount rule", "c2", "Should the cap be configurable?", SummaryTours.DraftText, AnswerType.Concern, AnswerState.Draft),
            new SummaryAnswer("gone", null, "c9", null, "Who owns this?", AnswerType.Concern, AnswerState.Sent),
         ],
         answers.Concerns);
      Assert.Equal([new SummaryAnswer("s5", "Retry policy", "c3", "How often should it retry?", "Ask the team.", AnswerType.OpenQuestion, AnswerState.Sent)], answers.OpenQuestions);
   }

   [Fact]
   public void Outdated_answers_are_listed_apart_with_the_version_they_stay_on_and_its_question()
   {
      var answers = ReviewSummary.Of(Sample, Sample).Answers;

      Assert.Equal([new SummaryAnswer("s2", "The new discount rule", "c0", SummaryTours.OldQuestion, "Keep the old rule.", AnswerType.Decision, AnswerState.Sent, OutdatedOn: 1)], answers.Outdated);
      Assert.DoesNotContain(answers.Decisions, answer => answer.CheckId == "c0");
   }

   [Fact]
   public void Outdated_answer_takes_its_question_from_the_earlier_versions_passed_beside_the_tour()
   {
      var shown = AgentView.Of(Sample);

      var outdated = Assert.Single(ReviewSummary.Of(shown, Sample).Answers.Outdated);

      Assert.Equal(SummaryTours.OldQuestion, outdated.Question);
   }

   [Fact]
   public void Answers_of_one_type_follow_plan_order_and_check_order()
   {
      var now = DateTimeOffset.UtcNow;
      var tour = Sample with
      {
         Answers =
         [
            new CheckAnswer("s5", "c3", "Three.", AnswerType.Decision, AnswerState.Draft, now),
            new CheckAnswer("gone", "c9", "Unknown.", AnswerType.Decision, AnswerState.Draft, now),
            new CheckAnswer("s2", "c2", "No.", AnswerType.Decision, AnswerState.Draft, now),
            new CheckAnswer("s2", "c1", "Yes.", AnswerType.Decision, AnswerState.Draft, now),
         ],
      };

      var decisions = ReviewSummary.Of(tour, tour).Answers.Decisions;

      Assert.Equal([("s2", "c1"), ("s2", "c2"), ("s5", "c3"), ("gone", "c9")], decisions.Select(answer => (answer.StopId, answer.CheckId)));
   }

   [Fact]
   public void Risk_stops_are_listed_as_concerns_in_plan_order_with_their_scenario()
   {
      var risks = ReviewSummary.Of(Sample, Sample).Risks;

      Assert.Equal([new SummaryRisk("s3", "Rounding on stacked discounts", "Two discounts round twice."), new SummaryRisk("s4", "Cache eviction under load", null)], risks);
   }

   [Fact]
   public void Threads_are_grouped_by_state_with_their_anchor_stop_and_action()
   {
      var threads = ReviewSummary.Of(Sample, Sample).Threads;
      var anchors = Sample.Threads.ToDictionary(thread => thread.Id, thread => thread.Anchor);

      Assert.Equal(
         [
            new SummaryThread("t1", anchors["t1"], "s2", "The new discount rule"),
            new SummaryThread("t3", anchors["t3"], null, null, TourAction.ReplanKind),
            new SummaryThread(SummaryTours.NoteThreadId, anchors[SummaryTours.NoteThreadId], "s1", "What this PR changes", Note: true),
            new SummaryThread("t6", anchors["t6"], null, null),
         ],
         threads.Open);
      Assert.Equal([new SummaryThread("t2", anchors["t2"], "s3", "Rounding on stacked discounts")], threads.Resolved);
      Assert.Equal([new SummaryThread("t5", anchors["t5"], "s4", "Cache eviction under load")], threads.Outdated);
   }

   [Fact]
   public void Typed_notes_are_grouped_by_their_type_apart_from_the_answers_in_every_thread_state()
   {
      var tour = Sample with { Threads = TypedNotes() };

      var typed = ReviewSummary.Of(tour, tour).TypedThreads!;

      var anchor = new ThreadAnchor.WholeStop("s3");
      Assert.Equal(
         [
            new SummaryTypedThread("n1", anchor, "s3", "Rounding on stacked discounts", "Keep the cap.", AnswerType.Decision, QuestionThreadState.Open, Note: true),
            new SummaryTypedThread("n4", new ThreadAnchor.WholeTour(), null, null, "Ship it.", AnswerType.Decision, QuestionThreadState.Outdated, Note: true),
         ],
         typed.Decisions);
      Assert.Equal([new SummaryTypedThread("n2", anchor, "s3", "Rounding on stacked discounts", "The cap drifts.", AnswerType.Concern, QuestionThreadState.Resolved, Note: true)], typed.Concerns);
      Assert.Equal([new SummaryTypedThread("n3", anchor, "s3", "Rounding on stacked discounts", "Who owns it?", AnswerType.OpenQuestion, QuestionThreadState.Open, Note: true)], typed.OpenQuestions);
      var answers = ReviewSummary.Of(tour, tour).Answers;
      var sampleAnswers = ReviewSummary.Of(Sample, Sample).Answers;
      Assert.Equal(
         [sampleAnswers.Decisions, sampleAnswers.Concerns, sampleAnswers.OpenQuestions, sampleAnswers.Outdated],
         [answers.Decisions, answers.Concerns, answers.OpenQuestions, answers.Outdated]);
   }

   [Fact]
   public void Typed_note_stays_in_the_thread_group_of_its_state()
   {
      var tour = Sample with { Threads = TypedNotes() };

      var threads = ReviewSummary.Of(tour, tour).Threads;

      Assert.Equal(["n1", "n3", "n5"], threads.Open.Select(thread => thread.Id));
      Assert.Equal(["n2"], threads.Resolved.Select(thread => thread.Id));
      Assert.Equal(["n4"], threads.Outdated.Select(thread => thread.Id));
      Assert.All([.. threads.Open, .. threads.Resolved, .. threads.Outdated], thread => Assert.True(thread.Note));
   }

   /// <summary>An unsent note is the reviewer's own and <see cref="AgentView.Of(Tour)"/> leaves it out, so the summary that Claude reads has no typed note.</summary>
   [Fact]
   public void Summary_of_the_agent_view_holds_no_typed_note()
   {
      var tour = Sample with { Threads = TypedNotes() };

      Assert.Null(AgentView.Summary(tour).TypedThreads);
   }

   [Fact]
   public void Sent_typed_thread_is_grouped_by_its_type_with_its_first_question_in_the_reviewer_and_the_agent_summary()
   {
      var tour = Sample with { Threads = SentTyped() };

      var reviewer = ReviewSummary.Of(tour, tour).TypedThreads!;
      var agent = AgentView.Summary(tour).TypedThreads!;

      var anchor = new ThreadAnchor.WholeStop("s3");
      var concern = new SummaryTypedThread("a1", anchor, "s3", "Rounding on stacked discounts", "The cap drifts.", AnswerType.Concern, QuestionThreadState.Open);
      var decision = new SummaryTypedThread("a2", anchor, "s3", "Rounding on stacked discounts", "Keep the cap.", AnswerType.Decision, QuestionThreadState.Resolved);
      Assert.Equal([concern], reviewer.Concerns);
      Assert.Equal([decision, new SummaryTypedThread("n1", anchor, "s3", "Rounding on stacked discounts", "Ship it.", AnswerType.Decision, QuestionThreadState.Open, Note: true)], reviewer.Decisions);
      Assert.Empty(reviewer.OpenQuestions);
      Assert.Equal([concern], agent.Concerns);
      Assert.Equal([decision], agent.Decisions);
      Assert.Empty(agent.OpenQuestions);
   }

   /// <summary>A synthesis based on the summary before the change shows as predating it, as for a changed answer.</summary>
   [Fact]
   public void Agent_summary_fingerprint_changes_with_the_type_of_a_sent_thread_and_not_with_a_typed_note()
   {
      var tour = Sample with { Threads = SentTyped() };
      var retyped = tour with { Threads = Retyped(tour.Threads, "a1", AnswerType.OpenQuestion) };
      var noteRetyped = tour with { Threads = ThreadFixtures.Threads([.. tour.Threads.Where(thread => thread.Id != "n1"), Note("n1", "Ship it.", AnswerType.Concern)]) };

      var before = AgentView.Summary(tour).Fingerprint(tour.Pin);

      Assert.NotEqual(before, AgentView.Summary(retyped).Fingerprint(retyped.Pin));
      Assert.Equal(before, AgentView.Summary(noteRetyped).Fingerprint(noteRetyped.Pin));
      Assert.Equal(["a1"], AgentView.Summary(retyped).TypedThreads!.OpenQuestions.Select(thread => thread.Id));
   }

   [Fact]
   public void Summary_with_sent_threads_that_have_no_type_holds_no_typed_threads()
   {
      var tour = Sample with { Threads = ThreadFixtures.Threads(ThreadFixtures.Asked("a1", new ThreadAnchor.WholeStop("s3"), text: "Why here?")) };

      Assert.Null(ReviewSummary.Of(tour, tour).TypedThreads);
      Assert.Null(AgentView.Summary(tour).TypedThreads);
   }

   /// <summary>
   /// The value is the fingerprint of the contract sample's agent summary in the golden fixture of lotsen 0.3.0. A summary without a typed thread writes the JSON of that release, so a
   /// synthesis that it stored keeps its verdict.
   /// </summary>
   [Fact]
   public void Agent_summary_without_a_typed_thread_leaves_typed_threads_out_of_its_json_and_keeps_the_fingerprint_of_earlier_releases()
   {
      var (summary, fingerprint) = ContractSamples.AgentSummary();

      Assert.False(JsonSerializer.SerializeToNode(summary, LotsenJsonContext.Default.ReviewSummary)!.AsObject().ContainsKey("typedThreads"));
      Assert.Equal("69f8e422a7a8a7bbeedd5a5943ca2fed4f2785b379d08f3a7efa351eaf5671f0", fingerprint.ToString());
   }

   [Fact]
   public void Thread_on_a_stop_that_the_plan_no_longer_has_names_the_stop_without_a_title()
   {
      var tour = Sample with { Threads = ThreadFixtures.Threads(ThreadFixtures.Note("t9", new ThreadAnchor.WholeStop("gone"))) };

      var thread = Assert.Single(ReviewSummary.Of(tour, tour).Threads.Open);

      Assert.Equal(("gone", (string?)null), (thread.StopId, thread.StopTitle));
   }

   [Fact]
   public void Skipped_and_stale_stops_are_listed_in_plan_order()
   {
      var summary = ReviewSummary.Of(Sample, Sample);

      Assert.Equal([new SummaryStop("s5", "Retry policy"), new SummaryStop("s6", "How prices were computed")], summary.SkippedStops);
      Assert.Equal([new SummaryStaleStop("s2", "The new discount rule", 2), new SummaryStaleStop("s5", "Retry policy", 3)], summary.StaleStops);
   }

   [Fact]
   public void Tour_without_a_plan_answers_or_threads_has_an_empty_summary_with_its_ID_and_update_time()
   {
      var tour = SummaryTours.Empty();

      var summary = ReviewSummary.Of(tour, tour);

      Assert.Equal((tour.Id, tour.UpdatedAt), (summary.TourId, summary.UpdatedAt));
      Assert.Empty(summary.Answers.Decisions);
      Assert.Empty(summary.Answers.Concerns);
      Assert.Empty(summary.Answers.OpenQuestions);
      Assert.Empty(summary.Answers.Outdated);
      Assert.Empty(summary.Risks);
      Assert.Empty(summary.Threads.Open);
      Assert.Empty(summary.Threads.Resolved);
      Assert.Empty(summary.Threads.Outdated);
      Assert.Null(summary.TypedThreads);
      Assert.Empty(summary.SkippedStops);
      Assert.Empty(summary.StaleStops);
   }

   // A note of each type in the thread states, the last without a type, in the order the reviewer wrote them.
   private static TourThreads TypedNotes()
   {
      var anchor = new ThreadAnchor.WholeStop("s3");
      return ThreadFixtures.Threads(
         ThreadFixtures.Note("n1", anchor, "Keep the cap.", type: AnswerType.Decision),
         ThreadFixtures.Note("n2", anchor, "The cap drifts.", state: QuestionThreadState.Resolved, type: AnswerType.Concern),
         ThreadFixtures.Note("n3", anchor, "Who owns it?", type: AnswerType.OpenQuestion),
         ThreadFixtures.Note("n4", new ThreadAnchor.WholeTour(), "Ship it.", state: QuestionThreadState.Outdated, type: AnswerType.Decision),
         ThreadFixtures.Note("n5", anchor, "Just a reminder."));
   }

   // A sent concern, a sent decision that the reviewer resolved, and a typed note that the reviewer has not sent.
   private static TourThreads SentTyped()
   {
      var anchor = new ThreadAnchor.WholeStop("s3");
      return ThreadFixtures.Threads(
         ThreadFixtures.Asked("a1", anchor, text: "The cap drifts.", type: AnswerType.Concern),
         ThreadFixtures.Asked("a2", anchor, QuestionThreadState.Resolved, request: RequestState.Done, text: "Keep the cap.", type: AnswerType.Decision),
         Note("n1", "Ship it.", AnswerType.Decision));
   }

   private static QuestionThread Note(string id, string text, AnswerType type) => ThreadFixtures.Note(id, new ThreadAnchor.WholeStop("s3"), text, type: type);

   private static TourThreads Retyped(TourThreads threads, string threadId, AnswerType type) =>
      ThreadFixtures.Threads([.. threads.Select(thread => thread.Id == threadId ? ((QuestionThread.Sent.Asked)thread).Typed(type) : thread)]);
}
