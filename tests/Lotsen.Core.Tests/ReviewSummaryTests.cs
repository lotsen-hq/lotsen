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
      Assert.Empty(summary.SkippedStops);
      Assert.Empty(summary.StaleStops);
   }
}
