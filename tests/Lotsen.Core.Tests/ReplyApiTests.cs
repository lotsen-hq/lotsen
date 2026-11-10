using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Server;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>Covers <c>reply</c>: what each resolution stores, the refusal of a second resolution, and the HTML of an answer that the UI receives and the store never keeps.</summary>
[Trait("Category", "Api")]
public sealed class ReplyApiTests : IClassFixture<QuestionTourFixture>
{
   private readonly QuestionTourFixture _fixture;

   public ReplyApiTests(QuestionTourFixture fixture)
   {
      _fixture = fixture;
   }

   [Theory]
   [InlineData("done", "Line two is `2` now.")]
   [InlineData("needsAnswer", "Which line do you mean?")]
   [InlineData("declined", "That is out of scope.")]
   [InlineData("declined", null)]
   public async Task Each_resolution_is_recorded_on_the_request_with_the_answer(string resolution, string? text)
   {
      var asked = await AskAsync();
      var body = text is null ? $$"""{ "resolution": "{{resolution}}" }""" : $$"""{ "resolution": "{{resolution}}", "text": "{{text}}" }""";

      var thread = await _fixture.Tour.Harness.AgentCallAsync(On("reply", _fixture.Tour.TourId, questionId: QuestionIdOf(asked)), body);

      var stored = await StoredThreadAsync((string)asked["id"]!);
      var request = thread["messages"]![0]!["requests"]![0]!;
      var reply = thread["messages"]![1]!;
      Assert.Equal(resolution, (string)request["state"]!);
      Assert.Equal(((string)request["id"]!, text), ((string)reply["requestId"]!, (string?)reply["text"]));
      Assert.Equal(text is not null, reply.AsObject().ContainsKey("text"));
      Assert.True(JsonNode.DeepEquals(thread, stored), $"The stored thread differs from the reply's result: {stored}");
   }

   [Fact]
   public async Task Blank_text_of_a_decline_is_stored_as_no_text()
   {
      var asked = await AskAsync();

      var thread = await _fixture.Tour.Harness.AgentCallAsync(On("reply", _fixture.Tour.TourId, questionId: QuestionIdOf(asked)), """{ "resolution": "declined", "text": " " }""");

      Assert.False(thread["messages"]![1]!.AsObject().ContainsKey("text"));
   }

   [Fact]
   public async Task Second_reply_to_an_answered_question_is_refused_and_changes_nothing()
   {
      var asked = await AskAsync();
      await _fixture.Tour.Harness.AgentCallAsync(On("reply", _fixture.Tour.TourId, questionId: QuestionIdOf(asked)), """{ "resolution": "done", "text": "Yes." }""");
      var before = await StoredThreadAsync((string)asked["id"]!);

      using var response = await _fixture.Tour.Harness.AgentSendJsonAsync(On("reply", _fixture.Tour.TourId, questionId: QuestionIdOf(asked)), """{ "resolution": "declined" }""");

      var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
      Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"Expected 409, got {(int)response.StatusCode}: {body}");
      var error = JsonNode.Parse(body)!;
      Assert.Equal(("questionAnswered", "questionId"), ((string)error["code"]!, (string)error["field"]!));
      Assert.Contains("already has its answer", (string)error["reason"]!, StringComparison.Ordinal);
      Assert.True(JsonNode.DeepEquals(before, await StoredThreadAsync((string)asked["id"]!)), "The refused reply changed the thread.");
   }

   [Fact]
   public async Task Reply_to_an_unknown_question_is_not_found()
   {
      using var response = await _fixture.Tour.Harness.AgentSendJsonAsync(On("reply", _fixture.Tour.TourId, questionId: "nosuchquestion"), """{ "resolution": "declined" }""");

      var error = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
      Assert.Equal((HttpStatusCode.NotFound, "questionNotFound"), (response.StatusCode, (string)error["code"]!));
   }

   /// <summary>The UI asks for HTML with the prose header. Claude, the CLI and the tour file see only the Markdown that the session wrote.</summary>
   [Fact]
   public async Task Answer_reaches_the_ui_as_html_and_the_store_keeps_only_its_markdown()
   {
      var asked = await AskAsync();
      var questionId = QuestionIdOf(asked);
      await _fixture.Tour.Harness.AgentCallAsync(On("reply", _fixture.Tour.TourId, questionId: questionId), """{ "resolution": "done", "text": "See [line 2](ref:head/src/modified.txt#L2-2). <b>x</b>" }""");

      var tour = await ForTheUiAsync(On("read_state", _fixture.Tour.TourId));
      var fetched = await WithHtmlAsync((await _fixture.Tour.Harness.AgentAsync()).Request(_fixture.Tour.Harness, On("fetch_question", _fixture.Tour.TourId, questionId: questionId)));
      var plain = await StoredThreadAsync((string)asked["id"]!);
      var file = await File.ReadAllTextAsync(Path.Combine(_fixture.Tour.Harness.DataDirectory.ToursPath, $"{_fixture.Tour.TourId}.json"), TestContext.Current.CancellationToken);

      var html = (string)tour["threads"]!.AsArray().Single(thread => (string)thread!["id"]! == (string)asked["id"]!)!["messages"]![1]!["html"]!;
      Assert.StartsWith("<p>See <a data-ref-side=\"head\" data-ref-path=\"src/modified.txt\" data-ref-start-line=\"2\" data-ref-end-line=\"2\">line 2</a>. &lt;b&gt;x&lt;/b&gt;</p>", html, StringComparison.Ordinal);
      Assert.Equal(html, (string)fetched["thread"]!["messages"]![1]!["html"]!);
      Assert.Null(fetched["thread"]!["messages"]![0]!["html"]);
      Assert.Null(plain["messages"]![1]!["html"]);
      Assert.DoesNotContain("\"html\"", file, StringComparison.Ordinal);
      Assert.DoesNotContain("data-ref", file, StringComparison.Ordinal);
   }

   private static string QuestionIdOf(JsonNode thread) => (string)thread["messages"]![0]!["id"]!;

   private Task<JsonNode> AskAsync() => _fixture.AskThreadAsync("""{ "text": "Why?", "anchor": { "kind": "tour" } }""");

   private async Task<JsonNode> StoredThreadAsync(string threadId) => (await _fixture.ThreadsAsync()).Single(thread => (string)thread!["id"]! == threadId)!;

   private Task<JsonNode> ForTheUiAsync(OperationCall call) => WithHtmlAsync(new PreparedCall(call, _fixture.UiCredential));

   // Asks for prose as HTML, as the UI does. Only an agent session fetches a question, so a fetch asks for it with the session's credential.
   private async Task<JsonNode> WithHtmlAsync(PreparedCall call)
   {
      using var response = await _fixture.Tour.Harness.SendAsync(call.WithProseHtml());
      var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
      Assert.True(response.IsSuccessStatusCode, $"{call.Call.Name} returned {(int)response.StatusCode}: {body}");
      return SuccessBody.Output(body);
   }
}
