using System.Text.Json.Nodes;
using Lotsen.Operations;
using Lotsen.Tours;
using Microsoft.Extensions.DependencyInjection;
using static Lotsen.Tests.OperationCall;
using static Lotsen.Tests.ThreadCalls;

namespace Lotsen.Tests;

/// <summary>
/// Covers what an answer's state means outside the answer's own writes: every agent tool's tour leaves a draft out and shows a sent or received answer, the UI's and the CLI's reads keep every
/// answer, and the check's ID stays taken after the check is gone.
/// </summary>
/// <remarks>Each test stores its answer through the store, so a tour without a pin or stop content can hold one.</remarks>
[Trait("Category", "Checks")]
public sealed class DraftAnswerTests : IAsyncLifetime
{
   private const string AnswerText = "Draft words of the reviewer.";

   private ServerHarness _harness = null!;
   private ThreadCalls _calls = null!;

   public async ValueTask InitializeAsync()
   {
      _harness = await ServerHarness.StartAsync();
      _calls = new ThreadCalls(_harness);
   }

   public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

   [Theory]
   [InlineData("read_state")]
   [InlineData("attach_tour")]
   [InlineData("set_plan")]
   [InlineData("pin_tour")]
   public async Task Draft_answer_is_left_out_of_the_tour_that_each_agent_tool_returns(string tool)
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      StoreDraft(id);

      var tour = await _calls.CallAsync(_calls.Agent(session, On(tool, id)), ToolBody(tool));

      Assert.Empty(tour["answers"]!.AsArray());
      Assert.DoesNotContain(AnswerText, tour.ToJsonString(), StringComparison.Ordinal);
   }

   [Theory]
   [InlineData("read_state", "sent")]
   [InlineData("read_state", "received")]
   [InlineData("attach_tour", "sent")]
   [InlineData("attach_tour", "received")]
   [InlineData("set_plan", "sent")]
   [InlineData("set_plan", "received")]
   [InlineData("pin_tour", "sent")]
   [InlineData("pin_tour", "received")]
   public async Task Sent_or_received_answer_is_shown_in_the_tour_that_each_agent_tool_returns(string tool, string state)
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      StoreAnswer(id, state == "sent" ? AnswerState.Sent : AnswerState.Received, session.Id);

      var tour = await _calls.CallAsync(_calls.Agent(session, On(tool, id)), ToolBody(tool));

      Assert.Equal([(AnswerText, state)], tour["answers"]!.AsArray().Select(answer => ((string)answer!["text"]!, (string)answer["state"]!)));
   }

   /// <summary>A fetched question carries no answers, so a sent answer to its stop's check stays out of it too, and <c>read_state</c> is where agents read it.</summary>
   [Theory]
   [InlineData("sent")]
   [InlineData("received")]
   public async Task Sent_or_received_answer_is_left_out_of_a_fetched_question_about_its_stop(string state)
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var plan = await _calls.CallAsync(
         _calls.Agent(session, On("set_plan", id)),
         """{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "title": "Keep it?", "kind": "question", "chapter": "a" }] }""");
      var stopId = (string)plan["plan"]!["stops"]![0]!["id"]!;
      StoreAnswer(id, state == "sent" ? AnswerState.Sent : AnswerState.Received, session.Id, stopId);
      var asked = await _calls.CallAsync(await _calls.UiAsync(On("ask_question", id)), $$"""{ "text": "Why?", "anchor": { "kind": "stop", "stopId": "{{stopId}}" } }""");

      var fetched = await _calls.FetchAsync(session, id, QuestionIdOf(asked));

      Assert.DoesNotContain(AnswerText, fetched.ToJsonString(), StringComparison.Ordinal);
   }

   [Fact]
   public async Task Tour_that_create_tour_returns_has_no_answers()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);

      var tour = await _calls.CallAsync(_calls.Agent(session, "create_tour"), CreateBody);

      Assert.Empty(tour["answers"]!.AsArray());
   }

   /// <summary>A fetched question carries its thread and its stop, and neither holds the answer to the stop's check.</summary>
   [Fact]
   public async Task Draft_answer_is_left_out_of_a_fetched_question_about_its_stop()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var plan = await _calls.CallAsync(
         _calls.Agent(session, On("set_plan", id)),
         """{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "title": "Keep it?", "kind": "question", "chapter": "a" }] }""");
      var stopId = (string)plan["plan"]!["stops"]![0]!["id"]!;
      StoreDraft(id, stopId);
      var asked = await _calls.CallAsync(await _calls.UiAsync(On("ask_question", id)), $$"""{ "text": "Why?", "anchor": { "kind": "stop", "stopId": "{{stopId}}" } }""");

      var fetched = await _calls.FetchAsync(session, id, QuestionIdOf(asked));

      Assert.Equal(stopId, (string)fetched["stop"]!["id"]!);
      Assert.DoesNotContain(AnswerText, fetched.ToJsonString(), StringComparison.Ordinal);
   }

   [Fact]
   public async Task Draft_answer_is_kept_in_the_tour_that_the_ui_reads()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      StoreDraft(id);

      var tour = await _calls.CallAsync(await _calls.UiAsync(On("read_state", id)));

      Assert.Equal([(AnswerText, "draft")], tour["answers"]!.AsArray().Select(answer => ((string)answer!["text"]!, (string)answer["state"]!)));
   }

   /// <summary>The tour has no plan, so the answer is all that holds the check's ID.</summary>
   [Fact]
   public async Task Check_id_of_a_stored_answer_stays_used_after_its_check_is_gone()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);

      var tour = StoreDraft(id, checkId: "gonechck");

      Assert.Null(tour.Plan);
      Assert.Contains("gonechck", TourIds.NamedBy(tour));
   }

   private Tour StoreDraft(string tourId, string stopId = "somestop", string checkId = "somechck") => StoreAnswer(tourId, AnswerState.Draft, sessionId: null, stopId, checkId);

   // Stores one answer in the state. A sent or received answer names a batch to the session.
   private Tour StoreAnswer(string tourId, AnswerState state, string? sessionId, string stopId = "somestop", string checkId = "somechck")
   {
      Assert.True(TourId.TryParse(tourId, out var id), $"The tour ID {tourId} does not parse.");
      var now = DateTimeOffset.UtcNow;
      var batch = state == AnswerState.Draft ? null : new AnswerBatch("somebtch", sessionId!, now, state == AnswerState.Received ? now : null);
      var answer = new CheckAnswer(stopId, checkId, AnswerText, AnswerType.Concern, state, now, batch);
      return _harness.ChangeStored(id, tour => tour with { Answers = [answer] })!;
   }

   private static string? ToolBody(string tool) => tool switch
   {
      "read_state" => null,
      "set_plan" => """{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "title": "What changes", "kind": "overview", "chapter": "a" }] }""",
      _ => "{}",
   };
}
