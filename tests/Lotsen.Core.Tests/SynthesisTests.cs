using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Operations;
using Lotsen.Server;
using Lotsen.Tours;
using Microsoft.Extensions.DependencyInjection;
using static Lotsen.Tests.OperationCall;
using static Lotsen.Tests.ThreadCalls;

namespace Lotsen.Tests;

/// <summary>
/// Covers <c>write_synthesis</c>, with which the bound session stores its synthesis of the review on the tour, and how <c>read_summary</c> returns the synthesis with whether it predates the summary.
/// </summary>
[Trait("Category", "Summary")]
public sealed class SynthesisTests : IClassFixture<StopTourFixture>, IAsyncLifetime
{
   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

   private const string DraftText = "DRAFT-ANSWER-WORDS";

   private const string NoteText = "NOTE-WORDS";

   private readonly PinnedTourFixture _tour;

   public SynthesisTests(StopTourFixture fixture)
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

   private const string WellFormedFingerprint = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

   private static string Body(string text, string basedOn = WellFormedFingerprint) => new JsonObject { ["text"] = text, ["basedOn"] = basedOn }.ToJsonString();

   // Claude reads the summary and writes the synthesis on it, as the tour skill says.
   private async Task<JsonNode> WriteAsync(AgentSessionConnection session, string tourId, string text) => await WriteAsync(session, tourId, text, await FingerprintAsync(session, tourId));

   private async Task<JsonNode> WriteAsync(AgentSessionConnection session, string tourId, string text, string basedOn) =>
      await Calls.CallAsync(Calls.Agent(session, On("write_synthesis", tourId)), Body(text, basedOn));

   private async Task<string> FingerprintAsync(AgentSessionConnection session, string tourId) => (string)(await AgentSummaryAsync(session, tourId))["summaryFingerprint"]!;

   private async Task<JsonNode> AgentSummaryAsync(AgentSessionConnection session, string tourId) => await Calls.CallAsync(Calls.Agent(session, On("read_summary", tourId)));

   private async Task<JsonNode> UiSummaryAsync(string tourId) => await Calls.CallAsync(await Calls.UiAsync(On("read_summary", tourId)));

   private async Task<bool> PredatesAsync(string tourId) => (bool)(await UiSummaryAsync(tourId))["synthesis"]!["predatesSummary"]!;

   [Fact]
   public async Task Bound_session_writes_the_synthesis_with_its_write_time_and_both_summaries_return_it()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);

      var written = await WriteAsync(session, tour.Id, "The review keeps the digit. See [the line](ref:head/src/modified.txt#L2-2).");
      var ui = await UiSummaryAsync(tour.Id);
      var agent = await Calls.CallAsync(Calls.Agent(session, On("read_summary", tour.Id)));

      Assert.Equal(tour.Id, (string)written["tourId"]!);
      Assert.Equal((string)written["updatedAt"]!, (string)ui["updatedAt"]!);
      Assert.All([ui, agent], summary =>
      {
         var synthesis = summary["synthesis"]!;
         Assert.Equal("The review keeps the digit. See [the line](ref:head/src/modified.txt#L2-2).", (string)synthesis["text"]!);
         Assert.Equal((DateTimeOffset)written["writtenAt"]!, (DateTimeOffset)synthesis["writtenAt"]!);
         Assert.False((bool)synthesis["predatesSummary"]!);
         Assert.Equal(_tour.Head.Value, (string)synthesis["pin"]!["head"]!);
      });
   }

   [Fact]
   public async Task New_synthesis_replaces_the_last_one()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);

      var first = await WriteAsync(session, tour.Id, "First take.");
      var second = await WriteAsync(session, tour.Id, "Second take.");
      var summary = await UiSummaryAsync(tour.Id);

      Assert.Equal("Second take.", (string)summary["synthesis"]!["text"]!);
      Assert.True((DateTimeOffset)second["writtenAt"]! > (DateTimeOffset)first["writtenAt"]!, "The second synthesis did not get a later write time.");
      Assert.DoesNotContain("First take.", summary.ToJsonString(), StringComparison.Ordinal);
   }

   [Fact]
   public async Task Tour_without_a_synthesis_has_none_in_its_summary()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);

      Assert.Null((await UiSummaryAsync(tour.Id))["synthesis"]);
   }

   [Fact]
   public async Task Ui_that_asks_for_html_gets_the_synthesis_html()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await WriteAsync(session, tour.Id, "The review keeps *the digit*.");
      var request = (await Calls.UiAsync(On("read_summary", tour.Id))).WithProseHtml();

      var summary = await Calls.CallAsync(request);

      Assert.Equal("<p>The review keeps <em>the digit</em>.</p>\n", (string)summary["synthesis"]!["html"]!);
      Assert.Null((await UiSummaryAsync(tour.Id))["synthesis"]!["html"]);
   }

   [Fact]
   public async Task Writing_publishes_synthesis_written_with_the_tours_update_time()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await using var stream = await EventStream.OpenAsync(Harness);

      var written = await WriteAsync(session, tour.Id, "A synthesis.");
      var published = await stream.NextAsync(Timeout);

      Assert.Equal(ServerEvents.SynthesisWritten.Name, published.Type);
      Assert.True(JsonNode.DeepEquals(written, EventStream.WithoutRevision(published.Data)), $"The event differs from the response: {published.Data}");
   }

   [Fact]
   public async Task Synthesis_from_another_session_or_from_a_session_that_lost_the_tour_is_refused_and_stores_nothing()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      await using var other = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);

      using var fromOther = await Calls.SendAsync(Calls.Agent(other, On("write_synthesis", tour.Id)), Body("From another session."));
      await Calls.CallAsync(Calls.Agent(other, On("attach_tour", tour.Id)), """{ "takeOver": true }""");
      using var fromFormer = await Calls.SendAsync(Calls.Agent(session, On("write_synthesis", tour.Id)), Body("From the former session."));

      await ErrorAsync(fromOther, HttpStatusCode.Conflict, "attachedToAnotherSession");
      await ErrorAsync(fromFormer, HttpStatusCode.Conflict, "attachedToAnotherSession");
      Assert.Null((await UiSummaryAsync(tour.Id))["synthesis"]);
   }

   [Fact]
   public async Task Synthesis_with_a_ref_link_that_cannot_be_followed_is_refused_and_stores_nothing()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);

      using var response = await Calls.SendAsync(Calls.Agent(session, On("write_synthesis", tour.Id)), Body("See [it](ref:head/src/missing.txt#L1-1)."));

      var error = await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("text", (string)error["field"]!);
      Assert.Contains("'ref:head/src/missing.txt#L1-1' cannot be followed", (string)error["reason"]!, StringComparison.Ordinal);
      Assert.Null((await UiSummaryAsync(tour.Id))["synthesis"]);
   }

   [Theory]
   [InlineData("")]
   [InlineData("  \n ")]
   public async Task Synthesis_without_text_is_refused(string text)
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);

      using var response = await Calls.SendAsync(Calls.Agent(session, On("write_synthesis", tour.Id)), Body(text));

      var error = await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("text", (string)error["field"]!);
   }

   [Fact]
   public async Task Synthesis_of_a_tour_that_is_not_pinned_is_refused_with_its_pin()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      Store(tour.Id, latest => latest with { Pin = new TourPin.Failed("The clone failed.") });

      using var response = await Calls.SendAsync(Calls.Agent(session, On("write_synthesis", tour.Id)), Body("A synthesis without links."));

      var error = await ErrorAsync(response, HttpStatusCode.Conflict, "tourNotPinned");
      Assert.Equal("failed", (string)error["pin"]!["state"]!);
   }

   [Fact]
   public async Task Synthesis_whose_pin_changes_while_its_links_are_checked_is_refused_and_stores_nothing()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      Writer.Hook = WriterHooks.AfterGit(id =>
      {
         Store(id.ToString(), latest => latest with { Pin = ((TourPin.Pinned)latest.Pin) with { Iteration = 7 } });
         return Task.CompletedTask;
      }, "write_synthesis");

      using var response = await Calls.SendAsync(Calls.Agent(session, On("write_synthesis", tour.Id)), Body("See [it](ref:head/src/modified.txt#L2-2)."));

      var error = await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("text", (string)error["field"]!);
      Assert.Null((await UiSummaryAsync(tour.Id))["synthesis"]);
   }

   /// <summary>
   /// The synthesis predates the summary that an agent session reads. So a draft answer and a note leave the verdict as it is, which shows that neither reaches the fingerprint, and a batch that
   /// Claude fetched is no change either.
   /// </summary>
   [Fact]
   public async Task Synthesis_predates_the_summary_only_after_a_change_that_claude_can_see()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await Answers.AnswerAsync(tour, 0, "Keep it.");
      var batch = AnswerCalls.BatchIdOf(await Answers.SendAsync(tour.Id));
      await WriteAsync(session, tour.Id, "The review keeps the digit.");

      await Answers.FetchAsync(session, tour.Id, batch);
      var afterReceived = await PredatesAsync(tour.Id);
      await Answers.AnswerAsync(tour, 1, DraftText);
      await Calls.SaveNoteAsync(tour.Id, NoteText);
      var afterDrafts = await PredatesAsync(tour.Id);
      await Calls.StartThreadAsync(session, tour.Id);
      var afterThread = await PredatesAsync(tour.Id);

      Assert.False(afterReceived, "A batch that Claude received made the synthesis predate the summary.");
      Assert.False(afterDrafts, "A draft answer or a note made the synthesis predate the summary.");
      Assert.True(afterThread, "A new thread did not make the synthesis predate the summary.");
   }

   /// <summary>A summarize asks for the synthesis and adds nothing to the review, so its thread, and the reviewer's resolve of it, leave the verdict as it is.</summary>
   [Fact]
   public async Task Synthesis_does_not_predate_the_summary_because_of_a_summarize_thread()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      var action = await Calls.CallAsync(await Calls.UiAsync(On("request_tour_action", tour.Id)), TourActionTests.ActionBody(TourAction.SummarizeKind, """{ "kind": "tour" }"""));
      await WriteAsync(session, tour.Id, "The review keeps the digit.");

      await Calls.CallAsync(Calls.Agent(session, On("reply", tour.Id, questionId: QuestionIdOf(action))), """{ "resolution": "done", "text": "Wrote the synthesis." }""");
      await Calls.CallAsync(await Calls.UiAsync(On("set_thread_state", tour.Id, threadId: ThreadIdOf(action))), """{ "state": "resolved" }""");
      var afterResolve = await PredatesAsync(tour.Id);
      await Calls.CallAsync(await Calls.UiAsync(On("request_tour_action", tour.Id)), TourActionTests.ActionBody(TourAction.SummarizeKind, """{ "kind": "tour" }"""));

      Assert.False(afterResolve, "Resolving the summarize thread made the synthesis predate the summary.");
      Assert.False(await PredatesAsync(tour.Id), "A new summarize made the synthesis predate the summary.");
   }

   /// <summary>A follow-up action hands a part of the review to Claude and adds nothing to it, so its thread leaves the verdict as it is, as a summarize thread does.</summary>
   [Fact]
   public async Task Synthesis_does_not_predate_the_summary_because_of_a_follow_up_action_thread()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await Answers.AnswerAsync(tour, 0, "Keep the digit.");
      await Answers.SendAsync(tour.Id);
      await WriteAsync(session, tour.Id, "The review keeps the digit.");

      var request = TourActionTests.ActionBody(TourAction.DraftWorkItemsKind, """{ "kind": "tour" }""");
      var action = await Calls.CallAsync(await Calls.UiAsync(On("request_tour_action", tour.Id)), request);
      var afterRequest = await PredatesAsync(tour.Id);
      await Calls.CallAsync(Calls.Agent(session, On("reply", tour.Id, questionId: QuestionIdOf(action))), """{ "resolution": "done", "text": "Created one work item." }""");
      await Calls.CallAsync(await Calls.UiAsync(On("set_thread_state", tour.Id, threadId: ThreadIdOf(action))), """{ "state": "resolved" }""");

      Assert.False(afterRequest, "A follow-up action made the synthesis predate the summary.");
      Assert.False(await PredatesAsync(tour.Id), "Resolving a follow-up action's thread made the synthesis predate the summary.");
   }

   [Theory]
   [InlineData("skip")]
   [InlineData("resolve")]
   [InlineData("send")]
   public async Task Synthesis_predates_the_summary_after_a_skip_a_resolved_thread_or_a_sent_answer(string change)
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      var asked = await Calls.AskAsync(tour.Id);
      await WriteAsync(session, tour.Id, "The review is open.");

      switch (change)
      {
         case "skip":
            await Calls.CallAsync(await Calls.UiAsync(On("set_stop_skipped", tour.Id, stopId: tour.StopId)), """{ "skipped": true }""");
            break;
         case "resolve":
            await Calls.CallAsync(await Calls.UiAsync(On("set_thread_state", tour.Id, threadId: ThreadIdOf(asked))), """{ "state": "resolved" }""");
            break;
         default:
            await Answers.AnswerAsync(tour, 0, "Keep it.");
            await Answers.SendAsync(tour.Id);
            break;
      }

      Assert.True(await PredatesAsync(tour.Id), $"The {change} did not make the synthesis predate the summary.");
   }

   /// <summary>The reviewer answers while Claude composes the synthesis, so the synthesis never covered that answer.</summary>
   [Fact]
   public async Task Synthesis_based_on_a_summary_that_changed_before_the_write_predates_the_summary()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      var read = await FingerprintAsync(session, tour.Id);

      await Answers.AnswerAsync(tour, 0, "Keep it.");
      await Answers.SendAsync(tour.Id);
      await WriteAsync(session, tour.Id, "Nothing decided yet.", read);

      Assert.True(await PredatesAsync(tour.Id));
   }

   [Fact]
   public async Task Synthesis_based_on_the_current_summary_does_not_predate_it()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      var before = await FingerprintAsync(session, tour.Id);
      await Answers.AnswerAsync(tour, 0, "Keep it.");
      await Answers.SendAsync(tour.Id);

      var current = await FingerprintAsync(session, tour.Id);
      await WriteAsync(session, tour.Id, "The review keeps the digit.", current);

      Assert.NotEqual(before, current);
      Assert.False(await PredatesAsync(tour.Id));
   }

   /// <summary>A well-formed fingerprint that no summary had is stored as it is, so the synthesis predates every summary.</summary>
   [Fact]
   public async Task Synthesis_based_on_a_fingerprint_that_no_summary_had_predates_the_summary()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);

      await WriteAsync(session, tour.Id, "The review keeps the digit.", WellFormedFingerprint);

      Assert.True(await PredatesAsync(tour.Id));
   }

   [Theory]
   [InlineData("")]
   [InlineData("abc")]
   [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
   [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")]
   [InlineData("0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef")]
   [InlineData("0123456789abcdeg0123456789abcdef0123456789abcdef0123456789abcdef")]
   public async Task Synthesis_based_on_a_malformed_fingerprint_is_refused_and_stores_nothing(string basedOn)
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);

      using var response = await Calls.SendAsync(Calls.Agent(session, On("write_synthesis", tour.Id)), Body("A synthesis.", basedOn));

      var error = await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("basedOn", (string)error["field"]!);
      Assert.Contains("summaryFingerprint", (string)error["reason"]!, StringComparison.Ordinal);
      Assert.Null((await UiSummaryAsync(tour.Id))["synthesis"]);
   }

   [Fact]
   public async Task Synthesis_without_a_fingerprint_is_refused_and_stores_nothing()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);

      using var response = await Calls.SendAsync(Calls.Agent(session, On("write_synthesis", tour.Id)), new JsonObject { ["text"] = "A synthesis." }.ToJsonString());

      await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Null((await UiSummaryAsync(tour.Id))["synthesis"]);
   }

   /// <summary>The UI's summary holds a draft answer and a note, and its fingerprint is still the one that Claude reads, so the fingerprint tells nothing of them.</summary>
   [Fact]
   public async Task Ui_and_agent_summaries_carry_the_fingerprint_of_the_agent_summary()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      var before = await FingerprintAsync(session, tour.Id);
      await Answers.AnswerAsync(tour, 0, DraftText);
      await Calls.SaveNoteAsync(tour.Id, NoteText);

      var ui = (string)(await UiSummaryAsync(tour.Id))["summaryFingerprint"]!;
      var agent = await FingerprintAsync(session, tour.Id);

      Assert.Equal((before, before), (ui, agent));
      Assert.Equal(64, before.Length);
   }

   /// <summary>The summary names the check's question and the stop's title beside the answer, so Claude's own writes change it too.</summary>
   [Theory]
   [InlineData("question")]
   [InlineData("title")]
   public async Task Synthesis_predates_the_summary_after_put_stop_rewrites_an_answered_question_or_set_plan_renames_its_stop(string change)
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await Answers.AnswerAsync(tour, 0, "Keep it.");
      await Answers.SendAsync(tour.Id);
      await WriteAsync(session, tour.Id, "The review keeps the digit.");

      if (change == "question")
      {
         var checks = $$"""[{ "id": "{{tour.CheckIds[0]}}", "question": "Keep the digit after all?" }, { "id": "{{tour.CheckIds[1]}}", "question": "Is line one still right?" }]""";
         await Answers.PutStopAsync(session, tour.Id, tour.StopId, checks);
      }
      else
      {
         var plan = $$"""{ "chapters": [{ "id": "code", "title": "Code" }], "stops": [{ "id": "{{tour.StopId}}", "title": "Keep the new name?", "kind": "question", "chapter": "code" }] }""";
         await Calls.CallAsync(Calls.Agent(session, On("set_plan", tour.Id)), plan);
      }

      Assert.True(await PredatesAsync(tour.Id), $"The {change} change did not make the synthesis predate the summary.");
   }

   [Theory]
   [InlineData("head")]
   [InlineData("mergeBase")]
   public async Task Synthesis_predates_the_summary_once_the_tour_has_another_pin(string moved)
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await WriteAsync(session, tour.Id, "The review keeps the digit.");

      Store(tour.Id, latest => latest with { Pin = Moved((TourPin.Pinned)latest.Pin, moved) });

      Assert.True(await PredatesAsync(tour.Id));
   }

   /// <summary>The summary reads the same at both pins, but Claude read the code at the earlier one.</summary>
   [Theory]
   [InlineData("head")]
   [InlineData("mergeBase")]
   public async Task Synthesis_based_on_a_summary_read_at_another_pin_predates_the_summary(string moved)
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      var read = await FingerprintAsync(session, tour.Id);

      Store(tour.Id, latest => latest with { Pin = Moved((TourPin.Pinned)latest.Pin, moved) });
      var current = await FingerprintAsync(session, tour.Id);
      await WriteAsync(session, tour.Id, "The review keeps the digit.", read);

      Assert.NotEqual(read, current);
      Assert.True(await PredatesAsync(tour.Id));
   }

   [Fact]
   public async Task Synthesis_based_on_the_summary_read_at_the_current_pin_does_not_predate_it()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      Store(tour.Id, latest => latest with { Pin = Moved((TourPin.Pinned)latest.Pin, "head") });

      await WriteAsync(session, tour.Id, "The review keeps the digit.");

      Assert.False(await PredatesAsync(tour.Id));
   }

   /// <summary>The fingerprint names the pin's commits alone, so a pin with the same commits under another iteration number reads the same code.</summary>
   [Fact]
   public async Task Synthesis_does_not_predate_the_summary_when_the_pin_keeps_its_commits()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await WriteAsync(session, tour.Id, "The review keeps the digit.");

      Store(tour.Id, latest => latest with { Pin = ((TourPin.Pinned)latest.Pin) with { Iteration = 9 } });

      Assert.False(await PredatesAsync(tour.Id));
   }

   // Any other commit serves, because the summary reads no file at the pin.
   private static TourPin.Pinned Moved(TourPin.Pinned pin, string moved) => moved == "head" ? pin with { Head = pin.MergeBase } : pin with { MergeBase = pin.Head };

   /// <summary>The leak path of the tool's own response, with a draft answer and a note on the tour.</summary>
   [Fact]
   public async Task Synthesis_response_and_agent_summary_hold_no_draft_answer_and_no_note()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tour = await Answers.TourAsync(session);
      await Answers.AnswerAsync(tour, 0, DraftText);
      await Calls.SaveNoteAsync(tour.Id, NoteText);

      var written = (await WriteAsync(session, tour.Id, "A synthesis.")).ToJsonString();
      var summary = (await Calls.CallAsync(Calls.Agent(session, On("read_summary", tour.Id)))).ToJsonString();

      Assert.All([written, summary], json =>
      {
         Assert.DoesNotContain(DraftText, json, StringComparison.Ordinal);
         Assert.DoesNotContain(NoteText, json, StringComparison.Ordinal);
      });
   }

   private void Store(string tourId, Func<Tour, Tour> change)
   {
      Assert.True(TourId.TryParse(tourId, out var id), $"The tour ID {tourId} does not parse.");
      Harness.ChangeStored(id, change);
   }
}
