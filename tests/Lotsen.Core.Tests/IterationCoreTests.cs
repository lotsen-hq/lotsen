using Lotsen.Git;
using Lotsen.Pinning;
using Lotsen.Platforms;
using Lotsen.Tours;
using static Lotsen.Tests.IterationRows;

namespace Lotsen.Tests;

/// <summary>Covers the outcomes that <see cref="IterationCore"/> decides for the first pin, the iteration check and the iteration update, from a tour, a platform answer and hand-built git facts.</summary>
[Trait("Category", "Remap")]
public sealed class IterationCoreTests
{
   private static readonly Tour Planned = TourOf([Stop("s1", StopKind.Inspect, Passage("a.txt", 2, 2)), Stop("s2", StopKind.Inspect, Passage("b.txt", 1, 1))]);

   [Fact]
   public void First_pin_asks_for_the_merge_base_then_pins_the_source_commit_and_the_merge_base()
   {
      var latest = Answer(NewHead).Latest;

      var missing = Missing(IterationCore.FirstPin(PullRequest.Url, latest, FactSet.Empty));
      var pin = Decided(IterationCore.FirstPin(PullRequest.Url, latest, new FactSetBuilder().Add(new FactKey.MergeBaseOf(NewHead, Target), Base).Build()));

      Assert.Equal(new FactKey[] { new FactKey.MergeBaseOf(NewHead, Target) }, missing.ToArray());
      Assert.Equal(new TourPin.Pinned(Base, NewHead, 2, Remote), pin);
   }

   [Fact]
   public void First_pin_of_commits_without_a_merge_base_fails_naming_the_pull_request()
   {
      var facts = new FactSetBuilder().Add(new FactKey.MergeBaseOf(NewHead, Target), null).Build();

      var failed = Assert.IsType<TourPin.Failed>(Decided(IterationCore.FirstPin(PullRequest.Url, Answer(NewHead).Latest, facts)));

      Assert.Contains(PullRequest.Url.AbsoluteUri, failed.Reason, StringComparison.Ordinal);
      Assert.Contains("shares no history", failed.Reason, StringComparison.Ordinal);
   }

   /// <summary>Only the source commit counts, so a new target or a new iteration number with the pinned head is no new iteration.</summary>
   [Theory]
   [InlineData(PullRequestState.Active)]
   [InlineData(PullRequestState.Completed)]
   public void Check_that_finds_the_pinned_head_is_unchanged_with_the_state_and_needs_no_fact(PullRequestState state)
   {
      var outcome = Decided(IterationCore.Check(Planned, Answer(Head, target: NewBase, iteration: 5, state: state), FactSet.Empty, Now));

      Assert.Equal(new CheckOutcome.Unchanged(state), outcome);
   }

   [Fact]
   public void Check_whose_platform_gave_no_answer_fails_with_its_reason_and_no_state()
   {
      var outcome = Decided(IterationCore.Check(Planned, new PlatformAnswer.Unreachable("Run 'az login', then try again.", Now), FactSet.Empty, Now));

      Assert.Equal(new CheckOutcome.Failed("Run 'az login', then try again.", null), outcome);
   }

   /// <summary>
   /// The pass needs the merge base first, then the changed paths, then the hunks and the new text of a.txt and the new pin's diff of each stop's file. The stop on a.txt is stale, and the stop
   /// on b.txt, which the new head leaves, moves.
   /// </summary>
   [Fact]
   public void Check_that_finds_a_new_head_reads_its_facts_in_rounds_then_offers_the_iteration_with_its_stale_stops_and_state()
   {
      var answer = Answer(NewHead, state: PullRequestState.Completed);
      var all = Facts("a.txt").Changed("a.txt", Text(1, 10, (2, "TWO")), new LineHunk(2, 1, 2, 1)).InDiff("b.txt").Build();
      var rounds = new List<IReadOnlySet<FactKey>>();
      var known = new Dictionary<FactKey, object?>();

      CheckOutcome? outcome = null;
      for (var round = 0; outcome is null && round < 5; round++)
      {
         switch (IterationCore.Check(Planned, answer, FactSet.Of(known), Now))
         {
            case Decision<CheckOutcome>.Decided { Value: var decided }:
               outcome = decided;
               break;
            case Decision<CheckOutcome>.NeedsFacts { Missing: var missing }:
               rounds.Add(missing);
               foreach (var key in missing)
               {
                  Assert.True(all.TryFind(key, out var value), $"The pass asked for {key}, which the row does not hold.");
                  known[key] = value;
               }

               break;
         }
      }

      var found = Assert.IsType<CheckOutcome.Found>(outcome);
      Assert.Equal((new TourPin.Pinned(Base, NewHead, 2, Remote), Now, PullRequestState.Completed), (found.Available.Pin, found.Available.FoundAt, found.PullRequestState));
      Assert.Equal(["s1"], found.Available.StaleStops);
      Assert.Equal(3, rounds.Count);
      Assert.Equal(new FactKey[] { new FactKey.MergeBaseOf(NewHead, Target) }, rounds[0].ToArray());
      Assert.Equal(new FactKey[] { new FactKey.ChangedPaths(Head, NewHead) }, rounds[1].ToArray());
      Assert.Equal(new HashSet<FactKey> { new FactKey.Hunks(Head, "a.txt", NewHead, "a.txt"), new FactKey.PinDiff(Base, NewHead, FileSide.Head, "b.txt") }, [.. rounds[2]]);
   }

   [Fact]
   public void Check_counts_a_stop_that_an_earlier_update_left_stale()
   {
      var stale = Stop("s2", StopKind.Inspect, Passage("b.txt", 1, 1)) with { StaleOn = 1 };
      var tour = TourOf([Stop("s1", StopKind.Inspect, Passage("a.txt", 2, 2)), stale]);

      var outcome = Decided(IterationCore.Check(tour, Answer(NewHead), Facts().InDiff("a.txt").InDiff("b.txt").Build(), Now));

      Assert.Equal(["s2"], Assert.IsType<CheckOutcome.Found>(outcome).Available.StaleStops);
   }

   [Fact]
   public void Check_of_a_new_head_without_a_merge_base_fails_with_the_state()
   {
      var facts = new FactSetBuilder().Add(new FactKey.MergeBaseOf(NewHead, Target), null).Build();

      var failed = Assert.IsType<CheckOutcome.Failed>(Decided(IterationCore.Check(Planned, Answer(NewHead, state: PullRequestState.Abandoned), facts, Now)));

      Assert.Equal(PullRequestState.Abandoned, failed.PullRequestState);
      Assert.Contains("shares no history", failed.Reason, StringComparison.Ordinal);
   }

   [Fact]
   public void Update_of_a_tour_without_an_available_iteration_is_refused()
   {
      var outcome = Decided(IterationCore.Update(Planned, FactSet.Empty));

      Assert.Equal(OperationErrorCode.IterationNotAvailable, Assert.IsType<UpdateOutcome.Refused>(outcome).Refusal.Code);
   }

   [Fact]
   public void Update_while_an_earlier_update_waits_is_refused()
   {
      var waiting = ThreadFixtures.Decided("t1", ThreadFixtures.Decision("""{ "kind": "iterationUpdated", "iteration": 1, "stopIds": ["s1"] }"""));
      var tour = Planned with { AvailableIteration = new AvailableIteration(NewPin, ["s1"], Now), Threads = ThreadFixtures.Threads(waiting) };

      var outcome = Decided(IterationCore.Update(tour, FactSet.Empty));

      Assert.Equal(OperationErrorCode.QuestionWaiting, Assert.IsType<UpdateOutcome.Refused>(outcome).Refusal.Code);
   }

   /// <summary>The update moves the tour onto the pin that its available iteration holds, whatever the platform would say now, so it takes no platform answer.</summary>
   [Fact]
   public void Update_moves_the_latest_tour_onto_the_available_pin_or_asks_for_the_facts_that_its_stops_need()
   {
      var tour = Planned with { AvailableIteration = new AvailableIteration(NewPin, ["s1"], Now) };

      var missing = Missing(IterationCore.Update(tour, FactSet.Empty));
      var repin = Assert.IsType<UpdateOutcome.Repinned>(Decided(IterationCore.Update(tour, Facts("a.txt").Changed("a.txt", Text(1, 10, (2, "TWO")), new LineHunk(2, 1, 2, 1)).InDiff("b.txt").Build()))).Repin;

      Assert.Equal(new FactKey[] { new FactKey.ChangedPaths(Head, NewHead) }, missing.ToArray());
      Assert.Equal(NewPin, repin.Tour.Pin);
      Assert.Null(repin.Tour.AvailableIteration);
      Assert.Equal(["s1"], repin.StaleStops);
      Assert.Equal(2, repin.Decision!.Iteration);
      Assert.Equal(["s1"], repin.Decision.StopIds);
   }

   [Fact]
   public void Commits_name_the_pin_and_the_latest_iteration_or_the_available_pin()
   {
      var tour = Planned with { AvailableIteration = new AvailableIteration(new TourPin.Pinned(NewBase, NewHead, 2, Remote), [], Now) };

      Assert.Equal([Base, Head, NewHead, Target], IterationCore.Commits(tour, Answer(NewHead).Latest));
      Assert.Equal([Base, Head, NewBase, NewHead], IterationCore.Commits(tour, null));
   }
}
