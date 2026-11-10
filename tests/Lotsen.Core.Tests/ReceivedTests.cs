using System.Text.Json.Nodes;
using Lotsen.Operations;
using static Lotsen.Tests.OperationCall;
using static Lotsen.Tests.ThreadCalls;

namespace Lotsen.Tests;

/// <summary>
/// Covers the received mark of a request: the first <c>fetch_question</c> of the session that a request went to marks the request received, once, and the mark reaches the UI as an event.
/// </summary>
[Trait("Category", "Threads")]
public sealed class ReceivedTests : IAsyncLifetime
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
   public async Task Received_is_marked_by_the_first_fetch_and_reaches_the_ui_as_an_event()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var asked = await _calls.AskAsync(id);
      await using var stream = await EventStream.OpenAsync(_harness);

      var fetched = await _calls.FetchAsync(session, id, QuestionIdOf(asked));
      var updated = await stream.NextAsync(EventWait);

      Assert.False(RequestOf(asked).AsObject().ContainsKey("receivedAt"), $"The request was received before any fetch: {asked}");
      var received = RequestOf(fetched["thread"]!);
      Assert.Equal("pending", (string)received["state"]!);
      Assert.True((DateTimeOffset)received["receivedAt"]! >= (DateTimeOffset)received["sentAt"]!, $"The request was received before it was sent: {received}");
      Assert.Equal(ServerEvents.ThreadUpdated.Name, updated.Type);
      Assert.True(JsonNode.DeepEquals(fetched["thread"], EventStream.WithoutStatus(updated.Data["thread"]!)), $"The event's thread differs from the fetched one: {updated.Data}");
      Assert.True(JsonNode.DeepEquals(fetched["thread"], (await _calls.ThreadsAsync(id))[0]), "The stored thread differs from the fetched one.");
   }

   /// <summary>The thread asked after the second fetch is the stream's next event, so the second fetch published nothing.</summary>
   [Fact]
   public async Task Received_stays_as_the_first_fetch_marked_it_and_a_later_fetch_changes_nothing()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var asked = await _calls.AskAsync(id);
      var first = await _calls.FetchAsync(session, id, QuestionIdOf(asked));
      var before = (string)(await _harness.CallAsync(On("read_state", id)))["updatedAt"]!;
      await using var stream = await EventStream.OpenAsync(_harness);

      var second = await _calls.FetchAsync(session, id, QuestionIdOf(asked));
      var after = (string)(await _harness.CallAsync(On("read_state", id)))["updatedAt"]!;
      var marker = await _calls.AskAsync(id, "Marker.");
      var next = await stream.NextAsync(EventWait);

      Assert.True(JsonNode.DeepEquals(first, second), $"The second fetch differs from the first: {second}");
      Assert.Equal(before, after);
      Assert.Equal((ServerEvents.ThreadCreated.Name, ThreadIdOf(marker)), (next.Type, (string)next.Data["thread"]!["id"]!));
   }

   /// <summary>A fetch tells that its session has the question, so it marks only the requests that went to that session.</summary>
   [Fact]
   public async Task Received_is_not_marked_by_a_fetch_from_a_session_that_the_request_did_not_go_to()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      await using var other = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var asked = await _calls.AskAsync(id);

      var fetched = await _calls.CallAsync(_calls.Agent(other, On("fetch_question", id, questionId: QuestionIdOf(asked))));

      Assert.False(RequestOf(fetched["thread"]!).AsObject().ContainsKey("receivedAt"), $"The fetch marked the request: {fetched}");
      Assert.True(JsonNode.DeepEquals(asked, (await _calls.ThreadsAsync(id))[0]), "The fetch changed the stored thread.");
   }

   /// <summary>Both requests of a question sent twice went to the same session, which has the question once it fetches it.</summary>
   [Fact]
   public async Task Received_marks_every_request_of_the_question_that_went_to_the_fetching_session()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var asked = await _calls.AskAsync(id);
      await _calls.CallAsync(await _calls.UiAsync(On("resend_question", id, questionId: QuestionIdOf(asked))));

      var fetched = await _calls.FetchAsync(session, id, QuestionIdOf(asked));

      var requests = fetched["thread"]!["messages"]![0]!["requests"]!.AsArray();
      Assert.Equal(["canceled", "pending"], requests.Select(request => (string)request!["state"]!));
      Assert.All(requests, request => Assert.True(request!.AsObject().ContainsKey("receivedAt"), $"A request was not marked: {request}"));
   }

   [Fact]
   public async Task Received_of_a_request_sent_after_the_fetch_waits_for_the_next_fetch()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var asked = await _calls.AskAsync(id);
      var first = await _calls.FetchAsync(session, id, QuestionIdOf(asked));
      var resent = await _calls.CallAsync(await _calls.UiAsync(On("resend_question", id, questionId: QuestionIdOf(asked))));

      var second = await _calls.FetchAsync(session, id, QuestionIdOf(asked));

      Assert.False(RequestOf(resent, 0, 1).AsObject().ContainsKey("receivedAt"), $"The re-sent request was received before a fetch: {resent}");
      Assert.Equal((string)RequestOf(first["thread"]!)["receivedAt"]!, (string)RequestOf(second["thread"]!)["receivedAt"]!);
      Assert.True(RequestOf(second["thread"]!, 0, 1).AsObject().ContainsKey("receivedAt"), $"The second fetch did not mark the re-sent request: {second}");
   }

   [Fact]
   public async Task Received_is_marked_on_a_question_sent_from_a_note_and_on_a_follow_up()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var note = ThreadIdOf((await _calls.SaveNoteAsync(id))["thread"]!);
      var sent = await _calls.SendNotesAsync(id, note);
      var questionId = QuestionIdOf(sent["threads"]![0]!);
      await _calls.FetchAsync(session, id, questionId);
      await _calls.CallAsync(_calls.Agent(session, On("reply", id, questionId: questionId)), Done);
      var followed = await _calls.CallAsync(await _calls.UiAsync(On("ask_follow_up", id, threadId: note)), """{ "text": "And more?" }""");

      var fetched = await _calls.FetchAsync(session, id, QuestionIdOf(followed, 2));

      Assert.True(RequestOf(fetched["thread"]!).AsObject().ContainsKey("receivedAt"), $"The sent note's request was not marked: {fetched}");
      Assert.True(RequestOf(fetched["thread"]!, 2).AsObject().ContainsKey("receivedAt"), $"The follow-up's request was not marked: {fetched}");
   }

   /// <summary>Two fetches race: the store's update marks the request once, so both see the same mark and the stream gets one event for it.</summary>
   [Fact]
   public async Task Received_by_two_racing_fetches_is_marked_once()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var asked = await _calls.AskAsync(id);
      await using var stream = await EventStream.OpenAsync(_harness);

      var fetches = await Task.WhenAll(_calls.FetchAsync(session, id, QuestionIdOf(asked)), _calls.FetchAsync(session, id, QuestionIdOf(asked)));
      var updated = await stream.NextAsync(EventWait);
      var marker = await _calls.AskAsync(id, "Marker.");
      var next = await stream.NextAsync(EventWait);

      Assert.Equal((string)RequestOf(fetches[0]["thread"]!)["receivedAt"]!, (string)RequestOf(fetches[1]["thread"]!)["receivedAt"]!);
      Assert.Equal(ServerEvents.ThreadUpdated.Name, updated.Type);
      Assert.Equal((ServerEvents.ThreadCreated.Name, ThreadIdOf(marker)), (next.Type, (string)next.Data["thread"]!["id"]!));
   }
}
