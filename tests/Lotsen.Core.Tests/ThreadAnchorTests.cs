using System.Text;
using System.Text.Json.Nodes;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>Covers the anchors of threads: a thread at each anchor kind, open or resolved, reads back from the tour's file after the server restarts as the ask stored it.</summary>
[Trait("Category", "Threads")]
public sealed class ThreadAnchorTests : IAsyncLifetime
{
   private readonly QuestionTourFixture _fixture = new();

   public async ValueTask InitializeAsync() => await _fixture.InitializeAsync();

   public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

   /// <summary>Every question is canceled before the restart, so the restart's sweep of pending requests leaves the threads as they were.</summary>
   [Fact]
   public async Task Anchor_of_each_kind_round_trips_through_the_store_with_its_thread_state()
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
      var asked = new List<JsonNode>();
      foreach (var (anchor, index) in anchors.Select((anchor, index) => (anchor, index)))
      {
         var thread = await _fixture.AskThreadAsync($$"""{ "text": "Question {{index}}", "anchor": {{anchor}} }""");
         await UiSendAsync(On("cancel_question", _fixture.Tour.TourId, questionId: (string)thread["messages"]![0]!["id"]!), "{}");
         if (index % 2 == 0)
         {
            await UiSendAsync(On("set_thread_state", _fixture.Tour.TourId, threadId: (string)thread["id"]!), """{ "state": "resolved" }""");
         }

         asked.Add(thread);
      }

      var before = await _fixture.ThreadsAsync();

      await _fixture.Tour.RestartServerAsync();

      var after = (await _fixture.Tour.Harness.CallAsync(On("read_state", _fixture.Tour.TourId)))["threads"]!.AsArray();
      Assert.True(JsonNode.DeepEquals(before, after), $"The threads differ after the restart: {after}");
      Assert.Equal(["code", "code", "stopText", "file", "stop", "tour"], after.Select(thread => (string)thread!["anchor"]!["kind"]!));
      Assert.Equal(["base", "head"], after.Take(2).Select(thread => (string)thread!["anchor"]!["side"]!));
      Assert.Equal(["two\nthree", "2"], after.Take(2).Select(thread => (string)thread!["anchor"]!["text"]!));
      Assert.Equal([_fixture.Tour.MergeBase.Value, _fixture.Tour.Head.Value, _fixture.Tour.Head.Value], after.Where(thread => thread!["anchor"]!["commit"] is not null).Select(thread => (string)thread!["anchor"]!["commit"]!));
      Assert.Equal(["resolved", "open", "resolved", "open", "resolved", "open"], after.Select(thread => (string)thread!["state"]!));
      Assert.All(asked.Zip(after), pair => Assert.True(JsonNode.DeepEquals(pair.First["anchor"], pair.Second!["anchor"]), $"The anchor differs from the ask's: {pair.Second!["anchor"]}"));
   }

   [Fact]
   public async Task Stop_text_anchor_on_a_checks_question_and_recommendation_round_trips_through_the_store()
   {
      var question = await _fixture.AskThreadAsync($$"""{ "text": "Why?", "anchor": { "kind": "stopText", "stopId": "{{_fixture.InspectId}}", "field": "checks[{{_fixture.CheckId}}].question", "text": "a digit" } }""");
      var recommendation = await _fixture.AskThreadAsync($$"""{ "text": "Sure?", "anchor": { "kind": "stopText", "stopId": "{{_fixture.InspectId}}", "field": "checks[{{_fixture.CheckId}}].recommendation", "text": "sorts first" } }""");
      foreach (var thread in new[] { question, recommendation })
      {
         await UiSendAsync(On("cancel_question", _fixture.Tour.TourId, questionId: (string)thread["messages"]![0]!["id"]!), "{}");
      }

      var before = await _fixture.ThreadsAsync();

      await _fixture.Tour.RestartServerAsync();

      var after = (await _fixture.Tour.Harness.CallAsync(On("read_state", _fixture.Tour.TourId)))["threads"]!.AsArray();
      Assert.True(JsonNode.DeepEquals(before, after), $"The threads differ after the restart: {after}");
      Assert.Equal([$"checks[{_fixture.CheckId}].question", $"checks[{_fixture.CheckId}].recommendation"], after.Select(thread => (string)thread!["anchor"]!["field"]!));
   }

   private async Task UiSendAsync(OperationCall call, string body)
   {
      using var response = await _fixture.Tour.Harness.SendAsync(new PreparedCall(call, _fixture.UiCredential), body);
      var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
      Assert.True(response.IsSuccessStatusCode, $"{call.Name} returned {(int)response.StatusCode}: {text}");
   }
}
