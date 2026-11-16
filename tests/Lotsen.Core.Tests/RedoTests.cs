using System.Text.Json;
using System.Text.Json.Nodes;
using Lotsen.Json;
using Lotsen.Tours;
using static Lotsen.Tests.TourValues;

namespace Lotsen.Tests;

/// <summary>Covers the <c>redo</c> of <c>fetch_question</c> for a follow-up in the thread of a tour action or a reviewer decision, on a tour that the store holds.</summary>
/// <remarks>
/// Each row stores a plan, a level and a thread whose first question Claude answered, and whose follow-up waits. A row with a reason shows it in <c>redo</c>, and a row without one leaves
/// <c>redo</c> out.
/// </remarks>
[Trait("Category", "TourActions")]
public sealed class RedoTests : IAsyncLifetime
{
   private const string FollowUpId = "q-follow-up";

   private static readonly Dictionary<string, (Func<Tour, Tour> Tour, Func<Tour, JsonObject> Thread, string? Reason)> Rows = new()
   {
      ["a familiarity change fits while the tour holds its level"] = (tour => Leveled(tour, Familiarity.New), tour => Decided(Change(tour)), null),
      ["a familiarity change after a later change of the level"] = (
         tour => Leveled(tour, Familiarity.Familiar),
         tour => Decided(Change(tour)),
         "You changed the tour's familiarity level again since this change, so the later change replaced it. To have Claude rewrite stops at the current level, ask in the thread of your latest familiarity change, or use Re-plan… with your words."),
      ["a plan approval fits after Claude wrote stops and the reviewer skipped one"] = (
         tour => Approving(tour) with { Plan = Plan(Written("s1") with { Skipped = true }, Written("s2")) },
         tour => Decided(ReviewerDecision.PlanApproved.For(Approving(tour), ["s1", "s2"])),
         null),
      ["a plan approval after the plan's stops changed"] = (
         tour => Approving(tour) with { Plan = Plan(Written("s1"), Pending("s3")) },
         tour => Decided(ReviewerDecision.PlanApproved.For(Approving(tour), ["s1", "s2"])),
         "The plan's stops changed since you approved the plan, so the approved order no longer names each stop once. To have Claude change or rewrite stops of the current plan, use Re-plan… in the Plan panel's ⋯ menu."),
      ["an iteration update fits after Claude revised every stale stop"] = (tour => tour with { Plan = Plan(Written("s1")) }, _ => Decided(ReviewerDecision.IterationUpdated.For(1, ["s1"])), null),
      ["an iteration update off its iteration"] = (
         Stale,
         _ => Decided(ReviewerDecision.IterationUpdated.For(2, ["s1"])),
         "The tour moved to another update since this move to update 2, so its stops point at other code now. To have Claude revise stops against the update the tour is on, use Move to update when a newer update is offered, or ask for a revise in a stop's Stop panel."),
      ["an explain of a stop that Claude revised since takes no check"] = (Revised, _ => Asked(new ThreadAnchor.WholeStop("s1"), new TourAction.Explain(1)), null),
      ["a revise of a stop that Claude revised since takes no check"] = (Revised, _ => Asked(new ThreadAnchor.WholeStop("s1"), new TourAction.Revise(1)), null),
      ["a replan takes no check"] = (tour => tour with { Plan = Plan(Written("s1")) }, _ => Asked(new ThreadAnchor.WholeTour(), new TourAction.Replan()), null),
      ["a summarize takes no check"] = (tour => tour with { Plan = Plan(Written("s1")) }, _ => Asked(new ThreadAnchor.WholeTour(), new TourAction.Summarize()), null),
      ["a follow-up action takes no check"] = (tour => tour with { Plan = Plan(Written("s1")) }, _ => Asked(new ThreadAnchor.WholeTour(), new TourAction.FollowUp.DraftWorkItems([])), null),
   };

   private ServerHarness _harness = null!;
   private ThreadCalls _calls = null!;

   public static TheoryData<string> RowNames => [.. Rows.Keys];

   public async ValueTask InitializeAsync()
   {
      _harness = await ServerHarness.StartAsync();
      _calls = new ThreadCalls(_harness);
   }

   public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

   [Theory]
   [MemberData(nameof(RowNames))]
   public async Task Follow_up_row(string row)
   {
      var (change, thread, reason) = Rows[row];
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var tourId = await _calls.CreateAsync(session);
      Assert.True(TourId.TryParse(tourId, out var id), $"The tour ID {tourId} does not parse.");
      _harness.ChangeStored(id, stored =>
      {
         var changed = change(stored with { Pin = Pinned });
         return changed with { Threads = ThreadFixtures.Threads(ThreadFixtures.Parse(thread(changed))) };
      });

      var fetched = await _calls.FetchAsync(session, tourId, FollowUpId);

      if (reason is null)
      {
         Assert.False(fetched.AsObject().ContainsKey("redo"), $"fetched: {fetched}");
      }
      else
      {
         Assert.Equal(reason, (string?)fetched["redo"]);
      }
   }

   /// <summary>A familiarity change that a later change of the level replaced, whose follow-up has a <c>redo</c>, so only its first question shows that <c>redo</c> belongs to a follow-up.</summary>
   [Fact]
   public async Task First_question_of_the_thread_has_no_redo()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var tourId = await _calls.CreateAsync(session);
      Assert.True(TourId.TryParse(tourId, out var id), $"The tour ID {tourId} does not parse.");
      _harness.ChangeStored(id, stored =>
      {
         var leveled = Leveled(stored with { Pin = Pinned }, Familiarity.Familiar);
         return leveled with { Threads = ThreadFixtures.Threads(ThreadFixtures.Decided("t1", Change(leveled))) };
      });

      var fetched = await _calls.FetchAsync(session, tourId, "q-t1");

      Assert.False(fetched.AsObject().ContainsKey("redo"), $"fetched: {fetched}");
   }

   private static Tour Leveled(Tour tour, Familiarity level) => tour with { Plan = Plan(Written("s1")), Familiarity = level };

   private static ReviewerDecision Change(Tour tour) => ReviewerDecision.FamiliarityChanged.For(tour, Familiarity.Some, Familiarity.New, null);

   private static Tour Approving(Tour tour) => tour with { Plan = Plan(Pending("s1"), Pending("s2") with { Skipped = true }), PlanApproval = PlanApproval.Given };

   private static Tour Stale(Tour tour) => tour with { Plan = Plan(Written("s1") with { StaleOn = 1 }) };

   private static Tour Revised(Tour tour) => tour with { Plan = Plan(Written("s1") with { Version = 2, Earlier = [new StopVersion("Stop s1", StopKind.Inspect, Content(), Now)] }) };

   private static JsonObject Asked(ThreadAnchor anchor, TourAction action)
   {
      var thread = WithFollowUp("asked", anchor);
      thread["action"] = JsonSerializer.SerializeToNode(action, LotsenJsonContext.Default.TourAction);
      return thread;
   }

   private static JsonObject Decided(ReviewerDecision decision)
   {
      var thread = WithFollowUp("decided", new ThreadAnchor.WholeTour());
      thread["decision"] = JsonSerializer.SerializeToNode(decision, LotsenJsonContext.Default.ReviewerDecision);
      return thread;
   }

   // A first question that Claude answered, then the follow-up that asks for the work again and waits for its reply.
   private static JsonObject WithFollowUp(string kind, ThreadAnchor anchor)
   {
      var at = ThreadFixtures.At;
      return ThreadFixtures.Thread(kind, "t1", anchor, QuestionThreadState.Open,
      [
         ThreadFixtures.Question("q-t1", "", null, at, ("r-t1", RequestState.Done, "session1")),
         ThreadFixtures.Reply("a-t1", "r-t1", "session1", "Done.", at),
         ThreadFixtures.Question(FollowUpId, "Do it again.", null, at, ("r-follow-up", RequestState.Pending, "session1")),
      ]);
   }
}
