using Lotsen.Git;
using Lotsen.Operations;
using Lotsen.Pinning;
using Lotsen.Tours;
using static Lotsen.Tests.IterationRows;

namespace Lotsen.Tests;

/// <summary>Covers what <see cref="Repin.Map"/> makes of a tour on a new pin: the moved content, the stale markers, the file marks, the anchors' fact and the decision, from hand-built git facts.</summary>
/// <remarks>The new head inserts one line at the top of a.txt, so every range there moves down by one line, and replaces line 9 of b.txt.</remarks>
[Trait("Category", "Remap")]
public sealed class RepinTests
{
   private static readonly FactSet Shifted = Facts("a.txt", "b.txt")
      .Changed("a.txt", "top\n" + Text(1, 10), new LineHunk(0, 0, 1, 1))
      .Changed("b.txt", Text(1, 10, (9, "nine")), new LineHunk(9, 1, 9, 1))
      .Add(new FactKey.Lines(Head, "a.txt"), new FileLines(Text(1, 10)))
      .InDiff("a.txt")
      .InDiff("b.txt")
      .Build();

   [Fact]
   public void Stop_whose_passages_and_links_move_takes_their_new_places_at_its_version_and_keeps_a_code_span_as_written()
   {
      var stop = Stop("s1", StopKind.Inspect, Passage("a.txt", 3, 3));
      stop = stop with { Content = stop.Content with { How = "The [line](ref:head/a.txt#L3-3) and <ref:head/a.txt#L1-1> stay, but `ref:head/a.txt#L3-3` is code." } };

      var repin = Map(TourOf([stop]));

      var moved = Assert.IsType<Stop.Written>(Assert.Single(repin.Tour.Plan!.Stops));
      Assert.Equal((1, (int?)null), (moved.Version, moved.StaleOn));
      Assert.Equal(Passage("a.txt", 3, 3) with { StartLine = 4, EndLine = 4, Commit = NewHead }, Assert.Single(moved.Content.Passages));
      Assert.Equal("The [line](ref:head/a.txt#L4-4) and <ref:head/a.txt#L2-2> stay, but `ref:head/a.txt#L3-3` is code.", moved.Content.How);
      Assert.Empty(repin.StaleStops);
      Assert.Null(repin.Decision);
   }

   /// <summary>Each row puts the link in one of the fields that other rows leave without a link.</summary>
   [Theory]
   [InlineData("pinnedBy")]
   [InlineData("scenario")]
   [InlineData("checks[c1].recommendation")]
   public void Moved_link_in_any_prose_field_is_written_at_its_new_place_in_that_field(string field)
   {
      var stop = Stop("s1", StopKind.Inspect, Passage("a.txt", 3, 3));
      stop = stop with { Content = stop.Content.WithProse(field, "See [the line](ref:head/a.txt#L3-3).") };

      var repin = Map(TourOf([stop]));

      var moved = Assert.IsType<Stop.Written>(Assert.Single(repin.Tour.Plan!.Stops));
      Assert.Null(moved.StaleOn);
      Assert.Equal("See [the line](ref:head/a.txt#L4-4).", StopProse.Read(moved.Content, field));
   }

   /// <summary>The stored destination holds a backslash escape, so the prose does not hold the link in the form that the remap read, and the new place cannot be written.</summary>
   [Fact]
   public void Stop_whose_moved_link_holds_a_backslash_escape_is_stale_with_its_content_as_written()
   {
      var stop = Stop("s1", StopKind.Inspect, Passage("a.txt", 3, 3));
      stop = stop with { Content = stop.Content with { How = @"The [line](ref:head/a\.txt#L3-3) moves." } };

      var repin = Map(TourOf([stop]));

      var stale = Assert.IsType<Stop.Written>(Assert.Single(repin.Tour.Plan!.Stops));
      Assert.Equal((2, stop.Content), (stale.StaleOn, stale.Content));
      Assert.Equal(["s1"], repin.StaleStops);
   }

   /// <summary>The stop on b.txt is stale on the new pin, and the stop that an earlier update left stale keeps its marker and its content, and counts as stale again.</summary>
   [Fact]
   public void Stale_stop_gets_the_new_iterations_marker_and_a_stop_stale_already_stays_as_it_is_and_both_count()
   {
      var stale = Stop("s1", StopKind.Inspect, Passage("b.txt", 9, 9));
      var earlier = Stop("s2", StopKind.Inspect, Passage("a.txt", 3, 3)) with { StaleOn = 1 };
      var moving = Stop("s3", StopKind.Inspect, Passage("a.txt", 5, 5));

      var repin = Map(TourOf([stale, earlier, moving]));

      var stops = repin.Tour.Plan!.Stops.Cast<Stop.Written>().ToList();
      Assert.Equal((2, stale.Content), (stops[0].StaleOn, stops[0].Content));
      Assert.Same(earlier, stops[1]);
      Assert.Null(stops[2].StaleOn);
      Assert.Equal(["s1", "s2"], repin.StaleStops);
      Assert.Equal(["s1", "s2"], repin.Decision!.StopIds);
      Assert.Equal(2, repin.Decision.Iteration);
   }

   [Fact]
   public void Repin_takes_the_new_pin_clears_the_available_iteration_and_keeps_the_plan_approval()
   {
      var tour = TourOf([Stop("s1", StopKind.Inspect, Passage("a.txt", 3, 3))]) with { AvailableIteration = new AvailableIteration(NewPin, [], Now), PlanApproval = PlanApproval.Given };

      var repin = Map(tour);

      Assert.Equal((NewPin, null, PlanApproval.Given), (repin.Tour.Pin, repin.Tour.AvailableIteration, repin.Tour.PlanApproval));
   }

   /// <summary>The new head renames c.txt to d.txt, and d.txt holds a mark already, so only the mark on e.txt follows its rename.</summary>
   [Fact]
   public void File_mark_follows_a_rename_unless_another_mark_holds_its_new_path()
   {
      FileMark[] marks = [new("c.txt", FileMarkKind.Generated, "A tool writes it."), new("d.txt", FileMarkKind.Mechanical, "A rename."), new("e.txt", FileMarkKind.Generated, "A tool writes it.")];
      var facts = new FactSetBuilder()
         .Add(new FactKey.ChangedPaths(Head, NewHead), [new ChangedPath("d.txt", FileChangeKind.Renamed, "c.txt"), new ChangedPath("f.txt", FileChangeKind.Renamed, "e.txt")])
         .Build();

      var repin = Assert.IsType<Decision<Repin>.Decided>(Repin.Map(TourOf([], marks: marks), Pin, NewPin, facts)).Value;

      Assert.Equal(["c.txt", "d.txt", "f.txt"], repin.Tour.Plan!.FileMarks.Select(mark => mark.Path));
   }

   /// <summary>
   /// The anchor on a.txt moves with the insertion, and the one on line 9 of b.txt is stale. Settling the anchors' fact outdates the open thread on the stale anchor and keeps the stop text
   /// thread as it was.
   /// </summary>
   [Fact]
   public void Anchors_fact_moves_a_code_anchor_and_outdates_the_open_thread_whose_anchor_is_stale()
   {
      var stop = Stop("s1", StopKind.Inspect, Passage("a.txt", 3, 3));
      stop = stop with { Content = stop.Content with { How = "See <ref:head/a.txt#L3-3>." } };
      QuestionThread[] threads =
      [
         ThreadFixtures.Asked("t1", new ThreadAnchor.CodeRange(FileSide.Head, "a.txt", 3, 4, Head, "line 3\nline 4"), request: RequestState.Done, at: Now),
         ThreadFixtures.Asked("t2", new ThreadAnchor.CodeRange(FileSide.Head, "b.txt", 9, 9, Head, "line 9"), request: RequestState.Done, at: Now),
         ThreadFixtures.Asked("t3", new ThreadAnchor.StopText("s1", "how", "a.txt#L3-3", 1), request: RequestState.Done, at: Now),
      ];

      var repin = Map(TourOf([stop], threads));
      var settled = TourThreads.Settle(repin.Tour, repin.Anchors).Next;

      Assert.Equal(["t1", "t2"], repin.Anchors.Anchors.Select(anchor => anchor.ThreadId));
      var moved = settled.Threads.Single(thread => thread.Id == "t1");
      Assert.Equal((QuestionThreadState.Open, (ThreadAnchor)new ThreadAnchor.CodeRange(FileSide.Head, "a.txt", 4, 5, NewHead, "line 3\nline 4")), (moved.State, moved.Anchor));
      Assert.Equal(QuestionThreadState.Outdated, settled.Threads.Single(thread => thread.Id == "t2").State);
      Assert.Equal(threads[2].Anchor, settled.Threads.Single(thread => thread.Id == "t3").Anchor);
      Assert.Equal(QuestionThreadState.Open, settled.Threads.Single(thread => thread.Id == "t3").State);
   }

   private static Repin Map(Tour tour) => Assert.IsType<Decision<Repin>.Decided>(Repin.Map(tour, Pin, NewPin, Shifted)).Value;
}
