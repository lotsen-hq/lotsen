using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Operations;
using Lotsen.Sessions;
using static Lotsen.Tests.OperationCall;
using static Lotsen.Tests.ThreadCalls;

namespace Lotsen.Tests;

/// <summary>
/// Covers the answer type of a sent thread: a typed note keeps its type when <c>send_notes</c> sends it, alone or in a batch, <c>ask_question</c> gives a question asked at once its type,
/// <c>fetch_question</c> shows the type to the session, and <c>set_thread_type</c> changes the type of a note or an asked thread in any state without sending the session anything.
/// </summary>
[Trait("Category", "Threads")]
public sealed class ThreadTypeTests : IAsyncLifetime
{
   private ServerHarness _harness = null!;
   private ThreadCalls _calls = null!;

   public async ValueTask InitializeAsync()
   {
      _harness = await ServerHarness.StartAsync();
      _calls = new ThreadCalls(_harness);
   }

   public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

   [Fact]
   public async Task Typed_note_sent_alone_keeps_its_type_and_fetch_question_shows_it_to_the_session()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var saved = await _calls.SaveNoteAsync(id, "The cap is wrong.", "concern");

      var sent = await _calls.SendNotesAsync(id, ThreadIdOf(saved["thread"]!));
      var thread = sent["threads"]![0]!;
      var fetched = await _calls.FetchAsync(session, id, QuestionIdOf(thread));

      Assert.Equal(("asked", "concern"), ((string)thread["kind"]!, (string)thread["type"]!));
      Assert.Equal("concern", (string)(await _calls.ThreadsAsync(id))[0]!["type"]!);
      Assert.Equal(("concern", "The cap is wrong."), ((string)fetched["thread"]!["type"]!, (string)fetched["thread"]!["messages"]![0]!["text"]!));
   }

   [Fact]
   public async Task Typed_notes_sent_in_a_batch_each_keep_their_own_type_and_an_untyped_note_stays_untyped()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var decision = ThreadIdOf((await _calls.SaveNoteAsync(id, "Split the class.", "decision"))["thread"]!);
      var untyped = ThreadIdOf((await _calls.SaveNoteAsync(id, "Why here?"))["thread"]!);
      var open = ThreadIdOf((await _calls.SaveNoteAsync(id, "Keep the cap?", "openQuestion"))["thread"]!);

      var sent = await _calls.SendNotesAsync(id, decision, untyped, open);

      var threads = sent["threads"]!.AsArray();
      Assert.Equal(["decision", null, "openQuestion"], threads.Select(thread => (string?)thread!["type"]));
      Assert.All(threads, thread => Assert.Equal("asked", (string)thread!["kind"]!));
      Assert.Equal(["decision", null, "openQuestion"], (await _calls.ThreadsAsync(id)).Select(thread => (string?)thread!["type"]));
   }

   [Fact]
   public async Task Typed_question_asked_at_once_holds_its_type_and_the_session_sees_it_in_fetch_question_read_state_and_read_summary()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);

      var asked = await _calls.AskAsync(id, "Keep the cap?", "openQuestion");
      var fetched = await _calls.FetchAsync(session, id, QuestionIdOf(asked));
      var state = await _calls.CallAsync(_calls.Agent(session, On("read_state", id)));
      var summary = await _calls.CallAsync(_calls.Agent(session, On("read_summary", id)));

      Assert.Equal(("asked", "openQuestion"), ((string)asked["kind"]!, (string)asked["type"]!));
      Assert.Equal("openQuestion", (string)fetched["thread"]!["type"]!);
      Assert.Equal("openQuestion", (string)state["threads"]![0]!["type"]!);
      var expected = JsonNode.Parse($$"""{ "id": "{{ThreadIdOf(asked)}}", "anchor": { "kind": "tour" }, "text": "Keep the cap?", "type": "openQuestion", "state": "open" }""");
      Assert.True(JsonNode.DeepEquals(expected, Assert.Single(summary["typedThreads"]!["openQuestions"]!.AsArray())), $"The agent's summary reads {summary["typedThreads"]}");
   }

   /// <summary>A typed note sent with <c>send_notes</c> gives the same thread as a question asked at once with the type, apart from the IDs and times that the server gives.</summary>
   [Fact]
   public async Task Typed_question_asked_at_once_matches_a_typed_note_that_send_notes_sent()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var saved = await _calls.SaveNoteAsync(id, "The cap is wrong.", "concern");
      var sent = (await _calls.SendNotesAsync(id, ThreadIdOf(saved["thread"]!)))["threads"]![0]!;

      var asked = await _calls.AskAsync(id, "The cap is wrong.", "concern");

      Assert.True(JsonNode.DeepEquals(WithoutIdsAndTimes(sent), WithoutIdsAndTimes(asked)), $"The asked thread {asked} differs from the sent note {sent}.");
   }

   [Fact]
   public async Task Question_asked_at_once_without_a_type_holds_none()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);

      var asked = await _calls.AskAsync(id);

      Assert.False(asked.AsObject().ContainsKey("type"), $"The thread holds a type: {asked}");
      Assert.False((await _calls.ThreadsAsync(id))[0]!.AsObject().ContainsKey("type"), "The stored thread holds a type.");
   }

   [Fact]
   public async Task Question_asked_with_an_unknown_type_is_refused_and_stores_nothing()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);

      using var response = await _calls.SendAsync(await _calls.UiAsync(On("ask_question", id)), """{ "text": "Why?", "anchor": { "kind": "tour" }, "type": "suggestion" }""");

      var error = await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("type", (string)error["field"]!);
      Assert.Empty(await _calls.ThreadsAsync(id));
   }

   /// <summary>A tour action and a follow-up take no type, so their inputs have no type property.</summary>
   [Theory]
   [InlineData("request_tour_action", """{ "action": { "kind": "summarize" }, "type": "concern" }""")]
   [InlineData("ask_follow_up", """{ "text": "And here?", "type": "concern" }""")]
   public async Task Tour_action_or_follow_up_with_a_type_is_refused_and_stores_nothing(string operation, string body)
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var asked = await _calls.AskAsync(id);
      await _calls.CallAsync(_calls.Agent(session, On("reply", id, questionId: QuestionIdOf(asked))), Done);
      var before = await _calls.ThreadsAsync(id);

      using var response = await _calls.SendAsync(await _calls.UiAsync(On(operation, id, threadId: operation == "ask_follow_up" ? ThreadIdOf(asked) : null)), body);

      var error = await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("type", (string)error["field"]!);
      Assert.True(JsonNode.DeepEquals(before, await _calls.ThreadsAsync(id)), "The refused call stored something.");
   }

   /// <summary>The question asked after the change is the session's next event, so the change sent the session nothing.</summary>
   [Fact]
   public async Task Set_type_of_a_sent_thread_stores_it_reaches_other_views_as_an_event_and_sends_the_session_nothing()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      await _calls.AckAsync(session, await session.ProbeAsync());
      var saved = await _calls.SaveNoteAsync(id, "The cap is wrong.", "concern");
      var asked = (await _calls.SendNotesAsync(id, ThreadIdOf(saved["thread"]!)))["threads"]![0]!;
      await session.Stream.NextAsync(EventWait);
      await using var stream = await EventStream.OpenAsync(_harness);

      var retyped = await _calls.SetTypeAsync(id, ThreadIdOf(asked), "decision");
      var updated = await stream.NextAsync(EventWait);
      var marker = await _calls.AskAsync(id);
      var next = await session.Stream.NextAsync(EventWait);

      var thread = retyped["thread"]!;
      Assert.Equal("decision", (string)thread["type"]!);
      Assert.True(JsonNode.DeepEquals(asked["messages"], thread["messages"]), $"The change of the type changed the thread's messages: {thread}");
      Assert.Equal(ServerEvents.ThreadUpdated.Name, updated.Type);
      Assert.True(JsonNode.DeepEquals(thread, EventStream.WithoutStatus(updated.Data["thread"]!)), $"The event's thread differs from the response's: {updated.Data}");
      Assert.True(JsonNode.DeepEquals(thread, (await _calls.ThreadsAsync(id))[0]), "The stored thread differs from the response's.");
      Assert.Equal((SessionEvents.Question, ThreadIdOf(marker)), (next.Type, (string)next.Data["threadId"]!));
   }

   [Fact]
   public async Task Set_type_of_a_thread_asked_at_once_types_it_and_omitting_the_type_takes_it_away()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var asked = await _calls.AskAsync(id);

      var typed = await _calls.SetTypeAsync(id, ThreadIdOf(asked), "openQuestion");
      var untyped = await _calls.SetTypeAsync(id, ThreadIdOf(asked), null);

      Assert.Equal("openQuestion", (string)typed["thread"]!["type"]!);
      Assert.False(untyped["thread"]!.AsObject().ContainsKey("type"), $"The thread keeps a type: {untyped["thread"]}");
      Assert.True(JsonNode.DeepEquals(asked, (await _calls.ThreadsAsync(id))[0]), "The thread differs from the thread as asked.");
   }

   /// <summary>The question asked after the change is the next event of the stream, so the change published none.</summary>
   [Fact]
   public async Task Set_type_to_the_type_a_thread_has_stores_nothing_and_publishes_no_event()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var asked = await _calls.AskAsync(id);
      var typed = await _calls.SetTypeAsync(id, ThreadIdOf(asked), "concern");
      await using var stream = await EventStream.OpenAsync(_harness);

      var again = await _calls.SetTypeAsync(id, ThreadIdOf(asked), "concern");
      var marker = await _calls.AskAsync(id);
      var next = await stream.NextAsync(EventWait);

      Assert.Equal((string)typed["updatedAt"]!, (string)again["updatedAt"]!);
      Assert.Equal((ServerEvents.ThreadCreated.Name, ThreadIdOf(marker)), (next.Type, ThreadIdOf(next.Data["thread"]!)));
   }

   [Fact]
   public async Task Set_type_works_with_no_session_bound_and_on_a_resolved_thread()
   {
      var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var asked = await _calls.AskAsync(id);
      await _calls.CallAsync(await _calls.UiAsync(On("set_thread_state", id, threadId: ThreadIdOf(asked))), """{ "state": "resolved" }""");
      await _calls.DropAsync(session);

      var typed = await _calls.SetTypeAsync(id, ThreadIdOf(asked), "decision");

      Assert.Equal(("decision", "resolved"), ((string)typed["thread"]!["type"]!, (string)typed["thread"]!["state"]!));
   }

   [Fact]
   public async Task Set_type_of_a_note_keeps_its_words_and_save_time()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var saved = await _calls.SaveNoteAsync(id, "The cap is wrong.");

      var typed = await _calls.SetTypeAsync(id, ThreadIdOf(saved["thread"]!), "concern");

      Assert.Equal(("note", "concern", "The cap is wrong."), ((string)typed["thread"]!["kind"]!, (string)typed["thread"]!["type"]!, (string)typed["thread"]!["text"]!));
      Assert.Equal((string)saved["thread"]!["savedAt"]!, (string)typed["thread"]!["savedAt"]!);
   }

   [Fact]
   public async Task Set_type_of_a_thread_that_the_session_started_is_refused_and_stores_nothing()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var started = (await _calls.StartThreadAsync(session, id))["thread"]!;

      using var response = await _calls.SendAsync(await _calls.UiAsync(On("set_thread_type", id, threadId: ThreadIdOf(started))), """{ "type": "concern" }""");

      var error = await ErrorAsync(response, HttpStatusCode.Conflict, "threadTakesNoType");
      Assert.Equal("threadId", (string)error["field"]!);
      Assert.True(JsonNode.DeepEquals(new JsonArray(started.DeepClone()), await _calls.ThreadsAsync(id)), "The refused change stored something.");
   }

   [Fact]
   public async Task Set_type_of_a_missing_thread_is_refused()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);

      using var response = await _calls.SendAsync(await _calls.UiAsync(On("set_thread_type", id, threadId: "gone")), """{ "type": "concern" }""");

      await ErrorAsync(response, HttpStatusCode.NotFound, "threadNotFound");
   }

   [Fact]
   public async Task Set_type_with_an_unknown_type_is_refused_and_stores_nothing()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var asked = await _calls.AskAsync(id);

      using var response = await _calls.SendAsync(await _calls.UiAsync(On("set_thread_type", id, threadId: ThreadIdOf(asked))), """{ "type": "suggestion" }""");

      var error = await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("type", (string)error["field"]!);
      Assert.True(JsonNode.DeepEquals(new JsonArray(asked.DeepClone()), await _calls.ThreadsAsync(id)), "The refused change stored something.");
   }

   [Fact]
   public async Task Set_type_by_an_agent_session_is_refused()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var asked = await _calls.AskAsync(id);

      using var response = await _calls.SendAsync(_calls.Agent(session, On("set_thread_type", id, threadId: ThreadIdOf(asked))), """{ "type": "concern" }""");

      await ErrorAsync(response, HttpStatusCode.Forbidden, "callerNotAllowed");
   }

   // Returns a copy without what the server gives each new thread: the IDs and the times of the thread, its question and the question's request.
   private static JsonNode WithoutIdsAndTimes(JsonNode thread)
   {
      var copy = thread.DeepClone();
      copy.AsObject().Remove("id");
      var question = copy["messages"]![0]!.AsObject();
      question.Remove("id");
      question.Remove("createdAt");
      foreach (var request in question["requests"]!.AsArray())
      {
         request!.AsObject().Remove("id");
         request.AsObject().Remove("sentAt");
      }

      return copy;
   }
}
