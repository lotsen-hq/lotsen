using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Git;
using Lotsen.Tours;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>
/// Covers <c>create_tour</c> with a dive origin: the dive takes its parent's target and is pinned at once to the parent's snapshot, its family's root comes from the parent, and the parent's
/// <c>read_state</c> and the tour list name the family.
/// </summary>
/// <remarks>
/// Each test starts with a parent, an orient tour of the test platform's repository pinned to the fixture's first commit, whose plan holds the overview <c>stops[0]</c> and the pending context
/// stop <c>stops[1]</c>.
/// </remarks>
[Trait("Category", "Pinning")]
public sealed class DivePinningTests : IAsyncLifetime
{
   private const string Plan = """
      {
        "chapters": [{ "id": "start", "title": "Start" }],
        "stops": [{ "title": "The shop", "kind": "overview", "chapter": "start" }, { "title": "The app", "kind": "context", "chapter": "start" }]
      }
      """;

   // A tour ID that no stored tour has.
   private const string Missing = "zzzzzzzzzzzzzzzz";

   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

   private readonly GitFixture _git = new();
   private readonly TestPlatform _platform = new();
   private readonly RecordingLoggerProvider _serverLog = new();
   private ServerHarness _harness = null!;
   private CommitId _first;
   private string _parentId = null!;
   private string[] _stops = [];
   private string? _pullRequestId;

   private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

   public static TheoryData<string> Refusals => [.. RefusalRows.Keys];

   // Each row changes the parent, or names another parent, and names the field and the reason of the refusal.
   private static Dictionary<string, (Func<DivePinningTests, Task<string>> Body, string Field, Func<DivePinningTests, string> Reason)> RefusalRows => new()
   {
      ["a parent that no tour has"] = (
         test => Task.FromResult(DiveBody(Missing)),
         "diveOrigin.parentTourId",
         _ => $"no tour has the ID {Missing}. Give the tourId of the repository tour that the dive starts from."),
      ["a pull request's tour as the parent"] = (
         async test => DiveBody(await test.PullRequestTourAsync()),
         "diveOrigin.parentTourId",
         test => $"tour {test._pullRequestId} tours a pull request, and only a repository tour has dives. Give the tourId of a repository tour."),
      ["a stop that the parent's plan lacks"] = (
         test => Task.FromResult(DiveBody(test._parentId, "no-such-stop")),
         "diveOrigin.stopId",
         test => $"tour {test._parentId} has no stop 'no-such-stop' in its plan. Give the stopId of the dive action's anchor, or omit it for a dive from the whole tour."),
      ["a parent that is still pinning"] = (
         test => Task.FromResult(DiveBody(test.Repinned(new TourPin.Pinning()))),
         "diveOrigin.parentTourId",
         test => $"tour {test._parentId} is not pinned to a commit yet, so the dive has no commit to show. Wait until read_state shows the tour's pin as snapshot, then call create_tour again."),
      ["a parent whose pin failed"] = (
         test => Task.FromResult(DiveBody(test.Repinned(new TourPin.Failed("Sign in, then try again.")))),
         "diveOrigin.parentTourId",
         test => $"tour {test._parentId} could not be pinned, so the dive has no commit to show. Pin the tour again with pin_tour, then call create_tour again."),
   };

   public async ValueTask InitializeAsync()
   {
      _first = _git.Commit("Base", ("src/app.txt", "one\ntwo\nthree\n"));
      _platform.Repository(_git.Url, _first);
      _harness = await ServerHarness.StartAsync(platform: _platform, configure: options => options with { LoggerProvider = _serverLog });
      _parentId = (string)(await _harness.AgentCallAsync("create_tour", RepositoryPinningTests.CreateBody(@ref: null)))["id"]!;
      await _harness.PinsIdleAsync().WaitAsync(Timeout, Cancellation);
      var planned = await _harness.AgentCallAsync(On("set_plan", _parentId), Plan);
      _stops = [.. planned["plan"]!["stops"]!.AsArray().Select(stop => (string)stop!["id"]!)];
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

   /// <summary>The platform's repository moves on to a later commit after the parent pinned, so a dive that resolved the ref again would show that commit.</summary>
   [Fact]
   public async Task Dive_from_a_stop_is_pinned_at_once_to_the_parents_commit_with_the_parents_target_without_resolving_the_ref_again()
   {
      _platform.Repository(_git.Url, _git.Commit("Main moves on", ("src/app.txt", "one\n")));
      var resolves = _platform.Resolves;
      var parent = await _harness.CallAsync(On("read_state", _parentId));

      var dive = await _harness.AgentCallAsync("create_tour", DiveBody(_parentId, _stops[1], words: "How the app starts."));
      await _harness.PinsIdleAsync().WaitAsync(Timeout, Cancellation);
      var read = await _harness.CallAsync(On("read_state", (string)dive["id"]!));

      Assert.True(JsonNode.DeepEquals(JsonNode.Parse($$"""{ "state": "snapshot", "head": "{{_first}}", "remoteUrl": "{{_git.Url.AbsoluteUri}}" }"""), dive["pin"]), dive["pin"]!.ToJsonString());
      Assert.True(JsonNode.DeepEquals(dive["pin"], read["pin"]), read["pin"]!.ToJsonString());
      Assert.True(JsonNode.DeepEquals(parent["target"], dive["target"]), dive["target"]!.ToJsonString());
      Assert.True(
         JsonNode.DeepEquals(JsonNode.Parse($$"""{ "parentId": "{{_parentId}}", "rootId": "{{_parentId}}", "stopId": "{{_stops[1]}}" }"""), dive["diveOrigin"]),
         dive["diveOrigin"]!.ToJsonString());
      Assert.Equal(("prepare", "How the app starts", "How the app starts.", "new"), ((string?)dive["purpose"], (string?)dive["title"], (string?)dive["words"], (string?)dive["familiarity"]));
      Assert.Equal(resolves, _platform.Resolves);
   }

   [Fact]
   public async Task Dive_of_a_dive_names_its_own_parent_and_keeps_the_family_root()
   {
      var first = (string)(await _harness.AgentCallAsync("create_tour", DiveBody(_parentId, words: "The app.")))["id"]!;

      var second = await _harness.AgentCallAsync("create_tour", DiveBody(first, words: "Its start."));

      Assert.True(
         JsonNode.DeepEquals(JsonNode.Parse($$"""{ "parentId": "{{first}}", "rootId": "{{_parentId}}" }"""), second["diveOrigin"]),
         second["diveOrigin"]!.ToJsonString());
   }

   [Theory]
   [MemberData(nameof(Refusals))]
   public async Task Dive_origin_is_refused_with_its_field_and_stores_no_tour(string row)
   {
      var (body, field, reason) = RefusalRows[row];
      var input = await body(this);
      var before = (await _harness.CallAsync("list_tours"))["tours"]!.AsArray().Count;

      using var response = await _harness.AgentSendJsonAsync("create_tour", input);

      var error = await ThreadCalls.ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal((field, $"{field}: {reason(this)}"), ((string)error["field"]!, (string)error["reason"]!));
      Assert.Equal(before, (await _harness.CallAsync("list_tours"))["tours"]!.AsArray().Count);
   }

   [Fact]
   public async Task Dive_origin_beside_a_repository_is_refused()
   {
      var input = JsonNode.Parse(DiveBody(_parentId))!.AsObject();
      input["repository"] = JsonNode.Parse(RepositoryPinningTests.CreateBody(@ref: null))!["repository"]!.DeepClone();

      using var response = await _harness.AgentSendJsonAsync("create_tour", input.ToJsonString());

      var error = await ThreadCalls.ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("diveOrigin: give diveOrigin alone, without prUrl or repository, because a dive tours its parent's repository.", (string)error["reason"]!);
   }

   /// <summary>The reviewer finished the first dive and opened nothing of the second, so the two show different reading states.</summary>
   [Fact]
   public async Task Parent_state_lists_its_dives_in_creation_order_with_the_stop_and_the_reading_state_of_each()
   {
      var fromStop = (string)(await _harness.AgentCallAsync("create_tour", DiveBody(_parentId, _stops[1])))["id"]!;
      var fromTour = (string)(await _harness.AgentCallAsync("create_tour", DiveBody(_parentId, words: "The tests.", purpose: "evaluate")))["id"]!;
      await _harness.CallAsync(On("record_reading_progress", fromStop), """{ "summary": true }""");

      var reviewer = await _harness.CallAsync(On("read_state", _parentId));
      var agent = await _harness.AgentCallAsync(On("read_state", _parentId));
      var dive = await _harness.CallAsync(On("read_state", fromStop));

      var expected = JsonNode.Parse($$"""
         [
           { "tourId": "{{fromStop}}", "title": "How the app starts", "purpose": "prepare", "state": "finished", "stopId": "{{_stops[1]}}" },
           { "tourId": "{{fromTour}}", "title": "How the app starts", "purpose": "evaluate", "state": "generating" }
         ]
         """);
      Assert.True(JsonNode.DeepEquals(expected, reviewer["dives"]), reviewer["dives"]?.ToJsonString());
      Assert.True(JsonNode.DeepEquals(expected, agent["dives"]), agent["dives"]?.ToJsonString());
      Assert.False(dive.AsObject().ContainsKey("dives"), dive.ToJsonString());
   }

   [Fact]
   public async Task Parent_state_lists_its_dives_after_a_server_restart()
   {
      var dive = (string)(await _harness.AgentCallAsync("create_tour", DiveBody(_parentId, _stops[1])))["id"]!;
      await _harness.StopServerAsync().WaitAsync(Timeout, Cancellation);

      await using var restarted = await ServerHarness.StartAsync(dataDirectory: _harness.DataDirectory, platform: _platform);
      var read = await restarted.CallAsync(On("read_state", _parentId));

      Assert.Equal([dive], read["dives"]!.AsArray().Select(entry => (string)entry!["tourId"]!));
   }

   /// <summary>A damaged file of another tour sits in the store, so a read that decoded every stored tour would log it each time.</summary>
   [Fact]
   public async Task Reads_of_a_repository_tour_log_a_damaged_tour_file_at_most_once_and_still_list_the_dives()
   {
      var damaged = TourId.New();
      await File.WriteAllTextAsync(Path.Combine(_harness.DataDirectory.ToursPath, $"{damaged}.json"), "{ not json", Cancellation);
      await _harness.CallAsync(On("read_state", _parentId));
      var dive = (string)(await _harness.AgentCallAsync("create_tour", DiveBody(_parentId, _stops[1])))["id"]!;

      var reads = new List<JsonNode>();
      for (var i = 0; i < 3; i++)
      {
         reads.Add(await _harness.CallAsync(On("read_state", _parentId)));
      }

      Assert.All(reads, read => Assert.Equal([dive], read["dives"]!.AsArray().Select(entry => (string)entry!["tourId"]!)));
      Assert.Equal(1, _serverLog.Lines.Count(line => line.Contains($"Tour {damaged} could not be read", StringComparison.Ordinal)));
   }

   [Fact]
   public async Task Tour_list_carries_the_origin_of_each_dive_and_none_for_a_tour_that_is_no_dive()
   {
      var dive = (string)(await _harness.AgentCallAsync("create_tour", DiveBody(_parentId, _stops[1])))["id"]!;

      var tours = (await _harness.CallAsync("list_tours"))["tours"]!.AsArray();

      var parent = tours.Single(tour => (string?)tour!["id"] == _parentId)!;
      var summary = tours.Single(tour => (string?)tour!["id"] == dive)!;
      Assert.False(parent.AsObject().ContainsKey("diveOrigin"), parent.ToJsonString());
      Assert.True(
         JsonNode.DeepEquals(JsonNode.Parse($$"""{ "parentId": "{{_parentId}}", "rootId": "{{_parentId}}", "stopId": "{{_stops[1]}}" }"""), summary["diveOrigin"]),
         summary.ToJsonString());
   }

   // The input of create_tour for a dive from the stop, or from the whole tour without one, at level new.
   private static string DiveBody(string parentId, string? stopId = null, string? words = null, string purpose = "prepare")
   {
      var origin = new JsonObject { ["parentTourId"] = parentId, ["purpose"] = purpose, ["title"] = "How the app starts" };
      if (stopId is not null)
      {
         origin["stopId"] = stopId;
      }

      if (words is not null)
      {
         origin["words"] = words;
      }

      return new JsonObject { ["diveOrigin"] = origin, ["familiarity"] = "new" }.ToJsonString();
   }

   // A pull request's tour whose pin fails at once, so no pin job waits for an answer.
   private async Task<string> PullRequestTourAsync()
   {
      _platform.Fail("The test platform has no such pull request.");
      _pullRequestId = (string)_harness.CreateUnboundTour(ThreadCalls.CreateBody)["id"]!;
      await _harness.PinsIdleAsync().WaitAsync(Timeout, Cancellation);
      return _pullRequestId;
   }

   // Stores the pin on the parent, as a pin in progress or a failed pin leaves it, and returns the parent's ID.
   private string Repinned(TourPin pin)
   {
      Assert.True(TourId.TryParse(_parentId, out var id), $"The tour ID {_parentId} does not parse.");
      _harness.ChangeStored(id, tour => tour with { Pin = pin });
      return _parentId;
   }
}
