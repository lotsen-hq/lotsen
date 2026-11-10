using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lotsen.Git;
using Lotsen.Json;
using Lotsen.Operations;
using Lotsen.Pinning;
using Lotsen.Platforms;
using Lotsen.Sessions;
using Lotsen.Tours;
using Microsoft.Extensions.DependencyInjection;
using static Lotsen.Tests.OperationCall;
using static Lotsen.Tests.ThreadCalls;

namespace Lotsen.Tests;

/// <summary>
/// Covers <c>update_iteration</c>: the reviewer updates a tour to its available iteration, which re-pins the tour, moves what the remap moves, marks the stale stops and outdates the threads whose
/// anchors cannot move, and reports the update to the bound session in a decided thread whose iterationUpdated decision names the stale stops.
/// </summary>
/// <remarks>
/// The plan has three inspect stops at the head: <c>modified</c> on line 2 of <c>src/modified.txt</c>, which <see cref="PushAsync"/> changes, <c>renamed</c> on line 3 of
/// <c>src/renamed.txt</c>, whose prose links to that line and which the push moves to line 4, and <c>added</c> on line 1 of <c>src/added.txt</c>, which the push leaves as it is.
/// </remarks>
[Trait("Category", "Remap")]
public sealed class IterationUpdateTests : IAsyncLifetime
{
   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

   private PinnedTourFixture _tour = null!;
   private string _modifiedStop = "";
   private string _renamedStop = "";
   private string _addedStop = "";
   private string _uiCredential = "";

   private ServerHarness Harness => _tour.Harness;

   private ThreadCalls Calls => new(Harness);

   private IterationLifecycle Lifecycle => Harness.Server.Services.GetRequiredService<IterationLifecycle>();

   private TourWriter Writer => Harness.Server.Services.GetRequiredService<TourWriter>();

   public async ValueTask InitializeAsync()
   {
      _tour = await PinnedTourFixture.StartAsync();
      var plan = await Harness.AgentCallAsync(
         On("set_plan", _tour.TourId),
         """
         {
           "chapters": [{ "id": "a", "title": "A" }],
           "stops": [
             { "title": "Modified", "kind": "inspect", "chapter": "a" },
             { "title": "Renamed", "kind": "inspect", "chapter": "a" },
             { "title": "Added", "kind": "inspect", "chapter": "a" }
           ]
         }
         """);
      _modifiedStop = (string)plan["plan"]!["stops"]![0]!["id"]!;
      _renamedStop = (string)plan["plan"]!["stops"]![1]!["id"]!;
      _addedStop = (string)plan["plan"]!["stops"]![2]!["id"]!;
      await PutStopAsync(_modifiedStop, "src/modified.txt", 2);
      await PutStopAsync(_renamedStop, "src/renamed.txt", 3, "The [renamed line](ref:head/src/renamed.txt#L3-3) changes, and [the first](ref:head/src/renamed.txt#L1-1) stays, but the code `ref:head/src/renamed.txt#L3-3` stays as it is.");
      await PutStopAsync(_addedStop, "src/added.txt", 1);
      _uiCredential = await Harness.UiCredentialAsync();
   }

   public async ValueTask DisposeAsync()
   {
      Lifecycle.BeforeUpdatePass = null;
      await _tour.DisposeAsync();
   }

   [Fact]
   public async Task Update_repins_the_tour_moves_untouched_passages_and_marks_the_stops_with_changed_passages_stale()
   {
      var newHead = await PushAsync();
      await RefreshAsync();

      var updated = await UpdateAsync();
      var tour = await ReadAsync();

      Assert.Equal(("pinned", newHead.Value, 2), ((string)tour["pin"]!["state"]!, (string)tour["pin"]!["head"]!, (int)tour["pin"]!["iteration"]!));
      Assert.True(JsonNode.DeepEquals(tour["pin"], updated["pin"]), $"The update returned another pin: {updated["pin"]}");
      Assert.Equal((string)tour["updatedAt"]!, (string)updated["updatedAt"]!);
      Assert.Null(tour["availableIteration"]);
      Assert.Equal([_modifiedStop], Ids(updated["staleStops"]));

      var modified = StopOf(tour, _modifiedStop);
      Assert.Equal(2, (int)modified["staleOn"]!);
      Assert.Equal((1, "ready"), ((int)modified["version"]!, (string)modified["status"]!));
      Assert.Equal((_tour.Head.Value, 2, "2"), PassageOf(modified));

      var renamed = StopOf(tour, _renamedStop);
      Assert.Null(renamed["staleOn"]);
      Assert.Equal((1, "ready"), ((int)renamed["version"]!, (string)renamed["status"]!));
      Assert.Equal((newHead.Value, 4, "GAMMA"), PassageOf(renamed));
      Assert.Equal(
         "The [renamed line](ref:head/src/renamed.txt#L4-4) changes, and [the first](ref:head/src/renamed.txt#L2-2) stays, but the code `ref:head/src/renamed.txt#L3-3` stays as it is.",
         (string)renamed["content"]!["how"]!);

      var added = StopOf(tour, _addedStop);
      Assert.Null(added["staleOn"]);
      Assert.Equal((newHead.Value, 1, "new"), PassageOf(added));
      Assert.All(tour["plan"]!["stops"]!.AsArray(), stop => Assert.Empty(stop!["earlier"]!.AsArray()));
   }

   /// <summary>The same destination as the moved link sits in a code span, an image, an indented code block and a fenced code block, none of which the prose reads as a code reference.</summary>
   [Fact]
   public async Task Update_rewrites_a_moved_link_only_where_the_prose_reads_a_code_reference()
   {
      const string Kept = "code `[x](ref:head/src/renamed.txt#L3-3)`, image ![x](ref:head/src/renamed.txt#L3-3).\n\n    [x](ref:head/src/renamed.txt#L3-3)\n\n~~~\n[x](ref:head/src/renamed.txt#L3-3)\n~~~";
      await PutStopAsync(_renamedStop, "src/renamed.txt", 3, Json($"The [line](ref:head/src/renamed.txt#L3-3), {Kept}"));
      await PushAsync();
      await RefreshAsync();

      await UpdateAsync();
      var renamed = StopOf(await ReadAsync(), _renamedStop);

      Assert.Null(renamed["staleOn"]);
      Assert.Equal($"The [line](ref:head/src/renamed.txt#L4-4), {Kept}", (string)renamed["content"]!["how"]!);
   }

   /// <summary>An inline link in angle brackets, a link through a reference definition, an autolink and a link in an image's text are each read as a code reference, so each moves.</summary>
   [Fact]
   public async Task Update_rewrites_a_moved_link_in_each_form_that_the_prose_reads()
   {
      await PutStopAsync(_renamedStop, "src/renamed.txt", 3, Json("See [a](<ref:head/src/renamed.txt#L3-3>), [b][d], <ref:head/src/renamed.txt#L1-1> and ![c [d](ref:head/src/renamed.txt#L3-3)](e.png).\n\n[d]: ref:head/src/renamed.txt#L3-3"));
      await PushAsync();
      await RefreshAsync();

      await UpdateAsync();
      var renamed = StopOf(await ReadAsync(), _renamedStop);

      Assert.Null(renamed["staleOn"]);
      Assert.Equal("See [a](<ref:head/src/renamed.txt#L4-4>), [b][d], <ref:head/src/renamed.txt#L2-2> and ![c [d](ref:head/src/renamed.txt#L4-4)](e.png).\n\n[d]: ref:head/src/renamed.txt#L4-4", (string)renamed["content"]!["how"]!);
   }

   /// <summary>
   /// The reviewer selected an autolink's shown text, which the update writes with the moved lines. The anchor keeps its version and the thread stays open, because the link points at the same code.
   /// </summary>
   [Fact]
   public async Task Update_keeps_a_stop_text_thread_on_a_rewritten_field_open_on_its_version()
   {
      await PutStopAsync(_renamedStop, "src/renamed.txt", 3, "See <ref:head/src/renamed.txt#L3-3>.");
      var asked = await Calls.CallAsync(
         Ui(On("ask_question", _tour.TourId)),
         $$"""{ "text": "Why?", "anchor": { "kind": "stopText", "stopId": "{{_renamedStop}}", "field": "how", "text": "src/renamed.txt#L3-3" } }""");
      await ReplyAsync(QuestionIdOf(asked));
      await PushAsync();
      await RefreshAsync();

      await UpdateAsync();
      var tour = await ReadAsync();

      Assert.Equal("See <ref:head/src/renamed.txt#L4-4>.", (string)StopOf(tour, _renamedStop)["content"]!["how"]!);
      var thread = ThreadOf(tour["threads"]!.AsArray(), ThreadIdOf(asked));
      Assert.Equal(("open", 2), ((string)thread["state"]!, (int)thread["anchor"]!["version"]!));
   }

   /// <summary>A backslash escape in the link's destination: the stored text is not the destination that the remap read, so the update cannot write the new place.</summary>
   [Fact]
   public async Task Update_marks_a_stop_stale_when_a_moved_link_holds_a_backslash_escape()
   {
      const string How = @"The [line](ref:head/src/renamed\.txt#L3-3) moves.";
      await PutStopAsync(_renamedStop, "src/renamed.txt", 3, Json(How));
      await PushAsync();
      await RefreshAsync();

      await UpdateAsync();
      var renamed = StopOf(await ReadAsync(), _renamedStop);

      Assert.Equal(2, (int)renamed["staleOn"]!);
      Assert.Equal(How, (string)renamed["content"]!["how"]!);
   }

   [Fact]
   public async Task Update_moves_untouched_anchors_and_outdates_an_open_thread_whose_code_anchor_keeps_its_commit()
   {
      var newHead = await PushAsync();
      var answered = await AskAboutAsync("src/modified.txt", 2);
      await ReplyAsync(QuestionIdOf(answered));
      var moved = await AskAboutAsync("src/renamed.txt", 3);
      var waiting = await AskAboutAsync("src/modified.txt", 2);
      var resolved = await AskAboutAsync("src/modified.txt", 2);
      await ReplyAsync(QuestionIdOf(resolved));
      await Calls.CallAsync(Ui(On("set_thread_state", _tour.TourId, threadId: ThreadIdOf(resolved))), """{ "state": "resolved" }""");
      var note = await Calls.CallAsync(Ui(On("save_note", _tour.TourId)), CodeBody("Check this line.", "src/modified.txt", 2));
      await RefreshAsync();

      await UpdateAsync();
      var threads = (await ReadAsync())["threads"]!.AsArray();

      var outdated = ThreadOf(threads, ThreadIdOf(answered));
      Assert.Equal("outdated", (string)outdated["state"]!);
      Assert.True(JsonNode.DeepEquals(answered["anchor"], outdated["anchor"]), $"The stale anchor changed: {outdated["anchor"]}");
      Assert.Equal(_tour.Head.Value, (string)outdated["anchor"]!["commit"]!);
      var movedThread = ThreadOf(threads, ThreadIdOf(moved));
      Assert.Equal("open", (string)movedThread["state"]!);
      var movedAnchor = movedThread["anchor"]!;
      Assert.Equal((newHead.Value, 4, 4, "GAMMA"), ((string)movedAnchor["commit"]!, (int)movedAnchor["startLine"]!, (int)movedAnchor["endLine"]!, (string)movedAnchor["text"]!));
      Assert.Equal("open", (string)ThreadOf(threads, ThreadIdOf(waiting))["state"]!);
      Assert.Equal("resolved", (string)ThreadOf(threads, ThreadIdOf(resolved))["state"]!);
      var outdatedNote = ThreadOf(threads, ThreadIdOf(note["thread"]!));
      Assert.Equal(("outdated", _tour.Head.Value), ((string)outdatedNote["state"]!, (string)outdatedNote["anchor"]!["commit"]!));
   }

   [Fact]
   public async Task Update_publishes_the_pin_the_cleared_notice_the_plan_and_the_changed_threads_at_the_stored_time()
   {
      await PushAsync();
      var answered = await AskAboutAsync("src/modified.txt", 2);
      await ReplyAsync(QuestionIdOf(answered));
      await RefreshAsync();
      await using var events = await EventStream.OpenAsync(Harness);

      var updated = await UpdateAsync();
      var received = new List<(string Type, JsonNode Data)>();
      for (var i = 0; i < 5; i++)
      {
         received.Add(await events.NextAsync(EventWait));
      }

      Assert.Equal(
         [ServerEvents.TourPinned.Name, ServerEvents.AvailableIterationChanged.Name, ServerEvents.PlanSet.Name, ServerEvents.ThreadUpdated.Name, ServerEvents.ThreadCreated.Name],
         received.Select(received => received.Type));
      Assert.All(received, received => Assert.Equal((string)updated["updatedAt"]!, (string)received.Data["updatedAt"]!));
      Assert.True(JsonNode.DeepEquals(updated["pin"], received[0].Data["pin"]), "The tourPinned event holds another pin.");
      Assert.Null(received[1].Data["availableIteration"]);
      Assert.Equal(2, (int)received[2].Data["plan"]!["stops"]![0]!["staleOn"]!);
      Assert.Equal(("outdated", ThreadIdOf(answered)), ((string)received[3].Data["thread"]!["state"]!, ThreadIdOf(received[3].Data["thread"]!)));
      Assert.Equal("iterationUpdated", (string)received[4].Data["thread"]!["decision"]!["kind"]!);
   }

   [Fact]
   [Trait("Category", "TourActions")]
   public async Task Update_sends_an_iterationUpdate_action_naming_the_stale_stops_to_the_bound_session()
   {
      await PushAsync();
      await RefreshAsync();
      var session = await Harness.AgentAsync();

      var updated = await UpdateAsync();
      var sent = await NextQuestionAsync(session);

      var thread = updated["thread"]!;
      var question = Assert.Single(thread["messages"]!.AsArray())!;
      var action = new JsonObject { ["kind"] = "iterationUpdated", ["iteration"] = 2, ["stopIds"] = new JsonArray(_modifiedStop) };
      Assert.True(JsonNode.DeepEquals(action, thread["decision"]), $"action: {thread["decision"]}");
      Assert.True(JsonNode.DeepEquals(new JsonObject { ["kind"] = "tour" }, thread["anchor"]), $"anchor: {thread["anchor"]}");
      Assert.Equal(("", "pending", session.Id), ((string)question["text"]!, (string)RequestOf(thread)["state"]!, (string)RequestOf(thread)["sessionId"]!));
      Assert.Equal(("iterationUpdated", QuestionIdOf(thread)), ((string?)sent.Data["decision"], (string?)sent.Data["questionId"]));
      Assert.True(JsonNode.DeepEquals(thread, ThreadOf((await ReadAsync())["threads"]!.AsArray(), ThreadIdOf(thread))), "The stored thread differs from the update's result.");
   }

   [Fact]
   [Trait("Category", "TourActions")]
   public async Task Put_stop_on_a_stale_stop_against_the_new_pin_clears_its_marker()
   {
      await PushAsync();
      await RefreshAsync();
      await UpdateAsync();

      var stop = await PutStopAsync(_modifiedStop, "src/modified.txt", 2);
      var agentRead = await Harness.AgentCallAsync(On("read_state", _tour.TourId));

      Assert.Null(stop["staleOn"]);
      Assert.Equal(2, (int)stop["version"]!);
      Assert.Null(StopOf(agentRead, _modifiedStop)["staleOn"]);
   }

   [Fact]
   [Trait("Category", "TourActions")]
   public async Task Agent_read_reports_the_stale_stops()
   {
      await PushAsync();
      await RefreshAsync();
      await UpdateAsync();

      var agentRead = await Harness.AgentCallAsync(On("read_state", _tour.TourId));

      Assert.Equal(2, (int)StopOf(agentRead, _modifiedStop)["staleOn"]!);
      Assert.Null(StopOf(agentRead, _addedStop)["staleOn"]);
   }

   [Fact]
   [Trait("Category", "TourActions")]
   public async Task Resend_of_an_iterationUpdate_action_goes_while_a_named_stop_is_stale_and_is_refused_once_none_is()
   {
      await PushAsync();
      await RefreshAsync();
      var thread = (await UpdateAsync())["thread"]!;
      var questionId = QuestionIdOf(thread);
      await Calls.CallAsync(Ui(On("cancel_question", _tour.TourId, questionId: questionId)));

      var resent = await Calls.CallAsync(Ui(On("resend_question", _tour.TourId, questionId: questionId)));
      await PutStopAsync(_modifiedStop, "src/modified.txt", 2);
      await Calls.CallAsync(Ui(On("cancel_question", _tour.TourId, questionId: questionId)));
      using var refused = await Calls.SendAsync(Ui(On("resend_question", _tour.TourId, questionId: questionId)));

      Assert.Equal("pending", (string)RequestOf(resent, index: 1)["state"]!);
      var error = await ErrorAsync(refused, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Contains("no longer stale", (string)error["reason"]!, StringComparison.Ordinal);
   }

   /// <summary>
   /// The push renames <c>src/added.txt</c>, deletes <c>src/line-endings.txt</c> and leaves <c>src/renamed.txt</c> unchanged, and <c>src/absent.txt</c> is in neither commit, so only the mark on the
   /// renamed file has a new path to take.
   /// </summary>
   [Fact]
   public async Task Update_moves_a_file_mark_through_a_rename_at_the_head_and_keeps_every_other_mark_as_stored()
   {
      await SetFileMarksAsync("src/added.txt", "src/line-endings.txt", "src/renamed.txt", "src/absent.txt");
      await PushRenameAsync();
      await RefreshAsync();

      await UpdateAsync();

      Assert.Equal(["src/moved.txt", "src/line-endings.txt", "src/renamed.txt", "src/absent.txt"], MarkPaths(await ReadAsync()));
   }

   /// <summary>A mark already holds the path that a rename at the head gives another mark, so that other mark keeps its stored path and the plan keeps one mark for each path.</summary>
   [Fact]
   public async Task Update_keeps_a_file_mark_as_stored_when_its_new_path_holds_another_mark()
   {
      await SetFileMarksAsync("src/added.txt", "src/moved.txt");
      await PushRenameAsync();
      await RefreshAsync();

      await UpdateAsync();

      Assert.Equal(["src/added.txt", "src/moved.txt"], MarkPaths(await ReadAsync()));
   }

   /// <summary>
   /// The push renames <c>src/renamed.txt</c> and <c>src/line-endings.txt</c> to paths with a backslash, which git allows on Linux. The stop on the first and the mark on the second keep their
   /// stored paths, so the update stores. The code anchor on the first keeps its stored path and commit too.
   /// </summary>
   [Fact]
   public async Task Update_across_a_rename_to_a_path_with_a_backslash_marks_the_stop_stale_and_keeps_the_file_mark_and_the_code_anchor_as_stored()
   {
      Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows file names cannot hold a backslash.");
      await SetFileMarksAsync("src/line-endings.txt");
      var asked = await AskAboutAsync("src/renamed.txt", 3);
      await ReplyAsync(QuestionIdOf(asked));
      _tour.Git.Checkout("feature");
      _tour.Git.Git(_tour.Git.OriginPath, "mv", "src/renamed.txt", @"src\renamed.txt");
      _tour.Git.Git(_tour.Git.OriginPath, "mv", "src/line-endings.txt", @"src\line-endings.txt");
      var newHead = _tour.Git.Commit("Rename to paths with a backslash");
      _tour.Git.Checkout("main");
      _tour.Platform.PullRequest(_tour.Git.Url, newHead, _tour.Target, iteration: 2);
      await Harness.PinsIdleAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);
      await RefreshAsync();

      var updated = await UpdateAsync();
      var tour = await ReadAsync();

      Assert.Equal([_renamedStop], Ids(updated["staleStops"]));
      Assert.Equal(newHead.Value, (string)tour["pin"]!["head"]!);
      var renamed = StopOf(tour, _renamedStop);
      Assert.Equal((2, "src/renamed.txt"), ((int)renamed["staleOn"]!, (string)renamed["content"]!["passages"]![0]!["path"]!));
      Assert.Equal(["src/line-endings.txt"], MarkPaths(tour));
      var thread = ThreadOf(tour["threads"]!.AsArray(), ThreadIdOf(asked));
      Assert.Equal(("outdated", "src/renamed.txt", _tour.Head.Value), ((string)thread["state"]!, (string)thread["anchor"]!["path"]!, (string)thread["anchor"]!["commit"]!));
   }

   [Fact]
   public async Task File_marks_that_set_plan_changes_while_the_update_runs_are_mapped_again_before_the_update_stores()
   {
      await SetFileMarksAsync("src/renamed.txt");
      await PushRenameAsync();
      await RefreshAsync();
      var gate = HoldBeforePasses(out var reached);

      var update = UpdateAsync();
      await reached.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
      await SetFileMarksAsync("src/added.txt");
      gate.SetResult();
      await update.WaitAsync(Timeout, TestContext.Current.CancellationToken);

      Assert.Equal(["src/moved.txt"], MarkPaths(await ReadAsync()));
   }

   [Fact]
   public async Task Update_with_no_stale_stops_repins_and_sends_no_action()
   {
      _tour.Git.Checkout("feature");
      var newHead = _tour.Git.Commit("Push", ("src/same.txt", "same again\n"));
      _tour.Git.Checkout("main");
      _tour.Platform.PullRequest(_tour.Git.Url, newHead, _tour.Target, iteration: 2);
      await RefreshAsync();

      var updated = await UpdateAsync();
      var tour = await ReadAsync();

      Assert.Null(updated["thread"]);
      Assert.Empty(updated["staleStops"]!.AsArray());
      Assert.Equal(newHead.Value, (string)tour["pin"]!["head"]!);
      Assert.Empty(tour["threads"]!.AsArray());
   }

   [Fact]
   public async Task Update_without_an_attached_session_is_refused_and_stores_nothing()
   {
      await PushAsync();
      await RefreshAsync();
      await Harness.DropSessionAsync(await Harness.AgentAsync());
      var before = await ReadAsync();

      using var response = await Calls.SendAsync(Ui(On("update_iteration", _tour.TourId)), ShownInput());

      var error = await ErrorAsync(response, HttpStatusCode.Conflict, "noSessionAttached");
      Assert.Contains("the move to the new update", (string)error["reason"]!, StringComparison.Ordinal);
      var after = await ReadAsync();
      Assert.True(JsonNode.DeepEquals(before, after), "The refused update changed the tour.");
      Assert.Equal(2, (int)after["availableIteration"]!["pin"]!["iteration"]!);
   }

   [Fact]
   public async Task Update_without_an_available_iteration_is_refused()
   {
      using var response = await Calls.SendAsync(Ui(On("update_iteration", _tour.TourId)), ShownInput());

      await ErrorAsync(response, HttpStatusCode.Conflict, "iterationNotAvailable");
   }

   [Fact]
   public async Task Update_whose_pin_holds_an_unknown_member_is_refused_and_stores_nothing()
   {
      await PushAsync();
      await RefreshAsync();
      var shown = ShownInput();

      using var response = await Calls.SendAsync(Ui(On("update_iteration", _tour.TourId)), shown.Replace("\"iteration\":", "\"extra\":1,\"iteration\":", StringComparison.Ordinal));

      var error = await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("pin.extra", (string)error["field"]!);
      Assert.Equal(1, (int)(await ReadAsync())["pin"]!["iteration"]!);
   }

   /// <summary>The notice showed iteration 2, and a check stored iteration 3 before the reviewer's update.</summary>
   [Fact]
   public async Task Update_to_another_iteration_than_the_tour_holds_is_refused_and_stores_nothing()
   {
      await PushAsync();
      await RefreshAsync();
      var shown = ShownInput();
      await PushThirdAsync();
      await RefreshAsync();

      using var response = await Calls.SendAsync(Ui(On("update_iteration", _tour.TourId)), shown);

      var error = await ErrorAsync(response, HttpStatusCode.Conflict, "iterationNotAvailable");
      Assert.Equal("pin", (string)error["field"]!);
      Assert.Contains("moved on to update 3 since the notice showed update 2", (string)error["reason"]!, StringComparison.Ordinal);
      Assert.Contains("Read the notice for update 3", (string)error["reason"]!, StringComparison.Ordinal);
      var tour = await ReadAsync();
      Assert.Equal((1, 3), ((int)tour["pin"]!["iteration"]!, (int)tour["availableIteration"]!["pin"]!["iteration"]!));
      Assert.Empty(tour["threads"]!.AsArray());
   }

   /// <summary>The head moves on to iteration 3 after the notice showed iteration 2, which the update does not ask the platform about.</summary>
   [Fact]
   public async Task Update_after_the_head_moved_again_moves_onto_the_shown_iteration_and_the_check_after_it_shows_the_newer_one()
   {
      var second = await PushAsync();
      await RefreshAsync();
      _tour.Git.Checkout("feature");
      var third = _tour.Git.Commit("Third", ("src/added.txt", "newer\n"));
      _tour.Git.Checkout("main");
      _tour.Platform.PullRequest(_tour.Git.Url, third, _tour.Target, iteration: 3);

      var updated = await UpdateAsync();
      await Harness.PinsIdleAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);

      Assert.Equal((2, second.Value), ((int)updated["pin"]!["iteration"]!, (string)updated["pin"]!["head"]!));
      var tour = await ReadAsync();
      Assert.Equal((3, third.Value), ((int)tour["availableIteration"]!["pin"]!["iteration"]!, (string)tour["availableIteration"]!["pin"]!["head"]!));
      Assert.Equal([_modifiedStop, _addedStop], Ids(tour["availableIteration"]!["staleStops"]));
   }

   /// <summary>The target moves onto the new head after the notice showed iteration 2, so iteration 2 keeps its number and head and gets another merge base, which the update does not ask about.</summary>
   [Fact]
   public async Task Update_after_the_merge_base_alone_moved_moves_onto_the_merge_base_that_the_notice_showed()
   {
      var newHead = await PushAsync();
      await RefreshAsync();
      _tour.Git.Checkout("feature");
      _tour.Git.Checkout("target-moves", create: true);
      var target = _tour.Git.Commit("Target moves", ("src/same.txt", "same on the target\n"));
      _tour.Git.Checkout("main");
      _tour.Platform.PullRequest(_tour.Git.Url, newHead, target, iteration: 2);

      await UpdateAsync();
      await Harness.PinsIdleAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);

      var tour = await ReadAsync();
      Assert.Equal((_tour.MergeBase.Value, newHead.Value), ((string)tour["pin"]!["mergeBase"]!, (string)tour["pin"]!["head"]!));
      Assert.Null(tour["availableIteration"]);
   }

   /// <summary>The platform reports the pinned head again after the notice showed iteration 2. The update moves the tour onto iteration 2, so the check after it shows the earlier head as the next iteration.</summary>
   [Fact]
   public async Task Update_after_the_head_went_back_to_the_pinned_one_moves_onto_the_shown_iteration_and_the_check_after_it_shows_the_earlier_head()
   {
      var newHead = await PushAsync();
      await RefreshAsync();
      _tour.Platform.PullRequest(_tour.Git.Url, _tour.Head, _tour.Target);

      await UpdateAsync();
      await Harness.PinsIdleAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);

      var tour = await ReadAsync();
      Assert.Equal(newHead.Value, (string)tour["pin"]!["head"]!);
      Assert.Equal((1, _tour.Head.Value), ((int)tour["availableIteration"]!["pin"]!["iteration"]!, (string)tour["availableIteration"]!["pin"]!["head"]!));
   }

   [Fact]
   public async Task Update_while_the_platform_fails_moves_onto_the_shown_iteration_without_the_platform()
   {
      var newHead = await PushAsync();
      await RefreshAsync();
      _tour.Platform.Fail("The sign-in expired. Run 'az login', then try again.");

      var updated = await UpdateAsync();

      Assert.Equal(newHead.Value, (string)updated["pin"]!["head"]!);
      Assert.Equal([_modifiedStop], Ids(updated["staleStops"]));
      await Harness.PinsIdleAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);
      Assert.Null((await ReadAsync())["availableIteration"]);
   }

   [Fact]
   [Trait("Category", "TourActions")]
   public async Task Update_while_an_earlier_update_waits_is_refused()
   {
      await PushAsync();
      await RefreshAsync();
      var first = await UpdateAsync();
      await PushThirdAsync();
      await RefreshAsync();

      using var response = await Calls.SendAsync(Ui(On("update_iteration", _tour.TourId)), ShownInput());

      var error = await ErrorAsync(response, HttpStatusCode.Conflict, "questionWaiting");
      Assert.Contains("Claude still revises the stops that the last update made stale.", (string)error["reason"]!, StringComparison.Ordinal);
      Assert.DoesNotContain(ThreadIdOf(first["thread"]!), (string)error["reason"]!, StringComparison.Ordinal);
   }

   /// <summary>A stop that an earlier update made stale keeps its marker and its content, because its code references read against the pin before that update.</summary>
   [Fact]
   [Trait("Category", "TourActions")]
   public async Task Second_update_keeps_an_already_stale_stop_as_it_is_and_names_it_again()
   {
      await PushAsync();
      await RefreshAsync();
      var first = await UpdateAsync();
      await ReplyAsync(QuestionIdOf(first["thread"]!));
      var newHead = await PushThirdAsync();
      var found = await RefreshAsync();
      var before = StopOf(await ReadAsync(), _modifiedStop);

      var updated = await UpdateAsync();
      var tour = await ReadAsync();

      Assert.Equal([_modifiedStop], Ids(found["availableIteration"]!["staleStops"]));
      Assert.Equal(newHead.Value, (string)tour["pin"]!["head"]!);
      Assert.True(JsonNode.DeepEquals(before, StopOf(tour, _modifiedStop)), $"The stale stop changed: {StopOf(tour, _modifiedStop)}");
      Assert.Equal([_modifiedStop], Ids(updated["thread"]!["decision"]!["stopIds"]));
      Assert.Equal(3, (int)updated["thread"]!["decision"]!["iteration"]!);
   }

   [Fact]
   public async Task Update_of_a_plan_first_tour_whose_plan_was_approved_does_not_make_the_plan_wait_again()
   {
      var session = await Harness.AgentAsync();
      var tourId = (string)(await Calls.CallAsync(
         Calls.Agent(session, "create_tour"),
         $$"""{ "prUrl": "{{TestPlatform.PrUrl}}", "familiarity": "some", "planFirst": true }"""))["id"]!;
      await Harness.PinsIdleAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);
      var plan = await Harness.AgentCallAsync(
         On("set_plan", tourId),
         """{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "title": "Modified", "kind": "inspect", "chapter": "a" }] }""");
      var stopId = (string)plan["plan"]!["stops"]![0]!["id"]!;
      var approval = await Calls.CallAsync(Ui(On("approve_plan", tourId)), $$"""{ "stopIds": ["{{stopId}}"], "skippedStops": [], "familiarity": "some" }""");
      await ReplyAsync(QuestionIdOf(approval["thread"]!), tourId);
      await PutStopAsync(stopId, "src/modified.txt", 2, tourId: tourId);
      await PushAsync();
      await Calls.CallAsync(Ui(On("check_iteration", tourId)));

      await Calls.CallAsync(Ui(On("update_iteration", tourId)), ShownInput(tourId));
      var revised = await PutStopAsync(stopId, "src/modified.txt", 2, tourId: tourId);

      Assert.Equal("given", (string)(await Harness.CallAsync(On("read_state", tourId)))["planApproval"]!);
      Assert.Equal(2, (int)revised["version"]!);
   }

   [Fact]
   public async Task Put_stop_that_lands_while_the_update_waits_for_the_lock_is_mapped_before_the_update_stores()
   {
      await PushAsync();
      await RefreshAsync();
      var gate = HoldBeforePasses(out var reached);

      var update = UpdateAsync();
      await reached.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
      await PutStopAsync(_addedStop, "src/modified.txt", 2);
      gate.SetResult();
      var updated = await update.WaitAsync(Timeout, TestContext.Current.CancellationToken);

      Assert.Equal([_modifiedStop, _addedStop], Ids(updated["staleStops"]));
      var added = StopOf(await ReadAsync(), _addedStop);
      Assert.Equal((2, 2), ((int)added["staleOn"]!, (int)added["version"]!));
   }

   [Fact]
   public async Task Session_that_drops_while_the_update_runs_leaves_the_tour_unchanged()
   {
      await PushAsync();
      await RefreshAsync();
      var gate = HoldBeforePasses(out var reached);
      var before = await ReadAsync();

      var update = Harness.SendAsync(Ui(On("update_iteration", _tour.TourId)), ShownInput());
      await reached.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
      await Harness.DropSessionAsync(await Harness.AgentAsync());
      gate.SetResult();
      using var response = await update.WaitAsync(Timeout, TestContext.Current.CancellationToken);

      await ErrorAsync(response, HttpStatusCode.Conflict, "noSessionAttached");
      var after = await ReadAsync();
      var expected = before.DeepClone();
      expected["revision"] = (long)before["revision"]! + 1;
      Assert.True(JsonNode.DeepEquals(expected, after), $"The refused update changed more than the revision that the drop's release raised: {after}");
   }

   [Fact]
   [Trait("Category", "TourActions")]
   public async Task Take_over_while_the_update_runs_sends_the_action_to_the_new_session()
   {
      await PushAsync();
      await RefreshAsync();
      var gate = HoldBeforePasses(out var reached);
      await using var other = await AgentSessionConnection.OpenAsync(Harness);

      var update = UpdateAsync();
      await reached.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
      await Calls.CallAsync(Calls.Agent(other, On("attach_tour", _tour.TourId)), """{ "takeOver": true }""");
      gate.SetResult();
      var updated = await update.WaitAsync(Timeout, TestContext.Current.CancellationToken);
      var sent = await NextQuestionAsync(other);

      Assert.Equal(other.Id, (string)RequestOf(updated["thread"]!)["sessionId"]!);
      Assert.Equal(("iterationUpdated", QuestionIdOf(updated["thread"]!)), ((string?)sent.Data["decision"], (string?)sent.Data["questionId"]));
   }

   [Fact]
   public async Task Two_updates_that_race_store_one_update_and_refuse_the_other()
   {
      await PushAsync();
      await RefreshAsync();
      var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      var arrived = 0;
      var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      Lifecycle.BeforeUpdatePass = async _ =>
      {
         if (Interlocked.Increment(ref arrived) == 2)
         {
            both.TrySetResult();
         }

         await gate.Task;
      };

      var first = SendUpdateAsync();
      var second = SendUpdateAsync();
      await both.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
      gate.SetResult();
      using var firstResponse = await first.WaitAsync(Timeout, TestContext.Current.CancellationToken);
      using var secondResponse = await second.WaitAsync(Timeout, TestContext.Current.CancellationToken);

      var statuses = new[] { firstResponse.StatusCode, secondResponse.StatusCode };
      Assert.Single(statuses, status => status == HttpStatusCode.OK);
      await ErrorAsync(firstResponse.StatusCode == HttpStatusCode.OK ? secondResponse : firstResponse, HttpStatusCode.Conflict, "iterationNotAvailable");
      var tour = await ReadAsync();
      Assert.Single(tour["threads"]!.AsArray());
      Assert.Equal(2, (int)tour["pin"]!["iteration"]!);
   }

   [Fact]
   public async Task Iteration_check_that_ends_after_the_update_stores_nothing()
   {
      var newHead = await PushAsync();
      await RefreshAsync();
      var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      var url = new Uri(_tour.Git.Url.AbsoluteUri);
      var calls = 0;
      _tour.Platform.Answer(async cancellationToken =>
      {
         if (Interlocked.Increment(ref calls) == 1)
         {
            await gate.Task.WaitAsync(cancellationToken);
         }

         return new ResolvedPullRequest(url, newHead, _tour.Target, 2, PullRequestState.Active);
      });

      var refresh = RefreshAsync();
      await Poll.UntilAsync(() => Volatile.Read(ref calls) > 0 ? "" : null, Timeout, () => "The check did not resolve the pull request.");
      var updated = await UpdateAsync();
      gate.SetResult();
      var checkedIteration = await refresh.WaitAsync(Timeout, TestContext.Current.CancellationToken);
      await Harness.PinsIdleAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);

      Assert.Null(checkedIteration["availableIteration"]);
      var tour = await ReadAsync();
      Assert.Null(tour["availableIteration"]);
      Assert.Equal((string)updated["updatedAt"]!, (string)tour["updatedAt"]!);
   }

   private static string[] Ids(JsonNode? node) => [.. node!.AsArray().Select(id => (string)id!)];

   private static string[] MarkPaths(JsonNode tour) => [.. tour["plan"]!["fileMarks"]!.AsArray().Select(mark => (string)mark!["path"]!)];

   // Returns the text as the inside of a JSON string, for PutStopAsync's prose.
   private static string Json(string text) => JsonValue.Create(text).ToJsonString()[1..^1];

   private static JsonNode StopOf(JsonNode tour, string stopId) => tour["plan"]!["stops"]!.AsArray().Single(stop => (string)stop!["id"]! == stopId)!;

   private static JsonNode ThreadOf(JsonArray threads, string threadId) => threads.Single(thread => ThreadIdOf(thread!) == threadId)!;

   private static (string Commit, int StartLine, string Text) PassageOf(JsonNode stop)
   {
      var passage = stop["content"]!["passages"]![0]!;
      return ((string)passage["commit"]!, (int)passage["startLine"]!, (string)passage["text"]!);
   }

   private static string CodeBody(string text, string path, int line) =>
      $$"""{ "text": "{{text}}", "anchor": { "kind": "code", "side": "head", "path": "{{path}}", "startLine": {{line}}, "endLine": {{line}} } }""";

   private static async Task<(string Type, JsonNode Data)> NextQuestionAsync(AgentSessionConnection session)
   {
      while (true)
      {
         var received = await session.Stream.NextAsync(EventWait);
         if (received.Type == SessionEvents.Question)
         {
            return received;
         }
      }
   }

   private async Task<JsonNode> PutStopAsync(string stopId, string path, int line, string? how = null, string? tourId = null)
   {
      var prose = how is null ? "" : $$""", "how": "{{how}}" """;
      return await Harness.AgentCallAsync(
         On("put_stop", tourId ?? _tour.TourId, stopId: stopId),
         $$"""{ "headline": "The change.", "passages": [{ "side": "head", "path": "{{path}}", "startLine": {{line}}, "endLine": {{line}}, "role": "primary" }]{{prose}} }""");
   }

   // Pushes a commit to the source branch that changes line 2 of src/modified.txt and adds a line above the others of src/renamed.txt, and makes the platform report it as iteration 2.
   private async Task<CommitId> PushAsync()
   {
      _tour.Git.Checkout("feature");
      var newHead = _tour.Git.Commit("Push", ("src/modified.txt", "one\nTWO\nthree\nfour\nfive\n"), ("src/renamed.txt", "zero\n" + PinnedTourFixture.RenamedHead));
      _tour.Git.Checkout("main");
      _tour.Platform.PullRequest(_tour.Git.Url, newHead, _tour.Target, iteration: 2);
      await Harness.PinsIdleAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);
      return newHead;
   }

   // Pushes a commit to the source branch that moves src/added.txt to src/moved.txt and deletes src/line-endings.txt, and makes the platform report it as iteration 2.
   private async Task PushRenameAsync()
   {
      _tour.Git.Checkout("feature");
      var newHead = _tour.Git.Commit("Rename", ("src/added.txt", null), ("src/moved.txt", "new\n"), ("src/line-endings.txt", null));
      _tour.Git.Checkout("main");
      _tour.Platform.PullRequest(_tour.Git.Url, newHead, _tour.Target, iteration: 2);
      await Harness.PinsIdleAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);
   }

   // Sets the plan again with its three stops and a generated file mark on each path, in order.
   private async Task SetFileMarksAsync(params string[] paths)
   {
      var marks = string.Join(", ", paths.Select(path => $$"""{ "path": "{{path}}", "mark": "generated", "reason": "A tool writes it." }"""));
      await Harness.AgentCallAsync(
         On("set_plan", _tour.TourId),
         $$"""
         {
           "chapters": [{ "id": "a", "title": "A" }],
           "stops": [
             { "id": "{{_modifiedStop}}", "title": "Modified", "kind": "inspect", "chapter": "a" },
             { "id": "{{_renamedStop}}", "title": "Renamed", "kind": "inspect", "chapter": "a" },
             { "id": "{{_addedStop}}", "title": "Added", "kind": "inspect", "chapter": "a" }
           ],
           "fileMarks": [{{marks}}]
         }
         """);
   }

   // Pushes a commit that changes src/added.txt, as iteration 3.
   private async Task<CommitId> PushThirdAsync()
   {
      _tour.Git.Checkout("feature");
      var third = _tour.Git.Commit("Third", ("src/same.txt", "same again\n"));
      _tour.Git.Checkout("main");
      _tour.Platform.PullRequest(_tour.Git.Url, third, _tour.Target, iteration: 3);
      await Harness.PinsIdleAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);
      return third;
   }

   // Holds the update before each of its passes under the lock until the returned gate completes. Reached completes when the update arrives there first.
   private TaskCompletionSource HoldBeforePasses(out TaskCompletionSource reached)
   {
      var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      reached = arrived;
      Lifecycle.BeforeUpdatePass = async _ =>
      {
         arrived.TrySetResult();
         await gate.Task;
      };
      return gate;
   }

   private PreparedCall Ui(OperationCall call) => new(call, _uiCredential, null);

   private async Task<JsonNode> RefreshAsync() => await Calls.CallAsync(Ui(On("check_iteration", _tour.TourId)));

   private async Task<JsonNode> UpdateAsync() => await Calls.CallAsync(Ui(On("update_iteration", _tour.TourId)), ShownInput());

   private Task<HttpResponseMessage> SendUpdateAsync() => Calls.SendAsync(Ui(On("update_iteration", _tour.TourId)), ShownInput());

   // Returns the update's input with the tour's available iteration as the store holds it, or the tour's pin when it has none. A reviewer's read_state would nudge a check, which could store a
   // newer iteration first.
   private string ShownInput(string? tourId = null)
   {
      var tour = Writer.Read(TourId.TryParse(tourId ?? _tour.TourId, out var id) ? id : throw new ArgumentException(tourId)).Value!;
      var pin = tour.AvailableIteration?.Pin ?? (TourPin.Pinned)tour.Pin;
      return $$"""{ "pin": {{JsonSerializer.Serialize(pin, LotsenJsonContext.Default.Pinned)}} }""";
   }

   private async Task<JsonNode> AskAboutAsync(string path, int line) => await Calls.CallAsync(Ui(On("ask_question", _tour.TourId)), CodeBody("Why this line?", path, line));

   private async Task ReplyAsync(string questionId, string? tourId = null) =>
      await Harness.AgentCallAsync(On("reply", tourId ?? _tour.TourId, questionId: questionId), Done);

   private async Task<JsonNode> ReadAsync() => await Harness.CallAsync(On("read_state", _tour.TourId));
}
