using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Adapter;
using Lotsen.Operations;
using Lotsen.Sessions;
using Lotsen.Tours;
using static Lotsen.Tests.OperationCall;
using static Lotsen.Tests.ThreadCalls;

namespace Lotsen.Tests;

/// <summary>
/// Covers <c>remove_thread</c>: the reviewer deletes any thread, with or without a bound session, a question that waits is canceled first, the bound session gets one event that names the thread,
/// and the thread is gone for every caller.
/// </summary>
[Trait("Category", "Threads")]
public sealed class ReviewerThreadDeleteTests : IAsyncLifetime
{
   private ServerHarness _harness = null!;
   private ThreadCalls _calls = null!;

   public async ValueTask InitializeAsync()
   {
      _harness = await ServerHarness.StartAsync();
      _calls = new ThreadCalls(_harness);
   }

   public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

   /// <summary>The question asked after the delete is the session's next event, so the delete sent the session exactly one.</summary>
   [Fact]
   public async Task Remove_with_a_session_bound_deletes_the_thread_for_every_caller_and_sends_the_session_one_event_that_names_it()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      await session.ProbeAsync();
      var asked = await _calls.AskAsync(id);
      Assert.Equal(SessionEvents.Question, (await session.Stream.NextAsync(EventWait)).Type);
      await _calls.CallAsync(_calls.Agent(session, On("reply", id, questionId: QuestionIdOf(asked))), Done);
      var kept = await _calls.StartThreadAsync(session, id);
      await using var stream = await EventStream.OpenAsync(_harness);

      var deleted = await _calls.CallAsync(await _calls.UiAsync(On("remove_thread", id, threadId: ThreadIdOf(asked))));
      var removed = await stream.NextAsync(EventWait);
      var told = await session.Stream.NextAsync(EventWait);
      var marker = await _calls.AskAsync(id);
      var next = await session.Stream.NextAsync(EventWait);

      Assert.Equal((id, ThreadIdOf(asked)), ((string)deleted["tourId"]!, (string)deleted["threadId"]!));
      Assert.Equal(ServerEvents.ThreadDeleted.Name, removed.Type);
      Assert.True(JsonNode.DeepEquals(deleted, EventStream.WithoutRevision(removed.Data)), $"The event differs from the response: {removed.Data}");
      Assert.Equal(SessionEvents.ThreadDeleted, told.Type);
      Assert.True(JsonNode.DeepEquals(JsonNode.Parse($$"""{ "tourId": "{{id}}", "threadId": "{{ThreadIdOf(asked)}}" }"""), told.Data), $"The session event names more or less than the thread: {told.Data}");
      Assert.Equal((SessionEvents.Question, QuestionIdOf(marker)), (next.Type, (string)next.Data["questionId"]!));
      Assert.Equal([ThreadIdOf(kept["thread"]!), ThreadIdOf(marker)], (await _calls.ThreadsAsync(id)).Select(thread => ThreadIdOf(thread!)));
      var agentView = (await _calls.CallAsync(_calls.Agent(session, On("read_state", id))))["threads"]!.AsArray();
      Assert.DoesNotContain(ThreadIdOf(asked), agentView.Select(thread => ThreadIdOf(thread!)));
   }

   [Fact]
   public async Task Remove_with_no_session_bound_deletes_the_thread()
   {
      var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var asked = await _calls.AskAsync(id);
      await _calls.CallAsync(_calls.Agent(session, On("reply", id, questionId: QuestionIdOf(asked))), Done);
      await _calls.DropAsync(session);

      var deleted = await _calls.CallAsync(await _calls.UiAsync(On("remove_thread", id, threadId: ThreadIdOf(asked))));

      Assert.Null(await _harness.BindingOfAsync(id));
      Assert.Equal(ThreadIdOf(asked), (string)deleted["threadId"]!);
      Assert.Empty(await _calls.ThreadsAsync(id));
   }

   /// <summary>The thread table refuses to remove a thread whose question waits, so only the cancel before the removal lets the delete through.</summary>
   [Fact]
   public async Task Remove_of_a_thread_whose_question_waits_cancels_the_question_first_and_names_it_in_the_session_event()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      await session.ProbeAsync();
      var asked = await _calls.AskAsync(id);
      Assert.Equal(SessionEvents.Question, (await session.Stream.NextAsync(EventWait)).Type);

      var deleted = await _calls.CallAsync(await _calls.UiAsync(On("remove_thread", id, threadId: ThreadIdOf(asked))));
      var told = await session.Stream.NextAsync(EventWait);
      using var late = await _calls.SendAsync(_calls.Agent(session, On("reply", id, questionId: QuestionIdOf(asked))), Done);

      Assert.Equal(ThreadIdOf(asked), (string)deleted["threadId"]!);
      Assert.Equal((SessionEvents.ThreadDeleted, ThreadIdOf(asked), QuestionIdOf(asked)), (told.Type, (string)told.Data["threadId"]!, (string?)told.Data["questionId"]));
      await ErrorAsync(late, HttpStatusCode.NotFound, "questionNotFound");
      Assert.Empty(await _calls.ThreadsAsync(id));
   }

   /// <summary>No session saw the note, so the question asked after the delete is the session's next event.</summary>
   [Fact]
   public async Task Remove_of_a_note_deletes_it_and_sends_the_session_nothing()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      await session.ProbeAsync();
      var saved = await _calls.SaveNoteAsync(id);

      await _calls.CallAsync(await _calls.UiAsync(On("remove_thread", id, threadId: ThreadIdOf(saved["thread"]!))));
      var marker = await _calls.AskAsync(id);
      var next = await session.Stream.NextAsync(EventWait);

      Assert.Equal((SessionEvents.Question, QuestionIdOf(marker)), (next.Type, (string)next.Data["questionId"]!));
      Assert.Equal([ThreadIdOf(marker)], (await _calls.ThreadsAsync(id)).Select(thread => ThreadIdOf(thread!)));
   }

   [Fact]
   public async Task Remove_of_a_missing_thread_is_refused()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);

      using var response = await _calls.SendAsync(await _calls.UiAsync(On("remove_thread", id, threadId: "t-missing")));

      var error = await ErrorAsync(response, HttpStatusCode.NotFound, "threadNotFound");
      Assert.Equal("threadId", (string)error["field"]!);
   }

   [Fact]
   public void ThreadDeleted_reaches_Claude_as_a_channel_notification_that_names_the_thread_and_the_canceled_question()
   {
      var tourId = TourId.New();

      var answered = ChannelNotifications.ParamsFor(SessionEvents.ForThreadDeleted(new ThreadDeletedEvent(tourId, "t1")))!;
      var waiting = ChannelNotifications.ParamsFor(SessionEvents.ForThreadDeleted(new ThreadDeletedEvent(tourId, "t1", "q1")))!;

      Assert.True(
         JsonNode.DeepEquals(JsonNode.Parse($$"""{ "event": "threadDeleted", "tourId": "{{tourId}}", "threadId": "t1" }"""), answered["meta"]),
         $"The notification's meta differs: {answered["meta"]}");
      Assert.Equal("q1", (string?)waiting["meta"]!["questionId"]);
      Assert.Contains("deleted thread t1", (string)answered["content"]!, StringComparison.Ordinal);
      Assert.DoesNotContain("q1", (string)answered["content"]!, StringComparison.Ordinal);
      Assert.Contains("question q1", (string)waiting["content"]!, StringComparison.Ordinal);
   }
}
