using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Git;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>A repository tour that every test of <see cref="RepositoryStopTests"/> shares, pinned to a snapshot of a fixture repository's one commit. Each test sets its own plan first.</summary>
/// <remarks>The snapshot's commit holds <c>src/app.txt</c> and <c>README.md</c>, which its parent commit already held unchanged, so no passage at the snapshot touches a diff.</remarks>
public sealed class RepositoryTourFixture : IAsyncLifetime
{
   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

   private readonly GitFixture _git = new();

   internal ServerHarness Harness { get; private set; } = null!;

   internal string TourId { get; private set; } = "";

   internal CommitId Head { get; private set; }

   public async ValueTask InitializeAsync()
   {
      _git.Commit("Base", ("src/app.txt", "one\ntwo\nthree\n"), ("README.md", "# Shop\n"));
      Head = _git.Commit("Docs", ("docs/guide.md", "Read me.\n"));
      var platform = new TestPlatform();
      platform.Repository(_git.Url, Head);
      Harness = await ServerHarness.StartAsync(platform: platform);
      TourId = (string)(await Harness.AgentCallAsync("create_tour", RepositoryPinningTests.CreateBody(@ref: null)))["id"]!;
      await Harness.PinsIdleAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);
      var pin = (await Harness.CallAsync(On("read_state", TourId)))["pin"]!;
      Assert.True((string?)pin["state"] == "snapshot", $"The fixture's tour has no snapshot: {pin.ToJsonString()}");
   }

   public async ValueTask DisposeAsync()
   {
      // A fixture whose start failed may have no harness.
      if (Harness is not null)
      {
         await Harness.DisposeAsync();
      }

      _git.Dispose();
   }
}

/// <summary>
/// Covers <c>set_plan</c>, <c>put_stop</c> and code anchors on a repository tour: the plan's rules hold without file marks, passages and <c>ref:</c> links read at the snapshot's one commit with
/// no diff rule, and only context and inspect stops take gotchas and tryIt.
/// </summary>
[Trait("Category", "Validation")]
public sealed class RepositoryStopTests : IClassFixture<RepositoryTourFixture>
{
   // The stops by index: 0 overview, 1 context, 2 inspect, 3 risk, 4 question.
   private const string Plan = """
      {
        "chapters": [{ "id": "start", "title": "Start" }, { "id": "code", "title": "Code" }],
        "stops": [
          { "title": "The shop", "kind": "overview", "chapter": "start" },
          { "title": "Background", "kind": "context", "chapter": "start" },
          { "title": "The app", "kind": "inspect", "chapter": "code", "anchorHint": { "path": "src/app.txt", "line": 2 } },
          { "title": "A risk", "kind": "risk", "chapter": "code" },
          { "title": "A question", "kind": "question", "chapter": "code" }
        ]
      }
      """;

   private const int Context = 1;
   private const int Inspect = 2;

   private readonly RepositoryTourFixture _tour;

   public RepositoryStopTests(RepositoryTourFixture fixture)
   {
      _tour = fixture;
   }

   [Fact]
   public async Task Set_plan_stores_the_plan_and_keeps_the_overview_first_rule()
   {
      var ids = await SetPlanAsync();
      var before = await ReadStateAsync();

      using var response = await SendPlanAsync("""
         {
           "chapters": [{ "id": "start", "title": "Start" }],
           "stops": [{ "title": "Background", "kind": "context", "chapter": "start" }, { "title": "The shop", "kind": "overview", "chapter": "start" }]
         }
         """);

      Assert.Equal(5, ids.Length);
      var error = await ReadErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.StartsWith("stops[1]", (string)error["field"]!, StringComparison.Ordinal);
      await AssertUnchangedAsync(before);
   }

   [Fact]
   public async Task File_marks_are_refused_and_nothing_is_stored()
   {
      await SetPlanAsync();
      var before = await ReadStateAsync();

      using var response = await SendPlanAsync("""
         {
           "chapters": [{ "id": "start", "title": "Start" }],
           "stops": [{ "title": "The shop", "kind": "overview", "chapter": "start" }],
           "fileMarks": [{ "path": "README.md", "mark": "generated", "reason": "A tool writes it." }]
         }
         """);

      var error = await ReadErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("fileMarks", (string)error["field"]!);
      Assert.Equal("fileMarks: a repository tour has no changed files to mark. Leave fileMarks out.", (string)error["reason"]!);
      await AssertUnchangedAsync(before);
   }

   [Theory]
   [InlineData(Inspect, null)]
   [InlineData(3, """ "scenario": "Two writers race." """)]
   [InlineData(4, """ "checks": [{ "question": "Keep it?" }] """)]
   public async Task Primary_passage_on_code_that_no_diff_changes_is_stored_with_the_text_at_the_snapshot(int stop, string? extra)
   {
      var ids = await SetPlanAsync();

      var stored = await CallPutAsync(ids[stop], Body(Passage("head", "src/app.txt", 2, 3, expected: "two"), extra: extra));

      var passage = Assert.Single(stored["content"]!["passages"]!.AsArray())!;
      Assert.Equal(("head", _tour.Head.Value, "two\nthree"), ((string)passage["side"]!, (string)passage["commit"]!, (string)passage["text"]!));
   }

   [Theory]
   [InlineData("base", "head", "passages[1].side", "passages[1].side: a repository tour has only the head side, the one commit of its snapshot. Point the passage at head.")]
   [InlineData("head", "base", "passages[0].side", "passages[0].side: a repository tour has only the head side, the one commit of its snapshot. Point the passage at head.")]
   public async Task Passage_at_the_base_is_refused_on_its_side_and_nothing_is_stored(string related, string primary, string field, string reason)
   {
      var ids = await SetPlanAsync();
      var before = await ReadStateAsync();

      using var response = await PutAsync(ids[Inspect], Body(Passage(primary, "src/app.txt", 1, 1) + ", " + Passage(related, "README.md", 1, 1, role: "related")));

      var error = await ReadErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal((field, reason), ((string)error["field"]!, (string)error["reason"]!));
      await AssertUnchangedAsync(before);
   }

   [Fact]
   public async Task Ref_link_at_the_base_is_refused_on_its_field_and_a_link_at_the_head_is_stored()
   {
      var ids = await SetPlanAsync();
      var before = await ReadStateAsync();

      using var refused = await PutAsync(ids[Inspect], Body(Passage("head", "src/app.txt", 1, 1), extra: """ "how": "See [the readme](ref:base/README.md#L1-1)." """));

      var error = await ReadErrorAsync(refused, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("how", (string)error["field"]!);
      Assert.Equal(
         "how: the link 'ref:base/README.md#L1-1' points at the base, but a repository tour has only the head side, the one commit of its snapshot. Link to ref:head instead.",
         (string)error["reason"]!);
      await AssertUnchangedAsync(before);
      var stored = await CallPutAsync(ids[Inspect], Body(Passage("head", "src/app.txt", 1, 1), extra: """ "how": "See [the readme](ref:head/README.md#L1-1)." """));
      Assert.Equal("See [the readme](ref:head/README.md#L1-1).", (string)stored["content"]!["how"]!);
   }

   [Theory]
   [InlineData(Context)]
   [InlineData(Inspect)]
   public async Task Gotchas_and_try_it_are_stored_with_each_version_and_read_back(int stop)
   {
      var ids = await SetPlanAsync();
      await CallPutAsync(ids[stop], Body(Passage("head", "src/app.txt", 1, 1), extra: """ "gotchas": "Line *one* is cached.", "tryIt": "Run the app." """));

      var second = await CallPutAsync(ids[stop], Body(Passage("head", "src/app.txt", 1, 1), extra: """ "gotchas": "Line one is **never** cached." """));

      Assert.Equal((2, "Line one is **never** cached.", null), ((int)second["version"]!, (string?)second["content"]!["gotchas"], (string?)second["content"]!["tryIt"]));
      var agentView = (await _tour.Harness.AgentCallAsync(On("read_state", _tour.TourId)))["plan"]!["stops"]![stop]!;
      Assert.True(JsonNode.DeepEquals(second, agentView), agentView.ToJsonString());
      var reviewerView = (await ReadStateAsync())["plan"]!["stops"]![stop]!;
      var earlier = reviewerView["earlier"]![0]!["content"]!;
      Assert.Equal(("Line *one* is cached.", "Run the app."), ((string?)earlier["gotchas"], (string?)earlier["tryIt"]));
   }

   [Theory]
   [InlineData(0, null, "gotchas", "an overview stop takes no gotchas")]
   [InlineData(3, """ "scenario": "Two writers race." """, "tryIt", "a risk stop takes no tryIt")]
   [InlineData(4, """ "checks": [{ "question": "Keep it?" }] """, "gotchas", "a question stop takes no gotchas")]
   public async Task Gotchas_or_try_it_on_another_kind_is_refused_and_nothing_is_stored(int stop, string? extra, string field, string problem)
   {
      var ids = await SetPlanAsync();
      var before = await ReadStateAsync();
      var fields = $"\"{field}\": \"Mind the cache.\"" + (extra is null ? "" : ", " + extra);

      using var response = await PutAsync(ids[stop], Body(Passage("head", "src/app.txt", 1, 1), extra: fields));

      var error = await ReadErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal(field, (string)error["field"]!);
      Assert.StartsWith($"{field}: {problem}", (string)error["reason"]!, StringComparison.Ordinal);
      await AssertUnchangedAsync(before);
   }

   [Fact]
   public async Task Code_anchor_at_the_head_reads_the_snapshot_and_one_at_the_base_is_refused()
   {
      var started = await _tour.Harness.AgentCallAsync(
         On("start_thread", _tour.TourId),
         """{ "text": "See [this](ref:head/src/app.txt#L1-1).", "anchor": { "kind": "code", "side": "head", "path": "src/app.txt", "startLine": 2, "endLine": 3 } }""");
      using var refused = await _tour.Harness.AgentSendJsonAsync(
         On("start_thread", _tour.TourId),
         """{ "text": "Here.", "anchor": { "kind": "code", "side": "base", "path": "src/app.txt", "startLine": 2, "endLine": 3 } }""");

      var anchor = started["thread"]!["anchor"]!;
      Assert.Equal(("head", _tour.Head.Value, "two\nthree"), ((string)anchor["side"]!, (string)anchor["commit"]!, (string)anchor["text"]!));
      var error = await ReadErrorAsync(refused, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("anchor.side", (string)error["field"]!);
   }

   private static string Passage(string side, string path, int start, int end, string role = "primary", string? expected = null)
   {
      var passage = new JsonObject { ["side"] = side, ["path"] = path, ["startLine"] = start, ["endLine"] = end, ["role"] = role };
      if (expected is not null)
      {
         passage["expectedFirstLine"] = expected;
      }

      return passage.ToJsonString();
   }

   private static string Body(string passages, string? extra = null) => $$"""{ "headline": "A headline.", "passages": [{{passages}}]{{(extra is null ? "" : ", " + extra)}} }""";

   private async Task<string[]> SetPlanAsync()
   {
      var tour = await _tour.Harness.AgentCallAsync(On("set_plan", _tour.TourId), Plan);
      return [.. tour["plan"]!["stops"]!.AsArray().Select(stop => (string)stop!["id"]!)];
   }

   private Task<HttpResponseMessage> SendPlanAsync(string plan) => _tour.Harness.AgentSendJsonAsync(On("set_plan", _tour.TourId), plan);

   private Task<JsonNode> ReadStateAsync() => _tour.Harness.CallAsync(On("read_state", _tour.TourId));

   private Task<HttpResponseMessage> PutAsync(string stopId, string body) => _tour.Harness.AgentSendJsonAsync(On("put_stop", _tour.TourId, stopId: stopId), body);

   private Task<JsonNode> CallPutAsync(string stopId, string body) => _tour.Harness.AgentCallAsync(On("put_stop", _tour.TourId, stopId: stopId), body);

   private async Task AssertUnchangedAsync(JsonNode before)
   {
      var after = await ReadStateAsync();
      Assert.True(JsonNode.DeepEquals(before, after), $"The tour changed: {after}");
   }

   private static async Task<JsonNode> ReadErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
   {
      var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
      Assert.True(status == response.StatusCode, $"Expected {(int)status}, got {(int)response.StatusCode}: {body}");
      var error = JsonNode.Parse(body)!;
      Assert.Equal(code, (string)error["code"]!);
      return error;
   }
}
