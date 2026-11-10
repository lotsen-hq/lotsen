using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lotsen.Json;
using Lotsen.Operations;
using Lotsen.Server;
using Microsoft.Extensions.DependencyInjection;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>Covers the checks of a stop's content: how <c>put_stop</c> stores, identifies and refuses them, and how the tour returns them to Claude and, with their HTML, to the UI.</summary>
[Trait("Category", "Checks")]
public sealed class CheckTests : IClassFixture<StopTourFixture>
{
   // The stops by index: 0 inspect, 1 question.
   private const string Plan = """
      {
        "chapters": [{ "id": "code", "title": "Code" }],
        "stops": [
          { "title": "The modified file", "kind": "inspect", "chapter": "code" },
          { "title": "Keep the rename?", "kind": "question", "chapter": "code" }
        ]
      }
      """;

   private const int Inspect = 0;

   private const int Question = 1;

   private const string Passage = """{ "side": "head", "path": "src/modified.txt", "startLine": 2, "endLine": 2, "role": "primary" }""";

   private readonly PinnedTourFixture _tour;

   public CheckTests(StopTourFixture fixture)
   {
      _tour = fixture.Tour;
   }

   [Fact]
   public async Task Put_stop_stores_each_check_with_a_server_given_id_its_question_recommendation_and_typed_suggestions()
   {
      var ids = await SetPlanAsync();
      var checks = """
         [
           {
             "question": "Is a digit the right spelling?",
             "recommendation": "Yes: [the sort](ref:head/src/modified.txt#L2-2) needs it.",
             "suggestions": [{ "text": "Keep the digit.", "type": "decision" }, { "text": "Sorting may break.", "type": "concern" }, { "text": "What do other files use?", "type": "openQuestion" }]
           },
           { "question": "Is line one still right?" }
         ]
         """;

      var stored = await CallPutAsync(ids[Inspect], Body(checks));

      var stored0 = stored["content"]!["checks"]![0]!;
      var stored1 = stored["content"]!["checks"]![1]!;
      Assert.Equal(["id", "question", "recommendation", "suggestions"], stored0.AsObject().Select(property => property.Key));
      Assert.Equal(["id", "question"], stored1.AsObject().Select(property => property.Key));
      Assert.Equal(("Is a digit the right spelling?", "Yes: [the sort](ref:head/src/modified.txt#L2-2) needs it."), ((string)stored0["question"]!, (string)stored0["recommendation"]!));
      Assert.Equal(
         [("Keep the digit.", "decision"), ("Sorting may break.", "concern"), ("What do other files use?", "openQuestion")],
         stored0["suggestions"]!.AsArray().Select(suggestion => ((string)suggestion!["text"]!, (string)suggestion["type"]!)));
      Assert.Matches("^[a-z2-7]{8}$", (string)stored0["id"]!);
      Assert.NotEqual((string)stored0["id"]!, (string)stored1["id"]!);
      var read = await ReadStateAsync();
      Assert.True(JsonNode.DeepEquals(stored, read["plan"]!["stops"]![Inspect]), $"read_state differs from put_stop's result: {read}");
   }

   /// <summary>The suggestions are plain text, so the HTML holds only the question and the recommendation of each check.</summary>
   [Fact]
   public async Task Ui_read_carries_the_html_of_each_checks_question_and_recommendation()
   {
      var ids = await SetPlanAsync();
      await CallPutAsync(ids[Inspect], Body("""
         [
           { "question": "Is `2` right?", "recommendation": "Keep **it**.", "suggestions": [{ "text": "Keep <b>it</b>.", "type": "decision" }] },
           { "question": "And line one?" }
         ]
         """));

      var html = (await ReadAsTheUiAsync())["plan"]!["stops"]![Inspect]!["content"]!["html"]!["checks"]!.AsArray();

      Assert.Equal(2, html.Count);
      Assert.Equal(["question", "recommendation"], html[0]!.AsObject().Select(property => property.Key));
      Assert.Equal(("<p>Is <code>2</code> right?</p>\n", "<p>Keep <strong>it</strong>.</p>\n"), ((string)html[0]!["question"]!, (string)html[0]!["recommendation"]!));
      Assert.Equal(["question"], html[1]!.AsObject().Select(property => property.Key));
   }

   [Fact]
   public async Task Rewriting_a_stop_keeps_the_id_of_a_check_written_with_it_and_gives_a_check_without_one_a_new_id()
   {
      var ids = await SetPlanAsync();
      var first = await CallPutAsync(ids[Inspect], Body("""[{ "question": "First?" }, { "question": "Second?" }]"""));
      var firstIds = CheckIds(first);

      var second = await CallPutAsync(ids[Inspect], Body($$"""[{ "question": "New?" }, { "id": "{{firstIds[1]}}", "question": "Second, reworded?" }]"""));

      var secondIds = CheckIds(second);
      Assert.Equal(firstIds[1], secondIds[1]);
      Assert.DoesNotContain(secondIds[0], firstIds);
      Assert.Equal(["New?", "Second, reworded?"], second["content"]!["checks"]!.AsArray().Select(check => (string)check!["question"]!));
   }

   [Fact]
   public async Task Check_ids_are_unique_within_the_tour_and_a_check_cannot_take_an_id_of_another_stop()
   {
      var ids = await SetPlanAsync();
      var inspect = CheckIds(await CallPutAsync(ids[Inspect], Body("""[{ "question": "A?" }, { "question": "B?" }]""")));
      var question = CheckIds(await CallPutAsync(ids[Question], Body("""[{ "question": "C?" }, { "question": "D?" }]""")));
      var before = await ReadStateAsync();

      using var response = await PutAsync(ids[Question], Body($$"""[{ "id": "{{inspect[0]}}", "question": "A, moved?" }]"""));

      Assert.Equal(4, inspect.Concat(question).Distinct().Count());
      var error = await ReadErrorAsync(response);
      Assert.Equal("checks[0].id", (string)error["field"]!);
      await AssertUnchangedAsync(before);
   }

   public static TheoryData<string, string> UnknownIds => new()
   {
      { "nosuchid", "nosuchid" },
      { "", "" },
   };

   [Theory]
   [MemberData(nameof(UnknownIds))]
   public async Task Check_id_that_the_stop_does_not_have_is_refused_and_nothing_is_stored(string id, string quoted)
   {
      var ids = await SetPlanAsync();
      await CallPutAsync(ids[Inspect], Body("""[{ "question": "First?" }]"""));
      var before = await ReadStateAsync();

      using var response = await PutAsync(ids[Inspect], Body($$"""[{ "question": "First?" }, { "id": {{JsonValue.Create(id).ToJsonString()}}, "question": "Second?" }]"""));

      var error = await ReadErrorAsync(response);
      Assert.Equal("checks[1].id", (string)error["field"]!);
      Assert.Contains($"'{quoted}'", (string)error["reason"]!, StringComparison.Ordinal);
      Assert.Contains("omit id for a new check", (string)error["reason"]!, StringComparison.Ordinal);
      await AssertUnchangedAsync(before);
   }

   [Fact]
   public async Task Check_id_on_a_pending_stop_is_refused()
   {
      var ids = await SetPlanAsync();
      var before = await ReadStateAsync();

      using var response = await PutAsync(ids[Inspect], Body("""[{ "id": "abcdefgh", "question": "First?" }]"""));

      Assert.Equal("checks[0].id", (string)(await ReadErrorAsync(response))["field"]!);
      await AssertUnchangedAsync(before);
   }

   [Fact]
   public async Task Same_check_id_twice_in_one_write_is_refused()
   {
      var ids = await SetPlanAsync();
      var kept = CheckIds(await CallPutAsync(ids[Inspect], Body("""[{ "question": "First?" }]""")))[0];
      var before = await ReadStateAsync();

      using var response = await PutAsync(ids[Inspect], Body($$"""[{ "id": "{{kept}}", "question": "First?" }, { "id": "{{kept}}", "question": "Again?" }]"""));

      Assert.Equal("checks[1].id", (string)(await ReadErrorAsync(response))["field"]!);
      await AssertUnchangedAsync(before);
   }

   /// <summary>The stop's checks change while the passages are checked, so the kept ID is checked again against the latest content before the stop is stored.</summary>
   [Fact]
   public async Task Check_id_that_a_concurrent_write_removed_during_the_passage_checks_is_refused()
   {
      var ids = await SetPlanAsync();
      var kept = CheckIds(await CallPutAsync(ids[Inspect], Body("""[{ "question": "First?" }]""")))[0];
      var writer = _tour.Harness.Server.Services.GetRequiredService<TourWriter>();
      var plans = _tour.Harness.Server.Services.GetRequiredService<PlanOperations>();
      var pass = Passes.Admit<Access.BoundSession.Pass>("put_stop", await _tour.Harness.AgentCallerAsync());
      var replaced = false;
      writer.Hook = WriterHooks.AfterGit(async _ =>
      {
         if (!replaced)
         {
            replaced = true;
            var json = $$"""{ "tourId": "{{_tour.TourId}}", "stopId": "{{ids[Inspect]}}", "headline": "A headline.", "passages": [{{Passage}}], "checks": [{ "question": "Replaced?" }] }""";
            var result = await plans.PutStopAsync(pass, JsonSerializer.Deserialize(json, LotsenJsonContext.Default.PutStopInput)!, TestContext.Current.CancellationToken);
            Assert.True(result.Error is null, result.Error?.Reason);
         }
      }, "put_stop");
      try
      {
         var before = (await ReadStateAsync()).DeepClone();

         using var response = await PutAsync(ids[Inspect], Body($$"""[{ "id": "{{kept}}", "question": "First, reworded?" }]"""));

         Assert.Equal("checks[0].id", (string)(await ReadErrorAsync(response))["field"]!);
         var after = await ReadStateAsync();
         Assert.Equal(["Replaced?"], after["plan"]!["stops"]![Inspect]!["content"]!["checks"]!.AsArray().Select(check => (string)check!["question"]!));
         Assert.False(JsonNode.DeepEquals(before, after));
      }
      finally
      {
         writer.Hook = null;
      }
   }

   public static TheoryData<int, string, string> Rejected => new()
   {
      { Inspect, """[{ "question": "Fine?" }, { "recommendation": "Yes." }]""", "checks[1].question" },
      { Inspect, """[{ "question": "Fine?" }, { "question": " ", "recommendation": "Yes." }]""", "checks[1].question" },
      { Inspect, """[{ "question": "Fine?" }, { "question": null }]""", "checks[1].question" },
      { Inspect, """[null]""", "checks[0]" },
      { Inspect, """[{ "question": "Fine?", "suggestions": [null] }]""", "checks[0].suggestions[0]" },
      { Inspect, """[{ "question": "Fine?", "suggestions": [{ "text": "Yes." }] }]""", "checks[0].suggestions[0].type" },
      { Inspect, """[{ "question": "Fine?", "suggestions": [{ "text": "Yes.", "type": "answer" }] }]""", "checks[0].suggestions[0].type" },
      { Inspect, """[{ "question": "Fine?", "suggestions": [{ "text": "Yes.\u0001", "type": "decision" }] }]""", "checks[0].suggestions[0].text" },
      { Inspect, """[{ "question": "Fine?", "suggestions": [{ "text": "\u001b[1mYes.", "type": "decision" }] }]""", "checks[0].suggestions[0].text" },
      { Inspect, """[{ "question": "Fine?", "suggestions": [{ "text": "Yes.\u0085", "type": "decision" }] }]""", "checks[0].suggestions[0].text" },
      { Inspect, $$"""[{ "question": "Fine?" }, { "question": "Else?", "suggestions": [{ "text": "Yes.", "type": "decision" }, { "text": "{{new string('a', ReviewerWords.MaxLength + 1)}}", "type": "concern" }] }]""", "checks[1].suggestions[1].text" },
      { Inspect, """[{ "question": "Fine?", "kind": "decision" }]""", "checks[0].kind" },
      { Inspect, """["Fine?"]""", "checks[0]" },
      { Question, """[]""", "checks" },
      { Question, "null", "checks" },
   };

   [Theory]
   [MemberData(nameof(Rejected))]
   public async Task Invalid_check_is_refused_under_its_field_and_nothing_is_stored(int stop, string checks, string field)
   {
      var ids = await SetPlanAsync();
      var before = await ReadStateAsync();

      using var response = await PutAsync(ids[stop], Body(checks));

      var error = await ReadErrorAsync(response);
      Assert.Equal(field, (string)error["field"]!);
      Assert.StartsWith(field + ": ", (string)error["reason"]!, StringComparison.Ordinal);
      Assert.DoesNotContain("Lotsen.", (string)error["reason"]!, StringComparison.Ordinal);
      await AssertUnchangedAsync(before);
   }

   [Fact]
   public async Task Question_stop_without_checks_is_refused()
   {
      var ids = await SetPlanAsync();
      var before = await ReadStateAsync();

      using var response = await PutAsync(ids[Question], $$"""{ "headline": "A headline.", "passages": [{{Passage}}] }""");

      Assert.Equal("checks", (string)(await ReadErrorAsync(response))["field"]!);
      await AssertUnchangedAsync(before);
   }

   public static TheoryData<string, string, string> RejectedLinks => new()
   {
      { """[{ "question": "Fine?" }, { "question": "Is [this](ref:head/src/modified.txt#L5-6) right?" }]""", "checks[1].question", "ref:head/src/modified.txt#L5-6" },
      { """[{ "question": "Fine?", "recommendation": "See [x](ref:head/src/nothing.txt#L1-1)." }]""", "checks[0].recommendation", "ref:head/src/nothing.txt#L1-1" },
      { """[{ "question": "Fine?", "recommendation": "See [x](ref:left/src/modified.txt#L1-1)." }]""", "checks[0].recommendation", "ref:left/src/modified.txt#L1-1" },
   };

   [Theory]
   [MemberData(nameof(RejectedLinks))]
   public async Task Check_with_a_ref_link_that_cannot_be_followed_is_refused_under_its_field(string checks, string field, string link)
   {
      var ids = await SetPlanAsync();
      var before = await ReadStateAsync();

      using var response = await PutAsync(ids[Inspect], Body(checks));

      var error = await ReadErrorAsync(response);
      Assert.Equal(field, (string)error["field"]!);
      Assert.Contains($"'{link}'", (string)error["reason"]!, StringComparison.Ordinal);
      await AssertUnchangedAsync(before);
   }

   /// <summary>A suggestion is plain text, so a <c>ref:</c> link in it is text, and its target is never checked.</summary>
   [Fact]
   public async Task Empty_recommendation_and_empty_suggestions_are_dropped_and_a_suggestion_is_stored_as_written()
   {
      var ids = await SetPlanAsync();

      var stored = await CallPutAsync(ids[Inspect], Body("""
         [
           { "question": "Fine?", "recommendation": "  ", "suggestions": [{ "text": "", "type": "decision" }, { "text": " ", "type": "concern" }] },
           { "question": "Else?", "suggestions": [{ "text": "", "type": "decision" }, { "text": "See [x](ref:head/src/nothing.txt#L1-1).", "type": "openQuestion" }] }
         ]
         """));

      var checks = stored["content"]!["checks"]!.AsArray();
      Assert.Equal(["id", "question"], checks[0]!.AsObject().Select(property => property.Key));
      Assert.Equal(["See [x](ref:head/src/nothing.txt#L1-1)."], checks[1]!["suggestions"]!.AsArray().Select(suggestion => (string)suggestion!["text"]!));
   }

   [Fact]
   public async Task Replanning_keeps_a_ready_stops_checks_with_their_ids()
   {
      var ids = await SetPlanAsync();
      var stored = await CallPutAsync(ids[Inspect], Body("""[{ "question": "Fine?", "suggestions": [{ "text": "Yes.", "type": "decision" }] }]"""));

      var replanned = await _tour.Harness.AgentCallAsync(On("set_plan", _tour.TourId), $$"""
         {
           "chapters": [{ "id": "code", "title": "Code" }],
           "stops": [{ "id": "{{ids[Inspect]}}", "title": "Renamed", "kind": "inspect", "chapter": "code" }, { "id": "{{ids[Question]}}", "title": "Q", "kind": "question", "chapter": "code" }]
         }
         """);

      Assert.True(JsonNode.DeepEquals(stored["content"]!["checks"], replanned["plan"]!["stops"]![Inspect]!["content"]!["checks"]));
   }

   /// <summary>The store keeps the checks as put_stop wrote them, so a restart reads every ID, recommendation and suggestion back.</summary>
   [Fact]
   public async Task Checks_round_trip_through_the_store()
   {
      var ids = await SetPlanAsync();
      await CallPutAsync(ids[Inspect], Body("""[{ "question": "Fine?", "recommendation": "Yes.", "suggestions": [{ "text": "Ship it.", "type": "decision" }] }]"""));
      var before = await ReadStateAsync();

      await _tour.RestartServerAsync();

      Assert.True(JsonNode.DeepEquals(before["plan"], (await ReadStateAsync())["plan"]));
   }

   private static string Body(string checks) => $$"""{ "headline": "A headline.", "passages": [{{Passage}}], "checks": {{checks}} }""";

   private static string[] CheckIds(JsonNode stop) => [.. stop["content"]!["checks"]!.AsArray().Select(check => (string)check!["id"]!)];

   private async Task<string[]> SetPlanAsync()
   {
      var tour = await _tour.Harness.AgentCallAsync(On("set_plan", _tour.TourId), Plan);
      return [.. tour["plan"]!["stops"]!.AsArray().Select(stop => (string)stop!["id"]!)];
   }

   private Task<JsonNode> ReadStateAsync() => _tour.Harness.CallAsync(On("read_state", _tour.TourId));

   private async Task<JsonNode> ReadAsTheUiAsync()
   {
      var request = (new PreparedCall(On("read_state", _tour.TourId), _tour.Harness.UiCredential)).WithProseHtml();
      using var response = await _tour.Harness.SendAsync(request);
      return SuccessBody.Output(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
   }

   private Task<HttpResponseMessage> PutAsync(string stopId, string body) => _tour.Harness.AgentSendJsonAsync(On("put_stop", _tour.TourId, stopId: stopId), body);

   private Task<JsonNode> CallPutAsync(string stopId, string body) => _tour.Harness.AgentCallAsync(On("put_stop", _tour.TourId, stopId: stopId), body);

   private async Task AssertUnchangedAsync(JsonNode before)
   {
      var after = await ReadStateAsync();
      Assert.True(JsonNode.DeepEquals(before, after), $"The tour changed: {after}");
   }

   private static async Task<JsonNode> ReadErrorAsync(HttpResponseMessage response)
   {
      var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
      Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"Expected 400, got {(int)response.StatusCode}: {body}");
      var error = JsonNode.Parse(body)!;
      Assert.Equal("invalidInput", (string)error["code"]!);
      return error;
   }
}
