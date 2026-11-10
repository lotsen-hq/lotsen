using System.Text.Json;
using Lotsen.Json;
using Lotsen.Tours;
using static Lotsen.Tests.TourValues;

namespace Lotsen.Tests;

/// <summary>Covers the check of <see cref="TourDocument"/> on the way out: what <see cref="TourDocument.StoredTour.Next"/> sets, keeps and refuses.</summary>
[Trait("Category", "Rules")]
public sealed class TourDocumentTests
{
   private static readonly TourDocument.StoredTour Stored = TourDocument.New(Id, Tour().Target, Familiarity.Some, PlanApproval.NotNeeded, Now).Value!;

   [Fact]
   public void Next_takes_the_update_time_and_keeps_the_creation_time()
   {
      var later = Now.AddHours(1);

      var next = Stored.Next(Stored.Tour with { Familiarity = Familiarity.New }, later).Value!;

      Assert.Equal((Now, later, Familiarity.New), (next.Tour.CreatedAt, next.Tour.UpdatedAt, next.Tour.Familiarity));
      Assert.Same(next.Tour, next.Document.Tour);
   }

   [Fact]
   public void Next_refuses_another_id_or_creation_time()
   {
      var other = TourId.TryParse("zzzzzzzzzzzzzzzz", out var id) ? id : throw new InvalidOperationException("The ID does not parse.");

      Assert.Equal("id", Stored.Next(Stored.Tour with { Id = other }, Now).Fault?.Path.ToString());
      Assert.Equal("createdAt", Stored.Next(Stored.Tour with { CreatedAt = Now.AddDays(-1) }, Now).Fault?.Path.ToString());
   }

   [Fact]
   public void Next_throws_for_the_view_that_an_agent_session_reads_of_a_revised_stop()
   {
      var revised = Written("s1") with { Version = 2, Earlier = [new StopVersion("Stop s1", StopKind.Inspect, Content(), Now)] };
      var withPlan = Stored.Next(Stored.Tour with { Plan = Plan(revised) }, Now).Value!;

      Assert.Throws<InvalidOperationException>(() => withPlan.Next(AgentView.Of(withPlan.Tour), Now));
   }

   /// <summary>No stop is revised and the tour has no note, so only the view flag keeps the view from being stored without its draft answer.</summary>
   [Fact]
   public void Next_throws_for_the_view_that_an_agent_session_reads_of_a_tour_with_a_draft_answer()
   {
      var drafted = DraftedTour();

      Assert.Throws<InvalidOperationException>(() => drafted.Next(AgentView.Of(drafted.Tour), Now));
   }

   /// <summary>The copy holds the stored answers again, so only the view flag tells it from a tour that a write may store.</summary>
   [Fact]
   public void Next_throws_for_a_tour_built_with_with_from_an_agent_view()
   {
      var drafted = DraftedTour();
      var built = AgentView.Of(drafted.Tour) with { Familiarity = Familiarity.New, Answers = drafted.Tour.Answers };

      Assert.Throws<InvalidOperationException>(() => drafted.Next(built, Now));
   }

   [Fact]
   public void An_agent_view_serializes_as_the_tour_without_its_draft_answer()
   {
      var drafted = DraftedTour();
      var view = AgentView.Of(drafted.Tour);

      Assert.Empty(view.Answers);
      Assert.Equal(Json(drafted.Tour with { Answers = [] }), Json(view));
   }

   // A tour with one version-1 written stop, one draft answer and no notes.
   private static TourDocument.StoredTour DraftedTour() =>
      Stored.Next(Stored.Tour with { Plan = Plan(Written("s1")), Answers = [new CheckAnswer("s1", "c1", "A draft.", AnswerType.Decision, AnswerState.Draft, Now)] }, Now).Value!;

   private static string Json(Tour tour) => JsonSerializer.Serialize(tour, LotsenJsonContext.Default.Tour);

   [Fact]
   public void Marks_change_neither_the_tour_nor_its_update_time()
   {
      var marked = Stored.WithMarks([new ReviewMark("src/a.cs", null)]).Value!;

      Assert.Same(Stored.Tour, marked.Tour);
      Assert.Equal(["src/a.cs"], marked.Document.ReviewMarks.Select(mark => mark.Path));
      Assert.Equal("reviewMarks[1].path", Stored.WithMarks([new ReviewMark("src/a.cs", null), new ReviewMark("src/a.cs", null)]).Fault?.Path.ToString());
   }
}
