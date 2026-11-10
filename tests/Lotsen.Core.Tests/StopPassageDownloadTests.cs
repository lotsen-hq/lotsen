using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lotsen.Git;
using Lotsen.Hosting;
using Lotsen.Json;
using Lotsen.Operations;
using Lotsen.Processes;
using Lotsen.Tours;
using Microsoft.Extensions.DependencyInjection;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>
/// Covers the download before a write's git phase reads files: one fetch brings the files that the passages, the <c>ref:</c> links and the anchor name, and it completes after the request is
/// canceled. A failed download leaves each refusal to the checks, and an exception that no git failure explains reaches the request.
/// </summary>
[Trait("Category", "Git")]
public sealed class StopPassageDownloadTests : IAsyncDisposable
{
   private const string Plan = """{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "title": "S", "kind": "inspect", "chapter": "a" }] }""";

   // Every file but src/modified.txt is unchanged, so the pin downloaded none of them.
   private const string StopFields = """
      "headline": "Line two becomes a digit.",
      "how": "It reads [the third file](ref:head/lib/three.txt#L1-1).",
      "passages": [
        { "side": "head", "path": "src/modified.txt", "startLine": 2, "endLine": 2, "role": "primary" },
        { "side": "head", "path": "lib/one.txt", "startLine": 1, "endLine": 1, "role": "related" },
        { "side": "head", "path": "lib/two.txt", "startLine": 1, "endLine": 2, "role": "related" }
      ]
      """;

   private static readonly (string Path, string Content)[] Unchanged = [("lib/one.txt", "one\n"), ("lib/two.txt", "two\nmore\n"), ("lib/three.txt", "three\n")];

   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

   private GatedGit? _git;
   private PinnedTourFixture? _tour;

   private GatedGit Git => _git!;

   private PinnedTourFixture Tour => _tour!;

   [Fact]
   public async Task Put_stop_whose_related_passages_and_ref_link_point_into_three_unchanged_files_stores_after_one_fetch()
   {
      var stopId = await StartAsync();
      var before = Git.ContentFetches;

      var stored = await Tour.Harness.AgentCallAsync(On("put_stop", Tour.TourId, stopId: stopId), "{" + StopFields + "}");

      Assert.Equal(["2", "one", "two\nmore"], stored["content"]!["passages"]!.AsArray().Select(passage => (string)passage!["text"]!));
      Assert.Equal(1, Git.ContentFetches - before);
   }

   [Fact]
   public async Task Agent_message_whose_ref_links_and_code_anchor_point_into_unchanged_files_starts_its_thread_after_one_fetch()
   {
      await StartAsync();
      var before = Git.ContentFetches;
      const string body = """
         { "text": "See [one](ref:head/lib/one.txt#L1-1) and [two](ref:head/lib/two.txt#L2-2).", "anchor": { "kind": "code", "side": "head", "path": "lib/three.txt", "startLine": 1, "endLine": 1 } }
         """;

      var started = await Tour.Harness.AgentCallAsync(On("start_thread", Tour.TourId), body);

      Assert.Equal("three", (string)started["thread"]!["anchor"]!["text"]!);
      Assert.Equal(1, Git.ContentFetches - before);
   }

   /// <summary>The git double holds the download until the request is canceled, so a download on the request's token would end before it fetched anything.</summary>
   [Fact]
   public async Task Put_stop_canceled_during_the_download_stores_nothing_while_the_download_completes()
   {
      var stopId = await StartAsync();
      var services = Tour.Harness.Server.Services;
      var pass = Passes.Admit<Access.BoundSession.Pass>("put_stop", await Tour.Harness.AgentCallerAsync());
      var input = JsonSerializer.Deserialize($$"""{ "tourId": "{{Tour.TourId}}", "stopId": "{{stopId}}", {{StopFields}} }""", LotsenJsonContext.Default.PutStopInput)!;
      using var request = new CancellationTokenSource();
      Git.Hold();

      var put = services.GetRequiredService<PlanOperations>().PutStopAsync(pass, input, request.Token);
      await Poll.UntilAsync(() => Git.Holding ? "" : null, Timeout, () => "The download did not start.");
      await request.CancelAsync();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => put.WaitAsync(Timeout, TestContext.Current.CancellationToken));
      Git.Release();
      await Tour.Harness.PinsIdleAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);

      var missing = MissingObjects();
      Assert.All(Unchanged, file => Assert.DoesNotContain("?" + BlobOf(file.Path), missing));
      Assert.IsType<Stop.Pending>(Tour.Harness.StoredTour(input.TourId)!.FindStop(stopId));
      var fetches = Git.ContentFetches;
      await Tour.Harness.AgentCallAsync(On("put_stop", Tour.TourId, stopId: stopId), "{" + StopFields + "}");
      Assert.Equal(fetches, Git.ContentFetches);
   }

   /// <summary>The origin is gone, so the download of <c>lib/one.txt</c> fails, while the pin downloaded <c>src/modified.txt</c>, which has no line 9.</summary>
   [Fact]
   public async Task Put_stop_whose_download_fails_is_refused_for_the_first_passage_that_fails_its_check()
   {
      var stopId = await StartAsync();
      Directory.Move(Tour.Git.OriginPath, Path.Combine(Tour.Git.ScratchPath, "moved-origin"));
      const string body = """
         {
           "headline": "Line two becomes a digit.",
           "passages": [
             { "side": "head", "path": "src/modified.txt", "startLine": 2, "endLine": 9, "role": "primary" },
             { "side": "head", "path": "lib/one.txt", "startLine": 1, "endLine": 1, "role": "related" }
           ]
         }
         """;

      var (status, error) = await PutStopErrorAsync(stopId, body);

      Assert.Equal((HttpStatusCode.BadRequest, "invalidInput", "passages[0].endLine"), (status, (string)error["code"]!, (string?)error["field"]));
   }

   /// <summary>The origin is gone, so the download of <c>lib/one.txt</c> fails, and so does the check's own read of it.</summary>
   [Fact]
   public async Task Put_stop_whose_download_fails_is_refused_as_unavailable_when_a_check_reads_the_missing_file()
   {
      var stopId = await StartAsync();
      Directory.Move(Tour.Git.OriginPath, Path.Combine(Tour.Git.ScratchPath, "moved-origin"));

      var (status, error) = await PutStopErrorAsync(stopId, "{" + StopFields + "}");

      Assert.Equal((HttpStatusCode.BadGateway, "repositoryUnavailable"), (status, (string)error["code"]!));
   }

   /// <summary>The git double answers the download's listing with an entry that git never writes, so the download throws an exception that no git failure explains.</summary>
   [Fact]
   public async Task Put_stop_whose_download_throws_unexpectedly_fails_with_that_exception()
   {
      var stopId = await StartAsync();
      var pass = Passes.Admit<Access.BoundSession.Pass>("put_stop", await Tour.Harness.AgentCallerAsync());
      var input = JsonSerializer.Deserialize($$"""{ "tourId": "{{Tour.TourId}}", "stopId": "{{stopId}}", {{StopFields}} }""", LotsenJsonContext.Default.PutStopInput)!;
      Git.Garble();

      var put = Tour.Harness.Server.Services.GetRequiredService<PlanOperations>().PutStopAsync(pass, input, TestContext.Current.CancellationToken);

      await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => put.WaitAsync(Timeout, TestContext.Current.CancellationToken));
   }

   public async ValueTask DisposeAsync()
   {
      if (_tour is not null)
      {
         await _tour.DisposeAsync();
      }

      _git?.Dispose();
   }

   // Starts the server with the unchanged files and the tour's plan of one inspect stop, and returns the stop's ID.
   private async Task<string> StartAsync()
   {
      _git = new GatedGit();
      _tour = await PinnedTourFixture.StartAsync(_git.Options, Unchanged);
      return (string)(await Tour.Harness.AgentCallAsync(On("set_plan", Tour.TourId), Plan))["plan"]!["stops"]![0]!["id"]!;
   }

   private async Task<(HttpStatusCode Status, JsonNode Error)> PutStopErrorAsync(string stopId, string body)
   {
      using var response = await Tour.Harness.AgentSendJsonAsync(On("put_stop", Tour.TourId, stopId: stopId), body);
      return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!);
   }

   private string BlobOf(string path) => Tour.Git.Git(Tour.Git.OriginPath, "rev-parse", $"{Tour.Head}:{path}").Trim();

   // With --missing=print, rev-list prints each object that the clone lacks with a leading "?" and downloads nothing.
   private List<string> MissingObjects()
   {
      var clone = Assert.Single(Directory.GetDirectories(Tour.Harness.DataDirectory.ClonesPath));
      return [.. Tour.Git.Git(clone, "rev-list", "--objects", "--missing=print", Tour.MergeBase.Value, Tour.Head.Value).Split('\n').Where(line => line.StartsWith('?'))];
   }

   /// <summary>
   /// A <see cref="GitDouble"/> that makes git trace every command it runs. While the gate is closed it holds each diff-tree against the empty tree, and once garbled it answers each
   /// <c>ls-tree -z</c> with an entry that has no tab.
   /// </summary>
   private sealed class GatedGit : IDisposable
   {
      private const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

      private readonly string _directory;
      private volatile bool _gateClosed;
      private volatile bool _held;
      private volatile bool _garbled;

      public GatedGit()
      {
         _directory = Directory.CreateTempSubdirectory("lotsen-gated-git-").FullName;
         Options = GitDouble.Options(RunAsync);
      }

      public GitOptions Options { get; }

      /// <summary>Whether a diff-tree waits at the gate, or waited there.</summary>
      public bool Holding => _held;

      /// <summary>The number of fetches that git ran to download file contents that the clone lacked, as <see cref="RecordingGit.ContentFetchesIn"/> counts them.</summary>
      public int ContentFetches => File.Exists(TracePath) ? RecordingGit.ContentFetchesIn(File.ReadAllText(TracePath)) : 0;

      private string TracePath => Path.Combine(_directory, "trace.log");

      public void Hold() => _gateClosed = true;

      public void Release() => _gateClosed = false;

      public void Garble() => _garbled = true;

      public void Dispose() => DirectoryTree.Delete(_directory);

      private async Task<ProcessOutcome> RunAsync(GitRun run)
      {
         var arguments = run.Command.Arguments;
         if (_garbled && arguments is ["ls-tree", "-z", ..])
         {
            return GitRun.Exit(0, output: "garbled\0");
         }

         if (_gateClosed && run.Subcommand == "diff-tree" && arguments.Any(argument => argument.Contains(EmptyTree, StringComparison.Ordinal)))
         {
            _held = true;
            if (await run.HoldAsync(() => !_gateClosed) is { } ended)
            {
               return ended;
            }
         }

         return await run.GitAsync(run.Command with { Environment = new Dictionary<string, string?>(run.Command.Environment) { ["GIT_TRACE"] = TracePath } });
      }
   }
}
