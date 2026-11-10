using Lotsen.Git;
using Lotsen.Hosting;
using Lotsen.Operations;
using Lotsen.Pinning;
using Lotsen.Tours;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lotsen.Tests;

/// <summary>Covers how <see cref="TourRemap"/> maps a tour's passages, code references and thread anchors onto a new iteration, with the git facts that a scope reads from fixture commits.</summary>
[Trait("Category", "Remap")]
public sealed class TourRemapTests : IDisposable
{
   private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
   private static readonly PullRequestTarget PullRequest = new("test", new Uri("https://platform.test/shop/pull/42"));

   private readonly GitFixture _fixture = new();

   /// <summary>The new head replaces line 3 with three lines, so lines below it shift by two, and line 2 still differs from the merge base.</summary>
   [Fact]
   public async Task Passage_below_a_hunk_shifts_by_the_lines_the_hunk_adds_and_reads_its_text_at_the_new_head()
   {
      var mergeBase = _fixture.Commit("Base", ("a.txt", Lines(1, 10)));
      var head = _fixture.Commit("Head", ("a.txt", Lines(1, 10, ("line 2", "two"))));
      var newHead = _fixture.Commit("New head", ("a.txt", Lines(1, 10, ("line 2", "two"), ("line 3", "x\ny\nz"))));
      var primary = Passage(FileSide.Head, "a.txt", 2, 2, PassageRole.Primary, head, "two");
      var related = Passage(FileSide.Head, "a.txt", 6, 8, PassageRole.Related, head, "line 6\nline 7\nline 8");
      var tour = TourOf(Pin(mergeBase, head), [ReadyStop("s1", StopKind.Inspect, primary, related)]);

      var result = await MapAsync(tour, Pin(mergeBase, newHead));

      var stop = Assert.Single(result.Stops);
      Assert.False(stop.Stale);
      Assert.Empty(result.StaleStops);
      Assert.Equal(primary with { Commit = newHead }, Moved(stop.Passages[0]));
      Assert.Equal(related with { StartLine = 8, EndLine = 10, Commit = newHead }, Moved(stop.Passages[1]));
   }

   [Fact]
   public async Task Passage_whose_own_lines_changed_is_stale_with_its_old_place_and_makes_its_stop_stale()
   {
      var mergeBase = _fixture.Commit("Base", ("a.txt", Lines(1, 10)));
      var head = _fixture.Commit("Head", ("a.txt", Lines(1, 10, ("line 2", "two"))));
      var newHead = _fixture.Commit("New head", ("a.txt", Lines(1, 10, ("line 2", "two"), ("line 7", "seven"))));
      var related = Passage(FileSide.Head, "a.txt", 6, 8, PassageRole.Related, head, "line 6\nline 7\nline 8");
      var tour = TourOf(Pin(mergeBase, head), [ReadyStop("s1", StopKind.Context, related)]);

      var result = await MapAsync(tour, Pin(mergeBase, newHead));

      var stop = Assert.Single(result.StaleStops);
      var passage = Assert.Single(stop.StalePassages);
      Assert.Equal(related, passage.Passage);
      Assert.Equal(StaleReason.LinesChanged, StaleOf(passage.Outcome));
   }

   [Fact]
   public async Task Passage_in_a_file_deleted_at_the_new_head_is_stale()
   {
      var mergeBase = _fixture.Commit("Base", ("a.txt", Lines(1, 10)), ("b.txt", Lines(1, 4)));
      var head = _fixture.Commit("Head", ("a.txt", Lines(1, 10, ("line 2", "two"))));
      var newHead = _fixture.Commit("New head", ("b.txt", null));
      var primary = Passage(FileSide.Head, "a.txt", 2, 2, PassageRole.Primary, head, "two");
      var related = Passage(FileSide.Head, "b.txt", 1, 2, PassageRole.Related, head, "line 1\nline 2");
      var tour = TourOf(Pin(mergeBase, head), [ReadyStop("s1", StopKind.Inspect, primary, related)]);

      var result = await MapAsync(tour, Pin(mergeBase, newHead));

      var stop = Assert.Single(result.StaleStops);
      var passage = Assert.Single(stop.StalePassages);
      Assert.Equal(1, passage.Index);
      Assert.Equal(StaleReason.FileGone, StaleOf(passage.Outcome));
      Assert.Equal(primary with { Commit = newHead }, Moved(stop.Passages[0]));
   }

   /// <summary>The passage holds lines 4 to 6, and the new head inserts one line after the given line.</summary>
   [Theory]
   [InlineData(0, 5)]
   [InlineData(3, 5)]
   [InlineData(4, null)]
   [InlineData(5, null)]
   [InlineData(6, 4)]
   [InlineData(9, 4)]
   public async Task Insertion_moves_a_passage_unless_it_falls_between_the_passages_lines(int after, int? newStart)
   {
      var mergeBase = _fixture.Commit("Base", ("a.txt", Lines(1, 10)));
      var head = _fixture.Commit("Head", ("z.txt", "changed\n"));
      var inserted = string.Concat(Lines(1, 10).Split('\n')[..after].Select(line => line + "\n")) + "inserted\n" + string.Concat(Lines(1, 10).Split('\n')[after..^1].Select(line => line + "\n"));
      var newHead = _fixture.Commit("New head", ("a.txt", inserted));
      var passage = Passage(FileSide.Head, "a.txt", 4, 6, PassageRole.Primary, head, "line 4\nline 5\nline 6");

      var result = await MapAsync(TourOf(Pin(mergeBase, head), [ReadyStop("s1", StopKind.Context, passage)]), Pin(mergeBase, newHead));

      var outcome = Assert.Single(result.Stops).Passages[0];
      if (newStart is { } start)
      {
         Assert.Equal(passage with { StartLine = start, EndLine = start + 2, Commit = newHead }, Moved(outcome));
      }
      else
      {
         Assert.Equal(StaleReason.LinesChanged, StaleOf(outcome.Outcome));
      }
   }

   /// <summary>The passage holds lines 4 to 6, and the new head changes one line.</summary>
   [Theory]
   [InlineData(3, true)]
   [InlineData(4, false)]
   [InlineData(6, false)]
   [InlineData(7, true)]
   public async Task Change_of_a_line_next_to_a_passage_moves_it_and_a_change_of_its_own_line_makes_it_stale(int changed, bool moves)
   {
      var mergeBase = _fixture.Commit("Base", ("a.txt", Lines(1, 10)));
      var head = _fixture.Commit("Head", ("z.txt", "changed\n"));
      var newHead = _fixture.Commit("New head", ("a.txt", Lines(1, 10, ($"line {changed}", "edited"))));
      var passage = Passage(FileSide.Head, "a.txt", 4, 6, PassageRole.Primary, head, "line 4\nline 5\nline 6");

      var result = await MapAsync(TourOf(Pin(mergeBase, head), [ReadyStop("s1", StopKind.Context, passage)]), Pin(mergeBase, newHead));

      Assert.Equal(!moves, Assert.Single(result.Stops).Stale);
   }

   [Fact]
   public async Task Passage_in_a_file_renamed_between_the_heads_follows_the_rename()
   {
      var mergeBase = _fixture.Commit("Base", ("a.txt", Lines(1, 10)));
      var head = _fixture.Commit("Head", ("z.txt", "changed\n"));
      var newHead = _fixture.Commit("New head", ("a.txt", null), ("moved/b.txt", Lines(1, 10, ("line 1", "first"))));
      var passage = Passage(FileSide.Head, "a.txt", 6, 8, PassageRole.Related, head, "line 6\nline 7\nline 8");
      var other = Passage(FileSide.Head, "z.txt", 1, 1, PassageRole.Primary, head, "changed");

      var result = await MapAsync(TourOf(Pin(mergeBase, head), [ReadyStop("s1", StopKind.Context, other, passage)]), Pin(mergeBase, newHead));

      Assert.Equal(passage with { Path = "moved/b.txt", Commit = newHead }, Moved(Assert.Single(result.Stops).Passages[1]));
   }

   /// <summary>The new head renames a.txt to the given path. Git allows each of these names on Linux, and a stored stop refuses the last three.</summary>
   [Theory]
   [InlineData("moved/deep/b.txt", true)]
   [InlineData("1:b.txt", true)]
   [InlineData(@"moved\b.txt", false)]
   [InlineData("c:b.txt", false)]
   [InlineData("   ", false)]
   public async Task Passage_and_code_reference_follow_a_rename_only_to_a_path_that_a_stored_stop_holds(string newPath, bool moves)
   {
      SkipOnWindows();
      var mergeBase = _fixture.Commit("Base", ("a.txt", Lines(1, 10)));
      var head = _fixture.Commit("Head", ("z.txt", "changed\n"));
      var newHead = _fixture.Commit("New head", ("a.txt", null), (newPath, Lines(1, 10)));
      var passage = Passage(FileSide.Head, "a.txt", 6, 8, PassageRole.Related, head, "line 6\nline 7\nline 8");
      var other = Passage(FileSide.Head, "z.txt", 1, 1, PassageRole.Primary, head, "changed");
      var ready = ReadyStop("s1", StopKind.Context, other, passage);
      var stop = ready with { Content = ready.Content with { How = "See [this](ref:head/a.txt#L2-3)." } };

      var result = await MapAsync(TourOf(Pin(mergeBase, head), [stop]), Pin(mergeBase, newHead));

      var mapped = Assert.Single(result.Stops);
      Assert.Equal(!moves, mapped.Stale);
      Assert.Equal(other with { Commit = newHead }, Moved(mapped.Passages[0]));
      if (moves)
      {
         Assert.Equal(passage with { Path = newPath, Commit = newHead }, Moved(mapped.Passages[1]));
         Assert.Equal(new ReferencePlace(newPath, 2, 3), Assert.IsType<Remapped<ReferencePlace>.Moved>(Assert.Single(mapped.References).Outcome).To);
      }
      else
      {
         Assert.Equal(StaleReason.PathRefused, StaleOf(mapped.Passages[1].Outcome));
         Assert.Equal(StaleReason.PathRefused, StaleOf(Assert.Single(mapped.References).Outcome));
      }
   }

   /// <summary>
   /// The new head renames a.txt to the given path. The plan marks a.txt and held.txt, which is in no commit, so a rename to held.txt would give the plan two marks on one path. Git allows each
   /// of these names on Linux, and a stored plan refuses the last three.
   /// </summary>
   [Theory]
   [InlineData("moved/deep/b.txt", "moved/deep/b.txt")]
   [InlineData("ab:c.txt", "ab:c.txt")]
   [InlineData("held.txt", "a.txt")]
   [InlineData(@"moved\b.txt", "a.txt")]
   [InlineData("c:b.txt", "a.txt")]
   [InlineData("   ", "a.txt")]
   public async Task File_mark_follows_a_rename_only_to_a_path_that_no_other_mark_holds_and_a_stored_plan_accepts(string newPath, string expected)
   {
      SkipOnWindows();
      var mergeBase = _fixture.Commit("Base", ("a.txt", Lines(1, 10)));
      var head = _fixture.Commit("Head", ("z.txt", "changed\n"));
      var newHead = _fixture.Commit("New head", ("a.txt", null), (newPath, Lines(1, 10)));
      FileMark[] marks = [new("a.txt", FileMarkKind.Generated, "A tool writes it."), new("held.txt", FileMarkKind.Generated, "A tool writes it.")];
      var tour = TourOf(Pin(mergeBase, head), []);
      tour = tour with { Plan = tour.Plan! with { FileMarks = marks } };

      var result = await MapAsync(tour, Pin(mergeBase, newHead));

      Assert.Equal([expected, "held.txt"], result.FileMarks.Select(mark => mark.Path));
   }

   /// <summary>The new head renames a.txt to the given path. Git allows each of these names on Linux, and a stored anchor refuses the last three.</summary>
   [Theory]
   [InlineData("moved/deep/b.txt", true)]
   [InlineData("1:b.txt", true)]
   [InlineData(@"moved\b.txt", false)]
   [InlineData("c:b.txt", false)]
   [InlineData("   ", false)]
   public async Task Code_anchor_and_file_anchor_follow_a_rename_only_to_a_path_that_a_stored_anchor_holds(string newPath, bool moves)
   {
      SkipOnWindows();
      var mergeBase = _fixture.Commit("Base", ("a.txt", Lines(1, 10)));
      var head = _fixture.Commit("Head", ("z.txt", "changed\n"));
      var newHead = _fixture.Commit("New head", ("a.txt", null), (newPath, Lines(1, 10)));
      ThreadAnchor[] anchors = [new ThreadAnchor.CodeRange(FileSide.Head, "a.txt", 6, 8, head, "line 6\nline 7\nline 8"), new ThreadAnchor.WholeFile(FileSide.Head, "a.txt", head)];
      var tour = TourOf(Pin(mergeBase, head), [], [.. anchors.Select((anchor, i) => NoteThread($"t{i}", anchor))]);

      var result = await MapAsync(tour, Pin(mergeBase, newHead));

      if (moves)
      {
         Assert.Equal(new ThreadAnchor.CodeRange(FileSide.Head, newPath, 6, 8, newHead, "line 6\nline 7\nline 8"), MovedAnchor(result.Anchors[0]));
         Assert.Equal(new ThreadAnchor.WholeFile(FileSide.Head, newPath, newHead), MovedAnchor(result.Anchors[1]));
      }
      else
      {
         Assert.Equal([StaleReason.PathRefused, StaleReason.PathRefused], result.Anchors.Select(anchor => StaleOf(anchor.Outcome)));
      }
   }

   /// <summary>Git's patch shows a change of a file's type as a deletion and an addition, without a hunk between the two blobs.</summary>
   [Fact]
   public async Task Passage_in_a_file_that_becomes_a_symbolic_link_is_stale()
   {
      var mergeBase = _fixture.Commit("Base", ("a.txt", Lines(1, 10)));
      var head = _fixture.Commit("Head", ("z.txt", "changed\n"));
      var target = Path.Combine(_fixture.ScratchPath, "link-target");
      File.WriteAllText(target, "elsewhere.txt");
      var blob = _fixture.Git(_fixture.OriginPath, "hash-object", "-w", target).Trim();
      _fixture.Git(_fixture.OriginPath, "update-index", "--cacheinfo", $"120000,{blob},a.txt");
      _fixture.Git(_fixture.OriginPath, "commit", "--quiet", "--message", "Link");
      var newHead = CommitId.Parse(_fixture.Git(_fixture.OriginPath, "rev-parse", "HEAD").Trim());
      var passage = Passage(FileSide.Head, "a.txt", 1, 1, PassageRole.Primary, head, "line 1");

      var result = await MapAsync(TourOf(Pin(mergeBase, head), [ReadyStop("s1", StopKind.Context, passage)]), Pin(mergeBase, newHead));

      Assert.Equal(StaleReason.LinesChanged, StaleOf(Assert.Single(result.Stops).Passages[0].Outcome));
   }

   /// <summary>Main adds two lines at the top of a.txt, and the source merges main, which moves the merge base to main's commit.</summary>
   [Fact]
   public async Task Base_side_passage_maps_through_the_diff_between_the_merge_bases()
   {
      var mergeBase = _fixture.Commit("Base", ("a.txt", Lines(1, 10)), ("b.txt", "one\n"));
      _fixture.Checkout("feature", create: true);
      var head = _fixture.Commit("Head", ("b.txt", "two\n"));
      _fixture.Checkout("main");
      var newMergeBase = _fixture.Commit("Main moves on", ("a.txt", "top 1\ntop 2\n" + Lines(1, 10)));
      _fixture.Checkout("feature");
      var newHead = Merge("main");
      var passage = Passage(FileSide.Base, "a.txt", 6, 8, PassageRole.Related, mergeBase, "line 6\nline 7\nline 8");
      var primary = Passage(FileSide.Head, "b.txt", 1, 1, PassageRole.Primary, head, "two");

      var result = await MapAsync(TourOf(Pin(mergeBase, head), [ReadyStop("s1", StopKind.Inspect, primary, passage)]), Pin(newMergeBase, newHead));

      var stop = Assert.Single(result.Stops);
      Assert.False(stop.Stale);
      Assert.Equal(passage with { StartLine = 8, EndLine = 10, Commit = newMergeBase }, Moved(stop.Passages[1]));
   }

   /// <summary>The new head inserts a line at the top of a.txt and pads its end, so a.txt has the given size there.</summary>
   [Theory]
   [InlineData(FileOperations.MaxFileSize - 1, true)]
   [InlineData(FileOperations.MaxFileSize, true)]
   [InlineData(FileOperations.MaxFileSize + 1, false)]
   public async Task Passage_moves_while_its_file_at_the_new_head_is_no_larger_than_the_readable_size(long newSize, bool moves)
   {
      var mergeBase = _fixture.Commit("Base", ("a.txt", Lines(1, 10)));
      var head = _fixture.Commit("Head", ("z.txt", "changed\n"));
      var top = "top\n" + Lines(1, 10);
      var newHead = _fixture.Commit("New head", ("a.txt", top + new string('x', (int)newSize - top.Length - 1) + "\n"));
      var passage = Passage(FileSide.Head, "a.txt", 4, 6, PassageRole.Primary, head, "line 4\nline 5\nline 6");

      var result = await MapAsync(TourOf(Pin(mergeBase, head), [ReadyStop("s1", StopKind.Context, passage)]), Pin(mergeBase, newHead));

      var outcome = Assert.Single(result.Stops).Passages[0];
      if (moves)
      {
         Assert.Equal(passage with { StartLine = 5, EndLine = 7, Commit = newHead }, Moved(outcome));
      }
      else
      {
         Assert.Equal(StaleReason.Unreadable, StaleOf(outcome.Outcome));
      }
   }

   /// <summary>The new head adds a NUL byte to a.txt below the passage, which makes git treat the file as binary.</summary>
   [Fact]
   public async Task Passage_in_a_file_that_becomes_binary_is_stale()
   {
      var mergeBase = _fixture.Commit("Base", ("a.txt", Lines(1, 10)));
      var head = _fixture.Commit("Head", ("z.txt", "changed\n"));
      var newHead = _fixture.Commit("New head", ("a.txt", Lines(1, 10, ("line 9", "nine\0"))));
      var passage = Passage(FileSide.Head, "a.txt", 4, 6, PassageRole.Primary, head, "line 4\nline 5\nline 6");

      var result = await MapAsync(TourOf(Pin(mergeBase, head), [ReadyStop("s1", StopKind.Context, passage)]), Pin(mergeBase, newHead));

      Assert.Equal(StaleReason.Unreadable, StaleOf(Assert.Single(result.Stops).Passages[0].Outcome));
   }

   /// <summary>Main changes other.txt, which nothing in the tour names, and the source merges main, so both the heads and the merge bases differ in it.</summary>
   [Fact]
   public async Task Remap_downloads_no_content_of_a_changed_file_that_nothing_in_the_tour_names()
   {
      var mergeBase = _fixture.Commit("Base", ("a.txt", Lines(1, 10)), ("b.txt", "one\n"), ("other.txt", "before\n"));
      _fixture.Checkout("feature", create: true);
      var head = _fixture.Commit("Head", ("b.txt", "two\n"));
      _fixture.Checkout("main");
      var newMergeBase = _fixture.Commit("Main moves on", ("a.txt", "top\n" + Lines(1, 10)), ("other.txt", "after\n"));
      _fixture.Checkout("feature");
      var newHead = Merge("main");
      var passage = Passage(FileSide.Base, "a.txt", 6, 8, PassageRole.Related, mergeBase, "line 6\nline 7\nline 8");
      var primary = Passage(FileSide.Head, "b.txt", 1, 1, PassageRole.Primary, head, "two");
      var tour = TourOf(Pin(mergeBase, head), [ReadyStop("s1", StopKind.Inspect, primary, passage)]);
      var repository = await SyncAsync();

      var result = await MapAsync(tour, Pin(newMergeBase, newHead));

      Assert.Equal(passage with { StartLine = 7, EndLine = 9, Commit = newMergeBase }, Moved(Assert.Single(result.Stops).Passages[1]));
      // With --missing=print, rev-list prints each object that the clone lacks with a leading "?" and downloads nothing.
      var missing = _fixture.Git(repository.Path, "rev-list", "--objects", "--missing=print", mergeBase.Value, newHead.Value).Split('\n').Where(line => line.StartsWith('?')).ToList();
      Assert.Contains("?" + BlobOf(mergeBase, "other.txt"), missing);
      Assert.Contains("?" + BlobOf(newMergeBase, "other.txt"), missing);
      Assert.DoesNotContain("?" + BlobOf(newMergeBase, "a.txt"), missing);
   }

   /// <summary>The merge base is a commit that the clone lacks, so any git call about it would fail.</summary>
   [Fact]
   public async Task Base_side_passage_with_an_unchanged_merge_base_is_left_as_it_was()
   {
      var mergeBase = CommitId.Parse(new string('b', 40));
      var head = _fixture.Commit("Head", ("a.txt", Lines(1, 10)));
      var newHead = _fixture.Commit("New head", ("a.txt", "top\n" + Lines(1, 10)));
      var basePassage = Passage(FileSide.Base, "gone.txt", 1, 2, PassageRole.Related, mergeBase, "base text");
      var headPassage = Passage(FileSide.Head, "a.txt", 1, 2, PassageRole.Primary, head, "line 1\nline 2");

      var result = await MapAsync(TourOf(Pin(mergeBase, head), [ReadyStop("s1", StopKind.Context, headPassage, basePassage)]), Pin(mergeBase, newHead));

      var stop = Assert.Single(result.Stops);
      Assert.Equal(headPassage with { StartLine = 2, EndLine = 3, Commit = newHead }, Moved(stop.Passages[0]));
      Assert.Equal(basePassage, Moved(stop.Passages[1]));
   }

   /// <summary>Main makes the same edit as the source, and the source merges main, so the file no longer differs between the new merge base and the new head.</summary>
   [Fact]
   public async Task Primary_passage_of_a_change_stop_that_no_longer_touches_the_diff_is_stale()
   {
      var mergeBase = _fixture.Commit("Base", ("a.txt", Lines(1, 10)), ("b.txt", "one\n"));
      _fixture.Checkout("feature", create: true);
      var head = _fixture.Commit("Head", ("a.txt", Lines(1, 10, ("line 2", "two"))), ("b.txt", "two\n"));
      _fixture.Checkout("main");
      var newMergeBase = _fixture.Commit("Main makes the same edit", ("a.txt", Lines(1, 10, ("line 2", "two"))));
      _fixture.Checkout("feature");
      var newHead = Merge("main");
      var inspect = Passage(FileSide.Head, "a.txt", 2, 2, PassageRole.Primary, head, "two");
      var context = Passage(FileSide.Head, "a.txt", 2, 2, PassageRole.Primary, head, "two");
      var tour = TourOf(Pin(mergeBase, head), [ReadyStop("s1", StopKind.Inspect, inspect), ReadyStop("s2", StopKind.Context, context)]);

      var result = await MapAsync(tour, Pin(newMergeBase, newHead));

      var stale = Assert.Single(result.StaleStops);
      Assert.Equal("s1", stale.StopId);
      Assert.Equal(StaleReason.OffDiff, StaleOf(Assert.Single(stale.StalePassages).Outcome));
   }

   /// <summary>
   /// The source renames the given path to new/a.txt and edits line 2, and main makes the same edit, so line 2 no longer differs between the new merge base and the new head. Git allows each old
   /// path on Linux, and <see cref="RepositoryPath.IsValid"/> refuses the last two.
   /// </summary>
   [Theory]
   [InlineData("old/a.txt")]
   [InlineData(@"old\a.txt")]
   [InlineData("c:a.txt")]
   public async Task Primary_passage_in_a_file_renamed_from_any_path_is_stale_once_it_no_longer_touches_the_diff(string oldPath)
   {
      SkipOnWindows();
      var mergeBase = _fixture.Commit("Base", (oldPath, Lines(1, 10)));
      _fixture.Checkout("feature", create: true);
      var head = _fixture.Commit("Head", (oldPath, null), ("new/a.txt", Lines(1, 10, ("line 2", "two"))));
      _fixture.Checkout("main");
      var newMergeBase = _fixture.Commit("Main makes the same edit", (oldPath, Lines(1, 10, ("line 2", "two"))));
      _fixture.Checkout("feature");
      var newHead = Merge("main");
      var inspect = Passage(FileSide.Head, "new/a.txt", 2, 2, PassageRole.Primary, head, "two");
      var tour = TourOf(Pin(mergeBase, head), [ReadyStop("s1", StopKind.Inspect, inspect)]);

      var result = await MapAsync(tour, Pin(newMergeBase, newHead));

      var stale = Assert.Single(result.StaleStops);
      Assert.Equal(StaleReason.OffDiff, StaleOf(Assert.Single(stale.StalePassages).Outcome));
   }

   [Fact]
   public async Task Code_reference_moves_by_the_passage_rule_and_one_that_cannot_move_makes_its_stop_stale()
   {
      var mergeBase = _fixture.Commit("Base", ("a.txt", Lines(1, 10)));
      var head = _fixture.Commit("Head", ("a.txt", Lines(1, 10, ("line 2", "two"))));
      var newHead = _fixture.Commit("New head", ("a.txt", Lines(1, 10, ("line 2", "two"), ("line 3", "x\ny\nz"))));
      var primary = Passage(FileSide.Head, "a.txt", 2, 2, PassageRole.Primary, head, "two");
      var ready = ReadyStop("s1", StopKind.Inspect, primary);
      var stop = ready with { Content = ready.Content with { How = "See [this](ref:head/a.txt#L6-8).", Why = "Because of [that](ref:base/a.txt#L3-3)." } };

      var result = await MapAsync(TourOf(Pin(mergeBase, head), [stop]), Pin(mergeBase, newHead));

      var mapped = Assert.Single(result.Stops);
      Assert.Collection(
         mapped.References,
         reference =>
         {
            Assert.Equal(("how", "ref:head/a.txt#L6-8"), (reference.Reference.Field, reference.Reference.Reference.Link));
            Assert.Equal(new ReferencePlace("a.txt", 8, 10), Assert.IsType<Remapped<ReferencePlace>.Moved>(reference.Outcome).To);
         },
         reference =>
         {
            Assert.Equal("why", reference.Reference.Field);
            Assert.Equal(new ReferencePlace("a.txt", 3, 3), Assert.IsType<Remapped<ReferencePlace>.Moved>(reference.Outcome).To);
         });
      Assert.False(mapped.Stale);

      var staleStop = stop with { Content = stop.Content with { Why = "Because of [that](ref:head/a.txt#L3-3)." } };
      var staleResult = await MapAsync(TourOf(Pin(mergeBase, head), [staleStop]), Pin(mergeBase, newHead));

      var stale = Assert.Single(staleResult.StaleStops);
      Assert.Empty(stale.StalePassages);
      Assert.Equal(StaleReason.LinesChanged, StaleOf(Assert.Single(stale.StaleReferences).Outcome));
   }

   [Fact]
   public async Task Code_anchor_moves_by_the_passage_rule_and_a_file_anchor_follows_its_file()
   {
      var mergeBase = _fixture.Commit("Base", ("a.txt", Lines(1, 10)), ("b.txt", Lines(1, 10)), ("c.txt", "gone soon\n"));
      var head = _fixture.Commit("Head", ("a.txt", Lines(1, 10, ("line 2", "two"))));
      var newHead = _fixture.Commit("New head", ("a.txt", Lines(1, 10, ("line 2", "two"), ("line 3", "x\ny\nz"))), ("b.txt", null), ("moved/b.txt", Lines(1, 10)), ("c.txt", null));
      ThreadAnchor[] anchors =
      [
         new ThreadAnchor.CodeRange(FileSide.Head, "a.txt", 6, 8, head, "line 6\nline 7\nline 8"),
         new ThreadAnchor.CodeRange(FileSide.Head, "a.txt", 3, 4, head, "line 3\nline 4"),
         new ThreadAnchor.WholeFile(FileSide.Head, "b.txt", head),
         new ThreadAnchor.WholeFile(FileSide.Head, "c.txt", head),
         new ThreadAnchor.WholeFile(FileSide.Base, "c.txt", mergeBase),
         new ThreadAnchor.StopText("s1", "how", "words", 1),
         new ThreadAnchor.WholeStop("s1"),
         new ThreadAnchor.WholeTour(),
      ];
      var threads = anchors.Select((anchor, i) => i % 2 == 0 ? SentThread($"t{i}", anchor) : NoteThread($"t{i}", anchor)).ToList();
      var tour = TourOf(Pin(mergeBase, head), [], threads);

      var result = await MapAsync(tour, Pin(mergeBase, newHead));

      Assert.Equal(["t0", "t1", "t2", "t3", "t4"], result.Anchors.Select(anchor => anchor.ThreadId));
      Assert.Equal(anchors[..5], result.Anchors.Select(anchor => anchor.Anchor));
      Assert.Equal(new ThreadAnchor.CodeRange(FileSide.Head, "a.txt", 8, 10, newHead, "line 6\nline 7\nline 8"), MovedAnchor(result.Anchors[0]));
      Assert.Equal(StaleReason.LinesChanged, StaleOf(result.Anchors[1].Outcome));
      Assert.Equal(new ThreadAnchor.WholeFile(FileSide.Head, "moved/b.txt", newHead), MovedAnchor(result.Anchors[2]));
      Assert.Equal(StaleReason.FileGone, StaleOf(result.Anchors[3].Outcome));
      Assert.Equal(anchors[4], MovedAnchor(result.Anchors[4]));
   }

   /// <summary>The new head replaces a.txt with a submodule, whose gitlink names a commit of another repository, and edits c.txt.</summary>
   [Fact]
   public async Task File_anchor_on_a_path_that_becomes_a_submodule_is_stale_and_one_on_a_modified_file_moves()
   {
      var mergeBase = _fixture.Commit("Base", ("a.txt", Lines(1, 10)), ("c.txt", "one\n"));
      var head = _fixture.Commit("Head", ("z.txt", "changed\n"));
      _fixture.Commit("Edit", ("c.txt", "two\n"));
      _fixture.Git(_fixture.OriginPath, "update-index", "--cacheinfo", $"160000,{new string('2', 40)},a.txt");
      _fixture.Git(_fixture.OriginPath, "commit", "--quiet", "--message", "Submodule");
      var newHead = CommitId.Parse(_fixture.Git(_fixture.OriginPath, "rev-parse", "HEAD").Trim());
      ThreadAnchor[] anchors = [new ThreadAnchor.WholeFile(FileSide.Head, "a.txt", head), new ThreadAnchor.WholeFile(FileSide.Head, "c.txt", head)];
      var tour = TourOf(Pin(mergeBase, head), [], [.. anchors.Select((anchor, i) => NoteThread($"t{i}", anchor))]);

      var result = await MapAsync(tour, Pin(mergeBase, newHead));

      Assert.Equal(StaleReason.FileGone, StaleOf(result.Anchors[0].Outcome));
      Assert.Equal(new ThreadAnchor.WholeFile(FileSide.Head, "c.txt", newHead), MovedAnchor(result.Anchors[1]));
   }

   [Fact]
   public async Task Overview_without_passages_is_stale_when_a_code_reference_in_its_prose_cannot_move()
   {
      var mergeBase = _fixture.Commit("Base", ("a.txt", Lines(1, 10)));
      var head = _fixture.Commit("Head", ("a.txt", Lines(1, 10, ("line 2", "two"))));
      var newHead = _fixture.Commit("New head", ("a.txt", Lines(1, 10, ("line 2", "two"), ("line 3", "x\ny\nz"))));
      var overview = new Stop.Written("s0", "Overview", StopKind.Overview, "c1", new StopContent("See [this](ref:head/a.txt#L3-3).", []), 1, []);

      var result = await MapAsync(TourOf(Pin(mergeBase, head), [overview]), Pin(mergeBase, newHead));

      var stale = Assert.Single(result.StaleStops);
      Assert.Equal("s0", stale.StopId);
      Assert.Empty(stale.Passages);
      var reference = Assert.Single(stale.StaleReferences);
      Assert.Equal("ref:head/a.txt#L3-3", reference.Reference.Reference.Link);
      Assert.Equal(StaleReason.LinesChanged, StaleOf(reference.Outcome));
   }

   [Fact]
   public async Task Pending_stop_and_overview_without_passages_or_code_references_are_never_stale_and_earlier_versions_are_not_mapped()
   {
      var mergeBase = _fixture.Commit("Base", ("a.txt", Lines(1, 10)));
      var head = _fixture.Commit("Head", ("a.txt", Lines(1, 10, ("line 2", "two"))));
      var newHead = _fixture.Commit("New head", ("a.txt", null));
      var earlier = new StopVersion("Overview", StopKind.Inspect, new StopContent("Old.", [Passage(FileSide.Head, "a.txt", 2, 2, PassageRole.Primary, head, "two")]), Now);
      var overview = new Stop.Written("s0", "Overview", StopKind.Overview, "c1", new StopContent("The change.", []), 2, [earlier]);
      var pending = new Stop.Pending("s1", "Pending", StopKind.Inspect, "c1", new AnchorHint("a.txt", 2));
      var tour = TourOf(Pin(mergeBase, head), [overview, pending]);

      var result = await MapAsync(tour, Pin(mergeBase, newHead));

      var stop = Assert.Single(result.Stops);
      Assert.Equal(("s0", 2), (stop.StopId, stop.Version));
      Assert.Empty(stop.Passages);
      Assert.Empty(result.StaleStops);
   }

   public void Dispose() => _fixture.Dispose();

   private static StopPassage Moved(PassageRemap passage) => Assert.IsType<Remapped<StopPassage>.Moved>(passage.Outcome).To;

   private static ThreadAnchor MovedAnchor(AnchorRemap anchor) => Assert.IsType<Remapped<ThreadAnchor>.Moved>(anchor.Outcome).To;

   private static StaleReason StaleOf<T>(Remapped<T> outcome) => Assert.IsType<Remapped<T>.Stale>(outcome).Reason;

   private static void SkipOnWindows() => Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows file names cannot hold a backslash or a colon, or be only spaces.");

   /// <summary>Returns lines <c>line &lt;first&gt;</c> to <c>line &lt;last&gt;</c>, each ending in a line feed, with each given line replaced.</summary>
   private static string Lines(int first, int last, params (string Line, string Replacement)[] replacements) =>
      string.Concat(Enumerable.Range(first, last - first + 1).Select(i => $"line {i}").Select(line => (replacements.FirstOrDefault(r => r.Line == line).Replacement ?? line) + "\n"));

   private static StopPassage Passage(FileSide side, string path, int startLine, int endLine, PassageRole role, CommitId commit, string text) =>
      new(side, path, startLine, endLine, role, commit, text);

   private static Stop.Written ReadyStop(string id, StopKind kind, params StopPassage[] passages) => new(id, $"Stop {id}", kind, "c1", new StopContent($"Stop {id}.", passages), 1, []);

   private static QuestionThread SentThread(string id, ThreadAnchor anchor) => ThreadFixtures.Asked(id, anchor, text: "Why?", at: Now);

   private static QuestionThread NoteThread(string id, ThreadAnchor anchor) => ThreadFixtures.Note(id, anchor, at: Now);

   private TourPin.Pinned Pin(CommitId mergeBase, CommitId head) => new(mergeBase, head, 1, _fixture.Url);

   private static Tour TourOf(TourPin.Pinned pin, IReadOnlyList<Stop> stops, IReadOnlyList<QuestionThread>? threads = null) =>
      new(TourId.New(), PullRequest, Familiarity.Some, Now, Now, pin, new Plan([new Chapter("c1", "Chapter")], stops, []), ThreadFixtures.Threads([.. threads ?? []]), []);

   private CommitId Merge(string branch)
   {
      _fixture.Git(_fixture.OriginPath, "merge", "--quiet", "--no-edit", branch);
      return CommitId.Parse(_fixture.Git(_fixture.OriginPath, "rev-parse", "HEAD").Trim());
   }

   private string BlobOf(CommitId commit, string path) => _fixture.Git(_fixture.OriginPath, "rev-parse", $"{commit}:{path}").Trim();

   // Maps the tour through the fact reader of a scope over the fixture's clone, which SyncAsync brought up to date.
   private async Task<RemapResult> MapAsync(Tour tour, TourPin.Pinned to)
   {
      var cache = _fixture.NewCache();
      using var repository = await SyncAsync(cache);
      var facts = new CloneGitFacts(cache, NullLogger<CloneGitFacts>.Instance, FactCacheSize.Default);
      await using var scope = facts.Open(_fixture.Remote(), [], FetchPolicy.WhenMissing);
      return await FactRounds.SettleAsync(scope, known => TourRemap.Map(tour, (TourPin.Pinned)tour.Pin, to, known), TestContext.Current.CancellationToken);
   }

   private Task<GitRepository> SyncAsync(CloneCache? cache = null) =>
      (cache ?? _fixture.NewCache()).OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, TestContext.Current.CancellationToken);
}
