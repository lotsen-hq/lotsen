using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Operations;
using Lotsen.Sessions;
using Lotsen.Tours;
using Microsoft.Extensions.DependencyInjection;
using static Lotsen.Tests.AnswerCalls;
using static Lotsen.Tests.OperationCall;
using static Lotsen.Tests.ThreadCalls;

namespace Lotsen.Tests;

/// <summary>
/// Covers <c>send_answers</c> and <c>fetch_answers</c>: the UI sends every answer to send as one batch to the bound session, which gets one channel event with IDs only, and the first fetch by that
/// session marks the batch's answers received.
/// </summary>
[Trait("Category", "Checks")]
public sealed class AnswerBatchTests : IClassFixture<StopTourFixture>, IAsyncLifetime
{
   private const string FirstAnswer = "Keep it, sorting holds.";
   private const string SecondAnswer = "Line one is right.";

   private readonly PinnedTourFixture _tour;

   public AnswerBatchTests(StopTourFixture fixture)
   {
      _tour = fixture.Tour;
   }

   private ServerHarness Harness => _tour.Harness;

   private AnswerCalls Answers => new(Harness);

   private ThreadCalls Calls => new(Harness);

   private TourWriter Writer => Harness.Server.Services.GetRequiredService<TourWriter>();

   public ValueTask InitializeAsync() => ValueTask.CompletedTask;

   public ValueTask DisposeAsync()
   {
      Writer.Hook = null;
      return ValueTask.CompletedTask;
   }

   [Fact]
   public async Task Send_moves_every_draft_to_sent_in_one_batch_and_the_bound_session_gets_one_event_without_the_answers_text()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await Calls.AckAsync(session, await session.ProbeAsync());
      await Answers.AnswerAsync(tour, 0, FirstAnswer);
      await Answers.AnswerAsync(tour, 1, SecondAnswer);
      await using var stream = await EventStream.OpenAsync(Harness);

      var sent = await Answers.SendAsync(tour.Id);
      var update = await stream.NextAsync(EventWait);
      var batchEvent = await session.Stream.NextAsync(EventWait);
      var marker = await Calls.AskAsync(tour.Id, "Marker.");
      var next = await session.Stream.NextAsync(EventWait);

      var batchId = BatchIdOf(sent);
      Assert.Equal(tour.Id, (string)sent["tourId"]!);
      Assert.Equal([(tour.CheckIds[0], FirstAnswer), (tour.CheckIds[1], SecondAnswer)], sent["answers"]!.AsArray().Select(answer => ((string)answer!["checkId"]!, (string)answer["text"]!)));
      Assert.All(sent["answers"]!.AsArray(), answer =>
      {
         Assert.Equal("sent", (string)answer!["state"]!);
         Assert.Equal((batchId, session.Id), ((string)answer["batch"]!["id"]!, (string)answer["batch"]!["sessionId"]!));
         Assert.False(answer["batch"]!.AsObject().ContainsKey("receivedAt"), $"A sent answer is received before any fetch: {answer}");
      });
      Assert.True(JsonNode.DeepEquals(sent["answers"], await Answers.AnswersAsync(tour.Id)), "The stored answers differ from the batch's response.");
      Assert.Equal(ServerEvents.AnswerBatchUpdated.Name, update.Type);
      Assert.True(JsonNode.DeepEquals(sent, EventStream.WithoutRevision(update.Data)), $"The event differs from the response: {update.Data}");
      Assert.Equal(SessionEvents.Answers, batchEvent.Type);
      Assert.True(JsonNode.DeepEquals(JsonNode.Parse($$"""{ "tourId": "{{tour.Id}}", "batchId": "{{batchId}}" }"""), batchEvent.Data), $"The session event holds more than the IDs: {batchEvent.Data}");
      Assert.Equal((SessionEvents.Question, QuestionIdOf(marker)), (next.Type, (string)next.Data["questionId"]!));
   }

   [Fact]
   public async Task Batch_to_an_unconfirmed_session_carries_the_sessions_nonce()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      var nonce = await session.ProbeAsync();
      await Answers.AnswerAsync(tour, 0, FirstAnswer);

      await Answers.SendAsync(tour.Id);
      var batchEvent = await session.Stream.NextAsync(EventWait);

      Assert.Equal((SessionEvents.Answers, nonce), (batchEvent.Type, (string)batchEvent.Data["nonce"]!));
   }

   /// <summary>The answer to the dropped check stays stored as a draft, which the UI does not show, so the batch holds only what the unsent count counts.</summary>
   [Fact]
   public async Task Send_leaves_out_a_draft_to_a_check_that_a_rewrite_dropped()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await Answers.AnswerAsync(tour, 0, FirstAnswer);
      await Answers.AnswerAsync(tour, 1, SecondAnswer);
      await Answers.PutStopAsync(session, tour.Id, tour.StopId, $$"""[{ "id": "{{tour.CheckIds[0]}}", "question": "Keep the digit?" }]""");

      var sent = await Answers.SendAsync(tour.Id);

      Assert.Equal([tour.CheckIds[0]], sent["answers"]!.AsArray().Select(answer => (string)answer!["checkId"]!));
      Assert.Equal(
         [(tour.CheckIds[0], "sent"), (tour.CheckIds[1], "draft")],
         (await Answers.AnswersAsync(tour.Id)).Select(answer => ((string)answer!["checkId"]!, (string)answer["state"]!)));
   }

   [Fact]
   public async Task Fetch_returns_the_batches_answers_with_their_checks_and_types_and_marks_them_received_once()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await Answers.AnswerAsync(tour, 0, FirstAnswer);
      await Answers.SetTypeAsync(tour, 0, "concern");
      await Answers.AnswerAsync(tour, 1, SecondAnswer);
      var batchId = BatchIdOf(await Answers.SendAsync(tour.Id));
      await using var stream = await EventStream.OpenAsync(Harness);

      var fetched = await Answers.FetchAsync(session, tour.Id, batchId);
      var update = await stream.NextAsync(EventWait);
      var refetched = await Answers.FetchAsync(session, tour.Id, batchId);
      await Answers.AnswerAsync(tour, 1, "Marker.");
      var next = await stream.NextAsync(EventWait);

      Assert.Equal((tour.Id, batchId), ((string)fetched["tourId"]!, (string)fetched["batchId"]!));
      Assert.Equal(
         [(tour.StopId, tour.CheckIds[0], "Keep the digit?", FirstAnswer, "concern"), (tour.StopId, tour.CheckIds[1], "Is line one still right?", SecondAnswer, "decision")],
         fetched["answers"]!.AsArray().Select(answer => ((string)answer!["stopId"]!, (string)answer["checkId"]!, (string)answer["question"]!, (string)answer["text"]!, (string)answer["type"]!)));
      var receivedAt = (DateTimeOffset)fetched["receivedAt"]!;
      Assert.True(receivedAt >= (DateTimeOffset)fetched["sentAt"]!, $"The batch was received before it was sent: {fetched}");
      Assert.Equal(ServerEvents.AnswerBatchUpdated.Name, update.Type);
      Assert.All(update.Data["answers"]!.AsArray(), answer => Assert.Equal(("received", receivedAt), ((string)answer!["state"]!, (DateTimeOffset)answer["batch"]!["receivedAt"]!)));
      Assert.Equal(receivedAt, (DateTimeOffset)refetched["receivedAt"]!);
      Assert.Equal(ServerEvents.AnswerUpdated.Name, next.Type);
      var stored = await Answers.AnswersAsync(tour.Id);
      Assert.Equal(("received", receivedAt), ((string)stored[0]!["state"]!, (DateTimeOffset)stored[0]!["batch"]!["receivedAt"]!));
   }

   /// <summary>Only the session that the batch went to marks it received, as only that session marks a question's request, and every other session reads the batch as it is.</summary>
   [Fact]
   public async Task Fetch_by_another_session_reads_the_batch_and_marks_nothing()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      await using var other = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await Answers.AnswerAsync(tour, 0, FirstAnswer);
      var batchId = BatchIdOf(await Answers.SendAsync(tour.Id));

      var fetched = await Calls.CallAsync(Calls.Agent(other, On("fetch_answers", tour.Id, batchId: batchId)));

      Assert.Equal([FirstAnswer], fetched["answers"]!.AsArray().Select(answer => (string)answer!["text"]!));
      Assert.False(fetched.AsObject().ContainsKey("receivedAt"), $"Another session's fetch marked the batch received: {fetched}");
      Assert.Equal(["sent"], (await Answers.AnswersAsync(tour.Id)).Select(answer => (string)answer!["state"]!));
   }

   [Fact]
   public async Task Fetch_of_a_batch_that_the_tour_does_not_have_is_refused()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);

      using var response = await Calls.SendAsync(Calls.Agent(session, On("fetch_answers", tour.Id, batchId: "nosuchbt")));

      var error = await ErrorAsync(response, HttpStatusCode.NotFound, "batchNotFound");
      Assert.Equal("batchId", (string)error["field"]!);
   }

   [Fact]
   public async Task Send_with_no_session_attached_is_refused_and_changes_nothing()
   {
      var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await Answers.AnswerAsync(tour, 0, FirstAnswer);
      await Harness.DropSessionAsync(session);
      var before = await Answers.AnswersAsync(tour.Id);

      using var response = await Calls.SendAsync(await Calls.UiAsync(On("send_answers", tour.Id)));

      var error = await ErrorAsync(response, HttpStatusCode.Conflict, "noSessionAttached");
      Assert.Contains("answers", (string)error["reason"]!, StringComparison.Ordinal);
      Assert.True(JsonNode.DeepEquals(before, await Answers.AnswersAsync(tour.Id)), "The refused send changed the answers.");
   }

   [Fact]
   public async Task Send_with_nothing_to_send_is_refused_and_queues_no_event()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await session.ProbeAsync();
      await Answers.AnswerAsync(tour, 0, FirstAnswer);
      await Answers.SendAsync(tour.Id);
      _ = await session.Stream.NextAsync(EventWait);
      var before = await Answers.AnswersAsync(tour.Id);

      using var response = await Calls.SendAsync(await Calls.UiAsync(On("send_answers", tour.Id)));
      var marker = await Calls.AskAsync(tour.Id, "Marker.");
      var next = await session.Stream.NextAsync(EventWait);

      await ErrorAsync(response, HttpStatusCode.Conflict, "noAnswersToSend");
      Assert.True(JsonNode.DeepEquals(before, await Answers.AnswersAsync(tour.Id)), "The refused send changed the answers.");
      Assert.Equal(QuestionIdOf(marker), (string)next.Data["questionId"]!);
   }

   [Theory]
   [InlineData("sent", "text")]
   [InlineData("sent", "type")]
   [InlineData("received", "text")]
   [InlineData("received", "type")]
   public async Task Sent_answer_that_the_reviewer_changes_becomes_a_draft_and_the_next_send_includes_it(string state, string change)
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await Answers.AnswerAsync(tour, 0, FirstAnswer);
      await Answers.AnswerAsync(tour, 1, SecondAnswer);
      var first = BatchIdOf(await Answers.SendAsync(tour.Id));
      if (state == "received")
      {
         await Answers.FetchAsync(session, tour.Id, first);
      }

      var changed = change == "text" ? await Answers.AnswerAsync(tour, 1, "Line one changed after all.") : await Answers.SetTypeAsync(tour, 1, "openQuestion");
      var shown = await Calls.CallAsync(Calls.Agent(session, On("read_state", tour.Id)));
      var refetched = await Calls.CallAsync(Calls.Agent(session, On("fetch_answers", tour.Id, batchId: first)));
      var sent = await Answers.SendAsync(tour.Id);

      Assert.Equal("draft", (string)changed["answer"]!["state"]!);
      Assert.False(changed["answer"]!.AsObject().ContainsKey("batch"), $"A draft keeps its batch: {changed}");
      Assert.Equal([tour.CheckIds[0]], shown["answers"]!.AsArray().Select(answer => (string)answer!["checkId"]!));
      Assert.Equal([tour.CheckIds[0]], refetched["answers"]!.AsArray().Select(answer => (string)answer!["checkId"]!));
      Assert.NotEqual(first, BatchIdOf(sent));
      Assert.Equal([(tour.CheckIds[1], "sent")], sent["answers"]!.AsArray().Select(answer => ((string)answer!["checkId"]!, (string)answer["state"]!)));
   }

   /// <summary>Clearing removes the answer, whatever its state, so the batch it was sent in no longer holds it.</summary>
   [Fact]
   public async Task Clearing_a_sent_answer_removes_it_from_its_batch()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await Answers.AnswerAsync(tour, 0, FirstAnswer);
      await Answers.AnswerAsync(tour, 1, SecondAnswer);
      var batchId = BatchIdOf(await Answers.SendAsync(tour.Id));

      await Calls.CallAsync(await Calls.UiAsync(On("clear_answer", tour.Id, stopId: tour.StopId, checkId: tour.CheckIds[0])));
      var fetched = await Answers.FetchAsync(session, tour.Id, batchId);

      Assert.Equal([tour.CheckIds[1]], fetched["answers"]!.AsArray().Select(answer => (string)answer!["checkId"]!));
   }

   /// <summary>The answers stay sent to the dropped session, which never fetches them, so the next send to another session holds them.</summary>
   [Fact]
   public async Task Batch_whose_session_drops_before_its_fetch_stays_sent_and_goes_out_again_to_the_next_session()
   {
      var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await Answers.AnswerAsync(tour, 0, FirstAnswer);

      var first = await Answers.SendAsync(tour.Id);
      await Calls.DropAsync(session);
      var dropped = await Answers.AnswersAsync(tour.Id);
      await using var next = await AgentSessionConnection.OpenAsync(Harness);
      await Calls.CallAsync(Calls.Agent(next, On("attach_tour", tour.Id)));
      var again = await Answers.SendAsync(tour.Id);

      Assert.Equal(("sent", session.Id), ((string)first["answers"]![0]!["state"]!, (string)first["answers"]![0]!["batch"]!["sessionId"]!));
      Assert.True(JsonNode.DeepEquals(first["answers"], dropped), "The drop changed the sent answers.");
      Assert.NotEqual(BatchIdOf(first), BatchIdOf(again));
      Assert.Equal([(tour.CheckIds[0], next.Id)], again["answers"]!.AsArray().Select(answer => ((string)answer!["checkId"]!, (string)answer["batch"]!["sessionId"]!)));
   }

   /// <summary>The batch's event reaches the session that the batch names before the take-over's notice, and the new session gets the answers in its own batch.</summary>
   [Fact]
   public async Task Batch_before_a_take_over_reaches_the_first_session_and_goes_out_again_to_the_new_one()
   {
      await using var first = await AgentSessionConnection.OpenAsync(Harness);
      await using var second = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(first);
      await first.ProbeAsync();
      await Answers.AnswerAsync(tour, 0, FirstAnswer);

      var sent = await Answers.SendAsync(tour.Id);
      await Calls.CallAsync(Calls.Agent(second, On("attach_tour", tour.Id)), """{ "takeOver": true }""");
      var batchEvent = await first.Stream.NextAsync(EventWait);
      var takenOver = await first.Stream.NextAsync(EventWait);
      await second.ProbeAsync();
      var again = await Answers.SendAsync(tour.Id);
      var secondEvent = await second.Stream.NextAsync(EventWait);

      Assert.Equal(SessionEvents.TourTakenOver, takenOver.Type);
      Assert.Equal((SessionEvents.Answers, BatchIdOf(sent)), (batchEvent.Type, (string)batchEvent.Data["batchId"]!));
      Assert.Equal((SessionEvents.Answers, BatchIdOf(again)), (secondEvent.Type, (string)secondEvent.Data["batchId"]!));
      Assert.Equal(second.Id, (string)again["answers"]![0]!["batch"]!["sessionId"]!);
   }

   /// <summary>A session that lost the tour can no longer act on the batch it got, so its fetch only reads, and the next send takes the answers to the session that holds the tour.</summary>
   [Fact]
   public async Task Session_that_lost_the_tour_reads_its_batch_without_marking_it_received()
   {
      await using var first = await AgentSessionConnection.OpenAsync(Harness);
      await using var second = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(first);
      await Answers.AnswerAsync(tour, 0, FirstAnswer);
      var batchId = BatchIdOf(await Answers.SendAsync(tour.Id));
      await Calls.CallAsync(Calls.Agent(second, On("attach_tour", tour.Id)), """{ "takeOver": true }""");

      var fetched = await Answers.FetchAsync(first, tour.Id, batchId);
      var again = await Answers.SendAsync(tour.Id);

      Assert.False(fetched.AsObject().ContainsKey("receivedAt"), $"The fetch of the session that lost the tour marked the batch received: {fetched}");
      Assert.Equal([(tour.CheckIds[0], second.Id)], again["answers"]!.AsArray().Select(answer => ((string)answer!["checkId"]!, (string)answer["batch"]!["sessionId"]!)));
   }

   /// <summary>The batch goes to the bound session, whose own batch stays in flight, so a second send while it waits has nothing to send.</summary>
   [Fact]
   public async Task Sent_answer_waiting_for_the_bound_sessions_fetch_is_not_sent_again()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await Answers.AnswerAsync(tour, 0, FirstAnswer);
      await Answers.SendAsync(tour.Id);
      await Answers.AnswerAsync(tour, 1, SecondAnswer);

      var sent = await Answers.SendAsync(tour.Id);

      Assert.Equal([tour.CheckIds[1]], sent["answers"]!.AsArray().Select(answer => (string)answer!["checkId"]!));
   }
}
