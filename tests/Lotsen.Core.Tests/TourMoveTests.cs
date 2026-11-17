using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Git;
using Lotsen.Operations;
using Lotsen.Pinning;
using Lotsen.Platforms;
using Lotsen.Server;
using Lotsen.Sessions;
using Lotsen.Tours;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static Lotsen.Tests.OperationCall;
using static Lotsen.Tests.ThreadCalls;

namespace Lotsen.Tests;

/// <summary>
/// Covers <c>check_move</c> and <c>move_tour</c>: the reviewer checks a repository tour for a move to the latest commit of its ref, and moves the tour with the dives that the reviewer ticks,
/// which re-pins each moved tour to a snapshot of that commit, marks its stale stops and reports the move to its bound session in a decided thread with a tourMoved decision.
/// </summary>
/// <remarks>
/// The parent tours the default branch <c>main</c> at <see cref="_first"/>, with the stop <c>stays</c> on line 2 of <c>src/app.txt</c> and the stop <c>changes</c> on its line 5. Its dive
/// <c>first</c> has a stop on line 2 of <c>src/lib.txt</c>, its dive <c>second</c> has no plan, and <c>first</c> has the dive <c>deep</c>. <see cref="PushAsync"/> moves <c>main</c> to a commit
/// that inserts a line at the top of <c>src/app.txt</c>, changes its line 5 and changes line 2 of <c>src/lib.txt</c>.
/// </remarks>
[Trait("Category", "Pinning")]
public sealed class TourMoveTests : IAsyncLifetime
{
   private const string Plan = """
      {
        "chapters": [{ "id": "code", "title": "Code" }],
        "stops": [{ "title": "Stays", "kind": "context", "chapter": "code" }, { "title": "Changes", "kind": "context", "chapter": "code" }]
      }
      """;

   private const string DivePlan = """{ "chapters": [{ "id": "code", "title": "Code" }], "stops": [{ "title": "The library", "kind": "context", "chapter": "code" }] }""";

   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

   private readonly GitFixture _git = new();
   private readonly TestPlatform _platform = new();
   private ServerHarness _harness = null!;
   private CommitId _first;
   private string _parent = null!;
   private string _stays = null!;
   private string _changes = null!;
   private string _firstDive = null!;
   private string _diveStop = null!;
   private string _secondDive = null!;
   private string _deepDive = null!;

   private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

   private ThreadCalls Calls => new(_harness);

   public async ValueTask InitializeAsync()
   {
      _first = _git.Commit("First", ("src/app.txt", "one\ntwo\nthree\nfour\nfive\nsix\n"), ("src/lib.txt", "a\nb\nc\n"));
      _platform.Repository(_git.Url, _first, tags: new Dictionary<string, CommitId> { ["v1"] = _first });
      _harness = await ServerHarness.StartAsync(platform: _platform);
      _parent = (string)(await _harness.AgentCallAsync("create_tour", RepositoryPinningTests.CreateBody(@ref: null)))["id"]!;
      await _harness.PinsIdleAsync().WaitAsync(Timeout, Cancellation);
      var stops = (await _harness.AgentCallAsync(On("set_plan", _parent), Plan))["plan"]!["stops"]!.AsArray().Select(stop => (string)stop!["id"]!).ToList();
      (_stays, _changes) = (stops[0], stops[1]);
      await PutStopAsync(_parent, _stays, "src/app.txt", 2);
      await PutStopAsync(_parent, _changes, "src/app.txt", 5);
      _firstDive = await DiveAsync(_parent, "The library");
      _diveStop = (string)(await _harness.AgentCallAsync(On("set_plan", _firstDive), DivePlan))["plan"]!["stops"]![0]!["id"]!;
      await PutStopAsync(_firstDive, _diveStop, "src/lib.txt", 2);
      _secondDive = await DiveAsync(_parent, "The tests");
      _deepDive = await DiveAsync(_firstDive, "The parser");
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
   public async Task Check_move_returns_the_current_and_the_latest_commit_and_each_dive_of_the_subtree_with_its_parent()
   {
      var later = await PushAsync();
      await TickDoneAsync(_secondDive);

      var checkedMove = await CheckAsync(_parent);

      Assert.Equal(("available", _first.Value, later.Value), ((string)checkedMove["move"]!, (string)checkedMove["current"]!, (string)checkedMove["latest"]!));
      var pin = $$"""{ "state": "snapshot", "head": "{{_first}}", "remoteUrl": "{{_git.Url.AbsoluteUri}}" }""";
      var expected = JsonNode.Parse($$"""
         [
           { "tourId": "{{_firstDive}}", "parentId": "{{_parent}}", "title": "The library", "state": "ready", "pin": {{pin}} },
           { "tourId": "{{_secondDive}}", "parentId": "{{_parent}}", "title": "The tests", "state": "finished", "pin": {{pin}} },
           { "tourId": "{{_deepDive}}", "parentId": "{{_firstDive}}", "title": "The parser", "state": "generating", "pin": {{pin}} }
         ]
         """);
      Assert.True(JsonNode.DeepEquals(expected, checkedMove["dives"]), checkedMove["dives"]?.ToJsonString());
   }

   [Fact]
   public async Task Check_move_of_a_tour_at_the_latest_commit_offers_no_move_and_lists_no_dives()
   {
      var checkedMove = await CheckAsync(_parent);

      Assert.Equal(("atLatest", _first.Value, _first.Value), ((string)checkedMove["move"]!, (string)checkedMove["current"]!, (string)checkedMove["latest"]!));
      Assert.False(checkedMove.AsObject().ContainsKey("dives"), checkedMove.ToJsonString());
   }

   /// <summary>The ref names the fixture's first commit, as a tag or as its commit ID, while <c>main</c> has moved on, so only the ref's kind keeps the tour from a move.</summary>
   [Theory]
   [InlineData("v1", "tag")]
   [InlineData(null, "commit")]
   public async Task Check_move_of_a_tour_at_a_tag_or_a_commit_offers_no_move(string? @ref, string move)
   {
      var tour = await CreatePinnedAsync(@ref ?? _first.Value);
      await PushAsync();

      var checkedMove = await CheckAsync(tour);

      Assert.Equal((move, _first.Value, _first.Value), ((string)checkedMove["move"]!, (string)checkedMove["current"]!, (string)checkedMove["latest"]!));
   }

   [Fact]
   public async Task Move_with_one_dive_ticked_moves_the_parent_and_that_dive_only_and_marks_their_changed_stops_stale()
   {
      var later = await PushAsync();

      var moved = await MoveAsync(_parent, later, _firstDive);

      var parent = await ReadAsync(_parent);
      var expectedPin = JsonNode.Parse($$"""{ "state": "snapshot", "head": "{{later}}", "remoteUrl": "{{_git.Url.AbsoluteUri}}", "move": 1 }""");
      Assert.True(JsonNode.DeepEquals(expectedPin, parent["pin"]), parent["pin"]!.ToJsonString());
      Assert.True(JsonNode.DeepEquals(expectedPin, moved["pin"]), moved["pin"]!.ToJsonString());
      Assert.Equal([_changes], Ids(moved["staleStops"]));
      Assert.Equal((later.Value, 3, "two"), PassageOf(StopOf(parent, _stays)));
      Assert.Null(StopOf(parent, _stays)["staleOn"]);
      Assert.Equal((_first.Value, 5, "five"), PassageOf(StopOf(parent, _changes)));
      Assert.Equal(1, (int)StopOf(parent, _changes)["staleOn"]!);

      var dive = Assert.Single(moved["dives"]!.AsArray())!;
      Assert.Equal(_firstDive, (string)dive["tourId"]!);
      Assert.Equal([_diveStop], Ids(dive["staleStops"]));
      Assert.Equal(later.Value, (string)(await ReadAsync(_firstDive))["pin"]!["head"]!);
      Assert.Equal(1, (int)StopOf(await ReadAsync(_firstDive), _diveStop)["staleOn"]!);
      Assert.Equal(_first.Value, (string)(await ReadAsync(_secondDive))["pin"]!["head"]!);
      Assert.Equal(_first.Value, (string)(await ReadAsync(_deepDive))["pin"]!["head"]!);
   }

   [Fact]
   public async Task Move_reports_a_tour_moved_decision_whose_rule_speaks_of_the_commit_to_each_moved_tour_with_a_stale_stop()
   {
      var later = await PushAsync();

      var moved = await MoveAsync(_parent, later, _firstDive);

      var thread = moved["thread"]!;
      Assert.True(
         JsonNode.DeepEquals(JsonNode.Parse($$"""{ "kind": "tourMoved", "move": 1, "commit": "{{later}}", "stopIds": ["{{_changes}}"] }"""), thread["decision"]),
         thread["decision"]!.ToJsonString());
      var fetched = await Calls.FetchAsync(await _harness.AgentAsync(), _parent, QuestionIdOf(thread));
      Assert.StartsWith("The decision \"tourMoved\" says that the reviewer moved the tour to the latest commit of its ref", (string)fetched["howToAct"]!, StringComparison.Ordinal);
      var diveThread = moved["dives"]![0]!["thread"]!;
      Assert.Equal((string?)"tourMoved", (string?)diveThread["decision"]!["kind"]);
      Assert.Equal([_diveStop], Ids(diveThread["decision"]!["stopIds"]));
   }

   /// <summary>The second dive has no stops, so its move leaves none stale, sends no decision and needs no session.</summary>
   [Fact]
   public async Task Move_of_a_dive_alone_that_leaves_no_stop_stale_needs_no_session_and_sends_no_decision()
   {
      var later = await PushAsync();
      await _harness.DropSessionAsync(await _harness.AgentAsync());

      var moved = await MoveAsync(_secondDive, later);

      Assert.Null(moved["thread"]);
      Assert.Empty(moved["staleStops"]!.AsArray());
      Assert.False(moved.AsObject().ContainsKey("dives"), moved.ToJsonString());
      Assert.Equal(later.Value, (string)(await ReadAsync(_secondDive))["pin"]!["head"]!);
      Assert.Equal(_first.Value, (string)(await ReadAsync(_parent))["pin"]!["head"]!);
   }

   [Fact]
   public async Task Move_that_leaves_a_stop_stale_on_a_tour_without_a_session_is_refused_and_stores_nothing()
   {
      var later = await PushAsync();
      await _harness.DropSessionAsync(await _harness.AgentAsync());

      await AssertRefusedAsync(_parent, MoveBody(later, _firstDive), HttpStatusCode.Conflict, "noSessionAttached", "the move to the latest commit");
   }

   [Fact]
   public async Task Move_of_a_dive_whose_parent_stays_is_refused_and_stores_nothing()
   {
      var later = await PushAsync();

      var error = await AssertRefusedAsync(_parent, MoveBody(later, _deepDive), HttpStatusCode.BadRequest, "invalidInput", "diveIds[0]");

      Assert.Equal(
         $"diveIds[0]: dive {_deepDive} dives from tour {_firstDive}, which stays behind at its commit. Tick tour {_firstDive} too, or leave the dive out.",
         (string)error["reason"]!);
   }

   [Fact]
   public async Task Move_of_a_tour_that_is_not_a_dive_of_the_moved_tour_is_refused_and_stores_nothing()
   {
      var later = await PushAsync();

      var error = await AssertRefusedAsync(_firstDive, MoveBody(later, _secondDive), HttpStatusCode.BadRequest, "invalidInput", "diveIds[0]");

      Assert.Equal($"diveIds[0]: tour {_secondDive} is not a dive below tour {_firstDive}. Give only the tourIds of the dives that check_move listed.", (string)error["reason"]!);
   }

   [Fact]
   public async Task Move_after_the_branch_moved_on_again_is_refused_and_stores_nothing()
   {
      var later = await PushAsync();
      var latest = _git.Commit("Latest", ("src/other.txt", "new\n"));
      _platform.Repository(_git.Url, latest);

      var error = await AssertRefusedAsync(_parent, MoveBody(later), HttpStatusCode.Conflict, "moveNotAvailable", "commit");

      Assert.Equal($"commit: the ref moved on to commit {latest} since the check showed commit {later}, so the move stored nothing. Check for a move again, then move.", (string)error["reason"]!);
   }

   [Fact]
   public async Task Move_of_a_tour_at_a_tag_is_refused()
   {
      var tour = await CreatePinnedAsync("v1");
      var later = await PushAsync();

      var error = await AssertRefusedAsync(tour, MoveBody(later), HttpStatusCode.Conflict, "moveNotAvailable", "tourId");

      Assert.Equal("tourId: the tour shows the tag v1, which names one commit for good, so the tour has no move.", (string)error["reason"]!);
   }

   [Fact]
   [Trait("Category", "TourActions")]
   public async Task Move_while_the_decision_of_an_earlier_move_waits_is_refused()
   {
      var later = await PushAsync();
      await MoveAsync(_parent, later);
      var latest = _git.Commit("Latest", ("src/app.txt", "zero\n"));
      _platform.Repository(_git.Url, latest);

      var error = await AssertRefusedAsync(_parent, MoveBody(latest), HttpStatusCode.Conflict, "questionWaiting", "tourId");

      Assert.Contains("Claude still revises the stops that the last move made stale.", (string)error["reason"]!, StringComparison.Ordinal);
   }

   /// <summary>
   /// The facts that the move's scope knows are gone once its hold has checked the moved tours, as when the fact memo evicts them for another read. The parent and the dive each have a stop that
   /// the move makes stale, so their remaps read facts.
   /// </summary>
   [Fact]
   public async Task Move_stores_every_checked_tour_when_its_facts_are_evicted_between_the_check_and_the_stores()
   {
      var later = await PushAsync();
      var services = _harness.Server.Services;
      var facts = new ForgettingFacts(services.GetRequiredService<IGitFacts>());
      var moves = new TourMoves(
         services.GetRequiredService<TourWriter>(), services.GetRequiredService<PullRequestPlatforms>(), services.GetRequiredService<PlatformFreeRepositories>(), facts, NullLogger<TourMoves>.Instance)
      {
         BeforeStore = _ => facts.Forget(),
      };
      var pass = Passes.Admit<Access.Reviewer.Pass>("move_tour", Passes.Ui(services.GetRequiredService<ServerCredentials>(), services.GetRequiredService<AgentSessions>()));
      Assert.True(TourId.TryParse(_parent, out var parent));

      var moved = await moves.MoveAsync(pass, new MoveTourInput(parent, later, [_firstDive]), Cancellation);

      Assert.True(moved.Value is not null, moved.Error?.Reason);
      Assert.True(facts.Forgotten);
      Assert.Equal([_changes], moved.Value.StaleStops);
      Assert.Equal(later.Value, (string)(await ReadAsync(_parent))["pin"]!["head"]!);
      Assert.Equal(later.Value, (string)(await ReadAsync(_firstDive))["pin"]!["head"]!);
   }

   /// <summary>
   /// The disk refuses the dive's file after the parent's store, so the parent is at the latest commit and the dive stays behind. The parent then has a move for the dive alone, and its tour
   /// stays as it is.
   /// </summary>
   [Fact]
   public async Task Move_that_fails_on_the_disk_after_the_parent_stored_sends_the_parents_decision_and_a_move_through_the_parent_takes_the_dive_after()
   {
      var later = await PushAsync();
      var session = await _harness.AgentAsync();
      var moves = _harness.Server.Services.GetRequiredService<TourMoves>();
      moves.BeforeStore = id =>
      {
         if (id.Value == _firstDive)
         {
            throw new IOException("The disk is full.");
         }
      };
      try
      {
         var error = await AssertFailedAsync(_parent, MoveBody(later, _firstDive));

         Assert.Equal(
            $"tourId: lotsen could not store the move of tour {_firstDive}: The disk is full. Tour {_parent} moved to commit {later}. Tour {_firstDive} stays at its commit. Check that lotsen can write its data directory, then check for a move again.",
            (string)error["reason"]!);
      }
      finally
      {
         moves.BeforeStore = null;
      }

      var parent = await ReadAsync(_parent);
      Assert.Equal(later.Value, (string)parent["pin"]!["head"]!);
      var sent = await NextQuestionAsync(session);
      Assert.Equal((_parent, "tourMoved"), ((string?)sent["tourId"], (string?)sent["decision"]));
      Assert.Equal(_first.Value, (string)(await ReadAsync(_firstDive))["pin"]!["head"]!);

      var checkedMove = await CheckAsync(_parent);
      Assert.Equal(("available", later.Value, later.Value), ((string)checkedMove["move"]!, (string)checkedMove["current"]!, (string)checkedMove["latest"]!));
      Assert.Equal([_firstDive, _secondDive, _deepDive], checkedMove["dives"]!.AsArray().Select(dive => (string)dive!["tourId"]!));

      var moved = await MoveAsync(_parent, later, _firstDive, _deepDive);

      Assert.True(JsonNode.DeepEquals(parent["pin"], moved["pin"]), moved["pin"]!.ToJsonString());
      Assert.Equal((string)parent["updatedAt"]!, (string)moved["updatedAt"]!);
      Assert.Null(moved["thread"]);
      Assert.Equal([_changes], Ids(moved["staleStops"]));
      Assert.Equal([_firstDive, _deepDive], moved["dives"]!.AsArray().Select(dive => (string)dive!["tourId"]!));
      Assert.Equal(later.Value, (string)(await ReadAsync(_deepDive))["pin"]!["head"]!);
   }

   /// <summary>The session that holds the parent and the dive ends after the hold checked both and before the dive's store, so the dive's decision goes to a session that has ended.</summary>
   [Fact]
   public async Task Move_stores_each_checked_tour_when_its_session_ends_between_the_check_and_its_store()
   {
      var later = await PushAsync();
      var session = _harness.Sessions.Find((await _harness.AgentAsync()).Id)!;
      var moves = _harness.Server.Services.GetRequiredService<TourMoves>();
      moves.BeforeStore = id =>
      {
         if (id.Value == _firstDive)
         {
            session.End();
         }
      };
      try
      {
         var moved = await MoveAsync(_parent, later, _firstDive);

         Assert.Equal((string?)"tourMoved", (string?)moved["dives"]![0]!["thread"]!["decision"]!["kind"]);
      }
      finally
      {
         moves.BeforeStore = null;
      }

      Assert.Equal(later.Value, (string)(await ReadAsync(_firstDive))["pin"]!["head"]!);
   }

   /// <summary>The first move takes every dive, so the whole family is at the latest commit.</summary>
   [Fact]
   public async Task Move_of_a_tour_and_its_dives_at_the_latest_commit_is_refused_as_nothing_to_move()
   {
      var later = await PushAsync();
      await MoveAsync(_parent, later, _firstDive, _secondDive, _deepDive);

      var error = await AssertRefusedAsync(_parent, MoveBody(later, _firstDive), HttpStatusCode.Conflict, "moveNotAvailable", "tourId");

      Assert.Equal(
         $"tourId: the tour and each dive that diveIds names are at commit {later}, the latest commit of its ref, so nothing moves. Check for a move again once the ref moves on.",
         (string)error["reason"]!);
   }

   /// <summary>The first move takes the parent alone, so its dives stay behind at the first commit.</summary>
   [Fact]
   public async Task Move_of_a_tour_at_the_latest_commit_without_its_dives_behind_ticked_is_refused_and_names_them()
   {
      var later = await PushAsync();
      await MoveAsync(_parent, later);

      var error = await AssertRefusedAsync(_parent, MoveBody(later), HttpStatusCode.Conflict, "moveNotAvailable", "tourId");

      Assert.Equal(
         $"tourId: the tour and each dive that diveIds names are at commit {later}, the latest commit of its ref, so nothing moves. Tick the dives that are at another commit to move them: {_firstDive}, {_secondDive}, {_deepDive}.",
         (string)error["reason"]!);
   }

   private static async Task<JsonNode> NextQuestionAsync(AgentSessionConnection session)
   {
      while (true)
      {
         var received = await session.Stream.NextAsync(EventWait);
         if (received.Type == SessionEvents.Question)
         {
            return received.Data;
         }
      }
   }

   private async Task<JsonNode> AssertFailedAsync(string tourId, string body)
   {
      using var response = await _harness.SendJsonAsync(On("move_tour", tourId), body);
      return await ErrorAsync(response, HttpStatusCode.InternalServerError, "tourNotStored");
   }

   private static string MoveBody(CommitId commit, params string[] dives) =>
      new JsonObject { ["commit"] = commit.Value, ["diveIds"] = new JsonArray([.. dives.Select(dive => (JsonNode)dive)]) }.ToJsonString();

   private static JsonNode StopOf(JsonNode tour, string stopId) => tour["plan"]!["stops"]!.AsArray().Single(stop => (string)stop!["id"]! == stopId)!;

   private static (string Commit, int StartLine, string Text) PassageOf(JsonNode stop)
   {
      var passage = stop["content"]!["passages"]![0]!;
      return ((string)passage["commit"]!, (int)passage["startLine"]!, (string)passage["text"]!);
   }

   private static string[] Ids(JsonNode? ids) => [.. ids!.AsArray().Select(id => (string)id!)];

   // Moves main to a commit that inserts a line at the top of src/app.txt, changes its line 5 and changes line 2 of src/lib.txt.
   private Task<CommitId> PushAsync()
   {
      var later = _git.Commit("Later", ("src/app.txt", "zero\none\ntwo\nthree\nfour\nFIVE\nsix\n"), ("src/lib.txt", "a\nB\nc\n"));
      _platform.Repository(_git.Url, later, tags: new Dictionary<string, CommitId> { ["v1"] = _first });
      return Task.FromResult(later);
   }

   // Sends a refused move and checks that it changed none of the four tours.
   private async Task<JsonNode> AssertRefusedAsync(string tourId, string body, HttpStatusCode status, string code, string reasonPart)
   {
      string[] tours = [_parent, _firstDive, _secondDive, _deepDive, tourId];
      var before = await Task.WhenAll(tours.Select(ReadAsync));

      using var response = await _harness.SendJsonAsync(On("move_tour", tourId), body);

      var error = await ErrorAsync(response, status, code);
      Assert.Contains(reasonPart, (string)error["reason"]!, StringComparison.Ordinal);
      var after = await Task.WhenAll(tours.Select(ReadAsync));
      Assert.All(before.Zip(after), pair => Assert.True(JsonNode.DeepEquals(pair.First, pair.Second), $"The refused move changed a tour: {pair.Second}"));
      return error;
   }

   private async Task<string> CreatePinnedAsync(string @ref)
   {
      var id = (string)(await _harness.AgentCallAsync("create_tour", RepositoryPinningTests.CreateBody(@ref)))["id"]!;
      await _harness.PinsIdleAsync().WaitAsync(Timeout, Cancellation);
      return id;
   }

   private async Task<string> DiveAsync(string parent, string title)
   {
      var body = new JsonObject
      {
         ["diveOrigin"] = new JsonObject { ["parentTourId"] = parent, ["purpose"] = "prepare", ["title"] = title, ["words"] = $"{title}." },
         ["familiarity"] = "some",
      };
      return (string)(await _harness.AgentCallAsync("create_tour", body.ToJsonString()))["id"]!;
   }

   private async Task PutStopAsync(string tourId, string stopId, string path, int line) =>
      await _harness.AgentCallAsync(
         On("put_stop", tourId, stopId: stopId),
         $$"""{ "headline": "The code.", "passages": [{ "side": "head", "path": "{{path}}", "startLine": {{line}}, "endLine": {{line}}, "role": "primary" }] }""");

   private async Task TickDoneAsync(string tourId) => await _harness.CallAsync(On("record_reading_progress", tourId), """{ "summary": true }""");

   private async Task<JsonNode> CheckAsync(string tourId) => await _harness.CallAsync(On("check_move", tourId));

   private async Task<JsonNode> MoveAsync(string tourId, CommitId commit, params string[] dives) => await _harness.CallAsync(On("move_tour", tourId), MoveBody(commit, dives));

   private async Task<JsonNode> ReadAsync(string tourId) => await _harness.CallAsync(On("read_state", tourId));

   // Git facts whose scopes stop answering once Forget runs, as a memo that evicted every fact would.
   private sealed class ForgettingFacts : IGitFacts
   {
      private readonly IGitFacts _inner;

      public ForgettingFacts(IGitFacts inner)
      {
         _inner = inner;
      }

      public bool Forgotten { get; private set; }

      public FactSet Known => new Forgetting(this, _inner.Known);

      public void Forget() => Forgotten = true;

      public IFactScope Open(TourRemote remote, IReadOnlyCollection<CommitId> commits, FetchPolicy policy) => new Scope(this, _inner.Open(remote, commits, policy));

      public bool Downloaded(Uri remote, CommitId mergeBase, CommitId head) => _inner.Downloaded(remote, mergeBase, head);

      private sealed class Forgetting : FactSet
      {
         private readonly ForgettingFacts _owner;
         private readonly FactSet _inner;

         public Forgetting(ForgettingFacts owner, FactSet inner)
         {
            _owner = owner;
            _inner = inner;
         }

         internal override bool TryFind(FactKey key, out object? value)
         {
            value = null;
            return !_owner.Forgotten && _inner.TryFind(key, out value);
         }
      }

      private sealed class Scope : IFactScope
      {
         private readonly ForgettingFacts _owner;
         private readonly IFactScope _inner;

         public Scope(ForgettingFacts owner, IFactScope inner)
         {
            _owner = owner;
            _inner = inner;
         }

         public FactSet Known => new Forgetting(_owner, _inner.Known);

         public Task OpenCloneAsync(CancellationToken cancellationToken) => _inner.OpenCloneAsync(cancellationToken);

         public Task ReadAsync(IReadOnlyCollection<FactKey> keys, CancellationToken cancellationToken) => _inner.ReadAsync(keys, cancellationToken);

         public Task WarmAsync(CommitId mergeBase, CommitId head, CancellationToken cancellationToken) => _inner.WarmAsync(mergeBase, head, cancellationToken);

         public ValueTask DisposeAsync() => _inner.DisposeAsync();
      }
   }
}
