using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Git;
using Lotsen.Sessions;
using Lotsen.Tours;
using static Lotsen.Tests.OperationCall;
using static Lotsen.Tests.ThreadCalls;

namespace Lotsen.Tests;

/// <summary>
/// Covers the dive tour action: the reviewer asks the bound session of a repository tour for a dive from a stop or from the whole tour, the session creates the dive with <c>create_tour</c> and its
/// dive origin as the action's <c>howToAct</c> says, and a pull request's tour has no dive, as <see cref="ActionRules.NotOffered"/> says.
/// </summary>
/// <remarks>Each test creates its own repository tour with its own session, pinned to a snapshot, whose plan holds the overview <c>stops[0]</c> and the pending context stop <c>stops[1]</c>.</remarks>
[Trait("Category", "TourActions")]
public sealed class DiveActionTests : IAsyncLifetime
{
   private const string Plan = """
      {
        "chapters": [{ "id": "start", "title": "Start" }],
        "stops": [{ "title": "The shop", "kind": "overview", "chapter": "start" }, { "title": "The app", "kind": "context", "chapter": "start" }]
      }
      """;

   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

   private readonly GitFixture _git = new();
   private ServerHarness _harness = null!;
   private ThreadCalls _calls = null!;

   public async ValueTask InitializeAsync()
   {
      var head = _git.Commit("Base", ("src/app.txt", "one\ntwo\nthree\n"));
      var platform = new TestPlatform();
      platform.Repository(_git.Url, head);
      platform.Fail("The test platform has no such pull request.");
      _harness = await ServerHarness.StartAsync(platform: platform);
      _calls = new ThreadCalls(_harness);
   }

   public async ValueTask DisposeAsync()
   {
      // A start that failed may leave no harness.
      if (_harness is not null)
      {
         await _harness.DisposeAsync();
      }

      _git.Dispose();
   }

   [Fact]
   public async Task Dive_from_a_stop_reaches_the_bound_session_which_creates_the_dive_as_its_howToAct_says()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var (id, stops) = await RepositoryTourAsync(session);
      await session.ProbeAsync();

      var thread = await _calls.CallAsync(await _calls.UiAsync(On("request_tour_action", id)), DiveBody(stops[1], "How the app starts."));
      var sent = await session.Stream.NextAsync(Timeout);
      var fetched = await _calls.FetchAsync(session, id, QuestionIdOf(thread));
      var dive = await _calls.CallAsync(
         _calls.Agent(session, "create_tour"),
         $$"""{ "diveOrigin": { "parentTourId": "{{id}}", "stopId": "{{stops[1]}}", "purpose": "prepare", "title": "How the app starts", "words": "How the app starts." }, "familiarity": "new" }""");
      var replied = await _calls.CallAsync(_calls.Agent(session, On("reply", id, questionId: QuestionIdOf(thread))), """{ "resolution": "done", "text": "Created the dive How the app starts." }""");

      Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{ "kind": "dive", "purpose": "prepare", "familiarity": "new" }"""), thread["action"]), $"action: {thread["action"]}");
      Assert.True(JsonNode.DeepEquals(JsonNode.Parse($$"""{ "kind": "stop", "stopId": "{{stops[1]}}" }"""), thread["anchor"]), $"anchor: {thread["anchor"]}");
      Assert.Equal((SessionEvents.Question, TourAction.DiveKind), (sent.Type, (string?)sent.Data["action"]));
      Assert.Contains("Carry out dive with create_tour and its diveOrigin", (string)fetched["howToAct"]!, StringComparison.Ordinal);
      Assert.Equal(session.Id, await _harness.BoundSessionOfAsync((string)dive["id"]!));
      Assert.Equal("done", (string)RequestOf(replied)["state"]!);
      Assert.Equal((string)dive["id"]!, (string)(await _harness.CallAsync(On("read_state", id)))["dives"]![0]!["tourId"]!);
   }

   [Fact]
   public async Task Dive_from_the_whole_tour_without_words_is_refused_and_stores_nothing()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var (id, _) = await RepositoryTourAsync(session);

      using var response = await _calls.SendAsync(await _calls.UiAsync(On("request_tour_action", id)), DiveBody());

      var error = await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal(("text", "text: a dive from the whole tour needs the reviewer's words: say what the dive should cover."), ((string)error["field"]!, (string)error["reason"]!));
      Assert.Empty(await _calls.ThreadsAsync(id));
   }

   [Fact]
   public async Task Dive_on_a_pull_requests_tour_is_refused_and_its_offers_hold_no_dive()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      await _harness.PinsIdleAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);

      using var response = await _calls.SendAsync(await _calls.UiAsync(On("request_tour_action", id)), DiveBody(text: "The cache."));
      using var read = await _calls.SendAsync(await _calls.UiAsync(On("read_state", id)));
      var offers = JsonNode.Parse(await read.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["controls"]!["offers"]!.AsObject();

      var error = await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal(("action", $"action: {ActionRules.NoDiveOnPullRequest}"), ((string)error["field"]!, (string)error["reason"]!));
      Assert.False(offers.ContainsKey("dive"), offers.ToJsonString());
      Assert.Empty(await _calls.ThreadsAsync(id));
   }

   // The body of a dive request of a prepare tour at level new, from the stop or from the whole tour, with the reviewer's words when given.
   private static string DiveBody(string? stopId = null, string? text = null)
   {
      var action = new JsonObject { ["kind"] = TourAction.DiveKind, ["purpose"] = "prepare", ["familiarity"] = "new" };
      if (stopId is not null)
      {
         action["stopId"] = stopId;
      }

      var body = new JsonObject { ["action"] = action };
      if (text is not null)
      {
         body["text"] = text;
      }

      return body.ToJsonString();
   }

   // A repository tour created by the session, pinned to the snapshot, with its plan set, and the IDs of its stops.
   private async Task<(string Id, string[] Stops)> RepositoryTourAsync(AgentSessionConnection session)
   {
      var id = (string)(await _calls.CallAsync(_calls.Agent(session, "create_tour"), RepositoryPinningTests.CreateBody(@ref: null)))["id"]!;
      await _harness.PinsIdleAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);
      var planned = await _calls.CallAsync(_calls.Agent(session, On("set_plan", id)), Plan);
      return (id, [.. planned["plan"]!["stops"]!.AsArray().Select(stop => (string)stop!["id"]!)]);
   }
}
