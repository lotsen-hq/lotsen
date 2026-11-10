using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Operations;
using Microsoft.Extensions.DependencyInjection;
using static Lotsen.Tests.OperationCall;
using static Lotsen.Tests.ThreadCalls;

namespace Lotsen.Tests;

/// <summary>
/// Covers <c>start_thread</c>, <c>add_thread_message</c>, <c>outdate_thread</c> and <c>delete_thread</c>: the bound session starts a thread with its own message, adds messages that resolve no
/// request, marks a thread outdated and deletes one, and every other caller is refused.
/// </summary>
[Trait("Category", "Threads")]
public sealed class AgentThreadTests : IAsyncLifetime
{
   private ServerHarness _harness = null!;
   private ThreadCalls _calls = null!;

   public async ValueTask InitializeAsync()
   {
      _harness = await ServerHarness.StartAsync();
      _calls = new ThreadCalls(_harness);
   }

   public async ValueTask DisposeAsync()
   {
      Writer.Hook = null;
      await _harness.DisposeAsync();
   }

   [Fact]
   public async Task AgentThread_started_by_the_bound_session_holds_its_message_first_and_reaches_the_ui_as_an_event()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      await using var stream = await EventStream.OpenAsync(_harness);

      var started = await _calls.StartThreadAsync(session, id, "The rounding needs a test.");
      var created = await stream.NextAsync(EventWait);

      var thread = started["thread"]!;
      var message = thread["messages"]![0]!;
      Assert.Equal(id, (string)started["tourId"]!);
      Assert.Equal(("agentMessage", session.Id, "The rounding needs a test."), ((string)message["kind"]!, (string)message["sessionId"]!, (string)message["text"]!));
      Assert.Null(message["requestId"]);
      Assert.Single(thread["messages"]!.AsArray());
      Assert.Equal(("open", "tour"), ((string)thread["state"]!, (string)thread["anchor"]!["kind"]!));
      Assert.Equal(ServerEvents.ThreadCreated.Name, created.Type);
      Assert.Equal((string)started["updatedAt"]!, (string)created.Data["updatedAt"]!);
      Assert.Equal("<p>The rounding needs a test.</p>\n", (string)created.Data["thread"]!["messages"]![0]!["html"]!);
      Assert.True(JsonNode.DeepEquals(thread, (await _calls.ThreadsAsync(id))[0]), "The stored thread differs from the response's.");
   }

   [Fact]
   public async Task AgentThread_message_follows_the_earlier_messages_without_a_request_and_reaches_the_ui_as_an_event()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var asked = await _calls.AskAsync(id);
      await _calls.CallAsync(_calls.Agent(session, On("reply", id, questionId: QuestionIdOf(asked))), Done);
      await using var stream = await EventStream.OpenAsync(_harness);

      var added = await _calls.AddMessageAsync(session, id, ThreadIdOf(asked), "Folded into the revised stop.");
      var updated = await stream.NextAsync(EventWait);

      var messages = added["thread"]!["messages"]!.AsArray();
      Assert.Equal(["question", "reply", "agentMessage"], messages.Select(message => (string)message!["kind"]!));
      Assert.Equal(("Folded into the revised stop.", session.Id), ((string)messages[2]!["text"]!, (string)messages[2]!["sessionId"]!));
      Assert.Null(messages[2]!["requestId"]);
      Assert.Equal("done", (string)RequestOf(added["thread"]!)["state"]!);
      Assert.Equal(ServerEvents.ThreadUpdated.Name, updated.Type);
      Assert.Equal((string)added["updatedAt"]!, (string)updated.Data["updatedAt"]!);
      Assert.Equal("<p>Folded into the revised stop.</p>\n", (string)updated.Data["thread"]!["messages"]![2]!["html"]!);
      Assert.True(JsonNode.DeepEquals(added["thread"], (await _calls.ThreadsAsync(id))[0]), "The stored thread differs from the response's.");
   }

   [Fact]
   public async Task AgentThread_outdate_marks_the_thread_outdated_once_and_publishes_one_event()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var started = await _calls.StartThreadAsync(session, id);
      var threadId = ThreadIdOf(started["thread"]!);
      await using var stream = await EventStream.OpenAsync(_harness);

      var outdated = await _calls.CallAsync(_calls.Agent(session, On("outdate_thread", id, threadId: threadId)));
      var updated = await stream.NextAsync(EventWait);
      var again = await _calls.CallAsync(_calls.Agent(session, On("outdate_thread", id, threadId: threadId)));
      var marker = await _calls.StartThreadAsync(session, id, "Marker.");
      var next = await stream.NextAsync(EventWait);

      Assert.Equal("outdated", (string)outdated["thread"]!["state"]!);
      Assert.Equal(ServerEvents.ThreadUpdated.Name, updated.Type);
      Assert.Equal((threadId, "outdated"), ((string)updated.Data["thread"]!["id"]!, (string)updated.Data["thread"]!["state"]!));
      Assert.Equal((string)outdated["updatedAt"]!, (string)updated.Data["updatedAt"]!);
      Assert.True(JsonNode.DeepEquals(outdated, again), $"The outdate that changed nothing differs from the first: {again}");
      Assert.Equal((ServerEvents.ThreadCreated.Name, ThreadIdOf(marker["thread"]!)), (next.Type, (string)next.Data["thread"]!["id"]!));
   }

   [Fact]
   public async Task AgentThread_delete_removes_the_thread_and_reaches_the_ui_as_a_deletion_event()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var asked = await _calls.AskAsync(id);
      await _calls.CallAsync(_calls.Agent(session, On("reply", id, questionId: QuestionIdOf(asked))), Done);
      var kept = await _calls.StartThreadAsync(session, id);
      await using var stream = await EventStream.OpenAsync(_harness);

      var deleted = await _calls.CallAsync(_calls.Agent(session, On("delete_thread", id, threadId: ThreadIdOf(asked))));
      var removed = await stream.NextAsync(EventWait);

      Assert.Equal((id, ThreadIdOf(asked)), ((string)deleted["tourId"]!, (string)deleted["threadId"]!));
      Assert.Equal(ServerEvents.ThreadDeleted.Name, removed.Type);
      Assert.True(JsonNode.DeepEquals(deleted, EventStream.WithoutRevision(removed.Data)), $"The event differs from the response: {removed.Data}");
      Assert.Equal([ThreadIdOf(kept["thread"]!)], (await _calls.ThreadsAsync(id)).Select(thread => ThreadIdOf(thread!)));
   }

   /// <summary>The UI and the CLI are refused by the operations' access, as <c>AccessTests</c> covers, so only another session reaches the binding check.</summary>
   [Theory]
   [InlineData("start")]
   [InlineData("message")]
   [InlineData("outdate")]
   [InlineData("delete")]
   public async Task AgentThread_writes_by_another_session_are_refused(string action)
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      await using var other = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var started = await _calls.StartThreadAsync(session, id);
      var before = await _calls.ThreadsAsync(id);
      var (call, body) = Write(action, id, ThreadIdOf(started["thread"]!));
      using var response = await _calls.SendAsync(_calls.Agent(other, call), body);

      var error = await ErrorAsync(response, HttpStatusCode.Conflict, "attachedToAnotherSession");
      Assert.Equal("tourId", (string)error["field"]!);
      Assert.True(JsonNode.DeepEquals(before, await _calls.ThreadsAsync(id)), "The refused write changed the threads.");
   }

   /// <summary>A session that held the tour before a take-over is an unbound session too.</summary>
   [Theory]
   [InlineData("start")]
   [InlineData("message")]
   [InlineData("outdate")]
   [InlineData("delete")]
   public async Task AgentThread_writes_by_the_session_that_lost_the_tour_are_refused(string action)
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      await using var other = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var started = await _calls.StartThreadAsync(session, id);
      await _calls.CallAsync(_calls.Agent(other, On("attach_tour", id)), """{ "takeOver": true }""");
      var (call, body) = Write(action, id, ThreadIdOf(started["thread"]!));

      using var response = await _calls.SendAsync(_calls.Agent(session, call), body);

      var error = await ErrorAsync(response, HttpStatusCode.Conflict, "attachedToAnotherSession");
      Assert.Contains("attached to another session", (string)error["reason"]!, StringComparison.Ordinal);
   }

   [Theory]
   [InlineData("start", """{ "text": "  ", "anchor": { "kind": "tour" } }""", "text")]
   [InlineData("start", """{ "text": "See [this](ref:head/src)", "anchor": { "kind": "tour" } }""", "text")]
   [InlineData("start", """{ "text": "Here.", "anchor": { "kind": "stop", "stopId": "nosuchstop" } }""", "anchor.stopId")]
   [InlineData("start", """{ "text": "Here.", "anchor": { "kind": "file", "side": "head", "path": "../x" } }""", "anchor.path")]
   [InlineData("message", """{ "text": "" }""", "text")]
   [InlineData("message", """{ "text": "See [this](ref:head/src)" }""", "text")]
   public async Task AgentThread_writes_with_invalid_input_are_refused_and_store_nothing(string action, string body, string field)
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var started = await _calls.StartThreadAsync(session, id);
      var (call, _) = Write(action, id, ThreadIdOf(started["thread"]!));

      using var response = await _calls.SendAsync(_calls.Agent(session, call), body);

      var error = await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal(field, (string)error["field"]!);
      Assert.True(JsonNode.DeepEquals(new JsonArray(started["thread"]!.DeepClone()), await _calls.ThreadsAsync(id)), "The refused write changed the threads.");
   }

   /// <summary>The tour is not pinned, so it has no commits to read a file at, nor to check a link against.</summary>
   [Theory]
   [InlineData("""{ "text": "Here.", "anchor": { "kind": "file", "side": "head", "path": "src/a.txt" } }""")]
   [InlineData("""{ "text": "See [this](ref:head/src/a.txt#L1-2).", "anchor": { "kind": "tour" } }""")]
   public async Task AgentThread_start_that_needs_the_pin_is_refused_on_a_tour_that_is_not_pinned(string body)
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);

      using var response = await _calls.SendAsync(_calls.Agent(session, On("start_thread", id)), body);

      await ErrorAsync(response, HttpStatusCode.Conflict, "tourNotPinned");
      Assert.Empty(await _calls.ThreadsAsync(id));
   }

   /// <summary>The reviewer resolves the outdated thread between the message's checks and its store, so the store sees the resolved thread.</summary>
   [Fact]
   public async Task AgentThread_message_that_a_resolve_overtakes_after_the_checks_leaves_the_thread_resolved()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var threadId = ThreadIdOf((await _calls.StartThreadAsync(session, id))["thread"]!);
      await _calls.CallAsync(_calls.Agent(session, On("outdate_thread", id, threadId: threadId)));
      Writer.Hook = WriterHooks.AfterGit(async _ =>
      {
         Writer.Hook = null;
         await _calls.CallAsync(await _calls.UiAsync(On("set_thread_state", id, threadId: threadId)), """{ "state": "resolved" }""");
      }, "add_thread_message");

      var added = await _calls.AddMessageAsync(session, id, threadId);

      Assert.Equal("resolved", (string)added["thread"]!["state"]!);
      Assert.Equal(2, added["thread"]!["messages"]!.AsArray().Count);
   }

   [Fact]
   public async Task AgentThread_outdated_state_is_refused_to_the_reviewer()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var asked = await _calls.AskAsync(id);

      using var response = await _calls.SendAsync(await _calls.UiAsync(On("set_thread_state", id, threadId: ThreadIdOf(asked))), """{ "state": "outdated" }""");

      await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("open", (string)(await _calls.ThreadsAsync(id))[0]!["state"]!);
   }

   [Fact]
   public async Task AgentThread_start_whose_session_drops_after_the_checks_stores_nothing()
   {
      var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      Writer.Hook = WriterHooks.AfterGit(async _ =>
      {
         Writer.Hook = null;
         await _calls.DropAsync(session);
      }, "start_thread");

      using var response = await _calls.SendAsync(_calls.Agent(session, On("start_thread", id)), StartBody("Late."));

      await ErrorAsync(response, HttpStatusCode.Conflict, "noSessionAttached");
      Assert.Empty(await _calls.ThreadsAsync(id));
   }

   [Fact]
   public async Task AgentThread_start_that_a_take_over_overtakes_after_the_checks_stores_nothing()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      await using var other = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      Writer.Hook = WriterHooks.AfterGit(async _ =>
      {
         Writer.Hook = null;
         await _calls.CallAsync(_calls.Agent(other, On("attach_tour", id)), """{ "takeOver": true }""");
      }, "start_thread");

      using var response = await _calls.SendAsync(_calls.Agent(session, On("start_thread", id)), StartBody("Late."));

      await ErrorAsync(response, HttpStatusCode.Conflict, "attachedToAnotherSession");
      Assert.Empty(await _calls.ThreadsAsync(id));
   }

   [Fact]
   public async Task AgentThread_message_whose_thread_is_deleted_after_the_checks_is_not_found_and_stores_nothing()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var threadId = ThreadIdOf((await _calls.StartThreadAsync(session, id))["thread"]!);
      Writer.Hook = WriterHooks.AfterGit(async _ =>
      {
         Writer.Hook = null;
         await _calls.CallAsync(_calls.Agent(session, On("delete_thread", id, threadId: threadId)));
      }, "add_thread_message");

      using var response = await _calls.SendAsync(_calls.Agent(session, On("add_thread_message", id, threadId: threadId)), MessageBody("Late."));

      await ErrorAsync(response, HttpStatusCode.NotFound, "threadNotFound");
      Assert.Empty(await _calls.ThreadsAsync(id));
   }

   /// <summary>The reviewer's follow-up lands between the message's checks and its store, so the thread's last question waits when the store checks again.</summary>
   [Fact]
   public async Task AgentThread_message_that_a_follow_up_overtakes_after_the_checks_is_refused_and_the_follow_up_stays()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var threadId = ThreadIdOf((await _calls.StartThreadAsync(session, id))["thread"]!);
      JsonNode? followed = null;
      Writer.Hook = WriterHooks.AfterGit(async _ =>
      {
         Writer.Hook = null;
         followed = await _calls.CallAsync(await _calls.UiAsync(On("ask_follow_up", id, threadId: threadId)), """{ "text": "Wait, why?" }""");
      }, "add_thread_message");

      using var response = await _calls.SendAsync(_calls.Agent(session, On("add_thread_message", id, threadId: threadId)), MessageBody("Late."));

      await ErrorAsync(response, HttpStatusCode.Conflict, "questionWaiting");
      Assert.True(JsonNode.DeepEquals(new JsonArray(followed!.DeepClone()), await _calls.ThreadsAsync(id)), "The refused message changed the thread.");
   }

   [Fact]
   public async Task AgentThread_message_whose_session_drops_after_the_checks_stores_nothing()
   {
      var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var started = await _calls.StartThreadAsync(session, id);
      Writer.Hook = WriterHooks.AfterGit(async _ =>
      {
         Writer.Hook = null;
         await _calls.DropAsync(session);
      }, "add_thread_message");

      using var response = await _calls.SendAsync(_calls.Agent(session, On("add_thread_message", id, threadId: ThreadIdOf(started["thread"]!))), MessageBody("Late."));

      await ErrorAsync(response, HttpStatusCode.Conflict, "noSessionAttached");
      Assert.True(JsonNode.DeepEquals(new JsonArray(started["thread"]!.DeepClone()), await _calls.ThreadsAsync(id)), "The refused message changed the thread.");
   }

   /// <summary>Claude's own threads are no notes, so each agent read of the tour holds them.</summary>
   [Fact]
   public async Task AgentThread_started_by_claude_is_in_the_tour_that_the_session_reads()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var started = await _calls.StartThreadAsync(session, id);
      await _calls.SaveNoteAsync(id);

      var tour = await _calls.CallAsync(_calls.Agent(session, On("read_state", id)));

      Assert.True(JsonNode.DeepEquals(new JsonArray(started["thread"]!.DeepClone()), tour["threads"]), $"The agent's read differs: {tour["threads"]}");
   }

   private static (OperationCall Call, string Body) Write(string action, string tourId, string threadId) => action switch
   {
      "start" => (On("start_thread", tourId), StartBody("Mine.")),
      "message" => (On("add_thread_message", tourId, threadId: threadId), MessageBody("Mine.")),
      "outdate" => (On("outdate_thread", tourId, threadId: threadId), "{}"),
      "delete" => (On("delete_thread", tourId, threadId: threadId), "{}"),
      _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
   };

   private TourWriter Writer => _harness.Server.Services.GetRequiredService<TourWriter>();
}
