using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Lotsen.Operations;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>
/// Covers <c>answer_check</c>, <c>set_answer_type</c> and <c>clear_answer</c>: the UI stores, changes and clears a draft answer to a check of a stop's current content, with or without a bound
/// session, and refuses an answer for a stop or a check that the tour does not have.
/// </summary>
[Trait("Category", "Checks")]
public sealed class AnswerTests : IClassFixture<StopTourFixture>
{
   private const string Plan = """
      {
        "chapters": [{ "id": "code", "title": "Code" }],
        "stops": [{ "title": "Keep the rename?", "kind": "question", "chapter": "code" }, { "title": "Later", "kind": "inspect", "chapter": "code" }]
      }
      """;

   private const string Passage = """{ "side": "head", "path": "src/modified.txt", "startLine": 2, "endLine": 2, "role": "primary" }""";

   private const string Checks = """
      [
        { "question": "Keep the digit?", "suggestions": [{ "text": "Keep the digit.", "type": "decision" }, { "text": "Sorting may break.", "type": "concern" }] },
        { "question": "Is line one still right?" }
      ]
      """;

   private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(10);

   private readonly PinnedTourFixture _tour;

   public AnswerTests(StopTourFixture fixture)
   {
      _tour = fixture.Tour;
   }

   private ServerHarness Harness => _tour.Harness;

   [Fact]
   public async Task Answer_from_a_suggestion_stores_its_text_and_type_as_a_draft()
   {
      var (tourId, stopId, checkIds) = await AnsweredTourAsync();

      var answered = await AnswerAsync(tourId, stopId, checkIds[0], """{ "text": "Sorting may break.", "fromSuggestion": true }""");

      var answer = answered["answer"]!;
      Assert.Equal((stopId, checkIds[0], "Sorting may break.", "concern", "draft"), ((string)answer["stopId"]!, (string)answer["checkId"]!, (string)answer["text"]!, (string)answer["type"]!, (string)answer["state"]!));
      Assert.Equal(tourId, (string)answered["tourId"]!);
      Assert.True(JsonNode.DeepEquals(new JsonArray(answer.DeepClone()), await AnswersAsync(tourId)), "The stored answers differ from the response's answer.");
   }

   [Fact]
   public async Task Free_text_answer_starts_as_a_decision()
   {
      var (tourId, stopId, checkIds) = await AnsweredTourAsync();

      var answered = await AnswerAsync(tourId, stopId, checkIds[1], """{ "text": "Yes, it is." }""");

      Assert.Equal(("Yes, it is.", "decision", "draft"), ((string)answered["answer"]!["text"]!, (string)answered["answer"]!["type"]!, (string)answered["answer"]!["state"]!));
   }

   /// <summary>The text names no suggestion of the check, so the answer cannot take a suggestion's type.</summary>
   [Fact]
   public async Task Answer_from_a_suggestion_the_check_does_not_have_is_refused_and_nothing_is_stored()
   {
      var (tourId, stopId, checkIds) = await AnsweredTourAsync();

      using var response = await UiSendAsync(On("answer_check", tourId, stopId: stopId, checkId: checkIds[1]), """{ "text": "Keep the digit.", "fromSuggestion": true }""");

      var error = await ThreadCalls.ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("text", (string)error["field"]!);
      Assert.Empty(await AnswersAsync(tourId));
   }

   [Fact]
   public async Task Changing_the_type_is_stored_and_keeps_the_text()
   {
      var (tourId, stopId, checkIds) = await AnsweredTourAsync();
      await AnswerAsync(tourId, stopId, checkIds[1], """{ "text": "Not sure yet." }""");

      var changed = await CallUiAsync(On("set_answer_type", tourId, stopId: stopId, checkId: checkIds[1]), """{ "type": "openQuestion" }""");

      Assert.Equal(("Not sure yet.", "openQuestion", "draft"), ((string)changed["answer"]!["text"]!, (string)changed["answer"]!["type"]!, (string)changed["answer"]!["state"]!));
      Assert.True(JsonNode.DeepEquals(new JsonArray(changed["answer"]!.DeepClone()), await AnswersAsync(tourId)), "The stored answers differ from the changed answer.");
   }

   [Fact]
   public async Task Rewriting_the_words_keeps_the_answers_type()
   {
      var (tourId, stopId, checkIds) = await AnsweredTourAsync();
      await AnswerAsync(tourId, stopId, checkIds[1], """{ "text": "Not sure yet." }""");
      await CallUiAsync(On("set_answer_type", tourId, stopId: stopId, checkId: checkIds[1]), """{ "type": "concern" }""");

      var rewritten = await AnswerAsync(tourId, stopId, checkIds[1], """{ "text": "Line one looks wrong." }""");

      Assert.Equal(("Line one looks wrong.", "concern", "draft"), ((string)rewritten["answer"]!["text"]!, (string)rewritten["answer"]!["type"]!, (string)rewritten["answer"]!["state"]!));
      Assert.True(JsonNode.DeepEquals(new JsonArray(rewritten["answer"]!.DeepClone()), await AnswersAsync(tourId)), "The stored answers differ from the rewritten answer.");
   }

   [Fact]
   public async Task Picking_another_suggestion_takes_its_type()
   {
      var (tourId, stopId, checkIds) = await AnsweredTourAsync();
      await AnswerAsync(tourId, stopId, checkIds[0], """{ "text": "Sorting may break.", "fromSuggestion": true }""");

      var picked = await AnswerAsync(tourId, stopId, checkIds[0], """{ "text": "Keep the digit.", "fromSuggestion": true }""");

      Assert.Equal(("Keep the digit.", "decision", "draft"), ((string)picked["answer"]!["text"]!, (string)picked["answer"]!["type"]!, (string)picked["answer"]!["state"]!));
   }

   [Fact]
   public async Task Answering_again_replaces_the_checks_answer_in_its_place_and_keeps_a_picked_suggestions_type()
   {
      var (tourId, stopId, checkIds) = await AnsweredTourAsync();
      await AnswerAsync(tourId, stopId, checkIds[0], """{ "text": "Sorting may break.", "fromSuggestion": true }""");
      await AnswerAsync(tourId, stopId, checkIds[1], """{ "text": "Yes." }""");

      await AnswerAsync(tourId, stopId, checkIds[0], """{ "text": "Keep it, it sorts fine." }""");

      var answers = await AnswersAsync(tourId);
      Assert.Equal([(checkIds[0], "Keep it, it sorts fine.", "concern"), (checkIds[1], "Yes.", "decision")], answers.Select(answer => ((string)answer!["checkId"]!, (string)answer["text"]!, (string)answer["type"]!)));
   }

   [Fact]
   public async Task Clearing_removes_the_answer()
   {
      var (tourId, stopId, checkIds) = await AnsweredTourAsync();
      await AnswerAsync(tourId, stopId, checkIds[0], """{ "text": "Keep the digit.", "fromSuggestion": true }""");

      var cleared = await CallUiAsync(On("clear_answer", tourId, stopId: stopId, checkId: checkIds[0]));

      Assert.Equal((tourId, stopId, checkIds[0]), ((string)cleared["tourId"]!, (string)cleared["stopId"]!, (string)cleared["checkId"]!));
      Assert.Empty(await AnswersAsync(tourId));
   }

   [Fact]
   public async Task Each_change_reaches_the_ui_as_an_event_with_the_responses_update_time()
   {
      var (tourId, stopId, checkIds) = await AnsweredTourAsync();
      await using var stream = await EventStream.OpenAsync(Harness);

      var answered = await AnswerAsync(tourId, stopId, checkIds[1], """{ "text": "Yes." }""");
      var answeredEvent = await stream.NextBesidesControlsAsync(EventWait);
      var typed = await CallUiAsync(On("set_answer_type", tourId, stopId: stopId, checkId: checkIds[1]), """{ "type": "concern" }""");
      var typedEvent = await stream.NextBesidesControlsAsync(EventWait);
      var cleared = await CallUiAsync(On("clear_answer", tourId, stopId: stopId, checkId: checkIds[1]));
      var clearedEvent = await stream.NextBesidesControlsAsync(EventWait);

      Assert.Equal((ServerEvents.AnswerUpdated.Name, ServerEvents.AnswerUpdated.Name, ServerEvents.AnswerCleared.Name), (answeredEvent.Type, typedEvent.Type, clearedEvent.Type));
      Assert.True(JsonNode.DeepEquals(answered, EventStream.WithoutRevision(answeredEvent.Data)), $"The event differs from the response: {answeredEvent.Data}");
      Assert.True(JsonNode.DeepEquals(typed, EventStream.WithoutRevision(typedEvent.Data)), $"The event differs from the response: {typedEvent.Data}");
      Assert.True(JsonNode.DeepEquals(cleared, EventStream.WithoutRevision(clearedEvent.Data)), $"The event differs from the response: {clearedEvent.Data}");
      Assert.True((DateTimeOffset)cleared["updatedAt"]! >= (DateTimeOffset)answered["updatedAt"]!, "The clear moved the tour's update time back.");
   }

   /// <summary>The answer saved after the repeat is the stream's next event, so the repeat that changed nothing published nothing.</summary>
   [Theory]
   [InlineData("answer")]
   [InlineData("type")]
   public async Task Repeating_the_answer_or_its_type_changes_nothing_and_publishes_no_event(string write)
   {
      var (tourId, stopId, checkIds) = await AnsweredTourAsync();
      var first = await AnswerAsync(tourId, stopId, checkIds[0], """{ "text": "Sorting may break.", "fromSuggestion": true }""");
      await using var stream = await EventStream.OpenAsync(Harness);

      var repeated = write == "answer"
         ? await AnswerAsync(tourId, stopId, checkIds[0], """{ "text": "Sorting may break.", "fromSuggestion": true }""")
         : await CallUiAsync(On("set_answer_type", tourId, stopId: stopId, checkId: checkIds[0]), """{ "type": "concern" }""");
      await AnswerAsync(tourId, stopId, checkIds[1], """{ "text": "Marker." }""");
      var next = await stream.NextAsync(EventWait);

      Assert.True(JsonNode.DeepEquals(first, repeated), $"The repeat that changed nothing differs from the first answer: {repeated}");
      Assert.Equal((ServerEvents.AnswerUpdated.Name, checkIds[1]), (next.Type, (string)next.Data["answer"]!["checkId"]!));
   }

   public static TheoryData<string, string, HttpStatusCode, string, string> UnknownTargets => new()
   {
      { "answer", "stop", HttpStatusCode.NotFound, "stopNotFound", "stopId" },
      { "answer", "check", HttpStatusCode.NotFound, "checkNotFound", "checkId" },
      { "answer", "pendingStop", HttpStatusCode.NotFound, "checkNotFound", "checkId" },
      { "type", "stop", HttpStatusCode.NotFound, "stopNotFound", "stopId" },
      { "type", "check", HttpStatusCode.NotFound, "checkNotFound", "checkId" },
      { "type", "unanswered", HttpStatusCode.NotFound, "answerNotFound", "checkId" },
      { "clear", "stop", HttpStatusCode.NotFound, "stopNotFound", "stopId" },
      { "clear", "check", HttpStatusCode.NotFound, "checkNotFound", "checkId" },
      { "clear", "unanswered", HttpStatusCode.NotFound, "answerNotFound", "checkId" },
   };

   [Theory]
   [MemberData(nameof(UnknownTargets))]
   public async Task Write_for_a_stop_or_check_that_the_tour_does_not_have_is_refused_and_nothing_is_stored(string write, string target, HttpStatusCode status, string code, string field)
   {
      var (tourId, stopId, checkIds) = await AnsweredTourAsync();
      await AnswerAsync(tourId, stopId, checkIds[0], """{ "text": "Keep the digit.", "fromSuggestion": true }""");
      var stops = await StopIdsAsync(tourId);
      var before = await AnswersAsync(tourId);
      var (targetStop, targetCheck) = target switch
      {
         "stop" => ("nosuchstop", checkIds[0]),
         "check" => (stopId, "nosuchck"),
         "pendingStop" => (stops[1], checkIds[0]),
         _ => (stopId, checkIds[1]),
      };

      using var response = await WriteAsync(write, tourId, targetStop, targetCheck);

      var error = await ThreadCalls.ErrorAsync(response, status, code);
      Assert.Equal(field, (string)error["field"]!);
      Assert.True(JsonNode.DeepEquals(before, await AnswersAsync(tourId)), "The refused write changed the answers.");
   }

   /// <summary>
   /// A rewrite of the stop drops the answered check, so the answer stays stored but the check is no longer one the tour has: the UI can no longer answer it, change it or clear it, and a new check
   /// never takes the dropped check's ID.
   /// </summary>
   [Fact]
   public async Task Answer_to_a_check_that_a_rewrite_drops_stays_stored_and_further_writes_are_refused()
   {
      var (tourId, stopId, checkIds) = await AnsweredTourAsync();
      var answered = await AnswerAsync(tourId, stopId, checkIds[1], """{ "text": "Yes." }""");
      await PutStopAsync(tourId, stopId, $$"""[{ "id": "{{checkIds[0]}}", "question": "Keep the digit?" }, { "question": "A new check?" }]""");

      using var response = await WriteAsync("answer", tourId, stopId, checkIds[1]);
      using var typeResponse = await WriteAsync("type", tourId, stopId, checkIds[1]);
      using var clearResponse = await WriteAsync("clear", tourId, stopId, checkIds[1]);

      await ThreadCalls.ErrorAsync(response, HttpStatusCode.NotFound, "checkNotFound");
      await ThreadCalls.ErrorAsync(typeResponse, HttpStatusCode.NotFound, "checkNotFound");
      await ThreadCalls.ErrorAsync(clearResponse, HttpStatusCode.NotFound, "checkNotFound");
      var outdated = answered["answer"]!.DeepClone();
      outdated["outdatedOn"] = 1;
      Assert.True(JsonNode.DeepEquals(new JsonArray(outdated), await AnswersAsync(tourId)), "The dropped check's answer is not stored as it was, outdated on the first version.");
   }

   [Theory]
   [InlineData("""{ "text": "  " }""")]
   [InlineData("""{ "text": "a\u0007b" }""")]
   public async Task Answer_with_invalid_words_is_refused_and_nothing_is_stored(string body)
   {
      var (tourId, stopId, checkIds) = await AnsweredTourAsync();

      using var response = await UiSendAsync(On("answer_check", tourId, stopId: stopId, checkId: checkIds[1]), body);

      var error = await ThreadCalls.ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("text", (string)error["field"]!);
      Assert.DoesNotContain("question", (string)error["reason"]!, StringComparison.Ordinal);
      Assert.Empty(await AnswersAsync(tourId));
   }

   [Fact]
   public async Task Answer_longer_than_a_question_may_be_is_refused()
   {
      var (tourId, stopId, checkIds) = await AnsweredTourAsync();

      using var response = await UiSendAsync(On("answer_check", tourId, stopId: stopId, checkId: checkIds[1]), $$"""{ "text": "{{new string('a', ReviewerWords.MaxLength + 1)}}" }""");

      Assert.Equal("text", (string)(await ThreadCalls.ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput"))["field"]!);
      Assert.Empty(await AnswersAsync(tourId));
   }

   /// <summary>A suggestion follows the rules of the reviewer's words when <c>put_stop</c> stores it, so the reviewer can pick every stored suggestion.</summary>
   [Theory]
   [InlineData("Keep\tthe digit.")]
   [InlineData("Keep the digit.\nSorting stays.")]
   [InlineData("Keep the digit.\r\nSorting stays.")]
   [InlineData(null)]
   public async Task Suggestion_that_put_stop_stores_can_be_picked(string? text)
   {
      text ??= new string('a', ReviewerWords.MaxLength);
      var session = await Harness.AgentAsync();
      var (tourId, stopId, _) = await AnsweredTourAsync(session);
      var suggestion = JsonValue.Create(text).ToJsonString();
      var stop = await PutStopAsync(tourId, stopId, $$"""[{ "question": "Keep the digit?", "suggestions": [{ "text": {{suggestion}}, "type": "concern" }] }]""", session);
      var checkId = (string)stop["content"]!["checks"]![0]!["id"]!;

      var answered = await AnswerAsync(tourId, stopId, checkId, $$"""{ "text": {{suggestion}}, "fromSuggestion": true }""");

      Assert.Equal(text, (string)answered["answer"]!["text"]!);
      Assert.Equal("concern", (string)answered["answer"]!["type"]!);
   }

   [Fact]
   public async Task Answers_are_written_with_no_session_attached()
   {
      var session = await AgentSessionConnection.OpenAsync(Harness);
      var (tourId, stopId, checkIds) = await AnsweredTourAsync(session);
      await Harness.DropSessionAsync(session);

      await AnswerAsync(tourId, stopId, checkIds[0], """{ "text": "Sorting may break.", "fromSuggestion": true }""");
      await AnswerAsync(tourId, stopId, checkIds[1], """{ "text": "Yes." }""");
      await CallUiAsync(On("set_answer_type", tourId, stopId: stopId, checkId: checkIds[1]), """{ "type": "openQuestion" }""");
      await CallUiAsync(On("clear_answer", tourId, stopId: stopId, checkId: checkIds[0]));

      Assert.Null(await Harness.BindingOfAsync(tourId));
      Assert.Equal([(checkIds[1], "openQuestion")], (await AnswersAsync(tourId)).Select(answer => ((string)answer!["checkId"]!, (string)answer["type"]!)));
   }

   [Fact]
   public async Task Answers_are_written_after_another_session_took_the_tour_over()
   {
      var (tourId, stopId, checkIds) = await AnsweredTourAsync();
      await using var other = await AgentSessionConnection.OpenAsync(Harness);
      using var takeOver = await Harness.SendAsync(other.Request(Harness, On("attach_tour", tourId)), """{ "takeOver": true }""");
      Assert.True(takeOver.IsSuccessStatusCode, $"The take-over returned {(int)takeOver.StatusCode}.");

      await AnswerAsync(tourId, stopId, checkIds[1], """{ "text": "Yes." }""");

      Assert.Single(await AnswersAsync(tourId));
   }

   /// <summary>The store keeps the answers in the tour's file, so a new server reads them back as they were.</summary>
   [Fact]
   public async Task Answers_survive_a_server_restart()
   {
      var (tourId, stopId, checkIds) = await AnsweredTourAsync();
      await AnswerAsync(tourId, stopId, checkIds[0], """{ "text": "Sorting may break.", "fromSuggestion": true }""");
      await AnswerAsync(tourId, stopId, checkIds[1], """{ "text": "Yes." }""");
      var before = await AnswersAsync(tourId);

      await _tour.RestartServerAsync();

      Assert.Equal(2, before.Count);
      Assert.True(JsonNode.DeepEquals(before, await AnswersAsync(tourId)), "The answers changed across the restart.");
   }

   // Creates a pinned tour, bound to the session or else to the harness's own, with a question stop whose content holds Checks, and returns the tour's ID, the stop's ID and the checks' IDs.
   private async Task<(string TourId, string StopId, string[] CheckIds)> AnsweredTourAsync(AgentSessionConnection? session = null)
   {
      session ??= await Harness.AgentAsync();
      var calls = new ThreadCalls(Harness);
      var created = await calls.CallAsync(session.Request(Harness, "create_tour"), ThreadCalls.CreateBody);
      await Harness.PinsIdleAsync();
      var tourId = (string)created["id"]!;
      var tour = await calls.CallAsync(session.Request(Harness, On("set_plan", tourId)), Plan);
      var stopId = (string)tour["plan"]!["stops"]![0]!["id"]!;
      var stop = await PutStopAsync(tourId, stopId, Checks, session);
      return (tourId, stopId, [.. stop["content"]!["checks"]!.AsArray().Select(check => (string)check!["id"]!)]);
   }

   private async Task<JsonNode> PutStopAsync(string tourId, string stopId, string checks, AgentSessionConnection? session = null) =>
      await new ThreadCalls(Harness).CallAsync(
         (session ?? await Harness.AgentAsync()).Request(Harness, On("put_stop", tourId, stopId: stopId)),
         $$"""{ "headline": "A headline.", "passages": [{{Passage}}], "checks": {{checks}} }""");

   private async Task<string[]> StopIdsAsync(string tourId) => [.. (await Harness.CallAsync(On("read_state", tourId)))["plan"]!["stops"]!.AsArray().Select(stop => (string)stop!["id"]!)];

   private async Task<JsonArray> AnswersAsync(string tourId) => (await Harness.CallAsync(On("read_state", tourId)))["answers"]!.AsArray();

   private Task<JsonNode> AnswerAsync(string tourId, string stopId, string checkId, string body) => CallUiAsync(On("answer_check", tourId, stopId: stopId, checkId: checkId), body);

   private static (OperationCall Call, string Body) WriteRequest(string write, string tourId, string stopId, string checkId) => write switch
   {
      "answer" => (On("answer_check", tourId, stopId: stopId, checkId: checkId), """{ "text": "Mine." }"""),
      "type" => (On("set_answer_type", tourId, stopId: stopId, checkId: checkId), """{ "type": "concern" }"""),
      _ => (On("clear_answer", tourId, stopId: stopId, checkId: checkId), "{}"),
   };

   private Task<HttpResponseMessage> WriteAsync(string write, string tourId, string stopId, string checkId)
   {
      var (call, body) = WriteRequest(write, tourId, stopId, checkId);
      return UiSendAsync(call, body);
   }

   private async Task<HttpResponseMessage> UiSendAsync(OperationCall call, string? json = null) => await Harness.SendAsync(ServerHarness.Request(call, await Harness.UiCredentialAsync(), json: json));

   private async Task<JsonNode> CallUiAsync(OperationCall call, string? json = null)
   {
      using var response = await UiSendAsync(call, json);
      var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
      Assert.True(response.IsSuccessStatusCode, $"{call.Name} returned {(int)response.StatusCode}: {body}");
      return SuccessBody.Output(body);
   }
}
