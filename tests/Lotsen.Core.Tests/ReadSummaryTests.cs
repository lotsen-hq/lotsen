using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Server;
using Lotsen.Tours;
using Microsoft.Extensions.DependencyInjection;
using static Lotsen.Tests.OperationCall;
using static Lotsen.Tests.ThreadCalls;

namespace Lotsen.Tests;

/// <summary>
/// Covers <c>read_summary</c> over HTTP: the UI reads it with or without a bound session, an agent session's summary leaves out the reviewer's draft answers and notes, and a tour with nothing
/// to summarize returns an empty summary.
/// </summary>
/// <remarks>Each test stores its state through the store, so a tour without a pin can hold stop content, answers and threads.</remarks>
[Trait("Category", "Summary")]
public sealed class ReadSummaryTests : IAsyncLifetime
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
   public async Task Ui_reads_the_summary_with_no_session_bound_and_sees_its_draft_answers_and_notes()
   {
      var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var stored = Seed(id, session.Id);
      await _harness.DropSessionAsync(session);

      var summary = await _calls.CallAsync(await _calls.UiAsync(On("read_summary", id)));

      Assert.Equal(stored.UpdatedAt, summary["updatedAt"]!.GetValue<DateTimeOffset>());
      Assert.Equal(["c1"], CheckIds(summary["answers"]!["decisions"]!));
      Assert.Equal(["c2", "c9"], CheckIds(summary["answers"]!["concerns"]!));
      Assert.Equal("draft", (string)summary["answers"]!["concerns"]![0]!["state"]!);
      Assert.Contains(summary["threads"]!["open"]!.AsArray(), thread => (string)thread!["id"]! == SummaryTours.NoteThreadId && (bool)thread["note"]!);
   }

   [Fact]
   public async Task Agent_summary_leaves_out_draft_answers_and_notes()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      Seed(id, session.Id);

      var summary = await _calls.CallAsync(_calls.Agent(session, On("read_summary", id)));
      var json = summary.ToJsonString();

      Assert.Equal(["c9"], CheckIds(summary["answers"]!["concerns"]!));
      Assert.DoesNotContain(SummaryTours.DraftText, json, StringComparison.Ordinal);
      Assert.DoesNotContain(SummaryTours.NoteText, json, StringComparison.Ordinal);
      Assert.DoesNotContain(summary["threads"]!["open"]!.AsArray(), thread => (string)thread!["id"]! == SummaryTours.NoteThreadId);
      Assert.Equal(["t1", "t3", "t6"], summary["threads"]!["open"]!.AsArray().Select(thread => (string)thread!["id"]!));
   }

   [Theory]
   [InlineData("open")]
   [InlineData("resolved")]
   [InlineData("outdated")]
   public async Task Ui_summary_lists_a_typed_note_in_the_group_of_its_type_in_each_thread_state_and_the_agent_summary_does_not(string state)
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var stored = Seed(id, session.Id, tour => tour with
      {
         Threads = ThreadFixtures.Threads([.. tour.Threads, ThreadFixtures.Note("t9", new ThreadAnchor.WholeStop("s3"), "The cap drifts.", state: StateOf(state), type: AnswerType.Concern)]),
      });

      var ui = await _calls.CallAsync(await _calls.UiAsync(On("read_summary", id)));
      var agent = await _calls.CallAsync(_calls.Agent(session, On("read_summary", id)));

      var concern = Assert.Single(ui["typedThreads"]!["concerns"]!.AsArray())!;
      var expected = JsonNode.Parse(
         $$"""{ "id": "t9", "anchor": { "kind": "stop", "stopId": "s3" }, "stopId": "s3", "stopTitle": "Rounding on stacked discounts", "text": "The cap drifts.", "type": "concern", "state": "{{state}}", "note": true }""");
      Assert.True(JsonNode.DeepEquals(expected, concern), $"The typed note reads {concern}");
      Assert.Empty(ui["typedThreads"]!["decisions"]!.AsArray());
      Assert.Empty(ui["typedThreads"]!["openQuestions"]!.AsArray());
      Assert.Contains(ui["threads"]![state]!.AsArray(), thread => (string)thread!["id"]! == "t9");
      Assert.Null(agent["typedThreads"]);
      Assert.DoesNotContain("The cap drifts.", agent.ToJsonString(), StringComparison.Ordinal);
      Assert.Equal(stored.UpdatedAt, ui["updatedAt"]!.GetValue<DateTimeOffset>());
   }

   /// <summary>A follow-up action collects its items from the summary that Claude reads, so a typed note gives draftWorkItems nothing to hand.</summary>
   [Fact]
   public async Task Ui_summary_with_a_typed_decision_note_and_no_decision_sent_names_why_draft_work_items_has_nothing_to_hand()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      Seed(id, session.Id, tour => tour with
      {
         Answers = [.. tour.Answers.Where(answer => answer.Type != AnswerType.Decision)],
         Threads = ThreadFixtures.Threads(ThreadFixtures.Note("t9", new ThreadAnchor.WholeTour(), "Keep the cap.", type: AnswerType.Decision)),
      });

      var summary = await _calls.CallAsync(await _calls.UiAsync(On("read_summary", id)));

      Assert.Single(summary["typedThreads"]!["decisions"]!.AsArray());
      Assert.Equal(ActionRules.NoDecisionSent, (string)summary["followUps"]![TourAction.DraftWorkItemsKind]!);
   }

   [Fact]
   public async Task Ui_and_agent_summary_list_a_sent_typed_thread_in_the_group_of_its_type_and_move_it_when_its_type_changes()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var saved = await _calls.SaveNoteAsync(id, "The cap drifts.", "concern");
      var threadId = ThreadIdOf(saved["thread"]!);
      await _calls.SendNotesAsync(id, threadId);

      var ui = await _calls.CallAsync(await _calls.UiAsync(On("read_summary", id)));
      var agent = await _calls.CallAsync(_calls.Agent(session, On("read_summary", id)));
      await _calls.SetTypeAsync(id, threadId, "openQuestion");
      var retyped = await _calls.CallAsync(_calls.Agent(session, On("read_summary", id)));

      var expected = JsonNode.Parse($$"""{ "id": "{{threadId}}", "anchor": { "kind": "tour" }, "text": "The cap drifts.", "type": "concern", "state": "open" }""");
      Assert.True(JsonNode.DeepEquals(expected, Assert.Single(ui["typedThreads"]!["concerns"]!.AsArray())), $"The reviewer's summary reads {ui["typedThreads"]}");
      Assert.True(JsonNode.DeepEquals(expected, Assert.Single(agent["typedThreads"]!["concerns"]!.AsArray())), $"The agent's summary reads {agent["typedThreads"]}");
      Assert.Empty(retyped["typedThreads"]!["concerns"]!.AsArray());
      Assert.Equal(threadId, (string)Assert.Single(retyped["typedThreads"]!["openQuestions"]!.AsArray())!["id"]!);
      Assert.NotEqual((string)agent["summaryFingerprint"]!, (string)retyped["summaryFingerprint"]!);
   }

   /// <summary>A follow-up action collects its items from the answers of the summary that Claude reads, so a sent typed thread gives draftWorkItems nothing to hand.</summary>
   [Fact]
   public async Task Ui_summary_with_a_sent_typed_decision_thread_and_no_decision_sent_names_why_draft_work_items_has_nothing_to_hand()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      Seed(id, session.Id, tour => tour with
      {
         Answers = [.. tour.Answers.Where(answer => answer.Type != AnswerType.Decision)],
         Threads = ThreadFixtures.Threads(ThreadFixtures.Asked("t9", new ThreadAnchor.WholeTour(), text: "Keep the cap.", type: AnswerType.Decision)),
      });

      var summary = await _calls.CallAsync(await _calls.UiAsync(On("read_summary", id)));

      Assert.Single(summary["typedThreads"]!["decisions"]!.AsArray());
      Assert.Empty(summary["answers"]!["decisions"]!.AsArray());
      Assert.Equal(ActionRules.NoDecisionSent, (string)summary["followUps"]![TourAction.DraftWorkItemsKind]!);
   }

   /// <summary>An agent session's tour holds no earlier versions, so the question of an outdated answer comes from the stored tour.</summary>
   [Fact]
   public async Task Agent_summary_names_the_question_of_an_outdated_answer()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      Seed(id, session.Id);

      var summary = await _calls.CallAsync(_calls.Agent(session, On("read_summary", id)));

      var outdated = Assert.Single(summary["answers"]!["outdated"]!.AsArray());
      Assert.Equal((SummaryTours.OldQuestion, 1), ((string)outdated!["question"]!, (int)outdated["outdatedOn"]!));
   }

   [Fact]
   public async Task Ui_that_asks_for_html_gets_the_question_and_scenario_html_and_without_the_header_gets_markdown_alone()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      Seed(id, session.Id);
      var request = (await _calls.UiAsync(On("read_summary", id))).WithProseHtml();

      var summary = await _calls.CallAsync(request);
      var plain = await _calls.CallAsync(await _calls.UiAsync(On("read_summary", id)));

      Assert.Equal("<p>Is the cap right?</p>\n", (string)summary["answers"]!["decisions"]![0]!["questionHtml"]!);
      Assert.Equal("<p>Two discounts round twice.</p>\n", (string)summary["risks"]![0]!["scenarioHtml"]!);
      Assert.DoesNotContain("Html", plain.ToJsonString(), StringComparison.Ordinal);
   }

   /// <summary>A follow-up action hands Claude only what Claude can read, so the UI's summary lists a draft decision while it names the reason that draftWorkItems has nothing to hand.</summary>
   [Fact]
   public async Task Ui_summary_names_why_a_follow_up_action_has_nothing_to_hand_from_the_summary_that_claude_reads()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      Seed(id, session.Id);
      Assert.True(TourId.TryParse(id, out var tourId), $"The tour ID {id} does not parse.");
      _harness.ChangeStored(tourId, tour => tour with
      {
         Answers = [.. tour.Answers.Select(answer => answer.Type == AnswerType.Decision ? new CheckAnswer(answer.StopId, answer.CheckId, answer.Text, answer.Type, AnswerState.Draft, answer.SavedAt, null, answer.OutdatedOn) : answer)],
      });

      var summary = await _calls.CallAsync(await _calls.UiAsync(On("read_summary", id)));

      Assert.Equal(["c1"], CheckIds(summary["answers"]!["decisions"]!));
      Assert.Equal(ActionRules.NoDecisionSent, (string)summary["followUps"]![TourAction.DraftWorkItemsKind]!);
      Assert.Equal([TourAction.DraftWorkItemsKind], summary["followUps"]!.AsObject().Select(entry => entry.Key));
   }

   [Fact]
   public async Task Tour_with_no_plan_returns_an_empty_summary()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);

      var summary = await _calls.CallAsync(await _calls.UiAsync(On("read_summary", id)));

      Assert.Equal(id, (string)summary["tourId"]!);
      Assert.All(["decisions", "concerns", "openQuestions", "outdated"], group => Assert.Empty(summary["answers"]![group]!.AsArray()));
      Assert.All(["open", "resolved", "outdated"], group => Assert.Empty(summary["threads"]![group]!.AsArray()));
      Assert.Null(summary["typedThreads"]);
      Assert.All(["risks", "skippedStops", "staleStops"], list => Assert.Empty(summary[list]!.AsArray()));
   }

   [Fact]
   public async Task Unknown_tour_is_refused()
   {
      using var response = await _calls.SendAsync(await _calls.UiAsync(On("read_summary", TourId.New().ToString())));

      await ThreadCalls.ErrorAsync(response, HttpStatusCode.NotFound, "tourNotFound");
   }

   private static IEnumerable<string> CheckIds(JsonNode answers) => answers.AsArray().Select(answer => (string)answer!["checkId"]!);

   private Tour Seed(string tourId, string sessionId, Func<Tour, Tour>? change = null)
   {
      Assert.True(TourId.TryParse(tourId, out var id), $"The tour ID {tourId} does not parse.");
      return _harness.ChangeStored(id, tour => (change ?? (seeded => seeded))(SummaryTours.Apply(tour, sessionId)))!;
   }

   private static QuestionThreadState StateOf(string state) => state switch
   {
      "resolved" => QuestionThreadState.Resolved,
      "outdated" => QuestionThreadState.Outdated,
      _ => QuestionThreadState.Open,
   };
}
