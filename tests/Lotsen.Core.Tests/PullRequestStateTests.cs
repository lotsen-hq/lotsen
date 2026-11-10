using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Git;
using Lotsen.Platforms;
using Lotsen.Tours;
using Microsoft.Extensions.DependencyInjection;
using static Lotsen.Tests.OperationCall;
using static Lotsen.Tests.ThreadCalls;

namespace Lotsen.Tests;

/// <summary>
/// Covers how the server learns the state of a pinned tour's pull request at each iteration check, stores the last state learned on the tour, and carries each change to the tour list in a
/// <c>tourStateChanged</c> event.
/// </summary>
[Trait("Category", "TourStates")]
public sealed class PullRequestStateTests : IAsyncLifetime
{
   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

   private PinnedTourFixture _tour = null!;

   private ServerHarness Harness => _tour.Harness;

   private ThreadCalls Calls => new(Harness);

   private TourId Id => TourId.TryParse(_tour.TourId, out var id) ? id : throw new InvalidOperationException(_tour.TourId);

   public async ValueTask InitializeAsync() => _tour = await PinnedTourFixture.StartAsync();

   public async ValueTask DisposeAsync() => await _tour.DisposeAsync();

   [Fact]
   public async Task Opening_the_tour_and_iteration_checks_store_a_completed_then_an_abandoned_pull_request_and_an_active_one_clears_the_flag()
   {
      await using var events = await EventStream.OpenAsync(Harness);
      Report(PullRequestState.Completed);

      await Calls.CallAsync(await Calls.UiAsync(On("read_state", _tour.TourId)));
      await IdleAsync();
      var completed = await NextStateEventAsync(events);
      var completedRow = await RowAsync();
      var completedRead = await ReadAsync();
      Report(PullRequestState.Abandoned);
      var abandonedCheck = await CheckAsync();
      var abandoned = await NextStateEventAsync(events);
      var abandonedRow = await RowAsync();
      Report(PullRequestState.Active);
      await CheckAsync();
      var active = await NextStateEventAsync(events);
      var activeRow = await RowAsync();
      var activeRead = await ReadAsync();

      Assert.True(
         JsonNode.DeepEquals(new JsonObject { ["tourId"] = _tour.TourId, ["updatedAt"] = completedRead["updatedAt"]!.DeepClone(), ["state"] = "generating", ["pullRequestState"] = "completed" }, EventStream.WithoutRevision(completed)),
         $"The event differs from the stored tour: {completed}");
      Assert.Equal("completed", (string)completedRow["pullRequestState"]!);
      Assert.Equal("generating", (string)completedRow["state"]!);
      Assert.Equal("completed", (string)completedRead["pullRequestState"]!);
      Assert.Null(completedRead["archived"]);
      Assert.Equal("abandoned", (string)abandoned["pullRequestState"]!);
      Assert.Equal("abandoned", (string)abandonedRow["pullRequestState"]!);
      Assert.Equal((string)abandonedCheck["updatedAt"]!, (string)abandoned["updatedAt"]!);
      Assert.Equal("active", (string)active["pullRequestState"]!);
      Assert.Equal("active", (string)activeRow["pullRequestState"]!);
      Assert.Null(activeRead["pullRequestState"]);
      Assert.Equal((string)activeRead["updatedAt"]!, (string)active["updatedAt"]!);
   }

   [Fact]
   public async Task Attaching_a_session_stores_the_pull_request_state()
   {
      Report(PullRequestState.Completed);
      await using var other = await AgentSessionConnection.OpenAsync(Harness);

      await Calls.CallAsync(Calls.Agent(other, On("attach_tour", _tour.TourId)), """{ "takeOver": true }""");
      await IdleAsync();

      Assert.Equal(PullRequestState.Completed, Harness.StoredTour(Id)!.PullRequestState);
   }

   [Fact]
   public async Task Check_that_learns_the_same_state_again_stores_nothing()
   {
      Report(PullRequestState.Completed);
      var first = await CheckAsync();

      var second = await CheckAsync();

      Assert.Equal((string)first["updatedAt"]!, (string)second["updatedAt"]!);
      Assert.Equal(PullRequestState.Completed, Harness.StoredTour(Id)!.PullRequestState);
   }

   [Fact]
   public async Task Failing_platform_keeps_the_last_state_learned()
   {
      Report(PullRequestState.Abandoned);
      var learned = await CheckAsync();
      _tour.Platform.Fail("The sign-in expired. Run 'az login', then try again.");

      var failed = await CheckAsync();

      Assert.NotNull(failed["failure"]);
      Assert.Equal((string)learned["updatedAt"]!, (string)failed["updatedAt"]!);
      Assert.Equal(PullRequestState.Abandoned, Harness.StoredTour(Id)!.PullRequestState);
   }

   [Fact]
   public async Task Check_that_finds_a_new_iteration_of_a_completed_pull_request_stores_both()
   {
      _tour.Git.Checkout("feature");
      var newHead = _tour.Git.Commit("Push", ("src/added.txt", "newer\n"));
      _tour.Git.Checkout("main");
      _tour.Platform.PullRequest(_tour.Git.Url, newHead, _tour.Target, iteration: 2, state: PullRequestState.Completed);

      var checkedIteration = await CheckAsync();

      Assert.Equal(newHead.Value, (string)checkedIteration["availableIteration"]!["pin"]!["head"]!);
      Assert.Equal(PullRequestState.Completed, Harness.StoredTour(Id)!.PullRequestState);
   }

   /// <summary>A pin that fails after the platform answered still tells the pull request's state.</summary>
   [Fact]
   public async Task Check_whose_new_iteration_cannot_be_pinned_stores_the_state_and_reports_the_failure()
   {
      _tour.Platform.PullRequest(_tour.Git.Url, CommitId.Parse(new string('e', 40)), _tour.Target, iteration: 2, state: PullRequestState.Abandoned);

      var checkedIteration = await CheckAsync();

      Assert.NotNull(checkedIteration["failure"]);
      Assert.Null(checkedIteration["availableIteration"]);
      Assert.Equal(PullRequestState.Abandoned, Harness.StoredTour(Id)!.PullRequestState);
   }

   /// <summary>The pin moves while the platform holds the answer, as an iteration update would move it. The state belongs to the pull request, whatever the pin.</summary>
   [Fact]
   public async Task Check_that_ends_after_the_pin_changed_still_stores_the_state()
   {
      var gate = Hold(PullRequestState.Completed);
      var resolves = _tour.Platform.Resolves;

      var check = CheckAsync();
      await Poll.UntilAsync(() => _tour.Platform.Resolves > resolves ? "" : null, Timeout, () => "The check did not resolve the pull request.");
      Harness.ChangeStored(Id, tour => tour with { Pin = new TourPin.Pinned(_tour.MergeBase, _tour.Target, 2, new Uri(_tour.Git.Url.AbsoluteUri)) });
      gate.SetResult();
      await check.WaitAsync(Timeout, TestContext.Current.CancellationToken);

      Assert.Equal(PullRequestState.Completed, Harness.StoredTour(Id)!.PullRequestState);
   }

   /// <summary>The check waits for the platform while the stored state changes. The check's answer may be the older one, so the check leaves the stored state alone.</summary>
   [Fact]
   public async Task Check_whose_tour_learned_another_state_while_it_asked_the_platform_keeps_that_state()
   {
      var gate = Hold(PullRequestState.Completed);
      var resolves = _tour.Platform.Resolves;

      var check = CheckAsync();
      await Poll.UntilAsync(() => _tour.Platform.Resolves > resolves ? "" : null, Timeout, () => "The check did not resolve the pull request.");
      Harness.ChangeStored(Id, tour => tour with { PullRequestState = PullRequestState.Abandoned });
      gate.SetResult();
      await check.WaitAsync(Timeout, TestContext.Current.CancellationToken);

      Assert.Equal(PullRequestState.Abandoned, Harness.StoredTour(Id)!.PullRequestState);
   }

   [Fact]
   public async Task Flagged_tour_is_neither_archived_nor_deleted_and_keeps_its_tour_state()
   {
      var before = await RowAsync();
      Report(PullRequestState.Completed);

      await CheckAsync();
      var after = await RowAsync();

      Assert.Equal((string)before["state"]!, (string)after["state"]!);
      Assert.Equal("completed", (string)after["pullRequestState"]!);
      Assert.False(Harness.StoredTour(Id)!.Archived);
   }

   // Makes the platform report the fixture's pinned iteration with the state.
   private void Report(PullRequestState state) => _tour.Platform.PullRequest(_tour.Git.Url, _tour.Head, _tour.Target, state: state);

   // Makes every resolve wait for the returned gate, then report the pinned iteration with the state.
   private TaskCompletionSource Hold(PullRequestState state)
   {
      var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      var url = new Uri(_tour.Git.Url.AbsoluteUri);
      _tour.Platform.Answer(async cancellationToken =>
      {
         await gate.Task.WaitAsync(cancellationToken);
         return new ResolvedPullRequest(url, _tour.Head, _tour.Target, 1, state);
      });
      return gate;
   }

   private async Task<JsonNode> CheckAsync() => await Calls.CallAsync(await Calls.UiAsync(On("check_iteration", _tour.TourId)));

   private async Task<JsonNode> ReadAsync() => await Harness.CallAsync(On("read_state", _tour.TourId));

   private async Task<JsonNode> RowAsync() =>
      (await Calls.CallAsync(await Calls.UiAsync("list_tours")))["tours"]!.AsArray().Single(row => (string)row!["id"]! == _tour.TourId)!;

   private async Task IdleAsync() => await Harness.PinsIdleAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);

   private async Task<JsonNode> NextStateEventAsync(EventStream stream)
   {
      while (true)
      {
         var next = await stream.NextAsync(EventWait);
         if (next.Type == Operations.ServerEvents.TourStateChanged.Name && (string?)next.Data["tourId"] == _tour.TourId)
         {
            return next.Data;
         }
      }
   }
}
