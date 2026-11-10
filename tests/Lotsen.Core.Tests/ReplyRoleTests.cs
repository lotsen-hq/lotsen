using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Lotsen.Operations;
using Lotsen.Sessions;
using Microsoft.Extensions.DependencyInjection;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>
/// Covers which session may reply to a question, how a pending request becomes unanswered when its session detaches, and how the UI re-sends a question to the session bound now.
/// </summary>
[Trait("Category", "Roles")]
public sealed class ReplyRoleTests : IAsyncLifetime
{
   private const string CreateBody = $$"""{ "prUrl": "{{TestPlatform.PrUrl}}", "familiarity": "some" }""";

   private const string TourQuestion = """{ "text": "What does this tour cover?", "anchor": { "kind": "tour" } }""";

   private const string Done = """{ "resolution": "done", "text": "It covers *the change*." }""";

   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

   private ServerHarness _harness = null!;

   public async ValueTask InitializeAsync() => _harness = await ServerHarness.StartAsync();

   public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

   [Fact]
   public async Task Reply_from_the_bound_session_is_stored_as_an_agent_message_of_that_session_and_resolves_the_request()
   {
      await using var bound = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(bound);
      var asked = await AskAsync(id);
      await using var stream = await EventStream.OpenAsync(_harness);

      var thread = await CallAsync(ReplyRequest(bound, id, QuestionIdOf(asked)), Done);
      var updated = await stream.NextAsync(Timeout);
      var read = await _harness.CallAsync(On("read_state", id));

      var messages = thread["messages"]!.AsArray();
      Assert.Equal(2, messages.Count);
      var reply = messages[1]!;
      Assert.Equal(("reply", bound.Id, RequestIdOf(asked)), ((string)reply["kind"]!, (string)reply["sessionId"]!, (string)reply["requestId"]!));
      Assert.Equal("It covers *the change*.", (string)reply["text"]!);
      Assert.False(reply.AsObject().ContainsKey("html"), "The reply holds HTML without the prose header.");
      Assert.Equal("done", (string)messages[0]!["requests"]![0]!["state"]!);
      Assert.True(JsonNode.DeepEquals(thread, Assert.Single(read["threads"]!.AsArray())), $"The stored thread differs from the reply's result: {read["threads"]}");
      Assert.Equal((ServerEvents.ThreadUpdated.Name, id, (string)read["updatedAt"]!), (updated.Type, (string)updated.Data["tourId"]!, (string)updated.Data["updatedAt"]!));
      Assert.Equal("<p>It covers <em>the change</em>.</p>\n", (string)updated.Data["thread"]!["messages"]![1]!["html"]!);
   }

   /// <summary>The UI and the CLI are refused by the access of <c>reply</c>, as <c>AccessTests</c> covers, so only another session reaches the rule of the question's requests.</summary>
   [Fact]
   public async Task Reply_from_a_session_that_the_question_did_not_reach_is_refused_and_stores_nothing()
   {
      await using var bound = await AgentSessionConnection.OpenAsync(_harness);
      await using var other = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(bound);
      var asked = await AskAsync(id);

      using var response = await SendAsync(other.Request(_harness, On("reply", id, questionId: QuestionIdOf(asked))), Done);

      await ErrorAsync(response, HttpStatusCode.Forbidden, "callerNotAllowed");
      Assert.True(JsonNode.DeepEquals(asked, Assert.Single(await ThreadsAsync(id))), "The refused reply changed the thread.");
   }

   /// <summary>A session that may not reply learns that before it learns what is wrong with the reply.</summary>
   [Fact]
   public async Task Invalid_reply_from_another_session_is_refused_for_the_caller()
   {
      await using var bound = await AgentSessionConnection.OpenAsync(_harness);
      await using var other = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(bound);
      var asked = await AskAsync(id);

      using var response = await SendAsync(other.Request(_harness, On("reply", id, questionId: QuestionIdOf(asked))), """{ "resolution": "done" }""");

      await ErrorAsync(response, HttpStatusCode.Forbidden, "callerNotAllowed");
   }

   [Fact]
   public async Task Reply_from_a_session_that_the_question_did_not_reach_is_refused_after_its_own_session_dropped()
   {
      var first = await AgentSessionConnection.OpenAsync(_harness);
      await using var second = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(first);
      var asked = await AskAsync(id);
      await DropAsync(first);

      using var response = await SendAsync(ReplyRequest(second, id, QuestionIdOf(asked)), Done);

      var error = await ErrorAsync(response, HttpStatusCode.Forbidden, "callerNotAllowed");
      Assert.Contains("was not sent to this session", (string)error["reason"]!, StringComparison.Ordinal);
      Assert.Single(Assert.Single(await ThreadsAsync(id))!["messages"]!.AsArray());
   }

   [Theory]
   [InlineData("dropped")]
   [InlineData("takenOver")]
   public async Task Pending_request_becomes_unanswered_when_its_session_detaches(string detach)
   {
      var first = await AgentSessionConnection.OpenAsync(_harness);
      await using var second = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(first);
      var asked = await AskAsync(id);
      var answered = await AskAsync(id, """{ "text": "And this?", "anchor": { "kind": "tour" } }""");
      await CallAsync(ReplyRequest(first, id, QuestionIdOf(answered)), Done);
      await using var stream = await EventStream.OpenAsync(_harness);

      if (detach == "dropped")
      {
         await DropAsync(first);
      }
      else
      {
         await TakeOverAsync(second, id);
      }

      var binding = await stream.NextBesidesControlsAsync(Timeout);
      var updated = await stream.NextBesidesControlsAsync(Timeout);
      var threads = await ThreadsAsync(id);
      await first.DisposeAsync();

      Assert.Equal(ServerEvents.TourBindingChanged.Name, binding.Type);
      Assert.Equal(ServerEvents.ThreadUpdated.Name, updated.Type);
      Assert.Equal((string)asked["id"]!, (string)updated.Data["thread"]!["id"]!);
      Assert.Equal(["unanswered", "done"], threads.Select(thread => (string)thread!["messages"]![0]!["requests"]![0]!["state"]!));
      Assert.True(JsonNode.DeepEquals(threads[0], EventStream.WithoutStatus(updated.Data["thread"]!)), $"The event's thread differs from the stored one: {updated.Data}");
   }

   /// <summary>The take-over marks the old session's request unanswered, which the old session can still answer.</summary>
   [Fact]
   public async Task Request_that_a_take_over_left_unanswered_is_still_answered_by_its_session()
   {
      await using var first = await AgentSessionConnection.OpenAsync(_harness);
      await using var second = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(first);

      var asked = await AskAsync(id);
      await TakeOverAsync(second, id);
      var swept = Assert.Single(await ThreadsAsync(id))!;
      var replied = await CallAsync(ReplyRequest(first, id, QuestionIdOf(asked)), Done);

      Assert.Equal("pending", (string)asked["messages"]![0]!["requests"]![0]!["state"]!);
      Assert.Equal(["unanswered"], RequestStates(swept));
      Assert.Equal(["done"], RequestStates(replied));
   }

   /// <summary>The tour file names another session in the pending request, which no binding change leaves behind, so only the request's own session refuses the reply.</summary>
   [Fact]
   public async Task Reply_from_the_bound_session_to_a_question_sent_to_another_session_is_refused()
   {
      await using var bound = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(bound);
      var asked = await AskAsync(id);
      var file = Path.Combine(_harness.DataDirectory.ToursPath, $"{id}.json");
      var stored = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
      Assert.Contains(bound.Id, stored, StringComparison.Ordinal);
      await File.WriteAllTextAsync(file, stored.Replace(bound.Id, "another-session", StringComparison.Ordinal), TestContext.Current.CancellationToken);

      using var response = await SendAsync(ReplyRequest(bound, id, QuestionIdOf(asked)), Done);

      var error = await ErrorAsync(response, HttpStatusCode.Forbidden, "callerNotAllowed");
      Assert.Contains("was not sent to this session", (string)error["reason"]!, StringComparison.Ordinal);
      Assert.Single(Assert.Single(await ThreadsAsync(id))!["messages"]!.AsArray());
   }

   [Fact]
   public async Task Resend_delivers_a_new_request_for_the_same_question_to_the_session_bound_now()
   {
      await using var first = await AgentSessionConnection.OpenAsync(_harness);
      await using var second = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(first);
      var asked = await AskAsync(id);
      await TakeOverAsync(second, id);
      await second.ProbeAsync();
      await using var stream = await EventStream.OpenAsync(_harness);

      var thread = await CallAsync(await ResendRequestAsync(id, QuestionIdOf(asked)));
      var updated = await stream.NextAsync(Timeout);
      var sent = await second.Stream.NextAsync(Timeout);
      var replied = await CallAsync(ReplyRequest(second, id, QuestionIdOf(asked)), """{ "resolution": "declined" }""");

      var requests = thread["messages"]![0]!["requests"]!.AsArray();
      Assert.Equal(2, requests.Count);
      Assert.Equal(("unanswered", first.Id), ((string)requests[0]!["state"]!, (string)requests[0]!["sessionId"]!));
      Assert.Equal(("pending", second.Id), ((string)requests[1]!["state"]!, (string)requests[1]!["sessionId"]!));
      Assert.NotEqual((string)requests[0]!["id"]!, (string)requests[1]!["id"]!);
      Assert.Equal(ServerEvents.ThreadUpdated.Name, updated.Type);
      Assert.True(JsonNode.DeepEquals(thread, EventStream.WithoutStatus(updated.Data["thread"]!)), $"The event's thread differs from the re-send's result: {updated.Data}");
      Assert.Equal(SessionEvents.Question, sent.Type);
      Assert.Equal((QuestionIdOf(asked), (string)requests[1]!["id"]!, "What does this tour cover?"), ((string)sent.Data["questionId"]!, (string)sent.Data["requestId"]!, (string)sent.Data["text"]!));
      Assert.Equal(["unanswered", "declined"], RequestStates(replied));
      Assert.Equal((string)requests[1]!["id"]!, (string)replied["messages"]![1]!["requestId"]!);
   }

   [Fact]
   public async Task Resend_with_no_session_bound_is_refused_with_the_reason_and_stores_nothing()
   {
      var bound = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(bound);
      var asked = await AskAsync(id);
      await _harness.DropSessionAsync(bound);
      var before = await ThreadsAsync(id);

      using var response = await SendAsync(await ResendRequestAsync(id, QuestionIdOf(asked)));

      var error = await ErrorAsync(response, HttpStatusCode.Conflict, "noSessionAttached");
      Assert.Equal($"tourId: this tour is attached to no Claude Code session, so no session can receive the question. Ask Claude in a Claude Code session that runs lotsen's channel to attach tour {id}, then try again.", (string)error["reason"]!);
      Assert.True(JsonNode.DeepEquals(before, await ThreadsAsync(id)), "The refused re-send changed the thread.");
   }

   [Fact]
   public async Task Resend_of_an_answered_question_is_refused()
   {
      await using var bound = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(bound);
      await bound.ProbeAsync();
      var asked = await AskAsync(id);
      await CallAsync(ReplyRequest(bound, id, QuestionIdOf(asked)), Done);
      var before = await ThreadsAsync(id);

      using var response = await SendAsync(await ResendRequestAsync(id, QuestionIdOf(asked)));

      var error = await ErrorAsync(response, HttpStatusCode.Conflict, "questionAnswered");
      Assert.Equal("questionId", (string)error["field"]!);
      Assert.Contains("already has its answer", (string)error["reason"]!, StringComparison.Ordinal);
      Assert.True(JsonNode.DeepEquals(before, await ThreadsAsync(id)), "The refused re-send changed the thread.");
      _ = await bound.Stream.NextAsync(Timeout);
      Assert.Null(await NextOrNullAsync(bound.Stream));
   }

   /// <summary>The tour file names another session in the pending request, as a sweep that could not store the tour leaves it.</summary>
   [Fact]
   public async Task Resend_of_a_question_left_pending_for_a_session_no_longer_bound_cancels_that_request_and_sends_a_new_one()
   {
      await using var bound = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(bound);
      await bound.ProbeAsync();
      var asked = await AskAsync(id);
      _ = await bound.Stream.NextAsync(Timeout);
      await NameAnotherSessionAsync(id, bound, "\"pending\"");
      await using var stream = await EventStream.OpenAsync(_harness);

      var thread = await CallAsync(await ResendRequestAsync(id, QuestionIdOf(asked)));
      var updated = await stream.NextAsync(Timeout);
      var sent = await bound.Stream.NextAsync(Timeout);
      var replied = await CallAsync(ReplyRequest(bound, id, QuestionIdOf(asked)), """{ "resolution": "declined" }""");

      var requests = thread["messages"]![0]!["requests"]!.AsArray();
      Assert.Equal(2, requests.Count);
      Assert.Equal(("canceled", "another-session"), ((string)requests[0]!["state"]!, (string)requests[0]!["sessionId"]!));
      Assert.Equal(("pending", bound.Id), ((string)requests[1]!["state"]!, (string)requests[1]!["sessionId"]!));
      Assert.True(JsonNode.DeepEquals(thread, EventStream.WithoutStatus(updated.Data["thread"]!)), $"The event's thread differs from the re-send's result: {updated.Data}");
      Assert.Equal((SessionEvents.Question, (string)requests[1]!["id"]!), (sent.Type, (string)sent.Data["requestId"]!));
      Assert.Equal(["canceled", "declined"], RequestStates(replied));
   }

   [Fact]
   public async Task Resend_of_a_question_left_pending_for_another_session_with_no_session_bound_is_refused_and_stores_nothing()
   {
      var bound = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(bound);
      var asked = await AskAsync(id);
      await using (var stream = await EventStream.OpenAsync(_harness))
      {
         await DropAsync(bound);
         Assert.Equal(ServerEvents.TourBindingChanged.Name, (await stream.NextBesidesControlsAsync(Timeout)).Type);
         Assert.Equal(ServerEvents.ThreadUpdated.Name, (await stream.NextBesidesControlsAsync(Timeout)).Type);
      }

      await NameAnotherSessionAsync(id, bound, "\"unanswered\"");
      var before = await ThreadsAsync(id);

      using var response = await SendAsync(await ResendRequestAsync(id, QuestionIdOf(asked)));

      await ErrorAsync(response, HttpStatusCode.Conflict, "noSessionAttached");
      Assert.Equal(["pending"], RequestStates(before[0]!));
      Assert.True(JsonNode.DeepEquals(before, await ThreadsAsync(id)), "The refused re-send changed the thread.");
   }

   [Fact]
   public async Task Resend_of_an_unknown_question_is_not_found()
   {
      await using var bound = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(bound);

      using var response = await SendAsync(await ResendRequestAsync(id, "nosuchquestion"));

      await ErrorAsync(response, HttpStatusCode.NotFound, "questionNotFound");
   }

   /// <summary>The test restores the tour file as it was before the stop, as after a server that ends without releasing its bindings, so only the start can mark the request.</summary>
   [Fact]
   public async Task Request_left_pending_by_a_server_that_stopped_is_unanswered_after_the_next_start()
   {
      var bound = await _harness.AgentAsync();
      var id = await CreateAsync(bound);
      await AskAsync(id);
      var file = Path.Combine(_harness.DataDirectory.ToursPath, $"{id}.json");
      var pending = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
      await _harness.StopServerAsync();
      await File.WriteAllTextAsync(file, pending, TestContext.Current.CancellationToken);

      await using var restarted = await ServerHarness.StartAsync(dataDirectory: _harness.DataDirectory);
      var read = await restarted.CallAsync(On("read_state", id));

      Assert.Contains("\"pending\"", pending, StringComparison.Ordinal);
      Assert.Equal(["unanswered"], RequestStates(Assert.Single(read["threads"]!.AsArray())!));
   }

   private TourWriter Writer => _harness.Server.Services.GetRequiredService<TourWriter>();

   private static string QuestionIdOf(JsonNode thread) => (string)thread["messages"]![0]!["id"]!;

   private static string RequestIdOf(JsonNode thread) => (string)thread["messages"]![0]!["requests"]![0]!["id"]!;

   private static string[] RequestStates(JsonNode thread) => [.. thread["messages"]![0]!["requests"]!.AsArray().Select(request => (string)request!["state"]!)];

   private PreparedCall ReplyRequest(AgentSessionConnection session, string tourId, string questionId) => session.Request(_harness, On("reply", tourId, questionId: questionId));

   private async Task<PreparedCall> ResendRequestAsync(string tourId, string questionId) =>
      new(On("resend_question", tourId, questionId: questionId), await _harness.UiCredentialAsync());

   private async Task<JsonNode> AskAsync(string tourId, string body = TourQuestion) =>
      await CallAsync(new PreparedCall(On("ask_question", tourId), await _harness.UiCredentialAsync()), body);

   private async Task TakeOverAsync(AgentSessionConnection session, string tourId) =>
      await CallAsync(session.Request(_harness, On("attach_tour", tourId)), """{ "takeOver": true }""");

   private Task DropAsync(AgentSessionConnection session) => _harness.DropSessionAsync(session);

   // Leaves the question's only request pending for a session that does not exist, as a sweep that could not store the tour leaves it. The state gives the request's state in the file now.
   private async Task NameAnotherSessionAsync(string tourId, AgentSessionConnection session, string state)
   {
      var file = Path.Combine(_harness.DataDirectory.ToursPath, $"{tourId}.json");
      var stored = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
      Assert.Contains(session.Id, stored, StringComparison.Ordinal);
      Assert.Contains(state, stored, StringComparison.Ordinal);
      var edited = stored.Replace(session.Id, "another-session", StringComparison.Ordinal).Replace(state, "\"pending\"", StringComparison.Ordinal);
      await File.WriteAllTextAsync(file, edited, TestContext.Current.CancellationToken);
   }

   private async Task<JsonArray> ThreadsAsync(string tourId) => (await _harness.CallAsync(On("read_state", tourId)))["threads"]!.AsArray();

   private async Task<string> CreateAsync(AgentSessionConnection session) => (string)(await CallAsync(session.Request(_harness, "create_tour"), CreateBody))["id"]!;

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

   // Its timeout cancels the read, which can close the stream's connection and so end an agent session, so a test calls it last.
   private static async Task<(string Type, JsonNode Data)?> NextOrNullAsync(EventStream stream)
   {
      try
      {
         return await stream.NextAsync(TimeSpan.FromMilliseconds(300));
      }
      catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
      {
         return null;
      }
   }
}
