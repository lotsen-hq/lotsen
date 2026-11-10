using System.Text.Json.Nodes;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>The requests that the answer tests share: a pinned tour with a question stop and its checks, the reviewer's answer writes, and the send and the fetch of a batch.</summary>
internal sealed class AnswerCalls
{
   public const string Plan = """
      {
        "chapters": [{ "id": "code", "title": "Code" }],
        "stops": [{ "title": "Keep the rename?", "kind": "question", "chapter": "code" }, { "title": "Later", "kind": "inspect", "chapter": "code" }]
      }
      """;

   public const string Checks = """
      [
        { "question": "Keep the digit?", "suggestions": [{ "text": "Keep the digit.", "type": "decision" }, { "text": "Sorting may break.", "type": "concern" }] },
        { "question": "Is line one still right?" }
      ]
      """;

   public const string Passage = """{ "side": "head", "path": "src/modified.txt", "startLine": 2, "endLine": 2, "role": "primary" }""";

   private readonly ServerHarness _harness;
   private readonly ThreadCalls _calls;

   public AnswerCalls(ServerHarness harness)
   {
      _harness = harness;
      _calls = new ThreadCalls(harness);
   }

   public ThreadCalls Calls => _calls;

   /// <summary>Creates a pinned tour bound to the session, with a question stop whose content holds <see cref="Checks"/>.</summary>
   public async Task<AnsweredTour> TourAsync(AgentSessionConnection session)
   {
      var created = await _calls.CallAsync(_calls.Agent(session, "create_tour"), ThreadCalls.CreateBody);
      await _harness.PinsIdleAsync();
      var tourId = (string)created["id"]!;
      var tour = await _calls.CallAsync(_calls.Agent(session, On("set_plan", tourId)), Plan);
      var stopId = (string)tour["plan"]!["stops"]![0]!["id"]!;
      var stop = await PutStopAsync(session, tourId, stopId, Checks);
      return new AnsweredTour(tourId, stopId, [.. stop["content"]!["checks"]!.AsArray().Select(check => (string)check!["id"]!)]);
   }

   public async Task<JsonNode> PutStopAsync(AgentSessionConnection session, string tourId, string stopId, string checks) =>
      await _calls.CallAsync(_calls.Agent(session, On("put_stop", tourId, stopId: stopId)), $$"""{ "headline": "A headline.", "passages": [{{Passage}}], "checks": {{checks}} }""");

   /// <summary>Answers the check in the reviewer's own words and returns the response.</summary>
   public async Task<JsonNode> AnswerAsync(AnsweredTour tour, int check, string text) =>
      await _calls.CallAsync(await _calls.UiAsync(On("answer_check", tour.Id, stopId: tour.StopId, checkId: tour.CheckIds[check])), $$"""{ "text": "{{text}}" }""");

   public async Task<JsonNode> SetTypeAsync(AnsweredTour tour, int check, string type) =>
      await _calls.CallAsync(await _calls.UiAsync(On("set_answer_type", tour.Id, stopId: tour.StopId, checkId: tour.CheckIds[check])), $$"""{ "type": "{{type}}" }""");

   public async Task<JsonNode> SendAsync(string tourId) => await _calls.CallAsync(await _calls.UiAsync(On("send_answers", tourId)));

   public async Task<JsonNode> FetchAsync(AgentSessionConnection session, string tourId, string batchId) => await _calls.CallAsync(_calls.Agent(session, On("fetch_answers", tourId, batchId: batchId)));

   /// <summary>Returns the tour's answers as the CLI reads them.</summary>
   public async Task<JsonArray> AnswersAsync(string tourId) => (await _harness.CallAsync(On("read_state", tourId)))["answers"]!.AsArray();

   public static string BatchIdOf(JsonNode sent) => (string)sent["batchId"]!;
}

/// <summary>A tour that <see cref="AnswerCalls.TourAsync"/> created: its ID, its question stop's ID and the IDs of that stop's checks, in content order.</summary>
internal sealed record AnsweredTour(string Id, string StopId, string[] CheckIds);
