using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Lotsen.Operations;
using Lotsen.Sessions;
using Microsoft.Extensions.DependencyInjection;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>
/// Covers <c>ask_follow_up</c>: a follow-up adds a question to an existing thread with its own pending request, Claude fetches it with the whole thread and answers it there, and each question
/// of the thread keeps its own requests and its own first answer.
/// </summary>
[Trait("Category", "Threads")]
public sealed class FollowUpTests : IAsyncLifetime
{
   private const string CreateBody = $$"""{ "prUrl": "{{TestPlatform.PrUrl}}", "familiarity": "some" }""";

   private const string TourQuestion = """{ "text": "What does this tour cover?", "anchor": { "kind": "tour" } }""";

   private const string FollowUp = """{ "text": "And what does it leave out?" }""";

   private const string Done = """{ "resolution": "done", "text": "It covers the change." }""";

   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

   private ServerHarness _harness = null!;

   public async ValueTask InitializeAsync() => _harness = await ServerHarness.StartAsync();

   public async ValueTask DisposeAsync()
   {
      Writer.Hook = null;
      await _harness.DisposeAsync();
   }

   [Fact]
   public async Task FollowUp_adds_a_question_with_a_pending_request_to_the_thread_and_its_reply_lands_in_the_same_thread()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(session);
      await session.ProbeAsync();
      var asked = await AskAsync(id);
      _ = await session.Stream.NextAsync(Timeout);
      await CallAsync(ReplyRequest(session, id, QuestionIdOf(asked, 0)), Done);
      await using var stream = await EventStream.OpenAsync(_harness);

      var followed = await CallAsync(await UiRequestAsync(On("ask_follow_up", id, threadId: ThreadIdOf(asked))), FollowUp);
      var updated = await stream.NextAsync(Timeout);
      var sent = await session.Stream.NextAsync(Timeout);
      var replied = await CallAsync(ReplyRequest(session, id, QuestionIdOf(followed, 2)), """{ "resolution": "done", "text": "It leaves out the tests." }""");

      Assert.Equal(["question", "reply", "question"], Kinds(followed));
      Assert.Equal("And what does it leave out?", (string)followed["messages"]![2]!["text"]!);
      Assert.Equal(["pending"], RequestStates(followed, 2));
      Assert.Equal(session.Id, (string)followed["messages"]![2]!["requests"]![0]!["sessionId"]!);
      Assert.Equal(ServerEvents.ThreadUpdated.Name, updated.Type);
      Assert.Equal(["question", "reply", "question"], Kinds(updated.Data["thread"]!));
      Assert.True(JsonNode.DeepEquals(followed["messages"]![2], updated.Data["thread"]!["messages"]![2]), $"The event's follow-up differs from the follow-up's result: {updated.Data}");
      Assert.Equal(SessionEvents.Question, sent.Type);
      Assert.Equal(
         (ThreadIdOf(asked), QuestionIdOf(followed, 2), RequestIdOf(followed, 2, 0), "And what does it leave out?"),
         ((string)sent.Data["threadId"]!, (string)sent.Data["questionId"]!, (string)sent.Data["requestId"]!, (string)sent.Data["text"]!));
      Assert.Equal(ThreadIdOf(asked), ThreadIdOf(replied));
      Assert.Equal(["question", "reply", "question", "reply"], Kinds(replied));
      Assert.Equal((RequestIdOf(followed, 2, 0), "It leaves out the tests."), ((string)replied["messages"]![3]!["requestId"]!, (string)replied["messages"]![3]!["text"]!));
      Assert.Equal(["done"], RequestStates(replied, 0));
      Assert.Equal(["done"], RequestStates(replied, 2));
      Assert.Single(await ThreadsAsync(id));
   }

   [Fact]
   public async Task FollowUp_is_fetched_with_its_thread_holding_every_earlier_message_and_the_stop_it_viewed()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(session);
      var plan = await CallAsync(
         session.Request(_harness, On("set_plan", id)),
         """{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "title": "What changes", "kind": "overview", "chapter": "a" }] }""");
      var stopId = (string)plan["plan"]!["stops"]![0]!["id"]!;
      var asked = await AskAsync(id);
      await CallAsync(ReplyRequest(session, id, QuestionIdOf(asked, 0)), Done);
      var followed = await CallAsync(await UiRequestAsync(On("ask_follow_up", id, threadId: ThreadIdOf(asked))), $$"""{ "text": "And what does it leave out?", "stopId": "{{stopId}}" }""");

      var fetched = await CallAsync(session.Request(_harness, On("fetch_question", id, questionId: QuestionIdOf(followed, 2))));

      Assert.Equal(QuestionIdOf(followed, 2), (string)fetched["questionId"]!);
      Assert.True(JsonNode.DeepEquals((await ThreadsAsync(id))[0], fetched["thread"]), $"The fetched thread differs from the stored one: {fetched["thread"]}");
      Assert.Equal(Kinds(followed), Kinds(fetched["thread"]!));
      Assert.Equal(["What does this tour cover?", "It covers the change.", "And what does it leave out?"], fetched["thread"]!["messages"]!.AsArray().Select(message => (string)message!["text"]!));
      Assert.Equal(stopId, (string)fetched["stop"]!["id"]!);
   }

   [Fact]
   public async Task FollowUp_with_no_session_attached_is_refused_as_a_first_question_is()
   {
      var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(session);
      var asked = await AskAsync(id);
      await CallAsync(ReplyRequest(session, id, QuestionIdOf(asked, 0)), Done);
      await _harness.DropSessionAsync(session);
      var before = await ThreadsAsync(id);

      using var followUp = await SendAsync(await UiRequestAsync(On("ask_follow_up", id, threadId: ThreadIdOf(asked))), FollowUp);
      using var question = await SendAsync(await UiRequestAsync(On("ask_question", id)), TourQuestion);

      var followUpError = await ErrorAsync(followUp, HttpStatusCode.Conflict, "noSessionAttached");
      var questionError = await ErrorAsync(question, HttpStatusCode.Conflict, "noSessionAttached");
      Assert.Equal((string)questionError["reason"]!, (string)followUpError["reason"]!);
      Assert.True(JsonNode.DeepEquals(before, await ThreadsAsync(id)), "The refused follow-up changed the thread.");
   }

   /// <summary>The follow-up's request names the session bound when the server stored it, so the settlement of the session's end marks it unanswered.</summary>
   [Fact]
   public async Task FollowUp_whose_session_drops_after_it_is_marked_unanswered()
   {
      var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(session);
      var asked = await AskAsync(id);
      await CallAsync(ReplyRequest(session, id, QuestionIdOf(asked, 0)), Done);

      var followed = await CallAsync(await UiRequestAsync(On("ask_follow_up", id, threadId: ThreadIdOf(asked))), FollowUp);
      await DropAsync(session);

      Assert.Equal(["pending"], RequestStates(followed, 2));
      Assert.Equal(["unanswered"], RequestStates((await ThreadsAsync(id))[0]!, 2));
   }

   [Fact]
   public async Task FollowUp_after_a_take_over_goes_to_the_new_session()
   {
      await using var first = await AgentSessionConnection.OpenAsync(_harness);
      await using var second = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(first);
      var asked = await AskAsync(id);
      await CallAsync(ReplyRequest(first, id, QuestionIdOf(asked, 0)), Done);
      await CallAsync(second.Request(_harness, On("attach_tour", id)), """{ "takeOver": true }""");

      var followed = await CallAsync(await UiRequestAsync(On("ask_follow_up", id, threadId: ThreadIdOf(asked))), FollowUp);

      Assert.Equal(second.Id, (string)followed["messages"]![2]!["requests"]![0]!["sessionId"]!);
   }

   [Theory]
   [InlineData("""{ "text": "  " }""", "text")]
   [InlineData("""{ "text": "Why?", "stopId": "nosuchstop" }""", "stopId")]
   public async Task FollowUp_with_invalid_input_is_refused(string body, string field)
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(session);
      var asked = await AskAsync(id);
      await CallAsync(ReplyRequest(session, id, QuestionIdOf(asked, 0)), Done);

      using var response = await SendAsync(await UiRequestAsync(On("ask_follow_up", id, threadId: ThreadIdOf(asked))), body);

      var error = await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal(field, (string)error["field"]!);
   }

   private TourWriter Writer => _harness.Server.Services.GetRequiredService<TourWriter>();

   private static string ThreadIdOf(JsonNode thread) => (string)thread["id"]!;

   private static string QuestionIdOf(JsonNode thread, int message) => (string)thread["messages"]![message]!["id"]!;

   private static string RequestIdOf(JsonNode thread, int message, int index) => (string)thread["messages"]![message]!["requests"]![index]!["id"]!;

   private static string[] RequestStates(JsonNode thread, int message) => [.. thread["messages"]![message]!["requests"]!.AsArray().Select(request => (string)request!["state"]!)];

   private static string[] Kinds(JsonNode thread) => [.. thread["messages"]!.AsArray().Select(message => (string)message!["kind"]!)];

   private PreparedCall ReplyRequest(AgentSessionConnection session, string tourId, string questionId) =>
      session.Request(_harness, On("reply", tourId, questionId: questionId));

   private async Task<PreparedCall> UiRequestAsync(OperationCall call) => new(call, await _harness.UiCredentialAsync());

   private async Task<JsonNode> AskAsync(string tourId) => await CallAsync(await UiRequestAsync(On("ask_question", tourId)), TourQuestion);

   private async Task<string> CreateAsync(AgentSessionConnection session) => (string)(await CallAsync(session.Request(_harness, "create_tour"), CreateBody))["id"]!;

   private async Task<JsonArray> ThreadsAsync(string tourId) => (await _harness.CallAsync(On("read_state", tourId)))["threads"]!.AsArray();

   private Task DropAsync(AgentSessionConnection session) => _harness.DropSessionAsync(session);

   private Task<HttpResponseMessage> SendAsync(PreparedCall call, string? json = null) => _harness.SendAsync(call, json);

   private async Task<JsonNode> CallAsync(PreparedCall call, string? json = null)
   {
      using var response = await SendAsync(call, json);
      var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
      Assert.True(response.IsSuccessStatusCode, $"{call.Call.Name} returned {(int)response.StatusCode}: {body}");
      return SuccessBody.Output(body);
   }

   private static async Task<JsonNode> ErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
   {
      var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
      Assert.True(response.StatusCode == status, $"Expected {(int)status}, got {(int)response.StatusCode}: {body}");
      var error = JsonNode.Parse(body)!;
      Assert.Equal(code, (string)error["code"]!);
      return error;
   }
}
