using System.Net;
using System.Text.Json.Nodes;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>Covers <c>fetch_question</c>: what it returns for a question of each anchor kind, and its refusal of an unknown question.</summary>
[Trait("Category", "Api")]
public sealed class FetchQuestionTests : IClassFixture<QuestionTourFixture>
{
   private readonly QuestionTourFixture _fixture;

   public FetchQuestionTests(QuestionTourFixture fixture)
   {
      _fixture = fixture;
   }

   /// <summary>The stop is the anchor's own for a stop or stop text anchor, and the stop being viewed for any other.</summary>
   [Theory]
   [InlineData("code", "2")]
   [InlineData("stopText", "replaces `two`")]
   [InlineData("file", null)]
   [InlineData("stop", null)]
   [InlineData("tour", null)]
   public async Task Fetch_returns_the_anchor_with_its_selected_text_the_stops_content_and_the_thread(string kind, string? selectedText)
   {
      var anchor = kind switch
      {
         "code" => """{ "kind": "code", "side": "head", "path": "src/modified.txt", "startLine": 2, "endLine": 2 }""",
         "stopText" => $$"""{ "kind": "stopText", "stopId": "{{_fixture.InspectId}}", "field": "how", "text": "replaces `two`" }""",
         "file" => """{ "kind": "file", "side": "head", "path": "logo.png" }""",
         "stop" => $$"""{ "kind": "stop", "stopId": "{{_fixture.InspectId}}" }""",
         _ => """{ "kind": "tour" }""",
      };
      var viewed = kind is "stopText" or "stop" ? _fixture.OverviewId : _fixture.InspectId;
      var thread = await _fixture.AskThreadAsync($$"""{ "text": "What about {{kind}}?", "anchor": {{anchor}}, "stopId": "{{viewed}}" }""");
      var questionId = (string)thread["messages"]![0]!["id"]!;

      var fetched = await _fixture.Tour.Harness.AgentCallAsync(On("fetch_question", _fixture.Tour.TourId, questionId: questionId));

      var read = await _fixture.Tour.Harness.CallAsync(On("read_state", _fixture.Tour.TourId));
      var inspect = read["plan"]!["stops"]![1]!;
      Assert.Equal((_fixture.Tour.TourId, questionId), ((string)fetched["tourId"]!, (string)fetched["questionId"]!));
      var stored = read["threads"]!.AsArray().Single(candidate => (string)candidate!["id"]! == (string)thread["id"]!);
      Assert.True(JsonNode.DeepEquals(stored, fetched["thread"]), $"The fetched thread differs from the stored one: {fetched["thread"]}");
      Assert.True(JsonNode.DeepEquals(thread["anchor"], fetched["thread"]!["anchor"]), $"The fetched anchor differs from the ask's: {fetched["thread"]}");
      Assert.Equal(kind, (string)fetched["thread"]!["anchor"]!["kind"]!);
      Assert.Equal(selectedText, (string?)fetched["thread"]!["anchor"]!["text"]);
      Assert.Equal($"What about {kind}?", (string)fetched["thread"]!["messages"]![0]!["text"]!);
      Assert.True(JsonNode.DeepEquals(inspect, fetched["stop"]), $"The fetched stop differs from the stored one: {fetched["stop"]}");
      Assert.Equal(QuestionTourFixture.Headline, (string)fetched["stop"]!["content"]!["headline"]!);
      if (kind is "code" or "file")
      {
         Assert.Equal(_fixture.Tour.Head.Value, (string)fetched["thread"]!["anchor"]!["commit"]!);
      }
   }

   [Fact]
   public async Task Question_asked_without_a_stop_being_viewed_is_fetched_without_a_stop()
   {
      var thread = await _fixture.AskThreadAsync("""{ "text": "Where do I start?", "anchor": { "kind": "tour" } }""");

      var fetched = await _fixture.Tour.Harness.AgentCallAsync(On("fetch_question", _fixture.Tour.TourId, questionId: (string)thread["messages"]![0]!["id"]!));

      Assert.False(fetched.AsObject().ContainsKey("stop"));
      Assert.False(thread["messages"]![0]!.AsObject().ContainsKey("stopId"));
   }

   [Fact]
   public async Task Unknown_question_is_not_found()
   {
      using var response = await _fixture.Tour.Harness.AgentSendJsonAsync(On("fetch_question", _fixture.Tour.TourId, questionId: "nosuchquestion"));

      var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
      Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
      Assert.Equal(("questionNotFound", "questionId"), ((string)body["code"]!, (string)body["field"]!));
      Assert.Contains("'nosuchquestion'", (string)body["reason"]!, StringComparison.Ordinal);
   }
}
