using Lotsen.Git;
using Lotsen.Operations;
using Lotsen.Pinning;
using Lotsen.Tours;
using static Lotsen.Tests.IterationRows;

namespace Lotsen.Tests;

/// <summary>Covers how <see cref="TourRemap.Map"/> decides each item's place on a new pin from hand-built git facts, and which facts it asks for when the set lacks them.</summary>
[Trait("Category", "Remap")]
public sealed class RemapCoreTests
{
   /// <summary>The new head replaces line 3 with three lines, so the lines below it shift by two.</summary>
   [Fact]
   public void Passage_below_a_hunk_shifts_by_the_lines_the_hunk_adds_and_takes_its_text_at_the_new_head()
   {
      var related = Passage("a.txt", 6, 8, PassageRole.Related);
      var facts = Facts("a.txt").Changed("a.txt", "line 1\nline 2\nx\ny\nz\n" + Text(4, 10), new LineHunk(3, 1, 3, 3)).Build();

      var result = Decided(TourRemap.Map(TourOf([Stop("s1", StopKind.Context, related)]), Pin, NewPin, facts));

      Assert.Equal(related with { StartLine = 8, EndLine = 10, Commit = NewHead }, Moved(Assert.Single(result.Stops).Passages[0]));
      Assert.Empty(result.StaleStops);
   }

   /// <summary>The passage holds lines 4 to 6, and the new head inserts one line after the given line.</summary>
   [Theory]
   [InlineData(3, 5)]
   [InlineData(4, null)]
   [InlineData(5, null)]
   [InlineData(6, 4)]
   public void Insertion_moves_a_passage_unless_it_falls_between_the_passages_lines(int after, int? newStart)
   {
      var passage = Passage("a.txt", 4, 6, PassageRole.Related);
      var inserted = string.Concat(Text(1, 10).Split('\n')[..after].Select(line => line + "\n")) + "inserted\n" + string.Concat(Text(1, 10).Split('\n')[after..^1].Select(line => line + "\n"));
      var facts = Facts("a.txt").Changed("a.txt", inserted, new LineHunk(after, 0, after + 1, 1)).Build();

      var outcome = Assert.Single(Decided(TourRemap.Map(TourOf([Stop("s1", StopKind.Context, passage)]), Pin, NewPin, facts)).Stops).Passages[0].Outcome;

      if (newStart is { } start)
      {
         Assert.Equal(passage with { StartLine = start, EndLine = start + 2, Commit = NewHead }, Assert.IsType<Remapped<StopPassage>.Moved>(outcome).To);
      }
      else
      {
         Assert.Equal(StaleReason.LinesChanged, StaleOf(outcome));
      }
   }

   /// <summary>Each row names the facts of a.txt at the new head, which <see cref="TourRemap"/> turns into the stale reason of a passage there.</summary>
   [Theory]
   [InlineData("deleted", "FileGone")]
   [InlineData("unreadable hunks", "Unreadable")]
   [InlineData("unreadable new text", "Unreadable")]
   [InlineData("hunk on the lines", "LinesChanged")]
   [InlineData("same lines with other text", "LinesChanged")]
   [InlineData(@"rename to moved\a.txt", "PathRefused")]
   public void Passage_that_cannot_move_is_stale_for_the_reason_that_its_facts_give(string row, string reason)
   {
      var passage = Passage("a.txt", 4, 6, PassageRole.Related);
      var facts = row switch
      {
         "deleted" => new FactSetBuilder().Add(new FactKey.ChangedPaths(Head, NewHead), [new ChangedPath("a.txt", FileChangeKind.Deleted)]),
         "unreadable hunks" => Facts("a.txt").Add(new FactKey.Hunks(Head, "a.txt", NewHead, "a.txt"), null),
         "unreadable new text" => Facts("a.txt").Add(new FactKey.Hunks(Head, "a.txt", NewHead, "a.txt"), []).Add(new FactKey.Lines(NewHead, "a.txt"), null),
         "hunk on the lines" => Facts("a.txt").Changed("a.txt", Text(1, 10, (5, "five")), new LineHunk(5, 1, 5, 1)),
         "same lines with other text" => Facts("a.txt").Changed("a.txt", Text(1, 10, (5, "a link now"))),
         _ => new FactSetBuilder()
            .Add(new FactKey.ChangedPaths(Head, NewHead), [new ChangedPath(@"moved\a.txt", FileChangeKind.Renamed, "a.txt")])
            .Add(new FactKey.Hunks(Head, "a.txt", NewHead, @"moved\a.txt"), [])
            .Add(new FactKey.Lines(NewHead, @"moved\a.txt"), new FileLines(Text(1, 10))),
      };

      var stop = Assert.Single(Decided(TourRemap.Map(TourOf([Stop("s1", StopKind.Context, passage)]), Pin, NewPin, facts.Build())).StaleStops);

      Assert.Equal(Enum.Parse<StaleReason>(reason), StaleOf(Assert.Single(stop.StalePassages).Outcome));
   }

   [Fact]
   public void Passage_in_a_file_renamed_between_the_heads_follows_the_rename()
   {
      var passage = Passage("a.txt", 6, 8, PassageRole.Related);
      var facts = new FactSetBuilder()
         .Add(new FactKey.ChangedPaths(Head, NewHead), [new ChangedPath("moved/b.txt", FileChangeKind.Renamed, "a.txt")])
         .Add(new FactKey.Hunks(Head, "a.txt", NewHead, "moved/b.txt"), [new LineHunk(1, 1, 1, 1)])
         .Add(new FactKey.Lines(NewHead, "moved/b.txt"), new FileLines(Text(1, 10, (1, "first"))))
         .Build();

      var result = Decided(TourRemap.Map(TourOf([Stop("s1", StopKind.Context, passage)]), Pin, NewPin, facts));

      Assert.Equal(passage with { Path = "moved/b.txt", Commit = NewHead }, Moved(Assert.Single(result.Stops).Passages[0]));
   }

   /// <summary>The new head leaves the file of the change stop's primary passage, but the new pin's diff no longer changes it.</summary>
   [Fact]
   public void Primary_passage_of_a_change_stop_that_no_longer_touches_the_diff_is_stale_and_that_of_a_context_stop_moves()
   {
      var facts = Facts().OffDiff("a.txt").Build();

      var result = Decided(TourRemap.Map(TourOf([Stop("s1", StopKind.Inspect, Passage("a.txt", 2, 2)), Stop("s2", StopKind.Context, Passage("a.txt", 2, 2))]), Pin, NewPin, facts));

      var stale = Assert.Single(result.StaleStops);
      Assert.Equal("s1", stale.StopId);
      Assert.Equal(StaleReason.OffDiff, StaleOf(Assert.Single(stale.StalePassages).Outcome));
   }

   /// <summary>The merge base is the same on both pins, so a base-side passage stays as it is and needs no fact.</summary>
   [Fact]
   public void Base_side_passage_with_an_unchanged_merge_base_stays_and_needs_no_fact()
   {
      var passage = Passage("gone.txt", 1, 2, PassageRole.Related, FileSide.Base);

      var result = Decided(TourRemap.Map(TourOf([Stop("s1", StopKind.Context, passage)]), Pin, NewPin, FactSet.Empty));

      Assert.Equal(passage, Moved(Assert.Single(result.Stops).Passages[0]));
   }

   [Fact]
   public void Code_reference_moves_by_the_passage_rule_and_one_that_cannot_move_makes_its_stop_stale()
   {
      var stop = Stop("s1", StopKind.Context, Passage("b.txt", 1, 1, PassageRole.Related));
      stop = stop with { Content = stop.Content with { How = "See [this](ref:head/a.txt#L6-8) and [that](ref:head/a.txt#L3-3)." } };
      var facts = Facts("a.txt").Changed("a.txt", "line 1\nline 2\nx\ny\nz\n" + Text(4, 10), new LineHunk(3, 1, 3, 3)).Add(new FactKey.Lines(Head, "a.txt"), new FileLines(Text(1, 10))).Build();

      var mapped = Assert.Single(Decided(TourRemap.Map(TourOf([stop]), Pin, NewPin, facts)).Stops);

      Assert.Equal(new ReferencePlace("a.txt", 8, 10), Assert.IsType<Remapped<ReferencePlace>.Moved>(mapped.References[0].Outcome).To);
      Assert.Equal(StaleReason.LinesChanged, StaleOf(mapped.References[1].Outcome));
      Assert.True(mapped.Stale);
   }

   /// <summary>Each row puts the links of the row above in one of the fields that other rows leave without a link.</summary>
   [Theory]
   [InlineData("pinnedBy")]
   [InlineData("scenario")]
   [InlineData("checks[c1].recommendation")]
   public void Code_reference_in_any_prose_field_moves_under_that_field_and_one_that_cannot_move_makes_its_stop_stale(string field)
   {
      var stop = Stop("s1", StopKind.Context, Passage("b.txt", 1, 1, PassageRole.Related));
      stop = stop with { Content = stop.Content.WithProse(field, "See [this](ref:head/a.txt#L6-8) and [that](ref:head/a.txt#L3-3).") };
      var facts = Facts("a.txt").Changed("a.txt", "line 1\nline 2\nx\ny\nz\n" + Text(4, 10), new LineHunk(3, 1, 3, 3)).Add(new FactKey.Lines(Head, "a.txt"), new FileLines(Text(1, 10))).Build();

      var mapped = Assert.Single(Decided(TourRemap.Map(TourOf([stop]), Pin, NewPin, facts)).Stops);

      Assert.Equal([field, field], mapped.References.Select(reference => reference.Reference.Field.ToString()));
      Assert.Equal(new ReferencePlace("a.txt", 8, 10), Assert.IsType<Remapped<ReferencePlace>.Moved>(mapped.References[0].Outcome).To);
      Assert.Equal(StaleReason.LinesChanged, StaleOf(mapped.References[1].Outcome));
      Assert.True(mapped.Stale);
   }

   [Fact]
   public void Code_anchor_moves_by_the_passage_rule_and_a_file_anchor_follows_its_file_or_is_gone_with_it()
   {
      ThreadAnchor[] anchors =
      [
         new ThreadAnchor.CodeRange(FileSide.Head, "a.txt", 6, 8, Head, "line 6\nline 7\nline 8"),
         new ThreadAnchor.WholeFile(FileSide.Head, "b.txt", Head),
         new ThreadAnchor.WholeFile(FileSide.Head, "c.txt", Head),
         new ThreadAnchor.WholeFile(FileSide.Head, "sub", Head),
         new ThreadAnchor.WholeFile(FileSide.Base, "c.txt", Base),
         new ThreadAnchor.WholeTour(),
      ];
      var facts = new FactSetBuilder()
         .Add(new FactKey.ChangedPaths(Head, NewHead), [
            new ChangedPath("a.txt", FileChangeKind.Modified),
            new ChangedPath("moved/b.txt", FileChangeKind.Renamed, "b.txt"),
            new ChangedPath("c.txt", FileChangeKind.Deleted),
            new ChangedPath("sub", FileChangeKind.Modified)])
         .Changed("a.txt", "line 1\nline 2\nx\ny\nz\n" + Text(4, 10), new LineHunk(3, 1, 3, 3))
         .Add(new FactKey.Blob(NewHead, "moved/b.txt"), new FileBlob(NewHead, "moved/b.txt", new string('1', 40), 10, false))
         .Add(new FactKey.Blob(NewHead, "sub"), null)
         .Build();
      var tour = TourOf([], [.. anchors.Select((anchor, i) => ThreadFixtures.Note($"t{i}", anchor, at: Now))]);

      var result = Decided(TourRemap.Map(tour, Pin, NewPin, facts));

      Assert.Equal(["t0", "t1", "t2", "t3", "t4"], result.Anchors.Select(anchor => anchor.ThreadId));
      Assert.Equal(new ThreadAnchor.CodeRange(FileSide.Head, "a.txt", 8, 10, NewHead, "line 6\nline 7\nline 8"), MovedAnchor(result.Anchors[0]));
      Assert.Equal(new ThreadAnchor.WholeFile(FileSide.Head, "moved/b.txt", NewHead), MovedAnchor(result.Anchors[1]));
      Assert.Equal(StaleReason.FileGone, StaleOf(result.Anchors[2].Outcome));
      Assert.Equal(StaleReason.FileGone, StaleOf(result.Anchors[3].Outcome));
      Assert.Equal(anchors[4], MovedAnchor(result.Anchors[4]));
   }

   /// <summary>The plan marks a.txt and held.txt, and the new head renames a.txt to the row's path.</summary>
   [Theory]
   [InlineData("moved/b.txt", "moved/b.txt")]
   [InlineData("held.txt", "a.txt")]
   [InlineData(@"moved\b.txt", "a.txt")]
   public void File_mark_follows_a_rename_only_to_a_path_that_no_other_mark_holds_and_a_stored_plan_accepts(string newPath, string expected)
   {
      FileMark[] marks = [new("a.txt", FileMarkKind.Generated, "A tool writes it."), new("held.txt", FileMarkKind.Generated, "A tool writes it.")];
      var facts = new FactSetBuilder().Add(new FactKey.ChangedPaths(Head, NewHead), [new ChangedPath(newPath, FileChangeKind.Renamed, "a.txt")]).Build();

      var result = Decided(TourRemap.Map(TourOf([], marks: marks), Pin, NewPin, facts));

      Assert.Equal([expected, "held.txt"], result.FileMarks.Select(mark => mark.Path));
   }

   /// <summary>
   /// The first pass knows no fact, so it asks only for the changed paths between the heads. The second asks for the hunks and the new text of the one changed file that a passage names, and
   /// for the new pin's diff of the inspect stop's file, which stays. It asks nothing about the changed file that nothing names.
   /// </summary>
   [Fact]
   public void Pass_asks_for_every_fact_it_can_reach_and_only_for_the_files_that_the_tour_names()
   {
      var tour = TourOf([Stop("s1", StopKind.Inspect, Passage("a.txt", 2, 2)), Stop("s2", StopKind.Context, Passage("b.txt", 4, 6, PassageRole.Related))]);

      var first = Missing(TourRemap.Map(tour, Pin, NewPin, FactSet.Empty));
      var second = Missing(TourRemap.Map(tour, Pin, NewPin, new FactSetBuilder().Add(new FactKey.ChangedPaths(Head, NewHead), [new ChangedPath("b.txt", FileChangeKind.Modified), new ChangedPath("other.txt", FileChangeKind.Modified)]).Build()));

      Assert.Equal(new FactKey[] { new FactKey.ChangedPaths(Head, NewHead) }, first.ToArray());
      Assert.Equal(
         new HashSet<FactKey> { new FactKey.Hunks(Head, "b.txt", NewHead, "b.txt"), new FactKey.PinDiff(Base, NewHead, FileSide.Head, "a.txt") },
         [.. second]);
   }

   [Fact]
   public void Pending_stop_and_overview_without_passages_or_code_references_are_never_stale_and_earlier_versions_are_not_mapped()
   {
      var earlier = new StopVersion("Overview", StopKind.Inspect, new StopContent("Old.", [Passage("a.txt", 2, 2)]), Now);
      var overview = new Stop.Written("s0", "Overview", StopKind.Overview, "c1", new StopContent("The change.", []), 2, [earlier]);
      var pending = new Stop.Pending("s1", "Pending", StopKind.Inspect, "c1", new AnchorHint("a.txt", 2));

      var result = Decided(TourRemap.Map(TourOf([overview, pending]), Pin, NewPin, Facts("a.txt").Build()));

      var stop = Assert.Single(result.Stops);
      Assert.Equal(("s0", 2), (stop.StopId, stop.Version));
      Assert.Empty(result.StaleStops);
   }

   private static StopPassage Moved(PassageRemap passage) => Assert.IsType<Remapped<StopPassage>.Moved>(passage.Outcome).To;

   private static ThreadAnchor MovedAnchor(AnchorRemap anchor) => Assert.IsType<Remapped<ThreadAnchor>.Moved>(anchor.Outcome).To;

   private static StaleReason StaleOf<T>(Remapped<T> outcome) => Assert.IsType<Remapped<T>.Stale>(outcome).Reason;
}
