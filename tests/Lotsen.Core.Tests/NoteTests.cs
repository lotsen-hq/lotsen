using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Operations;
using Lotsen.Sessions;
using Microsoft.Extensions.DependencyInjection;
using static Lotsen.Tests.OperationCall;
using static Lotsen.Tests.ThreadCalls;

namespace Lotsen.Tests;

/// <summary>
/// Covers <c>save_note</c>, <c>edit_note</c> and <c>discard_note</c>: the UI stores a thread as a draft note with or without a bound session, sends no session anything for it, and can edit or
/// discard the note until it is sent.
/// </summary>
[Trait("Category", "Threads")]
public sealed class NoteTests : IAsyncLifetime
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

   /// <summary>The question asked after the note is the session's next event, so the note sent the session nothing.</summary>
   [Theory]
   [InlineData("unconfirmed")]
   [InlineData("confirmed")]
   public async Task Note_saved_with_a_session_bound_is_a_draft_with_no_request_and_sends_no_channel_event(string level)
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var nonce = await session.ProbeAsync();
      if (level == "confirmed")
      {
         await _calls.AckAsync(session, nonce);
      }

      await using var stream = await EventStream.OpenAsync(_harness);

      var saved = await _calls.SaveNoteAsync(id, "Check the rounding later.");
      var created = await stream.NextAsync(EventWait);
      var marker = await _calls.AskAsync(id);
      var next = await session.Stream.NextAsync(EventWait);

      var thread = saved["thread"]!;
      Assert.Equal(id, (string)saved["tourId"]!);
      Assert.Equal(("note", "Check the rounding later."), ((string)thread["kind"]!, (string)thread["text"]!));
      Assert.Null(thread["messages"]);
      Assert.Equal("open", (string)thread["state"]!);
      Assert.Equal(ServerEvents.ThreadCreated.Name, created.Type);
      Assert.True(JsonNode.DeepEquals(thread, EventStream.WithoutStatus(created.Data["thread"]!)), $"The event's thread differs from the response's: {created.Data}");
      Assert.Equal((string)saved["updatedAt"]!, (string)created.Data["updatedAt"]!);
      Assert.True(JsonNode.DeepEquals(thread, (await _calls.ThreadsAsync(id))[0]), "The stored note differs from the response's.");
      Assert.Equal((SessionEvents.Question, QuestionIdOf(marker)), (next.Type, (string)next.Data["questionId"]!));
   }

   [Fact]
   public async Task Note_saved_with_no_session_bound_is_stored_as_a_draft()
   {
      var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      await _harness.DropSessionAsync(session);

      var saved = await _calls.SaveNoteAsync(id);

      Assert.Null(await _harness.BindingOfAsync(id));
      Assert.True(JsonNode.DeepEquals(saved["thread"], (await _calls.ThreadsAsync(id))[0]), "The stored note differs from the response's.");
      Assert.Null(saved["thread"]!["messages"]);
   }

   /// <summary>A note needs no session, so the session's drop after the note's checks leaves the note to be stored.</summary>
   [Fact]
   public async Task Note_saved_while_its_session_drops_after_the_checks_is_stored()
   {
      var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      Writer.Hook = WriterHooks.AfterGit(async _ =>
      {
         Writer.Hook = null;
         await _calls.DropAsync(session);
      }, "save_note");

      var saved = await _calls.SaveNoteAsync(id);

      Assert.Equal(ThreadIdOf(saved["thread"]!), ThreadIdOf((await _calls.ThreadsAsync(id))[0]!));
   }

   [Fact]
   public async Task Note_keeps_the_stop_that_the_reviewer_viewed()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var plan = await _calls.CallAsync(
         _calls.Agent(session, On("set_plan", id)),
         """{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "title": "What changes", "kind": "overview", "chapter": "a" }] }""");
      var stopId = (string)plan["plan"]!["stops"]![0]!["id"]!;

      var saved = await _calls.CallAsync(await _calls.UiAsync(On("save_note", id)), $$"""{ "text": "Later.", "anchor": { "kind": "stop", "stopId": "{{stopId}}" }, "stopId": "{{stopId}}" }""");

      Assert.Equal(stopId, (string)saved["thread"]!["stopId"]!);
      Assert.Equal("stop", (string)saved["thread"]!["anchor"]!["kind"]!);
   }

   [Fact]
   public async Task Note_edit_changes_its_words_and_reaches_other_views_as_an_event()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var saved = await _calls.SaveNoteAsync(id, "First words.");
      await using var stream = await EventStream.OpenAsync(_harness);

      var edited = await _calls.CallAsync(await _calls.UiAsync(On("edit_note", id, threadId: ThreadIdOf(saved["thread"]!))), """{ "text": "Second words." }""");
      var updated = await stream.NextAsync(EventWait);

      Assert.Equal("Second words.", (string)edited["thread"]!["text"]!);
      Assert.Null(edited["thread"]!["messages"]);
      Assert.True((DateTimeOffset)edited["updatedAt"]! >= (DateTimeOffset)saved["updatedAt"]!, "The edit moved the tour's update time back.");
      Assert.Equal(ServerEvents.ThreadUpdated.Name, updated.Type);
      Assert.True(JsonNode.DeepEquals(edited["thread"], EventStream.WithoutStatus(updated.Data["thread"]!)), $"The event's thread differs from the response's: {updated.Data}");
      Assert.Equal((string)edited["updatedAt"]!, (string)updated.Data["updatedAt"]!);
      Assert.True(JsonNode.DeepEquals(edited["thread"], (await _calls.ThreadsAsync(id))[0]), "The stored note differs from the edit's.");
   }

   [Fact]
   public async Task Note_edit_and_discard_work_with_no_session_bound()
   {
      var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var kept = await _calls.SaveNoteAsync(id, "Keep me.");
      var dropped = await _calls.SaveNoteAsync(id, "Drop me.");
      await _harness.DropSessionAsync(session);

      var edited = await _calls.CallAsync(await _calls.UiAsync(On("edit_note", id, threadId: ThreadIdOf(kept["thread"]!))), """{ "text": "Kept." }""");
      await _calls.CallAsync(await _calls.UiAsync(On("discard_note", id, threadId: ThreadIdOf(dropped["thread"]!))));

      var threads = await _calls.ThreadsAsync(id);
      Assert.Equal("Kept.", (string)edited["thread"]!["text"]!);
      Assert.True(JsonNode.DeepEquals(new JsonArray(edited["thread"]!.DeepClone()), threads), $"The stored threads are not the edited note alone: {threads}");
   }

   [Fact]
   public async Task Note_discard_removes_the_thread_and_reaches_other_views_as_a_deletion_event()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var saved = await _calls.SaveNoteAsync(id);
      var threadId = ThreadIdOf(saved["thread"]!);
      await using var stream = await EventStream.OpenAsync(_harness);

      var discarded = await _calls.CallAsync(await _calls.UiAsync(On("discard_note", id, threadId: threadId)));
      var deleted = await stream.NextAsync(EventWait);

      Assert.Equal((id, threadId), ((string)discarded["tourId"]!, (string)discarded["threadId"]!));
      Assert.True((DateTimeOffset)discarded["updatedAt"]! >= (DateTimeOffset)saved["updatedAt"]!, "The discard moved the tour's update time back.");
      Assert.Equal(ServerEvents.ThreadDeleted.Name, deleted.Type);
      Assert.True(JsonNode.DeepEquals(discarded, EventStream.WithoutRevision(deleted.Data)), $"The event differs from the response: {deleted.Data}");
      Assert.Empty(await _calls.ThreadsAsync(id));
   }

   [Theory]
   [InlineData("""{ "text": "  " }""")]
   [InlineData("""{ "text": "a\u0007b" }""")]
   public async Task Note_edit_with_invalid_words_is_refused_and_stores_nothing(string body)
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var saved = await _calls.SaveNoteAsync(id);

      using var response = await _calls.SendAsync(await _calls.UiAsync(On("edit_note", id, threadId: ThreadIdOf(saved["thread"]!))), body);

      var error = await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("text", (string)error["field"]!);
      Assert.True(JsonNode.DeepEquals(saved["thread"], (await _calls.ThreadsAsync(id))[0]), "The refused edit changed the note.");
   }

   [Theory]
   [InlineData("""{ "text": "", "anchor": { "kind": "tour" } }""", "text")]
   [InlineData("""{ "text": "Later.", "anchor": { "kind": "stop", "stopId": "nosuchstop" } }""", "anchor.stopId")]
   [InlineData("""{ "text": "Later.", "anchor": { "kind": "tour" }, "stopId": "nosuchstop" }""", "stopId")]
   public async Task Note_with_invalid_input_is_refused_as_a_question_is(string body, string field)
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);

      using var response = await _calls.SendAsync(await _calls.UiAsync(On("save_note", id)), body);

      var error = await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal(field, (string)error["field"]!);
      Assert.Empty(await _calls.ThreadsAsync(id));
   }

   /// <summary>A note is the reviewer's own until it is sent, so the agent session's reads of the tour leave it out, and the UI's reads keep it.</summary>
   [Fact]
   public async Task Note_is_left_out_of_the_tour_that_an_agent_session_reads_until_it_is_sent()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var asked = await _calls.AskAsync(id);
      var note = await _calls.SaveNoteAsync(id);
      var other = await _calls.SaveNoteAsync(id, "Another.");

      var asAgent = await _calls.CallAsync(_calls.Agent(session, On("read_state", id)));
      var attached = await _calls.CallAsync(_calls.Agent(session, On("attach_tour", id)), "{}");
      var asUi = await _calls.CallAsync(await _calls.UiAsync(On("read_state", id)));
      await _calls.SendNotesAsync(id, ThreadIdOf(note["thread"]!));
      var afterSend = await _calls.CallAsync(_calls.Agent(session, On("read_state", id)));

      Assert.Equal([ThreadIdOf(asked)], ThreadIds(asAgent));
      Assert.Equal([ThreadIdOf(asked)], ThreadIds(attached));
      Assert.Equal([ThreadIdOf(asked), ThreadIdOf(note["thread"]!), ThreadIdOf(other["thread"]!)], ThreadIds(asUi));
      Assert.Equal([ThreadIdOf(asked), ThreadIdOf(note["thread"]!)], ThreadIds(afterSend));
   }

   [Theory]
   [InlineData("read_state")]
   [InlineData("attach_tour")]
   [InlineData("set_plan")]
   [InlineData("pin_tour")]
   public async Task Note_is_left_out_of_the_tour_that_each_agent_tool_returns(string tool)
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var asked = await _calls.AskAsync(id);
      await _calls.SaveNoteAsync(id);

      var tour = await _calls.CallAsync(_calls.Agent(session, On(tool, id)), TourToolBody(tool));

      Assert.Equal([ThreadIdOf(asked)], ThreadIds(tour));
   }

   [Fact]
   public async Task Note_is_kept_in_the_tour_that_the_ui_reads()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var asked = await _calls.AskAsync(id);
      var note = await _calls.SaveNoteAsync(id);

      var tour = await _calls.CallAsync(await _calls.UiAsync(On("read_state", id)));

      Assert.Equal([ThreadIdOf(asked), ThreadIdOf(note["thread"]!)], ThreadIds(tour));
   }

   [Theory]
   [InlineData("fetch_question")]
   [InlineData("reply")]
   public async Task Note_is_not_found_by_the_question_tools_under_its_thread_id(string tool)
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var noteId = ThreadIdOf((await _calls.SaveNoteAsync(id))["thread"]!);

      using var response = tool == "fetch_question"
         ? await _calls.SendAsync(_calls.Agent(session, On("fetch_question", id, questionId: noteId)))
         : await _calls.SendAsync(_calls.Agent(session, On("reply", id, questionId: noteId)), Done);

      await ErrorAsync(response, HttpStatusCode.NotFound, "questionNotFound");
   }

   private static string? TourToolBody(string tool) => tool switch
   {
      "read_state" => null,
      "set_plan" => """{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "title": "What changes", "kind": "overview", "chapter": "a" }] }""",
      _ => "{}",
   };

   private TourWriter Writer => _harness.Server.Services.GetRequiredService<TourWriter>();

   private static string[] ThreadIds(JsonNode tour) => [.. tour["threads"]!.AsArray().Select(thread => ThreadIdOf(thread!))];
}
