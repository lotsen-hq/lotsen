using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Lotsen.Operations;
using Lotsen.Server;
using Lotsen.Sessions;
using Microsoft.Extensions.DependencyInjection;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>
/// Covers <c>set_thread_state</c>: the UI resolves and reopens a thread with or without a bound session, the change reaches only lotsen's record and the event stream, and a question that
/// the reviewer sends in a resolved thread reopens it.
/// </summary>
[Trait("Category", "Threads")]
public sealed class ResolveThreadTests : IAsyncLifetime
{
   private const string CreateBody = $$"""{ "prUrl": "{{TestPlatform.PrUrl}}", "familiarity": "some" }""";

   private const string TourQuestion = """{ "text": "What does this tour cover?", "anchor": { "kind": "tour" } }""";

   private const string FollowUp = """{ "text": "And what does it leave out?" }""";

   private const string Done = """{ "resolution": "done", "text": "It covers the change." }""";

   private const string Resolved = """{ "state": "resolved" }""";

   private const string Open = """{ "state": "open" }""";

   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

   private ServerHarness _harness = null!;

   public async ValueTask InitializeAsync() => _harness = await ServerHarness.StartAsync();

   public async ValueTask DisposeAsync()
   {
      Writer.Hook = null;
      await _harness.DisposeAsync();
   }

   [Fact]
   public async Task Resolve_and_reopen_round_trip_through_the_store_and_reach_other_views_as_events()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(session);
      var asked = await AskAsync(id);
      await using var stream = await EventStream.OpenAsync(_harness);

      var resolved = await CallAsync(await UiRequestAsync(On("set_thread_state", id, threadId: ThreadIdOf(asked))), Resolved);
      var resolvedEvent = await stream.NextAsync(Timeout);
      var stored = await ThreadsAsync(id);
      var reopened = await CallAsync(await UiRequestAsync(On("set_thread_state", id, threadId: ThreadIdOf(asked))), Open);
      var reopenedEvent = await stream.NextAsync(Timeout);

      Assert.Equal("open", (string)asked["state"]!);
      Assert.Equal(id, (string)resolved["tourId"]!);
      Assert.Equal("resolved", (string)resolved["thread"]!["state"]!);
      Assert.True(JsonNode.DeepEquals(asked["messages"], resolved["thread"]!["messages"]), "Resolving changed the thread's messages.");
      Assert.Equal(ServerEvents.ThreadUpdated.Name, resolvedEvent.Type);
      Assert.True(JsonNode.DeepEquals(resolved["thread"], EventStream.WithoutStatus(resolvedEvent.Data["thread"]!)), $"The event's thread differs from the response's: {resolvedEvent.Data}");
      Assert.Equal((string)resolved["updatedAt"]!, (string)resolvedEvent.Data["updatedAt"]!);
      Assert.True(JsonNode.DeepEquals(resolved["thread"], stored[0]), $"The stored thread differs from the response's: {stored[0]}");
      Assert.Equal("open", (string)reopened["thread"]!["state"]!);
      Assert.Equal(ServerEvents.ThreadUpdated.Name, reopenedEvent.Type);
      Assert.Equal("open", (string)reopenedEvent.Data["thread"]!["state"]!);
      Assert.True((DateTimeOffset)reopened["updatedAt"]! > (DateTimeOffset)resolved["updatedAt"]!, "Reopening did not move the tour's update time.");
      Assert.True(JsonNode.DeepEquals(asked, (await ThreadsAsync(id))[0]), "The reopened thread differs from the thread as asked.");
   }

   [Fact]
   public async Task Resolve_for_the_ui_with_prose_as_html_returns_each_reply_with_its_html_as_the_event_does()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(session);
      var asked = await AskAsync(id);
      await CallAsync(ReplyRequest(session, id, QuestionIdOf(asked, 0)), Done);
      await using var stream = await EventStream.OpenAsync(_harness);
      var request = (await UiRequestAsync(On("set_thread_state", id, threadId: ThreadIdOf(asked)))).WithProseHtml();

      var resolved = await CallAsync(request, Resolved);
      var updated = await stream.NextAsync(Timeout);

      Assert.Equal("<p>It covers the change.</p>\n", (string)resolved["thread"]!["messages"]![1]!["html"]!);
      Assert.True(JsonNode.DeepEquals(resolved["thread"], updated.Data["thread"]), $"The event's thread differs from the response's: {updated.Data}");
   }

   [Fact]
   public async Task Resolve_and_reopen_work_with_no_session_bound()
   {
      var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(session);
      var asked = await AskAsync(id);
      await CallAsync(ReplyRequest(session, id, QuestionIdOf(asked, 0)), Done);
      await _harness.DropSessionAsync(session);

      var resolved = await CallAsync(await UiRequestAsync(On("set_thread_state", id, threadId: ThreadIdOf(asked))), Resolved);
      var stored = await ThreadsAsync(id);
      var reopened = await CallAsync(await UiRequestAsync(On("set_thread_state", id, threadId: ThreadIdOf(asked))), Open);

      Assert.Null(await _harness.BindingOfAsync(id));
      Assert.Equal("resolved", (string)resolved["thread"]!["state"]!);
      Assert.Equal("resolved", (string)stored[0]!["state"]!);
      Assert.Equal("open", (string)reopened["thread"]!["state"]!);
   }

   [Fact]
   public async Task Resolve_shows_in_read_state_and_fetch_question_for_the_bound_session()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(session);
      var asked = await AskAsync(id);
      await CallAsync(await UiRequestAsync(On("set_thread_state", id, threadId: ThreadIdOf(asked))), Resolved);

      var read = await CallAsync(session.Request(_harness, On("read_state", id)));
      var fetched = await CallAsync(session.Request(_harness, On("fetch_question", id, questionId: QuestionIdOf(asked, 0))));

      Assert.Equal("resolved", (string)read["threads"]![0]!["state"]!);
      Assert.Equal("resolved", (string)fetched["thread"]!["state"]!);
   }

   [Fact]
   public async Task Resolve_after_a_follow_up_keeps_the_follow_up_waiting_and_resolves_the_thread()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(session);
      var asked = await AskAsync(id);
      await CallAsync(ReplyRequest(session, id, QuestionIdOf(asked, 0)), Done);

      await CallAsync(await UiRequestAsync(On("ask_follow_up", id, threadId: ThreadIdOf(asked))), FollowUp);
      var resolved = await CallAsync(await UiRequestAsync(On("set_thread_state", id, threadId: ThreadIdOf(asked))), Resolved);

      var stored = (await ThreadsAsync(id))[0]!;
      Assert.Equal(["question", "reply", "question"], Kinds(resolved["thread"]!));
      Assert.Equal("resolved", (string)stored["state"]!);
      Assert.Equal(["pending"], RequestStates(stored, 2));
   }

   /// <summary>The resolve lands after the reply's checks and before its store, so the reply's update keeps the state that the resolve stored.</summary>
   [Fact]
   public async Task Resolve_while_a_reply_is_checked_keeps_both_the_reply_and_the_resolved_state()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(session);
      var asked = await AskAsync(id);
      Writer.Hook = WriterHooks.AfterGit(async _ =>
      {
         Writer.Hook = null;
         await CallAsync(await UiRequestAsync(On("set_thread_state", id, threadId: ThreadIdOf(asked))), Resolved);
      }, "reply");

      var replied = await CallAsync(ReplyRequest(session, id, QuestionIdOf(asked, 0)), Done);

      Assert.Equal("resolved", (string)replied["state"]!);
      Assert.Equal(["question", "reply"], Kinds(replied));
      Assert.True(JsonNode.DeepEquals(replied, (await ThreadsAsync(id))[0]), "The stored thread differs from the reply's.");
   }

   [Theory]
   [InlineData("""{ "state": "closed" }""")]
   [InlineData("{}")]
   public async Task Resolve_with_an_unknown_or_missing_state_is_refused(string body)
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(session);
      var asked = await AskAsync(id);

      using var response = await SendAsync(await UiRequestAsync(On("set_thread_state", id, threadId: ThreadIdOf(asked))), body);

      await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("open", (string)(await ThreadsAsync(id))[0]!["state"]!);
   }

   private TourWriter Writer => _harness.Server.Services.GetRequiredService<TourWriter>();

   private static string ThreadIdOf(JsonNode thread) => (string)thread["id"]!;

   private static string QuestionIdOf(JsonNode thread, int message) => (string)thread["messages"]![message]!["id"]!;

   private static string[] RequestStates(JsonNode thread, int message) => [.. thread["messages"]![message]!["requests"]!.AsArray().Select(request => (string)request!["state"]!)];

   private static string[] Kinds(JsonNode thread) => [.. thread["messages"]!.AsArray().Select(message => (string)message!["kind"]!)];

   private PreparedCall ReplyRequest(AgentSessionConnection session, string tourId, string questionId) =>
      session.Request(_harness, On("reply", tourId, questionId: questionId));

   // A path that ends in /state takes a PUT, and every other UI write here a POST.
   private async Task<PreparedCall> UiRequestAsync(OperationCall call) => new(call, await _harness.UiCredentialAsync());

   private async Task<JsonNode> AskAsync(string tourId) => await CallAsync(await UiRequestAsync(On("ask_question", tourId)), TourQuestion);

   private async Task<string> CreateAsync(AgentSessionConnection session) => (string)(await CallAsync(session.Request(_harness, "create_tour"), CreateBody))["id"]!;

   private async Task<JsonArray> ThreadsAsync(string tourId) => (await _harness.CallAsync(On("read_state", tourId)))["threads"]!.AsArray();

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
