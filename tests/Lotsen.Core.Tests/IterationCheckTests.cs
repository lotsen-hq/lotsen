using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Git;
using Lotsen.Operations;
using Lotsen.Pinning;
using Lotsen.Platforms;
using Lotsen.Tours;
using Microsoft.Extensions.DependencyInjection;
using static Lotsen.Tests.OperationCall;
using static Lotsen.Tests.ThreadCalls;

namespace Lotsen.Tests;

/// <summary>
/// Covers how the server finds a new iteration of a pinned tour's pull request: when it checks, what it stores as the tour's available iteration and the event it publishes, without a change to
/// the tour's pin.
/// </summary>
/// <remarks>
/// The plan has two inspect stops: <c>modified</c> on line 2 of <c>src/modified.txt</c> at the head, which <see cref="PushAsync"/> changes, and <c>added</c> on line 1 of <c>src/added.txt</c>,
/// which it leaves as it is.
/// </remarks>
[Trait("Category", "Remap")]
public sealed class IterationCheckTests : IAsyncLifetime
{
   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

   private PinnedTourFixture _tour = null!;
   private string _modifiedStop = "";
   private string _addedStop = "";
   private string _uiCredential = "";

   private ServerHarness Harness => _tour.Harness;

   private ThreadCalls Calls => new(Harness);

   public async ValueTask InitializeAsync()
   {
      _tour = await PinnedTourFixture.StartAsync();
      var plan = await Harness.AgentCallAsync(
         On("set_plan", _tour.TourId),
         """
         {
           "chapters": [{ "id": "a", "title": "A" }],
           "stops": [{ "title": "Modified", "kind": "inspect", "chapter": "a" }, { "title": "Added", "kind": "inspect", "chapter": "a" }]
         }
         """);
      _modifiedStop = (string)plan["plan"]!["stops"]![0]!["id"]!;
      _addedStop = (string)plan["plan"]!["stops"]![1]!["id"]!;
      await PutStopAsync(_modifiedStop, "src/modified.txt", 2);
      await PutStopAsync(_addedStop, "src/added.txt", 1);
      _uiCredential = await Harness.UiCredentialAsync();
   }

   public async ValueTask DisposeAsync() => await _tour.DisposeAsync();

   [Fact]
   public async Task New_source_commit_yields_an_available_iteration_with_its_number_and_stale_stops_and_leaves_the_pin()
   {
      var before = await ReadAsync();
      var newHead = await PushAsync();
      await using var events = await EventStream.OpenAsync(Harness);

      var checkedIteration = await RefreshAsync();
      var changed = await events.NextAsync(EventWait);
      var after = await ReadAsync();

      var available = checkedIteration["availableIteration"]!;
      Assert.Null(checkedIteration["failure"]);
      Assert.Equal(2, (int)available["pin"]!["iteration"]!);
      Assert.Equal(newHead.Value, (string)available["pin"]!["head"]!);
      Assert.Equal(_tour.MergeBase.Value, (string)available["pin"]!["mergeBase"]!);
      Assert.Null(available["from"]);
      Assert.Equal([_modifiedStop], StaleStops(available));
      Assert.True(JsonNode.DeepEquals(before["pin"], after["pin"]), $"The check changed the pin: {after["pin"]!.ToJsonString()}");
      Assert.True(JsonNode.DeepEquals(available, after["availableIteration"]), $"read_state holds another available iteration: {after["availableIteration"]?.ToJsonString()}");
      Assert.Equal(ServerEvents.AvailableIterationChanged.Name, changed.Type);
      Assert.Equal(_tour.TourId, (string)changed.Data["tourId"]!);
      Assert.True(JsonNode.DeepEquals(available, changed.Data["availableIteration"]), $"The event holds another available iteration: {changed.Data.ToJsonString()}");
      Assert.Equal((string)after["updatedAt"]!, (string)changed.Data["updatedAt"]!);
   }

   [Fact]
   public async Task New_target_commit_with_the_same_source_commit_yields_no_available_iteration_and_leaves_the_tour()
   {
      _tour.Git.Checkout("main");
      var newTarget = _tour.Git.Commit("Main moves again", ("src/same.txt", "changed on main\n"));
      _tour.Platform.PullRequest(_tour.Git.Url, _tour.Head, newTarget, iteration: 2);
      var before = await ReadAsync();

      var checkedIteration = await RefreshAsync();

      Assert.Null(checkedIteration["availableIteration"]);
      Assert.Null(checkedIteration["failure"]);
      Assert.True(JsonNode.DeepEquals(before, await ReadAsync()), "The check changed the tour.");
   }

   [Fact]
   public async Task Check_that_finds_the_pinned_head_again_clears_the_available_iteration()
   {
      await PushAsync();
      await RefreshAsync();
      _tour.Platform.PullRequest(_tour.Git.Url, _tour.Head, _tour.Target);
      await using var events = await EventStream.OpenAsync(Harness);

      var checkedIteration = await RefreshAsync();
      var cleared = await events.NextAsync(EventWait);

      Assert.Null(checkedIteration["availableIteration"]);
      Assert.Null((await ReadAsync())["availableIteration"]);
      Assert.Equal(ServerEvents.AvailableIterationChanged.Name, cleared.Type);
      Assert.Null(cleared.Data["availableIteration"]);
   }

   [Fact]
   public async Task Check_that_finds_the_same_result_again_stores_nothing()
   {
      await PushAsync();
      var first = await RefreshAsync();

      var second = await RefreshAsync();

      Assert.Equal((string)first["updatedAt"]!, (string)second["updatedAt"]!);
      Assert.True(JsonNode.DeepEquals(first["availableIteration"], second["availableIteration"]), "The second check stored another result.");
   }

   [Fact]
   public async Task Check_that_finds_the_available_head_again_does_not_fetch()
   {
      await PushAsync();
      var first = await RefreshAsync();
      AddUnrelatedBranch();

      var second = await RefreshAsync();

      Assert.True(JsonNode.DeepEquals(first["availableIteration"], second["availableIteration"]), "The second check stored another result.");
      Assert.False(CloneHasUnrelatedBranch(), "The second check fetched from the remote.");
   }

   /// <summary>The target merges the pinned head with the "ours" strategy, which leaves its files as they were and moves the merge base of the new head onto the pinned head.</summary>
   [Fact]
   public async Task Check_that_finds_the_available_head_again_with_a_new_target_pins_the_new_merge_base()
   {
      var newHead = await PushAsync();
      await RefreshAsync();
      _tour.Git.Checkout("main");
      _tour.Git.Git(_tour.Git.OriginPath, "merge", "--quiet", "--no-ff", "--strategy=ours", "--message", "Merge the pinned head", _tour.Head.Value);
      var newTarget = CommitId.Parse(_tour.Git.Git(_tour.Git.OriginPath, "rev-parse", "HEAD").Trim());
      _tour.Platform.PullRequest(_tour.Git.Url, newHead, newTarget, iteration: 3);

      var checkedIteration = await RefreshAsync();

      var available = checkedIteration["availableIteration"]!;
      Assert.Equal(3, (int)available["pin"]!["iteration"]!);
      Assert.Equal(newHead.Value, (string)available["pin"]!["head"]!);
      Assert.Equal(_tour.Head.Value, (string)available["pin"]!["mergeBase"]!);
   }

   [Fact]
   public async Task Check_that_finds_the_available_head_again_after_a_stop_changed_maps_the_new_stops_without_a_fetch()
   {
      await PushAsync();
      await RefreshAsync();
      await PutStopAsync(_addedStop, "src/modified.txt", 2);
      AddUnrelatedBranch();

      var checkedIteration = await RefreshAsync();

      Assert.Equal([_modifiedStop, _addedStop], StaleStops(checkedIteration["availableIteration"]!));
      Assert.False(CloneHasUnrelatedBranch(), "The check fetched from the remote.");
   }

   [Fact]
   public async Task Check_that_finds_a_new_head_the_clone_holds_does_not_fetch()
   {
      _tour.Git.Checkout("feature");
      var second = _tour.Git.Commit("Second", ("src/modified.txt", "one\nTWO\nthree\nfour\nfive\n"));
      var third = _tour.Git.Commit("Third", ("src/added.txt", "newer\n"));
      _tour.Git.Checkout("main");
      _tour.Platform.PullRequest(_tour.Git.Url, second, _tour.Target, iteration: 2);
      await RefreshAsync();
      _tour.Platform.PullRequest(_tour.Git.Url, third, _tour.Target, iteration: 3);
      AddUnrelatedBranch();

      var checkedIteration = await RefreshAsync();

      var available = checkedIteration["availableIteration"]!;
      Assert.Equal(3, (int)available["pin"]!["iteration"]!);
      Assert.Equal(third.Value, (string)available["pin"]!["head"]!);
      Assert.Equal([_modifiedStop, _addedStop], StaleStops(available));
      Assert.False(CloneHasUnrelatedBranch(), "The check fetched from the remote.");
   }

   [Fact]
   public async Task Check_that_finds_a_new_head_the_clone_lacks_fetches_it()
   {
      await PushAsync();
      await RefreshAsync();
      AddUnrelatedBranch();
      _tour.Git.Checkout("feature");
      var third = _tour.Git.Commit("Third", ("src/added.txt", "newer\n"));
      _tour.Git.Checkout("main");
      _tour.Platform.PullRequest(_tour.Git.Url, third, _tour.Target, iteration: 3);

      var checkedIteration = await RefreshAsync();

      Assert.Equal(third.Value, (string)checkedIteration["availableIteration"]!["pin"]!["head"]!);
      Assert.True(CloneHasUnrelatedBranch(), "The check did not fetch from the remote.");
   }

   [Fact]
   public async Task Reading_the_tour_for_the_UI_checks_for_a_new_iteration()
   {
      await PushAsync();

      await Calls.CallAsync(Ui(On("read_state", _tour.TourId)));

      await AvailableAfterJobsAsync();
   }

   [Fact]
   public async Task Reading_the_tour_as_an_agent_session_does_not_check()
   {
      await PushAsync();
      var resolves = _tour.Platform.Resolves;

      await Harness.AgentCallAsync(On("read_state", _tour.TourId));
      await Harness.PinsIdleAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);

      Assert.Equal(resolves, _tour.Platform.Resolves);
      Assert.Null((await ReadAsync())["availableIteration"]);
   }

   /// <summary>Each operation that only the UI calls, which starts a check for the UI, refuses the agent session and the CLI before the check starts.</summary>
   [Theory]
   [InlineData("agent", "ask_question", """{ "text": "Why?", "anchor": { "kind": "tour" } }""")]
   [InlineData("cli", "ask_question", """{ "text": "Why?", "anchor": { "kind": "tour" } }""")]
   [InlineData("agent", "request_tour_action", """{ "action": { "kind": "replan" }, "text": "Again." }""")]
   [InlineData("cli", "request_tour_action", """{ "action": { "kind": "replan" }, "text": "Again." }""")]
   [InlineData("agent", "ask_follow_up", """{ "threadId": "aaaaaaaa", "text": "Why?" }""")]
   [InlineData("cli", "ask_follow_up", """{ "threadId": "aaaaaaaa", "text": "Why?" }""")]
   [InlineData("agent", "resend_question", """{ "questionId": "aaaaaaaa" }""")]
   [InlineData("cli", "resend_question", """{ "questionId": "aaaaaaaa" }""")]
   [InlineData("agent", "send_notes", """{ "threadIds": ["aaaaaaaa"] }""")]
   [InlineData("cli", "send_notes", """{ "threadIds": ["aaaaaaaa"] }""")]
   [InlineData("agent", "send_answers", "{}")]
   [InlineData("cli", "send_answers", "{}")]
   [InlineData("agent", "change_familiarity", """{ "familiarity": "new" }""")]
   [InlineData("cli", "change_familiarity", """{ "familiarity": "new" }""")]
   [InlineData("agent", "approve_plan", """{ "stopIds": [], "skippedStops": [], "familiarity": "new" }""")]
   [InlineData("cli", "approve_plan", """{ "stopIds": [], "skippedStops": [], "familiarity": "new" }""")]
   public async Task Ui_only_operation_that_refuses_an_agent_session_or_the_CLI_does_not_check(string caller, string operation, string body)
   {
      await PushAsync();
      var resolves = _tour.Platform.Resolves;

      using var response = caller == "agent"
         ? await Harness.AgentSendJsonAsync(On(operation, _tour.TourId), body)
         : await Harness.CliSendJsonAsync(On(operation, _tour.TourId), body);
      await ErrorAsync(response, HttpStatusCode.Forbidden, "callerNotAllowed");
      await Harness.PinsIdleAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);

      Assert.Equal(resolves, _tour.Platform.Resolves);
      Assert.Null((await ReadAsync())["availableIteration"]);
   }

   [Fact]
   public async Task Ui_send_that_finds_no_answer_to_send_still_checks_for_a_new_iteration()
   {
      await PushAsync();

      using var response = await Calls.SendAsync(Ui(On("send_answers", _tour.TourId)), "{}");

      await ErrorAsync(response, HttpStatusCode.Conflict, "noAnswersToSend");
      await AvailableAfterJobsAsync();
   }

   [Fact]
   public async Task Attaching_a_session_checks_for_a_new_iteration()
   {
      await PushAsync();
      await using var other = await AgentSessionConnection.OpenAsync(Harness);

      await Calls.CallAsync(Calls.Agent(other, On("attach_tour", _tour.TourId)), """{ "takeOver": true }""");

      await AvailableAfterJobsAsync();
   }

   [Fact]
   public async Task Asking_a_question_checks_for_a_new_iteration()
   {
      await PushAsync();

      await AskAsync();

      await AvailableAfterJobsAsync();
   }

   [Fact]
   public async Task Failing_platform_leaves_the_question_to_go_through_and_keeps_the_earlier_result()
   {
      await PushAsync();
      var found = await RefreshAsync();
      _tour.Platform.Fail("The sign-in expired. Run 'az login', then try again.");

      var thread = await AskAsync();
      var refreshed = await RefreshAsync();
      await Harness.PinsIdleAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);

      Assert.Equal("pending", (string)RequestOf(thread)["state"]!);
      Assert.Equal("The sign-in expired. Run 'az login', then try again.", (string)refreshed["failure"]!);
      Assert.True(JsonNode.DeepEquals(found["availableIteration"], refreshed["availableIteration"]), "The failed check changed the result.");
      Assert.True(JsonNode.DeepEquals(found["availableIteration"], (await ReadAsync())["availableIteration"]), "The failed check changed the stored result.");
   }

   [Fact]
   public async Task Question_goes_through_while_the_check_before_it_waits_for_the_platform()
   {
      var newHead = await PushAsync();
      var gate = HoldPlatform(newHead);

      var thread = await AskAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);

      Assert.Equal("pending", (string)RequestOf(thread)["state"]!);
      gate.SetResult();
      await AvailableAfterJobsAsync();
   }

   [Fact]
   public async Task Checks_requested_while_one_runs_run_once_after_it_and_one_at_a_time()
   {
      var newHead = await PushAsync();
      var gate = HoldPlatform(newHead);
      var resolves = _tour.Platform.Resolves;

      var first = RefreshAsync();
      await Poll.UntilAsync(() => _tour.Platform.Resolves > resolves ? "" : null, Timeout, () => "The first check did not resolve the pull request.");
      var second = RefreshAsync();
      var third = RefreshAsync();
      await Task.Delay(200, TestContext.Current.CancellationToken);
      var whileHeld = _tour.Platform.Resolves - resolves;
      gate.SetResult();
      await Task.WhenAll(first, second, third).WaitAsync(Timeout, TestContext.Current.CancellationToken);
      await Harness.PinsIdleAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);

      Assert.Equal(1, whileHeld);
      Assert.Equal(2, _tour.Platform.Resolves - resolves);
      Assert.Equal(2, (int)(await second)["availableIteration"]!["pin"]!["iteration"]!);
      Assert.True(JsonNode.DeepEquals((await second)["availableIteration"], (await third)["availableIteration"]), "The joined checks returned different results.");
   }

   [Fact]
   public async Task Check_that_ends_after_the_pin_changed_stores_nothing()
   {
      var newHead = await PushAsync();
      var gate = HoldPlatform(newHead);
      var resolves = _tour.Platform.Resolves;
      Assert.True(TourId.TryParse(_tour.TourId, out var id));

      var refresh = RefreshAsync();
      await Poll.UntilAsync(() => _tour.Platform.Resolves > resolves ? "" : null, Timeout, () => "The check did not resolve the pull request.");
      var repinned = Harness.ChangeStored(id, tour => tour with { Pin = new TourPin.Pinned(_tour.MergeBase, newHead, 2, new Uri(_tour.Git.Url.AbsoluteUri)) })!;
      gate.SetResult();
      var checkedIteration = await refresh.WaitAsync(Timeout, TestContext.Current.CancellationToken);

      Assert.Null(checkedIteration["availableIteration"]);
      var stored = Harness.StoredTour(id)!;
      Assert.Null(stored.AvailableIteration);
      Assert.Equal(repinned.UpdatedAt, stored.UpdatedAt);
   }

   /// <summary>
   /// The push changes line 3 of <c>src/renamed.txt</c> too, and the hook moves the added stop there once the check read the facts of the stops it knew, so the latest tour needs facts that the
   /// check has not read. The check reads them and maps the latest stops, without asking the platform again.
   /// </summary>
   [Fact]
   public async Task Check_whose_stops_changed_before_it_stored_reads_the_missing_facts_and_maps_the_latest_stops()
   {
      _tour.Git.Checkout("feature");
      var newHead = _tour.Git.Commit("Push", ("src/modified.txt", "one\nTWO\nthree\nfour\nfive\n"), ("src/renamed.txt", "alpha\nbeta\nGaMmA\ndelta\nepsilon\n"));
      _tour.Git.Checkout("main");
      _tour.Platform.PullRequest(_tour.Git.Url, newHead, _tour.Target, iteration: 2);
      var lifecycle = Harness.Server.Services.GetRequiredService<IterationLifecycle>();
      lifecycle.BeforeCheckStored = async _ =>
      {
         lifecycle.BeforeCheckStored = null;
         await PutStopAsync(_addedStop, "src/renamed.txt", 3);
      };
      var resolves = _tour.Platform.Resolves;

      var checkedIteration = await RefreshAsync();

      Assert.Equal([_modifiedStop, _addedStop], StaleStops(checkedIteration["availableIteration"]!));
      Assert.Equal(1, _tour.Platform.Resolves - resolves);
   }

   [Fact]
   public async Task Check_of_a_tour_that_is_not_pinned_is_refused()
   {
      _tour.Platform.Fail("not reached");
      using var created = await Calls.SendAsync(Calls.Agent(await Harness.AgentAsync(), "create_tour"), CreateBody);
      var tourId = (string)SuccessBody.Output(await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))["id"]!;
      await Harness.PinsIdleAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);

      using var response = await Calls.SendAsync(Ui(On("check_iteration", tourId)));

      var error = await ErrorAsync(response, HttpStatusCode.Conflict, "tourNotPinned");
      Assert.StartsWith("tourId: this tour is not pinned, because its pin failed", (string)error["reason"]!, StringComparison.Ordinal);
   }

   private static string[] StaleStops(JsonNode available) => [.. available["staleStops"]!.AsArray().Select(stop => (string)stop!)];

   private async Task PutStopAsync(string stopId, string path, int line) => await Harness.AgentCallAsync(
      On("put_stop", _tour.TourId, stopId: stopId),
      $$"""{ "headline": "The change.", "passages": [{ "side": "head", "path": "{{path}}", "startLine": {{line}}, "endLine": {{line}}, "role": "primary" }] }""");

   // Pushes a commit to the source branch that changes line 2 of src/modified.txt, and makes the platform report it as iteration 2.
   private async Task<CommitId> PushAsync()
   {
      _tour.Git.Checkout("feature");
      var newHead = _tour.Git.Commit("Push", ("src/modified.txt", "one\nTWO\nthree\nfour\nfive\n"));
      _tour.Git.Checkout("main");
      _tour.Platform.PullRequest(_tour.Git.Url, newHead, _tour.Target, iteration: 2);
      await Harness.PinsIdleAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);
      return newHead;
   }

   // Commits on a branch of the remote that no iteration names, so only a fetch of the remote's branches brings it into the clone.
   private void AddUnrelatedBranch()
   {
      _tour.Git.Checkout("unrelated", create: true);
      _tour.Git.Commit("Unrelated", ("src/unrelated.txt", "unrelated\n"));
      _tour.Git.Checkout("main");
   }

   private bool CloneHasUnrelatedBranch()
   {
      var clone = Assert.Single(Directory.GetDirectories(Harness.DataDirectory.ClonesPath));
      return _tour.Git.Git(clone, "for-each-ref", "--format=%(refname)", "refs/heads/unrelated").Trim().Length > 0;
   }

   // Makes every resolve wait for the returned gate, then report the new head as iteration 2.
   private TaskCompletionSource HoldPlatform(CommitId newHead)
   {
      var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      var url = new Uri(_tour.Git.Url.AbsoluteUri);
      _tour.Platform.Answer(async cancellationToken =>
      {
         await gate.Task.WaitAsync(cancellationToken);
         return new ResolvedPullRequest(url, newHead, _tour.Target, 2, PullRequestState.Active);
      });
      return gate;
   }

   private PreparedCall Ui(OperationCall call) => new(call, _uiCredential, null);

   private async Task<JsonNode> RefreshAsync() => await Calls.CallAsync(Ui(On("check_iteration", _tour.TourId)));

   private async Task<JsonNode> AskAsync() => await Calls.CallAsync(Ui(On("ask_question", _tour.TourId)), NoteBody("What does this tour cover?"));

   // Reads as the agent session, whose read starts no check.
   private async Task<JsonNode> ReadAsync() => await Harness.AgentCallAsync(On("read_state", _tour.TourId));

   private async Task AvailableAfterJobsAsync()
   {
      await Harness.PinsIdleAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);
      var available = (await ReadAsync())["availableIteration"];
      Assert.True(available is not null, "No check stored an available iteration.");
      Assert.Equal(2, (int)available["pin"]!["iteration"]!);
   }
}
