using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Operations;
using Lotsen.Sessions;
using Lotsen.Tours;
using Microsoft.Extensions.DependencyInjection;
using static Lotsen.Tests.OperationCall;
using static Lotsen.Tests.ThreadCalls;

namespace Lotsen.Tests;

/// <summary>
/// Covers <c>send_notes</c>: the UI sends one note or a batch of notes to the bound session, each note becomes a question with its own pending request and channel event in its own thread, and a
/// batch that holds a note that is not a draft any more sends nothing.
/// </summary>
[Trait("Category", "Threads")]
public sealed class NoteBatchTests : IAsyncLifetime
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
   public async Task Batch_send_moves_every_chosen_note_to_a_question_with_its_own_pending_request_and_channel_event()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      await _calls.AckAsync(session, await session.ProbeAsync());
      var first = ThreadIdOf((await _calls.SaveNoteAsync(id, "First note."))["thread"]!);
      var second = ThreadIdOf((await _calls.SaveNoteAsync(id, "Second note."))["thread"]!);
      var kept = await _calls.SaveNoteAsync(id, "Kept note.");
      await using var stream = await EventStream.OpenAsync(_harness);

      var sent = await _calls.SendNotesAsync(id, second, first);
      var updates = new[] { await stream.NextAsync(EventWait), await stream.NextAsync(EventWait) };
      var events = new[] { await session.Stream.NextAsync(EventWait), await session.Stream.NextAsync(EventWait) };

      var threads = sent["threads"]!.AsArray();
      Assert.Equal(id, (string)sent["tourId"]!);
      Assert.Equal([second, first], threads.Select(thread => ThreadIdOf(thread!)));
      Assert.Equal(["Second note.", "First note."], threads.Select(thread => (string)thread!["messages"]![0]!["text"]!));
      Assert.All(threads, thread =>
      {
         Assert.Equal(("asked", "question"), ((string)thread!["kind"]!, (string)thread["messages"]![0]!["kind"]!));
         Assert.False(thread.AsObject().ContainsKey("text"), $"A sent thread kept its note's words: {thread}");
         Assert.Equal("pending", (string)RequestOf(thread)["state"]!);
         Assert.Equal(session.Id, (string)RequestOf(thread)["sessionId"]!);
      });
      Assert.NotEqual((string)RequestOf(threads[0]!)["id"]!, (string)RequestOf(threads[1]!)["id"]!);
      Assert.All(updates, update => Assert.Equal(ServerEvents.ThreadUpdated.Name, update.Type));
      Assert.All(updates, update => Assert.Equal((string)sent["updatedAt"]!, (string)update.Data["updatedAt"]!));
      Assert.Equal([second, first], updates.Select(update => (string)update.Data["thread"]!["id"]!));
      Assert.All(events, question => Assert.Equal(SessionEvents.Question, question.Type));
      Assert.Equal(
         threads.Select(thread => (ThreadIdOf(thread!), QuestionIdOf(thread!), (string)RequestOf(thread!)["id"]!, (string)thread!["messages"]![0]!["text"]!)),
         events.Select(question => ((string)question.Data["threadId"]!, (string)question.Data["questionId"]!, (string)question.Data["requestId"]!, (string)question.Data["text"]!)));
      Assert.All(events, question => Assert.False(question.Data.AsObject().ContainsKey("nonce"), $"A question to the confirmed session carries a nonce: {question.Data}"));
      var stored = await _calls.ThreadsAsync(id);
      Assert.True(JsonNode.DeepEquals(kept["thread"], stored[2]), "The batch changed a note that it did not hold.");
      Assert.True(JsonNode.DeepEquals(threads[0], stored[1]) && JsonNode.DeepEquals(threads[1], stored[0]), "The stored threads differ from the batch's response.");
   }

   [Fact]
   public async Task Batch_of_one_note_sends_it_alone_with_the_stop_that_the_reviewer_viewed()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var plan = await _calls.CallAsync(
         _calls.Agent(session, On("set_plan", id)),
         """{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "title": "What changes", "kind": "overview", "chapter": "a" }] }""");
      var stopId = (string)plan["plan"]!["stops"]![0]!["id"]!;
      var saved = await _calls.CallAsync(await _calls.UiAsync(On("save_note", id)), $$"""{ "text": "Later.", "anchor": { "kind": "tour" }, "stopId": "{{stopId}}" }""");
      await session.ProbeAsync();

      var sent = await _calls.SendNotesAsync(id, ThreadIdOf(saved["thread"]!));
      var question = await session.Stream.NextAsync(EventWait);

      Assert.Equal(stopId, (string)sent["threads"]![0]!["messages"]![0]!["stopId"]!);
      Assert.Equal(stopId, (string)question.Data["stopId"]!);
   }

   /// <summary>The session has not acked, so every question of the batch carries the nonce that its probe issued.</summary>
   [Fact]
   public async Task Batch_to_an_unconfirmed_session_carries_the_session_nonce_on_every_question()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var nonce = await session.ProbeAsync();
      var first = ThreadIdOf((await _calls.SaveNoteAsync(id, "First."))["thread"]!);
      var second = ThreadIdOf((await _calls.SaveNoteAsync(id, "Second."))["thread"]!);

      await _calls.SendNotesAsync(id, first, second);
      var events = new[] { await session.Stream.NextAsync(EventWait), await session.Stream.NextAsync(EventWait) };

      Assert.All(events, question => Assert.Equal((SessionEvents.Question, nonce), (question.Type, (string?)question.Data["nonce"])));
   }

   [Fact]
   public async Task Batch_sent_question_is_fetched_and_answered_in_its_own_thread()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var first = ThreadIdOf((await _calls.SaveNoteAsync(id, "First."))["thread"]!);
      var second = ThreadIdOf((await _calls.SaveNoteAsync(id, "Second."))["thread"]!);
      var sent = await _calls.SendNotesAsync(id, first, second);
      var questionId = QuestionIdOf(sent["threads"]![1]!);

      var fetched = await _calls.FetchAsync(session, id, questionId);
      var replied = await _calls.CallAsync(_calls.Agent(session, On("reply", id, questionId: questionId)), Done);

      Assert.Equal(second, ThreadIdOf(fetched["thread"]!));
      Assert.Equal(second, ThreadIdOf(replied));
      Assert.Equal(["pending"], (await _calls.ThreadsAsync(id))[0]!["messages"]!.AsArray().Select(message => (string)message!["requests"]![0]!["state"]!));
   }

   [Fact]
   public async Task Batch_with_no_session_bound_is_refused_and_keeps_every_note_a_draft()
   {
      var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var note = ThreadIdOf((await _calls.SaveNoteAsync(id))["thread"]!);
      await _harness.DropSessionAsync(session);
      var before = await _calls.ThreadsAsync(id);

      using var response = await _calls.SendAsync(await _calls.UiAsync(On("send_notes", id)), SendBody(note));

      await ErrorAsync(response, HttpStatusCode.Conflict, "noSessionAttached");
      Assert.True(JsonNode.DeepEquals(before, await _calls.ThreadsAsync(id)), "The refused batch changed a note.");
   }

   /// <summary>The second send of the same note finds it sent, so a note goes to the session once whichever of two views sends first.</summary>
   [Fact]
   public async Task Second_send_of_the_same_note_is_refused_and_the_note_goes_to_the_session_once()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      await session.ProbeAsync();
      var note = ThreadIdOf((await _calls.SaveNoteAsync(id))["thread"]!);

      var sent = await _calls.SendNotesAsync(id, note);
      using var refused = await _calls.SendAsync(await _calls.UiAsync(On("send_notes", id)), SendBody(note));
      var question = await session.Stream.NextAsync(EventWait);
      var marker = await _calls.AskAsync(id, "Marker.");
      var next = await session.Stream.NextAsync(EventWait);

      await ErrorAsync(refused, HttpStatusCode.Conflict, "noteSent");
      Assert.Equal(QuestionIdOf(sent["threads"]![0]!), (string)question.Data["questionId"]!);
      Assert.Equal(QuestionIdOf(marker), (string)next.Data["questionId"]!);
   }

   /// <summary>The requests name the session bound when the server stored them, so the settlement of the session's end marks them unanswered.</summary>
   [Fact]
   public async Task Batch_whose_session_drops_after_the_send_is_marked_unanswered()
   {
      var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var first = ThreadIdOf((await _calls.SaveNoteAsync(id, "First."))["thread"]!);
      var second = ThreadIdOf((await _calls.SaveNoteAsync(id, "Second."))["thread"]!);

      var sent = await _calls.SendNotesAsync(id, first, second);
      await _calls.DropAsync(session);

      Assert.All(sent["threads"]!.AsArray(), thread => Assert.Equal("pending", (string)RequestOf(thread!)["state"]!));
      Assert.True(TourId.TryParse(id, out var tourId));
      var stored = Writer.Read(tourId).Value!;
      Assert.Equal(2, stored.Threads.Count);
      Assert.All(stored.Threads, thread => Assert.True(thread is QuestionThread.Sent { Messages: [ThreadMessage.Question { Requests: [{ State: RequestState.Unanswered }] }] }, $"{thread}"));
   }

   /// <summary>The questions reach the session that the requests name, and the take-over marks them unanswered.</summary>
   [Fact]
   public async Task Take_over_after_a_batch_marks_the_requests_unanswered_for_a_resend()
   {
      await using var first = await AgentSessionConnection.OpenAsync(_harness);
      await using var second = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(first);
      var note = ThreadIdOf((await _calls.SaveNoteAsync(id))["thread"]!);

      var sent = await _calls.SendNotesAsync(id, note);
      await _calls.CallAsync(_calls.Agent(second, On("attach_tour", id)), """{ "takeOver": true }""");
      var resent = await _calls.CallAsync(await _calls.UiAsync(On("resend_question", id, questionId: QuestionIdOf(sent["threads"]![0]!))));

      Assert.Equal(first.Id, (string)RequestOf(sent["threads"]![0]!)["sessionId"]!);
      Assert.Equal(["unanswered", "pending"], resent["messages"]![0]!["requests"]!.AsArray().Select(request => (string)request!["state"]!));
      Assert.Equal(second.Id, (string)RequestOf(resent, 0, 1)["sessionId"]!);
   }

   [Theory]
   [InlineData("""{ "threadIds": [] }""")]
   [InlineData("""{ "threadIds": ["same", "same"] }""")]
   [InlineData("{}")]
   public async Task Batch_with_invalid_thread_IDs_is_refused(string body)
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);

      using var response = await _calls.SendAsync(await _calls.UiAsync(On("send_notes", id)), body);

      await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
   }

   [Fact]
   public async Task Batch_of_more_notes_than_the_most_is_refused()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var ids = Enumerable.Range(0, NoteOperations.MaxNoteBatch + 1).Select(index => $"t{index}").ToArray();

      using var response = await _calls.SendAsync(await _calls.UiAsync(On("send_notes", id)), SendBody(ids));

      var error = await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("threadIds", (string)error["field"]!);
   }

   private TourWriter Writer => _harness.Server.Services.GetRequiredService<TourWriter>();
}
