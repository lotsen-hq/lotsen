using System.Text.Json.Nodes;
using Lotsen.Hosting;

namespace Lotsen.Tests;

/// <summary>Covers the background server that <c>lotsen mcp</c> starts on demand, and its exit once no agent session uses it, as separate processes.</summary>
[Trait("Category", "Lifecycle")]
public sealed class BackgroundServerTests : IDisposable
{
   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

   private readonly DataDirectory _dataDirectory = new(Path.Combine(Directory.CreateTempSubdirectory("lotsen-tests-").FullName, "data"));

   /// <summary>A stale state file names a process that has exited, so the adapter starts a server as when there is none.</summary>
   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public async Task Tool_call_without_a_running_server_starts_a_background_server_that_the_call_reaches(bool staleStateFile)
   {
      using var stalePort = HeldPort.Refusing();
      if (staleStateFile)
      {
         StateFile.Write(_dataDirectory, new ServerState(ExitedProcessId(), "127.0.0.1", stalePort.Port, "stale-token"));
      }

      using var adapter = AdapterProcess.Start(_dataDirectory.Path, LotsenProcess.WithoutBrowser());
      await adapter.InitializeAsync();

      var result = await adapter.CallToolAsync(2, "open_ui");

      Assert.False(result.IsError, $"{result.Text}\n{adapter.Output}");
      var state = StateFile.TryRead(_dataDirectory);
      Assert.NotNull(state);
      Assert.NotEqual(adapter.Id, state.Pid);
      Assert.Contains($":{state.Port}/#code=", (string)JsonNode.Parse(result.Text)!["url"]!);
   }

   [Fact]
   public async Task Background_server_exits_after_the_grace_period_once_its_adapter_closed_and_removes_its_state_file()
   {
      var grace = TimeSpan.FromSeconds(3);
      using var adapter = AdapterProcess.Start(_dataDirectory.Path, AdapterProcess.WithGracePeriod(grace));
      await adapter.InitializeAsync();
      await adapter.ReachServerAsync(2);
      var server = StateFile.TryRead(_dataDirectory)!;

      adapter.CloseInput();
      Assert.True(await adapter.WaitForExitAsync(Timeout), $"The adapter did not exit:\n{adapter.Output}");
      var runningAfterTheAdapter = BackgroundServerProcesses.IsRunning(server.Pid);
      await BackgroundServerProcesses.WaitForExitAsync(server.Pid, grace + Timeout, () => "The background server did not exit after its grace period.");

      Assert.True(runningAfterTheAdapter, "The background server exited with its adapter, before its grace period.");
      Assert.False(File.Exists(_dataDirectory.StateFilePath), "The background server left its state file.");
      using var claim = ClaimLock.TryTake(_dataDirectory);
      Assert.NotNull(claim);
   }

   [Fact]
   public async Task Two_adapters_started_together_share_one_server()
   {
      using var one = AdapterProcess.Start(_dataDirectory.Path, LotsenProcess.WithoutBrowser());
      using var other = AdapterProcess.Start(_dataDirectory.Path, LotsenProcess.WithoutBrowser());
      await Task.WhenAll(one.InitializeAsync(), other.InitializeAsync());

      var results = await Task.WhenAll(one.CallToolAsync(2, "open_ui"), other.CallToolAsync(2, "open_ui"));

      Assert.All(results, result => Assert.False(result.IsError, $"{result.Text}\n{one.Output}\n{other.Output}"));
      var server = StateFile.TryRead(_dataDirectory)!;
      Assert.All(results, result => Assert.Contains($":{server.Port}/", (string)JsonNode.Parse(result.Text)!["url"]!));
      if (OperatingSystem.IsLinux())
      {
         await Poll.UntilAsync(
            () => BackgroundServerProcesses.Running(_dataDirectory) is [var only] && only == server.Pid ? "one" : null,
            Timeout,
            () => $"Other background servers still run beside {server.Pid}: {string.Join(", ", BackgroundServerProcesses.Running(_dataDirectory))}");
      }
   }

   /// <summary>The adapter leads its own process group, as a foreground job in a terminal does, so the group's SIGINT is a Ctrl-C in that terminal.</summary>
   [Fact]
   public async Task Ctrl_c_in_the_adapters_terminal_leaves_the_background_server_running()
   {
      Assert.SkipUnless(OperatingSystem.IsLinux(), "The test reads the process groups from /proc and runs setsid.");
      using var adapter = AdapterProcess.Start(_dataDirectory.Path, AdapterProcess.WithGracePeriod(TimeSpan.FromSeconds(20)), ownProcessGroup: true);
      await adapter.InitializeAsync();
      await adapter.ReachServerAsync(2);
      var server = StateFile.TryRead(_dataDirectory)!;
      var adapterGroup = ProcessGroup(adapter.Id);

      adapter.InterruptProcessGroup();
      var adapterExited = await adapter.WaitForExitAsync(Timeout);
      await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

      Assert.Equal(adapter.Id, adapterGroup);
      Assert.True(adapterExited, $"The adapter ignored the SIGINT:\n{adapter.Output}");
      Assert.True(BackgroundServerProcesses.IsRunning(server.Pid), "The SIGINT to the adapter's process group ended the background server.");
      Assert.NotEqual(adapterGroup, ProcessGroup(server.Pid));
      using var next = AdapterProcess.Start(_dataDirectory.Path);
      await next.InitializeAsync();
      await next.ReachServerAsync(2);
      Assert.Equal(server, StateFile.TryRead(_dataDirectory));
   }

   [Fact]
   public async Task Background_server_that_no_adapter_reaches_exits_after_the_grace_period()
   {
      using var daemon = LotsenProcess.Start(_dataDirectory.Path, AdapterProcess.WithGracePeriod(TimeSpan.FromMilliseconds(500)), BackgroundServer.Command);
      await Poll.UntilAsync(() => StateFile.TryRead(_dataDirectory) is { } state && state.Pid == daemon.Id ? state : null, Timeout, () => $"The background server wrote no state file:\n{daemon.Output}");

      await daemon.WaitForExitAsync(Timeout);

      Assert.Equal(0, daemon.ExitCode);
      Assert.Empty(daemon.Output);
      Assert.False(File.Exists(_dataDirectory.StateFilePath));
   }

   /// <summary>A start that loses the claim lock leaves the server that holds it, and that server's log, as they are.</summary>
   [Fact]
   public async Task Background_server_that_loses_the_claim_lock_exits_0_quietly()
   {
      using var serve = LotsenProcess.Start(_dataDirectory.Path, null, "serve");
      var state = await Poll.UntilAsync(() => StateFile.TryRead(_dataDirectory), Timeout, () => $"lotsen serve wrote no state file:\n{serve.Output}");

      using var daemon = LotsenProcess.Start(_dataDirectory.Path, null, BackgroundServer.Command);
      await daemon.WaitForExitAsync(Timeout);

      Assert.Equal(0, daemon.ExitCode);
      Assert.Empty(daemon.Output);
      Assert.False(File.Exists(_dataDirectory.LogFilePath), "The losing start wrote a log file.");
      Assert.False(serve.HasExited, $"lotsen serve exited:\n{serve.Output}");
      Assert.Equal(state, StateFile.TryRead(_dataDirectory));
   }

   [Fact]
   public async Task Serve_with_no_clients_keeps_running_after_the_grace_period()
   {
      using var serve = LotsenProcess.Start(_dataDirectory.Path, AdapterProcess.WithGracePeriod(TimeSpan.FromMilliseconds(200)), "serve");
      var state = await Poll.UntilAsync(() => StateFile.TryRead(_dataDirectory), Timeout, () => $"lotsen serve wrote no state file:\n{serve.Output}");

      await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

      Assert.False(serve.HasExited, $"lotsen serve exited:\n{serve.Output}");
      Assert.Equal(state, StateFile.TryRead(_dataDirectory));
   }

   [Fact]
   public async Task Start_that_fails_fails_the_tool_call_with_the_reason_from_the_log()
   {
      using var held = HeldPort.Listening();
      using var adapter = AdapterProcess.Start(_dataDirectory.Path, new Dictionary<string, string> { [ServerPort.Variable] = held.Port.ToString() });
      await adapter.InitializeAsync();

      var result = await adapter.CallToolAsync(2, "open_ui");

      Assert.True(result.IsError);
      Assert.Contains(_dataDirectory.LogFilePath, result.Text);
      Assert.Contains($"Port {held.Port}", result.Text);
      Assert.Null(StateFile.TryRead(_dataDirectory));
   }

   /// <summary>The background server reads the variable from the environment that it inherits from the adapter, so a value that is not a size fails its start.</summary>
   [Fact]
   public async Task Start_with_a_fact_cache_variable_that_is_not_a_size_fails_the_tool_call_naming_the_variable()
   {
      using var adapter = AdapterProcess.Start(_dataDirectory.Path, new Dictionary<string, string> { [FactCacheSize.Variable] = "abc" });
      await adapter.InitializeAsync();

      var result = await adapter.CallToolAsync(2, "open_ui");

      Assert.True(result.IsError);
      Assert.Contains(_dataDirectory.LogFilePath, result.Text);
      Assert.Contains($"{FactCacheSize.Variable} is 'abc'", result.Text);
      Assert.Null(StateFile.TryRead(_dataDirectory));
   }

   public void Dispose() => BackgroundServerProcesses.CleanUp(_dataDirectory);

   private static int ProcessGroup(int pid)
   {
      // The command name in /proc/<pid>/stat is in parentheses and may hold spaces, so the fields after it are counted from the last one.
      var stat = File.ReadAllText($"/proc/{pid}/stat");
      return int.Parse(stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[2], System.Globalization.CultureInfo.InvariantCulture);
   }

   private static int ExitedProcessId()
   {
      using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", "--version") { RedirectStandardOutput = true })!;
      process.WaitForExit();
      return process.Id;
   }
}
