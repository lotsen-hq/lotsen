using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>Covers <c>start_thread</c> on a pinned tour: the bound session starts a thread at each anchor kind, checked as a question's anchor is, with prose whose <c>ref:</c> links pass the checks of a reply.</summary>
[Trait("Category", "Threads")]
public sealed class AgentThreadAnchorTests : IAsyncLifetime
{
   private readonly QuestionTourFixture _fixture = new();

   public async ValueTask InitializeAsync() => await _fixture.InitializeAsync();

   public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

   [Fact]
   public async Task AgentThread_starts_at_each_anchor_kind_with_the_agent_as_its_first_messages_author_and_round_trips_through_the_store()
   {
      string[] anchors =
      [
         """{ "kind": "code", "side": "base", "path": "src/modified.txt", "startLine": 2, "endLine": 3 }""",
         """{ "kind": "code", "side": "head", "path": "src/modified.txt", "startLine": 2, "endLine": 2 }""",
         $$"""{ "kind": "stopText", "stopId": "{{_fixture.InspectId}}", "field": "how", "text": "replaces `two`" }""",
         """{ "kind": "file", "side": "head", "path": "logo.png" }""",
         $$"""{ "kind": "stop", "stopId": "{{_fixture.InspectId}}" }""",
         """{ "kind": "tour" }""",
      ];
      var session = (await _fixture.Tour.Harness.AgentAsync()).Id;
      var started = new List<JsonNode>();
      foreach (var (anchor, index) in anchors.Select((anchor, index) => (anchor, index)))
      {
         started.Add((await StartAsync($$"""{ "text": "Concern {{index}}", "anchor": {{anchor}} }"""))["thread"]!);
      }

      var before = await _fixture.ThreadsAsync();

      await _fixture.Tour.RestartServerAsync();

      var after = (await _fixture.Tour.Harness.CallAsync(On("read_state", _fixture.Tour.TourId)))["threads"]!.AsArray();
      Assert.True(JsonNode.DeepEquals(before, after), $"The threads differ after the restart: {after}");
      Assert.Equal(["code", "code", "stopText", "file", "stop", "tour"], after.Select(thread => (string)thread!["anchor"]!["kind"]!));
      Assert.Equal(["two\nthree", "2"], after.Take(2).Select(thread => (string)thread!["anchor"]!["text"]!));
      Assert.Equal([_fixture.Tour.MergeBase.Value, _fixture.Tour.Head.Value, _fixture.Tour.Head.Value], after.Where(thread => thread!["anchor"]!["commit"] is not null).Select(thread => (string)thread!["anchor"]!["commit"]!));
      Assert.All(after, thread => Assert.Equal(("agentMessage", session), ((string)thread!["messages"]![0]!["kind"]!, (string)thread["messages"]![0]!["sessionId"]!)));
      Assert.All(started.Zip(after), pair => Assert.True(JsonNode.DeepEquals(pair.First, pair.Second), $"The stored thread differs from the start's: {pair.Second}"));
   }

   [Theory]
   [InlineData("""{ "kind": "code", "side": "head", "path": "src/modified.txt", "startLine": 2, "endLine": 99 }""", "anchor.endLine")]
   [InlineData("""{ "kind": "file", "side": "head", "path": "src/nosuchfile.txt" }""", "anchor.path")]
   [InlineData("""{ "kind": "stopText", "stopId": "STOP", "field": "why", "text": "x" }""", "anchor.field")]
   public async Task AgentThread_start_checks_the_anchor_as_a_question_does(string anchor, string field)
   {
      using var response = await SendStartAsync($$"""{ "text": "Here.", "anchor": {{anchor.Replace("STOP", _fixture.InspectId, StringComparison.Ordinal)}} }""");

      var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
      Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound, $"Expected a refusal, got {(int)response.StatusCode}: {body}");
      Assert.Equal(field, (string)body["field"]!);
      Assert.Empty(await _fixture.ThreadsAsync());
   }

   [Fact]
   public async Task AgentThread_message_renders_its_ref_links_for_the_ui_as_a_reply_does()
   {
      var started = await StartAsync("""{ "text": "See [the digit](ref:head/src/modified.txt#L2-2).", "anchor": { "kind": "tour" } }""");
      var request = (new PreparedCall(On("read_state", _fixture.Tour.TourId), _fixture.UiCredential)).WithProseHtml();
      using var response = await _fixture.Tour.Harness.SendAsync(request);

      var tour = SuccessBody.Output(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
      var html = (string)tour["threads"]![0]!["messages"]![0]!["html"]!;
      Assert.Null(started["thread"]!["messages"]![0]!["html"]);
      Assert.Contains("data-ref-path=\"src/modified.txt\" data-ref-start-line=\"2\" data-ref-end-line=\"2\"", html, StringComparison.Ordinal);
   }

   [Theory]
   [InlineData("start")]
   [InlineData("message")]
   public async Task AgentThread_text_with_a_link_that_cannot_be_followed_is_refused(string action)
   {
      var text = "See [the line](ref:head/src/modified.txt#L99-99).";
      var started = action == "message" ? await StartAsync("""{ "text": "First.", "anchor": { "kind": "tour" } }""") : null;
      var before = await _fixture.ThreadsAsync();

      using var response = started is null
         ? await SendStartAsync($$"""{ "text": "{{text}}", "anchor": { "kind": "tour" } }""")
         : await SendAgentAsync(On("add_thread_message", _fixture.Tour.TourId, threadId: (string)started["thread"]!["id"]!), $$"""{ "text": "{{text}}" }""");

      var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
      Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"Expected 400, got {(int)response.StatusCode}: {body}");
      Assert.Equal("text", (string)body["field"]!);
      Assert.True(JsonNode.DeepEquals(before, await _fixture.ThreadsAsync()), "The refused write changed the threads.");
   }

   private async Task<JsonNode> StartAsync(string body)
   {
      using var response = await SendStartAsync(body);
      var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
      Assert.True(response.IsSuccessStatusCode, $"The start returned {(int)response.StatusCode}: {text}");
      return SuccessBody.Output(text);
   }

   private Task<HttpResponseMessage> SendStartAsync(string body) => SendAgentAsync(On("start_thread", _fixture.Tour.TourId), body);

   private async Task<HttpResponseMessage> SendAgentAsync(OperationCall call, string body) =>
      await _fixture.Tour.Harness.SendAsync((await _fixture.Tour.Harness.AgentAsync()).Request(_fixture.Tour.Harness, call), body);
}
