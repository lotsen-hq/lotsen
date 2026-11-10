using System.Net;
using System.Text.Json.Nodes;
using static Lotsen.Tests.OperationCall;
using static Lotsen.Tests.ThreadCalls;

namespace Lotsen.Tests;

/// <summary>Covers the authorship of thread messages: the server takes each message's author from the caller's connection, and no input property names it.</summary>
[Trait("Category", "Threads")]
public sealed class AuthorshipTests : IAsyncLifetime
{
   private ServerHarness _harness = null!;
   private ThreadCalls _calls = null!;

   public async ValueTask InitializeAsync()
   {
      _harness = await ServerHarness.StartAsync();
      _calls = new ThreadCalls(_harness);
   }

   public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

   /// <summary>A second session takes the tour over halfway, so the agent messages name two sessions.</summary>
   [Fact]
   public async Task Authorship_of_each_message_follows_the_connection_that_wrote_it()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      await using var other = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var asked = await _calls.AskAsync(id);
      var threadId = ThreadIdOf(asked);
      await _calls.CallAsync(_calls.Agent(session, On("reply", id, questionId: QuestionIdOf(asked))), Done);
      await _calls.AddMessageAsync(session, id, threadId);
      await _calls.CallAsync(_calls.Agent(other, On("attach_tour", id)), """{ "takeOver": true }""");
      await _calls.AddMessageAsync(other, id, threadId);
      await _calls.CallAsync(await _calls.UiAsync(On("ask_follow_up", id, threadId: threadId)), """{ "text": "Thanks." }""");

      var messages = (await _calls.ThreadsAsync(id))[0]!["messages"]!.AsArray();

      Assert.Equal(["question", "reply", "agentMessage", "agentMessage", "question"], messages.Select(message => (string)message!["kind"]!));
      Assert.Equal([session.Id, session.Id, other.Id], messages.Where(message => (string)message!["kind"]! != "question").Select(message => (string)message!["sessionId"]!));
   }

   [Theory]
   [InlineData("start", """{ "text": "Mine.", "anchor": { "kind": "tour" }, "author": "user" }""")]
   [InlineData("start", """{ "text": "Mine.", "anchor": { "kind": "tour" }, "sessionId": "someone" }""")]
   [InlineData("start", """{ "text": "Mine.", "anchor": { "kind": "tour" }, "requestId": "r1" }""")]
   [InlineData("message", """{ "text": "Mine.", "author": "user" }""")]
   [InlineData("message", """{ "text": "Mine.", "sessionId": "someone" }""")]
   [InlineData("ask", """{ "text": "Mine.", "anchor": { "kind": "tour" }, "author": "agent" }""")]
   [InlineData("followUp", """{ "text": "Mine.", "author": "agent" }""")]
   public async Task Authorship_cannot_be_set_by_an_input_property(string write, string body)
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var started = await _calls.StartThreadAsync(session, id);
      var threadId = ThreadIdOf(started["thread"]!);
      var before = await _calls.ThreadsAsync(id);
      var request = write switch
      {
         "start" => _calls.Agent(session, On("start_thread", id)),
         "message" => _calls.Agent(session, On("add_thread_message", id, threadId: threadId)),
         "ask" => await _calls.UiAsync(On("ask_question", id)),
         _ => await _calls.UiAsync(On("ask_follow_up", id, threadId: threadId)),
      };

      using var response = await _calls.SendAsync(request, body);

      await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.True(JsonNode.DeepEquals(before, await _calls.ThreadsAsync(id)), "The refused write changed the threads.");
   }

   /// <summary>The UI writes only questions and notes, and an agent session only its own messages, so neither can write a message as the other.</summary>
   [Theory]
   [InlineData("uiStartsAThread")]
   [InlineData("uiAddsAMessage")]
   [InlineData("agentAsks")]
   [InlineData("agentFollowsUp")]
   public async Task Authorship_of_the_other_party_is_refused_to_each_caller(string write)
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var started = await _calls.StartThreadAsync(session, id);
      var threadId = ThreadIdOf(started["thread"]!);
      var request = write switch
      {
         "uiStartsAThread" => (await _calls.UiAsync(On("start_thread", id)), StartBody("Mine.")),
         "uiAddsAMessage" => (await _calls.UiAsync(On("add_thread_message", id, threadId: threadId)), MessageBody("Mine.")),
         "agentAsks" => (_calls.Agent(session, On("ask_question", id)), NoteBody("Mine.")),
         _ => (_calls.Agent(session, On("ask_follow_up", id, threadId: threadId)), MessageBody("Mine.")),
      };

      using var response = await _calls.SendAsync(request.Item1, request.Item2);

      await ErrorAsync(response, HttpStatusCode.Forbidden, "callerNotAllowed");
      Assert.True(JsonNode.DeepEquals(new JsonArray(started["thread"]!.DeepClone()), await _calls.ThreadsAsync(id)), "The refused write changed the threads.");
   }
}
