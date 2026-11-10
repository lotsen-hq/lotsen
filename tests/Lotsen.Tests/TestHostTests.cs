using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Lotsen.Tests;

/// <summary>Covers the test host's control API, driven as the UI's tests drive it: worlds that start, seal into templates, copy, restart and share the origin, and the host's exit.</summary>
[Trait("Category", "TestHost")]
public sealed class TestHostTests : IClassFixture<TestHostTests.SharedHost>
{
   private const string PlanInput = """{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "title": "S", "kind": "inspect", "chapter": "a" }] }""";

   private readonly TestHostProcess _host;

   public TestHostTests(SharedHost shared)
   {
      _host = shared.Host;
   }

   [Fact]
   public async Task World_serves_the_operations_to_its_ui_credential()
   {
      var world = await _host.ControlAsync("world.start");

      var listed = await OperationAsync(world, "list_tours", "{}");

      Assert.Empty(listed["tours"]!.AsArray());
   }

   [Fact]
   public async Task Copies_of_a_sealed_world_serve_its_tour_and_read_its_files_from_the_copied_clone()
   {
      var seeded = await _host.ControlAsync("world.start");
      var tourId = await SeedAsync(seeded);
      await _host.ControlAsync("world.seal", new JsonObject { ["world"] = (string)seeded["world"]!, ["name"] = "copies" });

      var first = await _host.ControlAsync("world.start", new JsonObject { ["template"] = "copies" });
      var second = await _host.ControlAsync("world.start", new JsonObject { ["template"] = "copies" });

      Assert.NotEqual((string)first["world"]!, (string)second["world"]!);
      foreach (var world in new[] { first, second })
      {
         Assert.Contains((await OperationAsync(world, "list_tours", "{}"))["tours"]!.AsArray(), tour => (string?)tour!["id"] == tourId);
         var diff = await OperationAsync(world, "diff_file", new JsonObject { ["tourId"] = tourId, ["path"] = "a.txt" }.ToJsonString());
         Assert.Equal("two\n", (string?)diff["headText"]);
      }
   }

   [Theory]
   [InlineData("../escape")]
   [InlineData("..")]
   [InlineData("a/b")]
   [InlineData("a\\b")]
   [InlineData("a..b")]
   [InlineData("")]
   public async Task Seal_refuses_a_template_name_that_is_not_a_plain_name_and_keeps_the_world(string name)
   {
      var world = await _host.ControlAsync("world.start");

      var (status, answer) = await _host.SendControlAsync("world.seal", new JsonObject { ["world"] = (string)world["world"]!, ["name"] = name });

      Assert.Equal(HttpStatusCode.InternalServerError, status);
      Assert.Contains("not a plain name", (string?)answer["error"], StringComparison.Ordinal);
      Assert.Empty((await OperationAsync(world, "list_tours", "{}"))["tours"]!.AsArray());
   }

   [Fact]
   public async Task Seal_takes_a_template_name_of_letters_digits_dashes_underscores_and_dots()
   {
      var world = await _host.ControlAsync("world.start");

      await _host.ControlAsync("world.seal", new JsonObject { ["world"] = (string)world["world"]!, ["name"] = "plain-Name_2.v1" });

      Assert.True((bool)(await _host.ControlAsync("template.has", new JsonObject { ["name"] = "plain-Name_2.v1" }))["has"]!);
   }

   [Fact]
   public async Task A_claim_that_waits_for_another_claims_build_gets_the_sealed_template()
   {
      var build = (await _host.ControlAsync("template.claim", new JsonObject { ["name"] = "shared" }))["build"]!.AsObject();
      var waiting = _host.SendControlAsync("template.claim", new JsonObject { ["name"] = "shared" });

      await _host.ControlAsync("world.seal", new JsonObject { ["world"] = (string)build["world"]!, ["name"] = "shared", ["data"] = "seed" });

      var (status, answer) = await waiting;
      Assert.Equal(HttpStatusCode.OK, status);
      Assert.Equal((true, "seed"), ((bool)answer["has"]!, (string?)answer["data"]));
   }

   [Fact]
   public async Task A_build_whose_world_stops_unsealed_fails_the_claim_that_waits_for_it_and_every_later_claim_at_once()
   {
      var build = (await _host.ControlAsync("template.claim", new JsonObject { ["name"] = "broken" }))["build"]!.AsObject();
      var waiting = _host.SendControlAsync("template.claim", new JsonObject { ["name"] = "broken" });

      var stopped = Stopwatch.StartNew();
      await _host.ControlAsync("world.stop", new JsonObject { ["world"] = (string)build["world"]! });

      foreach (var claim in new[] { waiting, _host.SendControlAsync("template.claim", new JsonObject { ["name"] = "broken" }) })
      {
         var (status, answer) = await claim;
         Assert.Equal(HttpStatusCode.InternalServerError, status);
         Assert.Contains("template 'broken'", (string?)answer["error"], StringComparison.Ordinal);
      }

      Assert.True(stopped.Elapsed < TimeSpan.FromSeconds(10), $"The claims answered after {stopped.Elapsed}.");
   }

   [Fact]
   public async Task Seal_refuses_a_build_world_under_another_template_name_and_keeps_the_world()
   {
      var build = (await _host.ControlAsync("template.claim", new JsonObject { ["name"] = "own" }))["build"]!.AsObject();

      var (status, answer) = await _host.SendControlAsync("world.seal", new JsonObject { ["world"] = (string)build["world"]!, ["name"] = "other" });

      Assert.Equal(HttpStatusCode.InternalServerError, status);
      Assert.Contains("seals only as 'own'", (string?)answer["error"], StringComparison.Ordinal);
      Assert.Empty((await OperationAsync(build, "list_tours", "{}"))["tours"]!.AsArray());
   }

   [Fact]
   public async Task Restart_keeps_the_tour_and_issues_a_new_ui_credential()
   {
      var world = await _host.ControlAsync("world.start");
      var baseCommit = await CommitAsync(world, "main", null, "one\n");
      var (tourId, _) = await CreateTourAsync(world, baseCommit, await CommitAsync(world, "feature", baseCommit, "two\n"));

      var restarted = await _host.ControlAsync("world.restart", new JsonObject { ["world"] = (string)world["world"]! });

      Assert.NotEqual((string)world["uiCredential"]!, (string)restarted["uiCredential"]!);
      Assert.Contains((await OperationAsync(restarted, "list_tours", "{}"))["tours"]!.AsArray(), tour => (string?)tour!["id"] == tourId);
   }

   [Fact]
   public async Task Worlds_that_commit_one_branch_name_get_their_own_refs_and_pins()
   {
      var first = await _host.ControlAsync("world.start");
      var second = await _host.ControlAsync("world.start");
      var firstBase = await CommitAsync(first, "main", null, "one\n");
      var secondBase = await CommitAsync(second, "main", null, "one\n");
      var firstHead = await CommitAsync(first, "feature", firstBase, "first\n");
      var secondHead = await CommitAsync(second, "feature", secondBase, "second\n");

      var refs = _host.OriginRefs();
      var firstPin = await PinnedHeadAsync(first, (await CreateTourAsync(first, firstBase, firstHead)).TourId);
      var secondPin = await PinnedHeadAsync(second, (await CreateTourAsync(second, secondBase, secondHead)).TourId);

      Assert.NotEqual(firstHead, secondHead);
      Assert.Equal(firstHead, refs[$"refs/worlds/{(string)first["world"]!}/feature"]);
      Assert.Equal(secondHead, refs[$"refs/worlds/{(string)second["world"]!}/feature"]);
      Assert.Equal((firstHead, secondHead), (firstPin, secondPin));
   }

   [Fact]
   public async Task Host_exits_when_its_standard_input_closes_and_removes_its_directory()
   {
      var temp = Directory.CreateTempSubdirectory("lotsen-host-tests-").FullName;
      try
      {
         using var host = await TestHostProcess.StartAsync(temp);
         await host.ControlAsync("world.start");
         Assert.Single(Directory.GetDirectories(temp, "lotsen-host-*"));

         var exitCode = await host.StopAsync();

         Assert.Equal(0, exitCode);
         Assert.Empty(Directory.GetDirectories(temp, "lotsen-host-*"));
      }
      finally
      {
         Directory.Delete(temp, recursive: true);
      }
   }

   // Commits a.txt with the text on the world's branch.
   [Fact]
   public async Task Quiet_ends_while_a_pin_waits_on_a_pull_request_that_the_test_has_not_set()
   {
      var world = await _host.ControlAsync("world.start");
      var name = (string)world["world"]!;
      var session = (string)(await _host.ControlAsync("claude.open", new JsonObject { ["world"] = name }))["session"]!;
      await ClaudeAsync(world, session, "create_tour", new JsonObject { ["prUrl"] = TestPlatform.PrUrl, ["familiarity"] = "some" });

      await _host.ControlAsync("jobs.quiet", new JsonObject { ["world"] = name });
   }

   [Fact]
   public async Task Quiet_ends_while_a_pin_waits_on_a_held_pull_request_and_waits_for_the_pin_once_the_hold_is_released()
   {
      var world = await _host.ControlAsync("world.start");
      var name = (string)world["world"]!;
      var baseCommit = await CommitAsync(world, "main", null, "one\n");
      var head = await CommitAsync(world, "feature", baseCommit, "two\n");
      await _host.ControlAsync("pr.set", new JsonObject { ["world"] = name, ["source"] = head, ["target"] = baseCommit });
      await _host.ControlAsync("pr.hold", new JsonObject { ["world"] = name });
      var session = (string)(await _host.ControlAsync("claude.open", new JsonObject { ["world"] = name }))["session"]!;
      var tourId = (string)(await ClaudeAsync(world, session, "create_tour", new JsonObject { ["prUrl"] = TestPlatform.PrUrl, ["familiarity"] = "some" }))["id"]!;

      await _host.ControlAsync("jobs.quiet", new JsonObject { ["world"] = name });
      await _host.ControlAsync("pr.release", new JsonObject { ["world"] = name });
      await _host.ControlAsync("jobs.quiet", new JsonObject { ["world"] = name });

      Assert.Equal(head, await PinnedHeadAsync(world, tourId));
   }

   private async Task<string> CommitAsync(JsonObject world, string branch, string? parent, string text)
   {
      var input = new JsonObject { ["world"] = (string)world["world"]!, ["branch"] = branch, ["files"] = new JsonObject { ["a.txt"] = text } };
      if (parent is not null)
      {
         input["parent"] = parent;
      }

      return (string)(await _host.ControlAsync("repo.commit", input))["commit"]!;
   }

   // Opens a session that creates a tour of the pull request from the head into the target, and returns the tour's ID and the session once the pin settled.
   private async Task<(string TourId, string Session)> CreateTourAsync(JsonObject world, string target, string head)
   {
      var name = (string)world["world"]!;
      await _host.ControlAsync("pr.set", new JsonObject { ["world"] = name, ["source"] = head, ["target"] = target });
      var session = (string)(await _host.ControlAsync("claude.open", new JsonObject { ["world"] = name }))["session"]!;
      var created = await ClaudeAsync(world, session, "create_tour", new JsonObject { ["prUrl"] = TestPlatform.PrUrl, ["familiarity"] = "some" });
      await _host.ControlAsync("jobs.idle", new JsonObject { ["world"] = name });
      return ((string)created["id"]!, session);
   }

   // Creates a pinned tour of a.txt changed from "one" to "two", with one written stop on the change.
   private async Task<string> SeedAsync(JsonObject world)
   {
      var baseCommit = await CommitAsync(world, "main", null, "one\n");
      var (tourId, session) = await CreateTourAsync(world, baseCommit, await CommitAsync(world, "feature", baseCommit, "two\n"));
      var plan = JsonNode.Parse(PlanInput)!.AsObject();
      plan["tourId"] = tourId;
      var stopId = (string)(await ClaudeAsync(world, session, "set_plan", plan))["plan"]!["stops"]![0]!["id"]!;
      await ClaudeAsync(world, session, "put_stop", JsonNode.Parse($$"""
         { "tourId": "{{tourId}}", "stopId": "{{stopId}}", "headline": "The text changes.", "passages": [{ "side": "head", "path": "a.txt", "startLine": 1, "endLine": 1, "role": "primary" }] }
         """)!.AsObject());
      return tourId;
   }

   private async Task<string> PinnedHeadAsync(JsonObject world, string tourId)
   {
      var pin = (await OperationAsync(world, "read_state", new JsonObject { ["tourId"] = tourId }.ToJsonString()))["pin"]!;
      Assert.True((string?)pin["state"] == "pinned", $"The tour is not pinned: {pin}");
      return (string)pin["head"]!;
   }

   private async Task<JsonNode> ClaudeAsync(JsonObject world, string session, string operation, JsonObject input)
   {
      var called = await _host.ControlAsync("claude.call", new JsonObject { ["world"] = (string)world["world"]!, ["session"] = session, ["operation"] = operation, ["input"] = input });
      return called["output"] ?? throw new Xunit.Sdk.XunitException($"{operation} was refused: {called["refusal"]}");
   }

   // Calls an operation on the world's server as the UI, requires success and returns the output.
   private async Task<JsonNode> OperationAsync(JsonObject world, string operation, string input)
   {
      using var request = new HttpRequestMessage(HttpMethod.Post, $"{world["url"]}/api/ops/{operation}") { Content = new StringContent(input, Encoding.UTF8, "application/json") };
      request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (string)world["uiCredential"]!);
      using var response = await _host.Client.SendAsync(request, TestContext.Current.CancellationToken);
      var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
      Assert.True(response.IsSuccessStatusCode, $"{operation} returned {(int)response.StatusCode}: {body}");
      return SuccessBody.Output(body);
   }

   /// <summary>One host for the tests of the class, whose temporary directory holds the host's own.</summary>
   public sealed class SharedHost : IAsyncLifetime
   {
      private readonly string _temp = Directory.CreateTempSubdirectory("lotsen-host-tests-").FullName;

      internal TestHostProcess Host { get; private set; } = null!;

      public async ValueTask InitializeAsync() => Host = await TestHostProcess.StartAsync(_temp);

      public async ValueTask DisposeAsync()
      {
         await Host.StopAsync();
         Host.Dispose();
         Directory.Delete(_temp, recursive: true);
      }
   }
}

/// <summary>Runs the built test host as a child process, whose temporary directory is one that the test chose.</summary>
internal sealed class TestHostProcess : IDisposable
{
   private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

   private readonly Process _process;
   private readonly string _token;
   private readonly Task<string> _errors;
   private readonly string _temp;

   private TestHostProcess(Process process, string url, string token, string temp)
   {
      _process = process;
      _token = token;
      _temp = temp;
      _errors = process.StandardError.ReadToEndAsync();
      Url = url;
   }

   /// <summary>The control API's base URL.</summary>
   public string Url { get; }

   public HttpClient Client { get; } = new(new SocketsHttpHandler { UseProxy = false });

   /// <summary>Starts the host with its temporary directory at <paramref name="temp"/>, and reads the control API's address from its first line.</summary>
   /// <remarks>The temporary directory comes from <c>TMPDIR</c> on Unix and from <c>TMP</c> or <c>TEMP</c> on Windows, so the host gets all three.</remarks>
   public static async Task<TestHostProcess> StartAsync(string temp)
   {
      var startInfo = new ProcessStartInfo("dotnet")
      {
         RedirectStandardInput = true,
         RedirectStandardOutput = true,
         RedirectStandardError = true,
         UseShellExecute = false,
      };
      startInfo.ArgumentList.Add(HostPath());
      foreach (var variable in (string[])["TMPDIR", "TMP", "TEMP"])
      {
         startInfo.Environment[variable] = temp;
      }
      var process = Process.Start(startInfo) ?? throw new InvalidOperationException("dotnet did not start.");
      try
      {
         var line = await process.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Wait, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException($"The host wrote no line: {await process.StandardError.ReadToEndAsync()}");
         var first = JsonNode.Parse(line)!;
         return new TestHostProcess(process, (string)first["url"]!, (string)first["token"]!, temp);
      }
      catch
      {
         process.Kill();
         process.Dispose();
         throw;
      }
   }

   /// <summary>Sends a control request, requires success and returns the answer.</summary>
   public async Task<JsonObject> ControlAsync(string verb, JsonObject? body = null)
   {
      var (status, answer) = await SendControlAsync(verb, body);
      Assert.True(status == HttpStatusCode.OK, $"{verb} returned {(int)status}: {answer.ToJsonString()}");
      return answer;
   }

   /// <summary>Sends a control request and returns its status and answer.</summary>
   public async Task<(HttpStatusCode Status, JsonObject Answer)> SendControlAsync(string verb, JsonObject? body = null)
   {
      using var request = new HttpRequestMessage(HttpMethod.Post, $"{Url}/control/{verb}") { Content = new StringContent((body ?? []).ToJsonString(), Encoding.UTF8, "application/json") };
      request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
      using var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);
      var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
      return (response.StatusCode, JsonNode.Parse(text)!.AsObject());
   }

   /// <summary>Returns each ref of the host's shared origin with the commit it points at.</summary>
   public IReadOnlyDictionary<string, string> OriginRefs()
   {
      var origin = Path.Combine(Assert.Single(Directory.GetDirectories(_temp, "lotsen-host-*")), "origin.git");
      var startInfo = new ProcessStartInfo("git") { RedirectStandardOutput = true, UseShellExecute = false };
      startInfo.ArgumentList.Add($"--git-dir={origin}");
      startInfo.ArgumentList.Add("show-ref");
      using var git = Process.Start(startInfo)!;
      var output = git.StandardOutput.ReadToEnd();
      git.WaitForExit();
      return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split(' ')).ToDictionary(parts => parts[1], parts => parts[0], StringComparer.Ordinal);
   }

   /// <summary>Closes the host's standard input, which stops it, and returns its exit code. A host that does not exit in time is killed.</summary>
   public async Task<int> StopAsync()
   {
      if (!_process.HasExited)
      {
         _process.StandardInput.Close();
         using var timeout = new CancellationTokenSource(Wait);
         try
         {
            await _process.WaitForExitAsync(timeout.Token);
         }
         catch (OperationCanceledException)
         {
            _process.Kill();
            await _process.WaitForExitAsync(CancellationToken.None);
            Assert.Fail($"The host did not exit after its standard input closed: {await _errors}");
         }
      }

      return _process.ExitCode;
   }

   public void Dispose()
   {
      if (!_process.HasExited)
      {
         _process.Kill();
         _process.WaitForExit();
      }

      Client.Dispose();
      _process.Dispose();
   }

   // The host's build sits beside this project's, in the same configuration and target framework: tests/Lotsen.TestHost/bin/<configuration>/<framework>.
   private static string HostPath()
   {
      var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
      var configuration = output.Parent!;
      var tests = configuration.Parent!.Parent!.Parent!;
      var path = Path.Combine(tests.FullName, "Lotsen.TestHost", "bin", configuration.Name, output.Name, "Lotsen.TestHost.dll");
      return File.Exists(path) ? path : throw new FileNotFoundException("The test host is not built beside the tests.", path);
   }
}
