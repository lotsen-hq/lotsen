using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Lotsen.Operations;
using Lotsen.Sessions;
using Microsoft.Extensions.DependencyInjection;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>Covers that a question needs a bound session, and how the question reaches that session as a session event.</summary>
[Trait("Category", "Roles")]
public sealed class QuestionRoleTests : IAsyncLifetime
{
   private const string CreateBody = $$"""{ "prUrl": "{{TestPlatform.PrUrl}}", "familiarity": "some" }""";

   private const string TourQuestion = """{ "text": "What does this tour cover?", "anchor": { "kind": "tour" } }""";

   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

   private ServerHarness _harness = null!;

   public async ValueTask InitializeAsync() => _harness = await ServerHarness.StartAsync();

   public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

   [Theory]
   [InlineData("never")]
   [InlineData("dropped")]
   public async Task Ask_to_a_tour_with_no_bound_session_is_refused_with_the_reason_and_stores_no_thread(string binding)
   {
      string id;
      if (binding == "never")
      {
         id = (string)_harness.CreateUnboundTour(CreateBody)["id"]!;
      }
      else
      {
         var session = await AgentSessionConnection.OpenAsync(_harness);
         id = await CreateAsync(session);

         // The settlement publishes the binding's release, so it ends before the stream opens.
         await _harness.DropSessionAsync(session);
      }

      await using var stream = await EventStream.OpenAsync(_harness);

      using var response = await AskAsync(id, TourQuestion);
      var marker = _harness.CreateUnboundTour(CreateBody);

      var error = await ErrorAsync(response, HttpStatusCode.Conflict, "noSessionAttached");
      Assert.Equal("tourId", (string)error["field"]!);
      Assert.Equal($"tourId: this tour is attached to no Claude Code session, so no session can receive the question. Ask Claude in a Claude Code session that runs lotsen's channel to attach tour {id}, then try again.", (string)error["reason"]!);
      Assert.Empty(await ThreadsAsync(id));
      await AssertNextIsCreationOfAsync(stream, marker);
   }

   [Fact]
   public async Task Ask_from_the_ui_stores_a_thread_and_sends_the_bound_session_the_words_and_ids_only()
   {
      await using var bound = await AgentSessionConnection.OpenAsync(_harness);
      await using var other = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(bound);
      var nonce = await bound.ProbeAsync();
      var stopId = await SetPlanAsync(bound, id);
      await using var stream = await EventStream.OpenAsync(_harness);

      var thread = await CallAsync(await AskRequestAsync(id), $$"""{ "text": "Why is this stop here?", "anchor": { "kind": "stop", "stopId": "{{stopId}}" }, "stopId": "{{stopId}}" }""");
      var sent = await bound.Stream.NextAsync(Timeout);
      var created = await stream.NextAsync(Timeout);
      var read = await _harness.CallAsync(On("read_state", id));

      var question = Assert.Single(thread["messages"]!.AsArray())!;
      var request = Assert.Single(question["requests"]!.AsArray())!;
      Assert.Equal("question", (string)question["kind"]!);
      Assert.Equal("Why is this stop here?", (string)question["text"]!);
      Assert.Equal(stopId, (string)question["stopId"]!);
      Assert.Equal(("pending", bound.Id), ((string)request["state"]!, (string)request["sessionId"]!));
      Assert.True(JsonNode.DeepEquals(new JsonObject { ["kind"] = "stop", ["stopId"] = stopId }, thread["anchor"]), $"anchor: {thread["anchor"]}");
      Assert.True(JsonNode.DeepEquals(thread, Assert.Single(read["threads"]!.AsArray())), $"The stored thread differs from the ask's result: {read["threads"]}");
      Assert.Equal(SessionEvents.Question, sent.Type);
      var payload = sent.Data.AsObject();
      Assert.Equal(nonce, (string?)payload["nonce"]);
      payload.Remove("nonce");
      Assert.True(
         JsonNode.DeepEquals(
            new JsonObject
            {
               ["tourId"] = id,
               ["threadId"] = (string)thread["id"]!,
               ["questionId"] = (string)question["id"]!,
               ["requestId"] = (string)request["id"]!,
               ["stopId"] = stopId,
               ["text"] = "Why is this stop here?",
            },
            payload),
         $"The session event holds more than the words, the IDs and the nonce: {payload}");
      Assert.Equal(ServerEvents.ThreadCreated.Name, created.Type);
      Assert.Equal((id, (string)read["updatedAt"]!), ((string)created.Data["tourId"]!, (string)created.Data["updatedAt"]!));
      Assert.True(JsonNode.DeepEquals(thread, EventStream.WithoutStatus(created.Data["thread"]!)), $"The event's thread differs from the ask's result: {created.Data}");
      Assert.Null(await NextOrNullAsync(other.Stream));
   }

   [Fact]
   public async Task Several_questions_are_each_their_own_event_in_the_order_they_were_asked()
   {
      await using var bound = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(bound);
      await bound.ProbeAsync();

      var first = await CallAsync(await AskRequestAsync(id), """{ "text": "First?", "anchor": { "kind": "tour" } }""");
      var second = await CallAsync(await AskRequestAsync(id), """{ "text": "Second?", "anchor": { "kind": "tour" } }""");
      var sentFirst = await bound.Stream.NextAsync(Timeout);
      var sentSecond = await bound.Stream.NextAsync(Timeout);

      Assert.Equal(("First?", QuestionIdOf(first)), ((string)sentFirst.Data["text"]!, (string)sentFirst.Data["questionId"]!));
      Assert.Equal(("Second?", QuestionIdOf(second)), ((string)sentSecond.Data["text"]!, (string)sentSecond.Data["questionId"]!));
      Assert.NotEqual((string)first["id"]!, (string)second["id"]!);
      Assert.NotEqual((string)sentFirst.Data["requestId"]!, (string)sentSecond.Data["requestId"]!);
      Assert.False(sentFirst.Data.AsObject().ContainsKey("stopId"));
      Assert.Equal(2, (await ThreadsAsync(id)).Count);
   }

   /// <summary>The ask delivers its question in the hold that stores it, so the old session receives the question before the take-over's notice, and the new session gets neither.</summary>
   [Fact]
   public async Task Question_stored_before_a_take_over_reaches_the_old_session_before_its_notice()
   {
      await using var first = await AgentSessionConnection.OpenAsync(_harness);
      await using var second = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(first);
      await first.ProbeAsync();

      var thread = await CallAsync(await AskRequestAsync(id), TourQuestion);
      await CallAsync(AttachRequest(second, id), """{ "takeOver": true }""");
      await second.ProbeAsync();
      var sent = await first.Stream.NextAsync(Timeout);
      var takenOver = await first.Stream.NextAsync(Timeout);

      Assert.Equal(first.Id, SessionOfRequest(thread));
      Assert.Equal((SessionEvents.Question, QuestionIdOf(thread)), (sent.Type, (string)sent.Data["questionId"]!));
      Assert.Equal(SessionEvents.TourTakenOver, takenOver.Type);
      Assert.Equal(second.Id, await _harness.BoundSessionOfAsync(id));
      Assert.Null(await NextOrNullAsync(second.Stream));
   }

   /// <summary>The drop's settlement marks the stored question's request unanswered, and the ask's own output still shows it pending.</summary>
   [Fact]
   public async Task Drop_of_the_bound_session_after_the_ask_keeps_the_thread()
   {
      var bound = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(bound);

      var thread = await CallAsync(await AskRequestAsync(id), TourQuestion);
      await _harness.DropSessionAsync(bound);
      var read = await _harness.CallAsync(On("read_state", id));

      var stored = Assert.Single(read["threads"]!.AsArray())!;
      Assert.Equal(bound.Id, SessionOfRequest(thread));
      Assert.Equal(("pending", "unanswered"), ((string)thread["messages"]![0]!["requests"]![0]!["state"]!, (string)stored["messages"]![0]!["requests"]![0]!["state"]!));
      Assert.Equal((string)thread["id"]!, (string)stored["id"]!);
      Assert.Null(await _harness.BindingOfAsync(id));
   }

   /// <summary>The binding is read again when the question is stored, so a take-over while the anchor is checked sends the question to the new session.</summary>
   [Fact]
   public async Task Take_over_while_the_question_is_checked_sends_it_to_the_new_session()
   {
      await using var first = await AgentSessionConnection.OpenAsync(_harness);
      await using var second = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(first);
      await first.ProbeAsync();
      Writer.Hook = WriterHooks.AfterGit(async _ => await CallAsync(AttachRequest(second, id), """{ "takeOver": true }"""), "ask_question");

      var thread = await CallAsync(await AskRequestAsync(id), TourQuestion);
      await second.ProbeAsync();
      var sent = await second.Stream.NextAsync(Timeout);
      var takenOver = await first.Stream.NextAsync(Timeout);

      Assert.Equal(second.Id, SessionOfRequest(thread));
      Assert.Equal((SessionEvents.Question, QuestionIdOf(thread)), (sent.Type, (string)sent.Data["questionId"]!));
      Assert.Equal(SessionEvents.TourTakenOver, takenOver.Type);
      Assert.Null(await NextOrNullAsync(first.Stream));
   }

   [Fact]
   public async Task Drop_of_the_bound_session_while_the_question_is_checked_refuses_the_ask_and_stores_no_thread()
   {
      var bound = await AgentSessionConnection.OpenAsync(_harness);
      var serverSession = _harness.Sessions.Find(bound.Id)!;
      var id = await CreateAsync(bound);
      Writer.Hook = WriterHooks.AfterGit(async _ =>
      {
         await bound.DisposeAsync();
         await Poll.EndedAsync(serverSession, Timeout, "The session did not end after its connection dropped.");
      }, "ask_question");

      using var response = await AskAsync(id, TourQuestion);

      await ErrorAsync(response, HttpStatusCode.Conflict, "noSessionAttached");
      Assert.Empty(await ThreadsAsync(id));
   }

   private TourWriter Writer => _harness.Server.Services.GetRequiredService<TourWriter>();

   private static string QuestionIdOf(JsonNode thread) => (string)thread["messages"]![0]!["id"]!;

   private static string SessionOfRequest(JsonNode thread) => (string)thread["messages"]![0]!["requests"]![0]!["sessionId"]!;

   private async Task<PreparedCall> AskRequestAsync(string tourId) => new(On("ask_question", tourId), await _harness.UiCredentialAsync());

   private async Task<HttpResponseMessage> AskAsync(string tourId, string body) => await SendAsync(await AskRequestAsync(tourId), body);

   private PreparedCall AttachRequest(AgentSessionConnection session, string tourId) => session.Request(_harness, On("attach_tour", tourId));

   private async Task<JsonArray> ThreadsAsync(string tourId) => (await _harness.CallAsync(On("read_state", tourId)))["threads"]!.AsArray();

   private async Task<string> CreateAsync(AgentSessionConnection session) => (string)(await CallAsync(session.Request(_harness, "create_tour"), CreateBody))["id"]!;

   private async Task<string> SetPlanAsync(AgentSessionConnection session, string tourId) => (string)(await CallAsync(
      session.Request(_harness, On("set_plan", tourId)),
      """{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "title": "The secret title", "kind": "overview", "chapter": "a" }] }"""))["plan"]!["stops"]![0]!["id"]!;

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

   // The marker tour is created after the action under test, so an event that the action published would arrive before the marker's.
   private static async Task AssertNextIsCreationOfAsync(EventStream stream, JsonNode marker)
   {
      var next = await stream.NextAsync(Timeout);
      Assert.Equal(("tourCreated", (string)marker["id"]!), (next.Type, (string?)next.Data["id"]));
   }
}
