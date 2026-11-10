using System.Text;
using System.Text.Json.Nodes;
using Lotsen.Git;
using Lotsen.Hosting;
using Microsoft.Extensions.Logging;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>A server with one tour pinned to a fixture repository's pull request, through a test platform.</summary>
/// <remarks>
/// The source branch, <c>feature</c>, forks from <see cref="MergeBase"/>. It modifies <c>src/modified.txt</c>, deletes <c>src/deleted.txt</c>, moves <c>src/old-name.txt</c> to <c>src/renamed.txt</c>
/// with one line edited, turns the LF line breaks of <c>src/line-endings.txt</c> into CRLF, and adds <c>src/added.txt</c>, an HTML page and <c>bytes.txt</c>, which is not valid UTF-8. It also adds
/// the binary <c>logo.png</c>, moves the binary <c>assets/old.bin</c> to <c>assets/new.bin</c>, and adds <c>data/large.txt</c>, one byte above the large-file size, and any extra files. After the
/// fork, <c>main</c> changes <c>src/modified.txt</c> too, so the base side is the merge base, not the target. The merge base also holds any unchanged files, which no commit changes.
/// The harness's agent session creates the tour, so that session writes its plan and stops.
/// </remarks>
internal sealed class PinnedTourFixture : IAsyncDisposable
{
   public const string ModifiedBase = "one\ntwo\nthree\nfour\n";
   public const string ModifiedHead = "one\n2\nthree\nfour\nfive\n";
   public const string RenamedBase = "alpha\nbeta\ngamma\ndelta\nepsilon\n";
   public const string RenamedHead = "alpha\nbeta\nGAMMA\ndelta\nepsilon\n";
   public const string HtmlPage = "<!doctype html><script>alert(1)</script>\n";

   /// <summary>The ASCII text that each binary file of the fixture holds between NUL bytes.</summary>
   public const string BinaryMarker = "binary-file-content";

   /// <summary>The text of <c>data/large.txt</c>: 65536 lines of 16 bytes and one more byte.</summary>
   public static readonly string LargeText = string.Concat(Enumerable.Repeat("0123456789abcde\n", 65536)) + "!";

   private const string CreateBody = $$"""{ "prUrl": "{{TestPlatform.PrUrl}}", "familiarity": "some" }""";

   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

   private readonly GitOptions _gitOptions;
   private readonly RecordingLoggerProvider _logs = new();

   private PinnedTourFixture(GitFixture git, TestPlatform platform, CommitId mergeBase, CommitId head, CommitId target, GitOptions gitOptions)
   {
      _gitOptions = gitOptions;
      Git = git;
      Platform = platform;
      MergeBase = mergeBase;
      Head = head;
      Target = target;
   }

   public GitFixture Git { get; }

   public TestPlatform Platform { get; }

   public CommitId MergeBase { get; }

   public CommitId Head { get; }

   /// <summary>The target commit of the pull request's iteration, on <c>main</c>.</summary>
   public CommitId Target { get; }

   public ServerHarness Harness { get; private set; } = null!;

   public string TourId { get; private set; } = "";

   /// <summary>Builds the fixture repository, with the extra files added on the source branch, and starts a server with its tour pinned.</summary>
   public static Task<PinnedTourFixture> StartAsync(params (string Path, string Content)[] extraFiles) => StartAsync(new GitOptions(), [], extraFiles);

   /// <summary>Builds the fixture repository, with the unchanged files in the merge base and the extra files added on the source branch, and starts a server that runs git with the options.</summary>
   public static async Task<PinnedTourFixture> StartAsync(GitOptions gitOptions, IReadOnlyList<(string Path, string Content)> unchangedFiles, params (string Path, string Content)[] extraFiles)
   {
      byte[] binary = [0x89, 0x50, 0x4E, 0x47, 0x00, .. Encoding.ASCII.GetBytes(BinaryMarker), 0x00, 0xFF];
      var git = new GitFixture();
      Directory.CreateDirectory(Path.Combine(git.OriginPath, "assets"));
      File.WriteAllBytes(Path.Combine(git.OriginPath, "assets", "old.bin"), binary);
      var mergeBase = git.Commit(
         "Base",
         [
            ("src/modified.txt", ModifiedBase),
            ("src/deleted.txt", "gone\n"),
            ("src/same.txt", "same\n"),
            ("src/old-name.txt", RenamedBase),
            ("src/line-endings.txt", "one\ntwo\n"),
            .. unchangedFiles,
         ]);
      git.Checkout("feature", create: true);
      git.Commit(
         "Change",
         ("src/modified.txt", ModifiedHead),
         ("src/deleted.txt", null),
         ("src/added.txt", "new\n"),
         ("src/old-name.txt", null),
         ("src/renamed.txt", RenamedHead),
         ("src/line-endings.txt", "one\r\ntwo\r\n"),
         ("page.html", HtmlPage));
      File.WriteAllBytes(Path.Combine(git.OriginPath, "bytes.txt"), [0x66, 0xFF, 0xFE, 0x0A]);
      File.WriteAllBytes(Path.Combine(git.OriginPath, "logo.png"), binary);
      File.Move(Path.Combine(git.OriginPath, "assets", "old.bin"), Path.Combine(git.OriginPath, "assets", "new.bin"));
      var head = git.Commit("Bytes", [("data/large.txt", LargeText), .. extraFiles]);
      git.Checkout("main");
      var target = git.Commit("Main moves on", ("src/modified.txt", ModifiedBase + "main\n"));

      var platform = new TestPlatform();
      platform.PullRequest(git.Url, head, target);
      var fixture = new PinnedTourFixture(git, platform, mergeBase, head, target, gitOptions);
      try
      {
         fixture.Harness = await fixture.StartServerAsync();
         var created = await fixture.Harness.AgentCallAsync("create_tour", CreateBody);
         await fixture.Harness.PinsIdleAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);
         fixture.TourId = (string)created["id"]!;
         var pin = (await fixture.Harness.CallAsync(On("read_state", fixture.TourId)))["pin"]!;
         var problems = fixture._logs.Entries.Where(entry => entry.Level >= LogLevel.Warning).Select(entry => entry.Line);
         Assert.True((string?)pin["state"] == "pinned", $"The fixture's tour is not pinned: {pin.ToJsonString()}\nThe server's warnings and errors:\n{string.Join('\n', problems)}");
         // The reviewer's read starts an iteration check at once, so a test starts with no check of its own tour running and asking the platform.
         await fixture.Harness.PinsIdleAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);
         return fixture;
      }
      catch
      {
         await fixture.DisposeAsync();
         throw;
      }
   }

   /// <summary>Stops the server and starts another on the same data directory, whose harness's agent session then attaches the tour.</summary>
   public async Task RestartServerAsync()
   {
      await Harness.StopServerAsync();
      Harness = await StartServerAsync(Harness.DataDirectory);
      await Harness.AgentCallAsync(On("attach_tour", TourId));
      // The attach starts an iteration check at once, as the reviewer's read in StartAsync does.
      await Harness.PinsIdleAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);
   }

   /// <summary>Calls an operation as the UI and returns the status, the content type and the parsed body.</summary>
   public async Task<(int Status, string? ContentType, JsonNode Body)> GetAsync(OperationCall call, string? json = null)
   {
      using var response = await Harness.SendJsonAsync(call, json);
      var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
      return ((int)response.StatusCode, response.Content.Headers.ContentType?.MediaType, SuccessBody.OutputOrError(response, text));
   }

   public async ValueTask DisposeAsync()
   {
      // A fixture whose start failed may have no harness.
      if (Harness is not null)
      {
         await Harness.DisposeAsync();
      }

      Git.Dispose();
   }

   private Task<ServerHarness> StartServerAsync(DataDirectory? dataDirectory = null) =>
      ServerHarness.StartAsync(dataDirectory: dataDirectory, platform: Platform, configure: options => options with { Git = _gitOptions, LoggerProvider = _logs });
}
