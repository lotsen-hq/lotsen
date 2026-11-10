using System.Net;
using System.Text.Json.Nodes;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>The HTTP calls that the note and request tests make to one harness's server: as the UI, as the CLI and as an agent session.</summary>
internal sealed class ThreadCalls
{
   public const string CreateBody = $$"""{ "prUrl": "{{TestPlatform.PrUrl}}", "familiarity": "some" }""";

   public const string Done = """{ "resolution": "done", "text": "It covers the change." }""";

   public static readonly TimeSpan EventWait = TimeSpan.FromSeconds(10);

   private readonly ServerHarness _harness;

   public ThreadCalls(ServerHarness harness)
   {
      _harness = harness;
   }

   public static string NoteBody(string text) => $$"""{ "text": "{{text}}", "anchor": { "kind": "tour" } }""";

   public static string StartBody(string text, string anchor = """{ "kind": "tour" }""") => $$"""{ "text": "{{text}}", "anchor": {{anchor}} }""";

   public static string MessageBody(string text) => $$"""{ "text": "{{text}}" }""";

   public static string SendBody(params string[] threadIds) => $$"""{ "threadIds": [{{string.Join(", ", threadIds.Select(id => $"\"{id}\""))}}] }""";

   public static string ThreadIdOf(JsonNode thread) => (string)thread["id"]!;

   public static string QuestionIdOf(JsonNode thread, int message = 0) => (string)thread["messages"]![message]!["id"]!;

   public static JsonNode RequestOf(JsonNode thread, int message = 0, int index = 0) => thread["messages"]![message]!["requests"]![index]!;

   /// <summary>Returns the call as the UI, with a UI credential from a new one-time code.</summary>
   public async Task<PreparedCall> UiAsync(OperationCall call) => new(call, await _harness.UiCredentialAsync(), null);

   public PreparedCall Cli(OperationCall call) => new(call, _harness.State.Token, null);

   public PreparedCall Agent(AgentSessionConnection session, OperationCall call) => session.Request(_harness, call);

   public async Task<string> CreateAsync(AgentSessionConnection session) => (string)(await CallAsync(Agent(session, "create_tour"), CreateBody))["id"]!;

   /// <summary>Asks a question about the tour as the UI and returns its thread.</summary>
   public async Task<JsonNode> AskAsync(string tourId, string text = "What does this tour cover?") =>
      await CallAsync(await UiAsync(On("ask_question", tourId)), NoteBody(text));

   /// <summary>Saves a note about the tour as the UI and returns the response, the thread with the tour's update time.</summary>
   public async Task<JsonNode> SaveNoteAsync(string tourId, string text = "Check the rounding later.") => await CallAsync(await UiAsync(On("save_note", tourId)), NoteBody(text));

   /// <summary>Sends notes as the UI and returns the response, the sent threads with the tour's update time.</summary>
   public async Task<JsonNode> SendNotesAsync(string tourId, params string[] threadIds) => await CallAsync(await UiAsync(On("send_notes", tourId)), SendBody(threadIds));

   /// <summary>Starts a thread about the tour as the agent session and returns the response, the thread with the tour's update time.</summary>
   public async Task<JsonNode> StartThreadAsync(AgentSessionConnection session, string tourId, string text = "The rounding needs a test.") =>
      await CallAsync(Agent(session, On("start_thread", tourId)), StartBody(text));

   /// <summary>Adds the agent session's message to a thread and returns the response, the thread with the tour's update time.</summary>
   public async Task<JsonNode> AddMessageAsync(AgentSessionConnection session, string tourId, string threadId, string text = "Folded into the revised stop.") =>
      await CallAsync(Agent(session, On("add_thread_message", tourId, threadId: threadId)), MessageBody(text));

   public async Task<JsonNode> FetchAsync(AgentSessionConnection session, string tourId, string questionId) => await CallAsync(Agent(session, On("fetch_question", tourId, questionId: questionId)));

   public async Task<JsonNode> AckAsync(AgentSessionConnection session, string nonce) =>
      await CallAsync(Agent(session, "ack"), $$"""{ "nonce": "{{nonce}}" }""");

   /// <summary>Returns the tour's threads as the CLI reads them.</summary>
   public async Task<JsonArray> ThreadsAsync(string tourId) => (await _harness.CallAsync(On("read_state", tourId)))["threads"]!.AsArray();

   /// <inheritdoc cref="ServerHarness.DropSessionAsync"/>
   public Task DropAsync(AgentSessionConnection session) => _harness.DropSessionAsync(session);

   /// <summary>Sends the call with the rest of its input, when given.</summary>
   public Task<HttpResponseMessage> SendAsync(PreparedCall call, string? json = null) => _harness.SendAsync(call, json);

   public async Task<JsonNode> CallAsync(PreparedCall call, string? json = null)
   {
      using var response = await SendAsync(call, json);
      var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
      Assert.True(response.IsSuccessStatusCode, $"{call.Call.Name} returned {(int)response.StatusCode}: {body}");
      return SuccessBody.Output(body);
   }

   public static async Task<JsonNode> ErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
   {
      var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
      Assert.True(response.StatusCode == status, $"Expected {(int)status}, got {(int)response.StatusCode}: {body}");
      var error = JsonNode.Parse(body)!;
      Assert.Equal(code, (string)error["code"]!);
      return error;
   }
}

