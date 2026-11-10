using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Lotsen.Operations;
using Lotsen.Sessions;
using Microsoft.Extensions.DependencyInjection;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>
/// Covers a question that waits for its answer: the UI cancels it or sends it again whatever the attachment level, the first answer to the question wins, and a session answers the requests that
/// it was sent even after it lost the tour.
/// </summary>
[Trait("Category", "Roles")]
public sealed class WaitingQuestionTests : IAsyncLifetime
{
   private const string CreateBody = $$"""{ "prUrl": "{{TestPlatform.PrUrl}}", "familiarity": "some" }""";

   private const string TourQuestion = """{ "text": "What does this tour cover?", "anchor": { "kind": "tour" } }""";

   private const string Done = """{ "resolution": "done", "text": "It covers the change." }""";

   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

   private ServerHarness _harness = null!;

   public async ValueTask InitializeAsync() => _harness = await ServerHarness.StartAsync();

   public async ValueTask DisposeAsync()
   {
      Writer.Hook = null;
      await _harness.DisposeAsync();
   }

   [Theory]
   [InlineData("unconfirmed")]
   [InlineData("confirmed")]
   public async Task Cancel_of_a_pending_request_stops_it_waiting_and_sends_the_session_nothing(string level)
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(session);
      var nonce = await session.ProbeAsync();
      if (level == "confirmed")
      {
         await CallAsync(session.Request(_harness, "ack"), $$"""{ "nonce": "{{nonce}}" }""");
      }

      var asked = await AskAsync(id);
      Assert.Equal(SessionEvents.Question, (await session.Stream.NextAsync(Timeout)).Type);
      await using var stream = await EventStream.OpenAsync(_harness);

      var canceled = await CallAsync(await UiRequestAsync(On("cancel_question", id, questionId: QuestionIdOf(asked))));
      var updated = await stream.NextAsync(Timeout);
      var marker = await AskAsync(id);
      var next = await session.Stream.NextAsync(Timeout);

      Assert.Equal(["canceled"], RequestStates(canceled));
      Assert.Single(canceled["messages"]!.AsArray());
      Assert.Equal(ServerEvents.ThreadUpdated.Name, updated.Type);
      Assert.True(JsonNode.DeepEquals(canceled, EventStream.WithoutStatus(updated.Data["thread"]!)), $"The event's thread differs from the cancel's result: {updated.Data}");
      Assert.True(JsonNode.DeepEquals(canceled, (await ThreadsAsync(id))[0]), "The stored thread differs from the cancel's result.");
      Assert.Equal((SessionEvents.Question, QuestionIdOf(marker)), (next.Type, (string)next.Data["questionId"]!));
   }

   [Fact]
   public async Task Cancel_of_a_question_with_no_session_attached_works()
   {
      var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(session);
      var asked = await AskAsync(id);
      await using (var stream = await EventStream.OpenAsync(_harness))
      {
         await DropAsync(session);
         Assert.Equal(ServerEvents.TourBindingChanged.Name, (await stream.NextBesidesControlsAsync(Timeout)).Type);
         Assert.Equal(ServerEvents.ThreadUpdated.Name, (await stream.NextBesidesControlsAsync(Timeout)).Type);
      }

      await NamePendingForAnotherSessionAsync(id, session);

      var canceled = await CallAsync(await UiRequestAsync(On("cancel_question", id, questionId: QuestionIdOf(asked))));

      Assert.Equal(["canceled"], RequestStates(canceled));
   }

   [Fact]
   public async Task Resend_of_a_canceled_question_sends_a_new_pending_request()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(session);
      await session.ProbeAsync();
      var asked = await AskAsync(id);
      await CallAsync(await UiRequestAsync(On("cancel_question", id, questionId: QuestionIdOf(asked))));

      var resent = await CallAsync(await UiRequestAsync(On("resend_question", id, questionId: QuestionIdOf(asked))));
      _ = await session.Stream.NextAsync(Timeout);
      var sent = await session.Stream.NextAsync(Timeout);

      Assert.Equal(["canceled", "pending"], RequestStates(resent));
      Assert.Equal((SessionEvents.Question, RequestIdOf(resent, 1)), (sent.Type, (string)sent.Data["requestId"]!));
   }

   [Fact]
   public async Task LateReply_from_a_session_that_lost_the_tour_to_a_take_over_is_accepted()
   {
      await using var first = await AgentSessionConnection.OpenAsync(_harness);
      await using var second = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(first);
      var asked = await AskAsync(id);
      await TakeOverAsync(second, id);
      await using var stream = await EventStream.OpenAsync(_harness);

      var replied = await CallAsync(ReplyRequest(first, id, QuestionIdOf(asked)), Done);
      var updated = await stream.NextAsync(Timeout);

      Assert.Equal(["done"], RequestStates(replied));
      Assert.Equal(first.Id, (string)replied["messages"]![1]!["sessionId"]!);
      Assert.Equal(ServerEvents.ThreadUpdated.Name, updated.Type);
      Assert.Equal(second.Id, (string)(await _harness.BindingOfAsync(id))!["sessionId"]!);
   }

   /// <summary>Both replies pass their checks before either stores, so only the store's check under the lock keeps the second answer out.</summary>
   [Fact]
   public async Task LateReply_racing_another_reply_to_the_question_is_refused_once_the_other_is_stored()
   {
      await using var first = await AgentSessionConnection.OpenAsync(_harness);
      await using var second = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(first);
      var asked = await AskAsync(id);
      await TakeOverAsync(second, id);
      await CallAsync(await UiRequestAsync(On("resend_question", id, questionId: QuestionIdOf(asked))));
      JsonNode? overtaking = null;
      Writer.Hook = WriterHooks.AfterGit(async _ =>
      {
         Writer.Hook = null;
         overtaking = await CallAsync(ReplyRequest(second, id, QuestionIdOf(asked)), """{ "resolution": "declined" }""");
      }, "reply");

      using var late = await SendAsync(ReplyRequest(first, id, QuestionIdOf(asked)), Done);

      await ErrorAsync(late, HttpStatusCode.Conflict, "questionAnswered");
      Assert.NotNull(overtaking);
      Assert.Equal(["unanswered", "declined"], RequestStates(overtaking));
      Assert.True(JsonNode.DeepEquals(overtaking, (await ThreadsAsync(id))[0]), "The refused reply changed the thread.");
   }

   [Fact]
   public async Task LateReply_while_its_session_drops_is_stored_after_the_sweep()
   {
      var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(session);
      var asked = await AskAsync(id);
      Writer.Hook = WriterHooks.AfterGit(async _ => await DropAsync(session), "reply");

      var replied = await CallAsync(ReplyRequest(session, id, QuestionIdOf(asked)), Done);

      Assert.Equal(["done"], RequestStates(replied));
      Assert.Equal(["done"], RequestStates((await ThreadsAsync(id))[0]!));
   }

   [Fact]
   public async Task LateReply_while_a_take_over_moves_the_tour_is_stored()
   {
      await using var first = await AgentSessionConnection.OpenAsync(_harness);
      await using var second = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(first);
      var asked = await AskAsync(id);
      Writer.Hook = WriterHooks.AfterGit(async _ => await TakeOverAsync(second, id), "reply");

      var replied = await CallAsync(ReplyRequest(first, id, QuestionIdOf(asked)), Done);

      Assert.Equal(["done"], RequestStates(replied));
      Assert.Equal(second.Id, (string)(await _harness.BindingOfAsync(id))!["sessionId"]!);
   }

   private TourWriter Writer => _harness.Server.Services.GetRequiredService<TourWriter>();

   private static string QuestionIdOf(JsonNode thread) => (string)thread["messages"]![0]!["id"]!;

   private static string RequestIdOf(JsonNode thread, int index) => (string)thread["messages"]![0]!["requests"]![index]!["id"]!;

   private static string[] RequestStates(JsonNode thread) => [.. thread["messages"]![0]!["requests"]!.AsArray().Select(request => (string)request!["state"]!)];

   private PreparedCall ReplyRequest(AgentSessionConnection session, string tourId, string questionId) =>
      session.Request(_harness, On("reply", tourId, questionId: questionId));

   private async Task<PreparedCall> UiRequestAsync(OperationCall call) => new(call, await _harness.UiCredentialAsync());

   private async Task<JsonNode> AskAsync(string tourId) => await CallAsync(await UiRequestAsync(On("ask_question", tourId)), TourQuestion);

   private async Task TakeOverAsync(AgentSessionConnection session, string tourId) =>
      await CallAsync(session.Request(_harness, On("attach_tour", tourId)), """{ "takeOver": true }""");

   private async Task<string> CreateAsync(AgentSessionConnection session) => (string)(await CallAsync(session.Request(_harness, "create_tour"), CreateBody))["id"]!;

   private async Task<JsonArray> ThreadsAsync(string tourId) => (await _harness.CallAsync(On("read_state", tourId)))["threads"]!.AsArray();

   private Task DropAsync(AgentSessionConnection session) => _harness.DropSessionAsync(session);

   // Leaves the question's unanswered request pending for a session that does not exist, as a sweep that could not store the tour leaves it.
   private async Task NamePendingForAnotherSessionAsync(string tourId, AgentSessionConnection session)
   {
      var file = Path.Combine(_harness.DataDirectory.ToursPath, $"{tourId}.json");
      var stored = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
      Assert.Contains(session.Id, stored, StringComparison.Ordinal);
      Assert.Contains("\"unanswered\"", stored, StringComparison.Ordinal);
      var edited = stored.Replace(session.Id, "another-session", StringComparison.Ordinal).Replace("\"unanswered\"", "\"pending\"", StringComparison.Ordinal);
      await File.WriteAllTextAsync(file, edited, TestContext.Current.CancellationToken);
   }

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
