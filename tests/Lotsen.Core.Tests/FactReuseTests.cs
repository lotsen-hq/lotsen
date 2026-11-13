using System.Text.Json.Nodes;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>Covers the git runs that the file reads and the passage checks of a pinned tour save, because they read git facts that the fact memo keeps.</summary>
[Trait("Category", "Git")]
public sealed class FactReuseTests : IDisposable
{
   private const string Plan = """{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "title": "S", "kind": "inspect", "chapter": "a" }] }""";

   private const string Stop = """
      {
        "headline": "Line two becomes a digit.",
        "how": "It reads [the other file](ref:head/lib/one.txt#L1-1).",
        "passages": [
          { "side": "head", "path": "src/modified.txt", "startLine": 2, "endLine": 2, "role": "primary" },
          { "side": "base", "path": "src/modified.txt", "startLine": 2, "endLine": 2, "role": "related" }
        ]
      }
      """;

   private readonly GitFixture _fixture = new();

   [Fact]
   public async Task Changed_file_list_read_again_runs_no_git()
   {
      var git = new RecordingGit(_fixture.ScratchPath);
      await using var tour = await PinnedTourFixture.StartAsync(git.Options, []);
      var first = await tour.Harness.CallAsync(On("list_changed_files", tour.TourId));
      var runsBefore = Runs(git);

      var again = await tour.Harness.CallAsync(On("list_changed_files", tour.TourId));

      Assert.True(JsonNode.DeepEquals(first, again), $"The lists differ: {first} and {again}");
      Assert.Equal(runsBefore, Runs(git));
   }

   [Theory]
   [InlineData("""{ "path": "src/modified.txt" }""")]
   [InlineData("""{ "path": "src/modified.txt", "whitespace": "ignore" }""")]
   [InlineData("""{ "path": "src/added.txt" }""")]
   public async Task Diff_read_again_runs_no_git(string input)
   {
      var git = new RecordingGit(_fixture.ScratchPath);
      await using var tour = await PinnedTourFixture.StartAsync(git.Options, []);
      var first = await tour.Harness.CallAsync(On("diff_file", tour.TourId), input);
      var runsBefore = Runs(git);

      var again = await tour.Harness.CallAsync(On("diff_file", tour.TourId), input);

      Assert.True(JsonNode.DeepEquals(first, again), $"The diffs differ: {first} and {again}");
      Assert.Equal(runsBefore, Runs(git));
   }

   [Fact]
   public async Task Put_stop_written_again_with_the_same_passages_and_link_runs_no_git()
   {
      var git = new RecordingGit(_fixture.ScratchPath);
      await using var tour = await PinnedTourFixture.StartAsync(git.Options, [("lib/one.txt", "one\n")]);
      var stopId = (string)(await tour.Harness.AgentCallAsync(On("set_plan", tour.TourId), Plan))["plan"]!["stops"]![0]!["id"]!;
      var first = await tour.Harness.AgentCallAsync(On("put_stop", tour.TourId, stopId: stopId), Stop);
      var runsBefore = Runs(git);

      var again = await tour.Harness.AgentCallAsync(On("put_stop", tour.TourId, stopId: stopId), Stop);

      Assert.Equal(2, (int)again["version"]!);
      Assert.True(JsonNode.DeepEquals(first["content"]!["passages"], again["content"]!["passages"]), $"The passages differ: {first} and {again}");
      Assert.Equal(runsBefore, Runs(git));
   }

   /// <summary>The first pin downloads the changed file, so the clone holds both of its blobs before the write, and the fact memo knows neither.</summary>
   [Fact]
   public async Task Put_stop_whose_files_the_clone_holds_downloads_nothing_in_advance()
   {
      var git = new RecordingGit(_fixture.ScratchPath);
      await using var tour = await PinnedTourFixture.StartAsync(git.Options, []);
      var stopId = (string)(await tour.Harness.AgentCallAsync(On("set_plan", tour.TourId), Plan))["plan"]!["stops"]![0]!["id"]!;
      const string body = """
         {
           "headline": "Line two becomes a digit.",
           "passages": [{ "side": "head", "path": "src/modified.txt", "startLine": 2, "endLine": 2, "role": "primary" }]
         }
         """;
      var (runsBefore, fetchesBefore) = (Runs(git).Count, git.ContentFetches);

      await tour.Harness.AgentCallAsync(On("put_stop", tour.TourId, stopId: stopId), body);

      var runs = Runs(git).Skip(runsBefore).ToList();
      Assert.DoesNotContain(runs, run => run.StartsWith("ls-tree ", StringComparison.Ordinal));
      Assert.Equal(fetchesBefore, git.ContentFetches);
   }

   public void Dispose() => _fixture.Dispose();

   private static List<string> Runs(RecordingGit git) => [.. git.Arguments.Split('\n', StringSplitOptions.RemoveEmptyEntries)];
}
