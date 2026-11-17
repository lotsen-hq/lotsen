using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Lotsen.Git;
using Lotsen.Operations;
using Lotsen.Tours;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>Covers how <c>delete_tour</c> and <c>set_tour_archived</c> take a tour's subtree of dives, and how unarchiving restores only the dives that the same archive took.</summary>
/// <remarks>
/// Each test starts with the family of the root R: its dives A and then C, and B, a dive of A. Another orient tour of the same repository stands beside the family. Every tour is pinned to the
/// fixture's one commit.
/// </remarks>
[Trait("Category", "Store")]
public sealed class DiveSubtreeTests : IAsyncLifetime
{
   private const string Plan = """{ "chapters": [{ "id": "c", "title": "C" }], "stops": [{ "title": "S", "kind": "context", "chapter": "c" }] }""";

   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

   private readonly GitFixture _git = new();
   private readonly TestPlatform _platform = new();
   private readonly List<ServerHarness> _stopped = [];
   private readonly Dictionary<string, string> _broken = [];
   private ServerHarness _harness = null!;
   private string _root = null!;
   private string _a = null!;
   private string _b = null!;
   private string _c = null!;
   private string _other = null!;

   private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

   private ThreadCalls Calls => new(_harness);

   public async ValueTask InitializeAsync()
   {
      _platform.Repository(_git.Url, _git.Commit("Base", ("src/app.txt", "one\n")));
      _harness = await ServerHarness.StartAsync(platform: _platform);
      _root = (string)(await _harness.AgentCallAsync("create_tour", RepositoryPinningTests.CreateBody(@ref: null)))["id"]!;
      _other = (string)(await _harness.AgentCallAsync("create_tour", RepositoryPinningTests.CreateBody(@ref: null)))["id"]!;
      await _harness.PinsIdleAsync().WaitAsync(Timeout, Cancellation);
      _a = await DiveAsync(_root);
      _b = await DiveAsync(_a);
      _c = await DiveAsync(_root);
   }

   public async ValueTask DisposeAsync()
   {
      // A start that failed may leave no harness.
      if (_harness is not null)
      {
         await _harness.DisposeAsync();
      }

      foreach (var stopped in _stopped)
      {
         await stopped.DisposeAsync();
      }

      _git.Dispose();
   }

   [Fact]
   public async Task Deleting_the_root_deletes_every_dive_below_it_and_no_other_tour()
   {
      await using var events = await EventStream.OpenAsync(_harness);

      var deleted = await DeleteAsync(_root);

      Assert.True(JsonNode.DeepEquals(JsonNode.Parse($$"""{ "tourId": "{{_root}}", "dives": [{ "tourId": "{{_a}}" }, { "tourId": "{{_c}}" }, { "tourId": "{{_b}}" }] }"""), deleted), deleted.ToJsonString());
      Assert.Equal([_other], await ListedAsync());
      Assert.Equal([_other], Directory.GetFiles(_harness.DataDirectory.ToursPath).Select(Path.GetFileNameWithoutExtension));
      Assert.Equal([_b, _c, _a, _root], await EventTourIdsAsync(events, ServerEvents.TourDeleted.Name, 4));
      using var write = await _harness.AgentSendJsonAsync(On("set_plan", _b), Plan);
      await ThreadCalls.ErrorAsync(write, HttpStatusCode.NotFound, "tourNotFound");
   }

   [Fact]
   public async Task Deleting_a_dive_deletes_its_subtree_and_leaves_its_parent_and_sibling()
   {
      var deleted = await DeleteAsync(_a);
      var later = await DiveAsync(_root);

      Assert.True(JsonNode.DeepEquals(JsonNode.Parse($$"""{ "tourId": "{{_a}}", "dives": [{ "tourId": "{{_b}}" }] }"""), deleted), deleted.ToJsonString());
      Assert.Equal(new HashSet<string> { _root, _c, _other, later }, [.. (await ListedAsync())]);
      Assert.Equal([_c, later], await DivesAsync(_root));
   }

   [Fact]
   public async Task Deleting_a_dive_without_dives_answers_with_its_ID_alone()
   {
      var deleted = await DeleteAsync(_b);

      Assert.True(JsonNode.DeepEquals(JsonNode.Parse($$"""{ "tourId": "{{_b}}" }"""), deleted), deleted.ToJsonString());
      Assert.Empty(await DivesAsync(_a));
   }

   [Fact]
   public async Task Deleting_a_family_keeps_the_clone_while_another_tour_needs_it_and_the_last_delete_removes_it()
   {
      var clone = Assert.Single(Clones());

      await DeleteAsync(_root);
      await _harness.PinsIdleAsync().WaitAsync(Timeout, Cancellation);
      var kept = Clones();
      await DeleteAsync(_other);
      await _harness.PinsIdleAsync().WaitAsync(Timeout, Cancellation);

      Assert.Equal([clone], kept);
      Assert.Empty(Clones());
   }

   /// <summary>B's file breaks after the parent's read listed the dives, so the dive index of the running server named B.</summary>
   [Fact]
   public async Task Delete_is_refused_and_deletes_nothing_while_a_dive_below_cannot_be_read()
   {
      Assert.Equal([_a, _c], await DivesAsync(_root));
      Break(_b, truncate: true);

      await AssertRefusedAsync(On("delete_tour", _root), _b);

      Assert.Equal(5, Directory.GetFiles(_harness.DataDirectory.ToursPath).Length);
   }

   /// <summary>
   /// B's file breaks while the server is stopped, so the restarted server never read B: cut in half, so the file names no parent, or with a broken rule, so the file still names A as B's parent.
   /// The file is fixed after the refusal, and the server keeps running.
   /// </summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public async Task Delete_is_refused_while_a_dive_broke_before_the_start_and_deletes_the_whole_subtree_once_the_file_is_fixed(bool truncate)
   {
      await RestartAsync(() => Break(_b, truncate));

      await AssertRefusedAsync(On("delete_tour", _root), _b);
      var files = Directory.GetFiles(_harness.DataDirectory.ToursPath).Length;
      Repair(_b);
      var deleted = await DeleteAsync(_root);

      Assert.Equal(5, files);
      Assert.Equal([_a, _c, _b], deleted["dives"]!.AsArray().Select(dive => (string)dive!["tourId"]!));
      Assert.Equal([_other], await ListedAsync());
   }

   /// <summary>B's file names a newer schema version while the server is stopped, as after a return to an older lotsen.</summary>
   [Fact]
   public async Task Delete_is_refused_while_a_newer_lotsen_wrote_a_dive_and_the_refusal_never_says_to_delete_its_file()
   {
      await RestartAsync(() => File.WriteAllText(FileOf(_b), File.ReadAllText(FileOf(_b)).Replace($"\"schemaVersion\":{TourDocument.CurrentSchemaVersion}", $"\"schemaVersion\":{TourDocument.CurrentSchemaVersion + 1}", StringComparison.Ordinal)));

      using var refused = await Calls.SendAsync(await Calls.UiAsync(On("delete_tour", _root)));

      var reason = (string)(await ThreadCalls.ErrorAsync(refused, HttpStatusCode.InternalServerError, "tourUnreadable"))["reason"]!;
      Assert.Contains($"Tour {_b} could not be read, because a newer version of lotsen wrote it", reason, StringComparison.Ordinal);
      Assert.Contains($"Delete or archive tour {_root} with that newer version", reason, StringComparison.Ordinal);
      Assert.DoesNotContain("delete that file", reason, StringComparison.Ordinal);
      Assert.Equal(5, Directory.GetFiles(_harness.DataDirectory.ToursPath).Length);
   }

   [Fact]
   public async Task Archive_is_refused_while_a_dive_broke_before_the_start_and_archives_nothing()
   {
      await RestartAsync(() => Break(_b, truncate: true));

      await AssertRefusedAsync(On("set_tour_archived", _a), _b, """{ "archived": true }""");

      Assert.Equal("generating", (await StatesAsync())[_a]);
   }

   /// <summary>The other tour's file breaks a rule while the server is stopped, and the file names no dive origin, so it lies outside every subtree.</summary>
   [Fact]
   public async Task Delete_and_archive_pass_over_a_tour_that_cannot_be_read_and_whose_file_names_no_parent()
   {
      await RestartAsync(() => Break(_other, truncate: false));

      var archived = await ArchiveAsync(_a, archived: true);
      var deleted = await DeleteAsync(_root);

      Assert.Equal([_b], archived["dives"]!.AsArray().Select(dive => (string)dive!["tourId"]!));
      Assert.Equal([_a, _c, _b], deleted["dives"]!.AsArray().Select(dive => (string)dive!["tourId"]!));
      Assert.Equal([_other], Directory.GetFiles(_harness.DataDirectory.ToursPath).Select(Path.GetFileNameWithoutExtension));
   }

   [Fact]
   public async Task Archiving_a_middle_dive_archives_its_subtree_and_unarchiving_it_restores_that_subtree()
   {
      await using var events = await EventStream.OpenAsync(_harness);

      var archived = await ArchiveAsync(_a, archived: true);
      var archivedStates = await StatesAsync();
      var archivedEvents = await EventTourIdsAsync(events, ServerEvents.TourStateChanged.Name, 2);
      var takenWith = (string?)(await ReadAsync(_b))["archivedWith"];
      var unarchived = await ArchiveAsync(_a, archived: false);
      var unarchivedEvents = await EventTourIdsAsync(events, ServerEvents.TourStateChanged.Name, 2);

      Assert.Equal(("archived", _b, "archived"), ((string)archived["state"]!, (string)archived["dives"]![0]!["tourId"]!, (string)archived["dives"]![0]!["state"]!));
      Assert.Single(archived["dives"]!.AsArray());
      Assert.Equal(new Dictionary<string, string> { [_root] = "generating", [_a] = "archived", [_b] = "archived", [_c] = "generating", [_other] = "generating" }, archivedStates);
      Assert.Equal([_b, _a], archivedEvents);
      Assert.Equal(_a, takenWith);
      Assert.Equal(("generating", _b, "generating"), ((string)unarchived["state"]!, (string)unarchived["dives"]![0]!["tourId"]!, (string)unarchived["dives"]![0]!["state"]!));
      Assert.Equal([_b, _a], unarchivedEvents);
      Assert.All((await StatesAsync()).Values, state => Assert.Equal("generating", state));
      Assert.Null((await ReadAsync(_b))["archivedWith"]);
   }

   [Fact]
   public async Task Unarchiving_the_root_leaves_archived_a_dive_that_the_reviewer_archived_on_its_own_before()
   {
      await ArchiveAsync(_b, archived: true);

      var archived = await ArchiveAsync(_root, archived: true);
      var unarchived = await ArchiveAsync(_root, archived: false);

      Assert.Equal([_a, _c], archived["dives"]!.AsArray().Select(dive => (string)dive!["tourId"]!));
      Assert.Equal([_a, _c], unarchived["dives"]!.AsArray().Select(dive => (string)dive!["tourId"]!));
      Assert.Equal(new Dictionary<string, string> { [_root] = "generating", [_a] = "generating", [_b] = "archived", [_c] = "generating", [_other] = "generating" }, await StatesAsync());
      Assert.Null((await ReadAsync(_b))["archivedWith"]);
   }

   [Fact]
   public async Task Unarchiving_a_dive_that_the_roots_archive_took_restores_its_own_dives_of_that_archive()
   {
      await ArchiveAsync(_root, archived: true);

      var unarchived = await ArchiveAsync(_a, archived: false);
      var states = await StatesAsync();
      var rest = await ArchiveAsync(_root, archived: false);

      Assert.Equal([_b], unarchived["dives"]!.AsArray().Select(dive => (string)dive!["tourId"]!));
      Assert.Equal(new Dictionary<string, string> { [_root] = "archived", [_a] = "generating", [_b] = "generating", [_c] = "archived", [_other] = "generating" }, states);
      Assert.Equal([_c], rest["dives"]!.AsArray().Select(dive => (string)dive!["tourId"]!));
      Assert.All((await StatesAsync()).Values, state => Assert.Equal("generating", state));
   }

   [Fact]
   public async Task Archiving_a_dive_without_dives_answers_with_its_state_alone()
   {
      var archived = await ArchiveAsync(_c, archived: true);

      Assert.False(archived.AsObject().ContainsKey("dives"), archived.ToJsonString());
      Assert.Null((await ReadAsync(_c))["archivedWith"]);
   }

   // Stops the server, runs the change on its data directory, and starts a server on that directory.
   private async Task RestartAsync(Action change)
   {
      await _harness.StopServerAsync();
      _stopped.Add(_harness);
      change();
      _harness = await ServerHarness.StartAsync(platform: _platform, dataDirectory: _harness.DataDirectory);
   }

   private string FileOf(string tourId) => Path.Combine(_harness.DataDirectory.ToursPath, $"{tourId}.json");

   // Makes the tour unreadable, cut in half or with an empty title, and keeps the file's content for Repair. The tours have no plan, so the tour's own title is the file's one title.
   private void Break(string tourId, bool truncate)
   {
      var content = File.ReadAllText(FileOf(tourId));
      _broken[tourId] = content;
      var untitled = Regex.Replace(content, "\"title\":\"[^\"]+\"", "\"title\":\"\"");
      Assert.NotEqual(content, untitled);
      File.WriteAllText(FileOf(tourId), truncate ? content[..(content.Length / 2)] : untitled);
   }

   private void Repair(string tourId) => File.WriteAllText(FileOf(tourId), _broken[tourId]);

   private async Task AssertRefusedAsync(OperationCall call, string unreadable, string? body = null)
   {
      using var refused = await Calls.SendAsync(await Calls.UiAsync(call), body);
      var error = await ThreadCalls.ErrorAsync(refused, HttpStatusCode.InternalServerError, "tourUnreadable");
      Assert.Contains($"Tour {unreadable} could not be read", (string)error["reason"]!, StringComparison.Ordinal);
   }

   private async Task<string> DiveAsync(string parentId)
   {
      var origin = new JsonObject { ["parentTourId"] = parentId, ["purpose"] = "prepare", ["title"] = "How the app starts", ["words"] = "How the app starts." };
      return (string)(await _harness.AgentCallAsync("create_tour", new JsonObject { ["diveOrigin"] = origin, ["familiarity"] = "new" }.ToJsonString()))["id"]!;
   }

   private async Task<JsonNode> DeleteAsync(string tourId) => await Calls.CallAsync(await Calls.UiAsync(On("delete_tour", tourId)));

   private async Task<JsonNode> ArchiveAsync(string tourId, bool archived) =>
      await Calls.CallAsync(await Calls.UiAsync(On("set_tour_archived", tourId)), $$"""{ "archived": {{(archived ? "true" : "false")}} }""");

   private async Task<JsonNode> ReadAsync(string tourId) => await _harness.CallAsync(On("read_state", tourId));

   private async Task<string[]> DivesAsync(string tourId) => [.. (await ReadAsync(tourId))["dives"]?.AsArray().Select(dive => (string)dive!["tourId"]!) ?? []];

   private async Task<JsonArray> RowsAsync() => (await Calls.CallAsync(await Calls.UiAsync("list_tours")))["tours"]!.AsArray();

   private async Task<string[]> ListedAsync() => [.. (await RowsAsync()).Select(row => (string)row!["id"]!)];

   private async Task<Dictionary<string, string>> StatesAsync() => (await RowsAsync()).ToDictionary(row => (string)row!["id"]!, row => (string)row!["state"]!);

   private string[] Clones() => Directory.Exists(_harness.DataDirectory.ClonesPath) ? Directory.GetDirectories(_harness.DataDirectory.ClonesPath) : [];

   // Returns the tour IDs of the next events of the type, in the order of the stream, and skips every other event.
   private static async Task<List<string>> EventTourIdsAsync(EventStream stream, string type, int count)
   {
      var ids = new List<string>();
      while (ids.Count < count)
      {
         var next = await stream.NextAsync(ThreadCalls.EventWait);
         if (next.Type == type)
         {
            ids.Add((string)next.Data["tourId"]!);
         }
      }

      return ids;
   }
}
