using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Lotsen.Client;
using Lotsen.Hosting;
using Lotsen.Json;
using Xunit.Sdk;

namespace Lotsen.Tests;

/// <summary>Covers the claim lock, the configured port, <c>lotsen stop</c> and <c>lotsen restart</c>, as separate processes.</summary>
[Trait("Category", "Lifecycle")]
public sealed class LifecycleTests : IDisposable
{
   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

   // The server that the restart starts has no session, so it exits when its grace period ends and removes its state file. The tests read that file and reach the server after the restart
   // returns, so the period outlasts a slow restart, and Dispose kills the server by the PID in the file.
   private static readonly TimeSpan RestartedServerGracePeriod = TimeSpan.FromMinutes(5);

   private const int PortAttempts = 5;

   private readonly DataDirectory _dataDirectory = new(Path.Combine(Directory.CreateTempSubdirectory("lotsen-tests-").FullName, "data"));

   [Fact]
   public async Task Second_server_on_the_same_data_directory_exits_1_naming_the_first_ones_pid_and_the_first_keeps_running()
   {
      using var first = LotsenProcess.Start(_dataDirectory.Path, null, "serve");
      var state = await WaitForStateAsync(first);

      using var second = LotsenProcess.Start(_dataDirectory.Path, null, "serve");
      await second.WaitForExitAsync(Timeout);

      Assert.Equal(1, second.ExitCode);
      Assert.Contains($"PID {first.Id}", second.StandardError);
      Assert.Contains("`lotsen stop`", second.StandardError);
      Assert.Empty(second.StandardOutput);
      Assert.False(first.HasExited, $"The first server exited:\n{first.Output}");
      Assert.Equal(state, StateFile.TryRead(_dataDirectory));
      Assert.True(await AcceptsConnectionAsync(state.Port));
   }

   /// <summary>Both servers start at once, so they race for the claim lock before either writes the state file.</summary>
   [Fact]
   public async Task Two_starts_at_once_leave_one_server_running()
   {
      using var one = LotsenProcess.Start(_dataDirectory.Path, null, "serve");
      using var other = LotsenProcess.Start(_dataDirectory.Path, null, "serve");

      var state = await Poll.UntilAsync(() => StateFile.TryRead(_dataDirectory), Timeout, () => $"No server wrote the state file:\n{one.Output}\n{other.Output}");
      var (winner, loser) = state.Pid == one.Id ? (one, other) : (other, one);
      await loser.WaitForExitAsync(Timeout);

      Assert.Equal(1, loser.ExitCode);
      Assert.Contains("Another lotsen server", loser.StandardError);
      Assert.False(winner.HasExited, $"The winning server exited:\n{winner.Output}");
      Assert.Equal(state, StateFile.TryRead(_dataDirectory));
   }

   [Fact]
   public async Task Killed_server_leaves_a_lock_that_the_next_start_takes_over()
   {
      using var killed = LotsenProcess.Start(_dataDirectory.Path, null, "serve");
      var stale = await WaitForStateAsync(killed);
      killed.Kill();
      await killed.WaitForExitAsync(Timeout);
      Assert.True(File.Exists(_dataDirectory.StateFilePath), "The killed server removed its state file, so the test shows no take-over of a stale one.");

      using var next = LotsenProcess.Start(_dataDirectory.Path, null, "serve");
      var state = await Poll.UntilAsync(() => StateFile.TryRead(_dataDirectory) is { } read && read.Pid == next.Id ? read : null, Timeout, () => $"The next server took no state file over:\n{next.Output}");

      Assert.NotEqual(stale.Pid, state.Pid);
      Assert.False(next.HasExited, $"The next server exited:\n{next.Output}");
      Assert.True(await AcceptsConnectionAsync(state.Port));
   }

   /// <summary>With .NET's file locking off, the claim lock does not refuse the second server, so both run and the second writes the state file over the first one's.</summary>
   [Fact]
   public async Task Exiting_server_keeps_the_state_file_that_another_server_wrote()
   {
      Assert.SkipWhen(OperatingSystem.IsWindows(), "The variable turns off file locking on Unix only.");
      var unlocked = new Dictionary<string, string> { ["DOTNET_SYSTEM_IO_DISABLEFILELOCKING"] = "1" };
      using var first = LotsenProcess.Start(_dataDirectory.Path, unlocked, "serve");
      await WaitForStateAsync(first);
      using var second = LotsenProcess.Start(_dataDirectory.Path, unlocked, "serve");
      var state = await WaitForStateAsync(second);

      first.Interrupt();
      await first.WaitForExitAsync(Timeout);

      Assert.Equal(0, first.ExitCode);
      Assert.Equal(state, StateFile.TryRead(_dataDirectory));
   }

   [Fact]
   public async Task Port_option_makes_the_server_listen_on_that_loopback_port()
   {
      using var serve = await StartOnFreePortAsync(port => (null, ["serve", "--port", port.ToString()]));

      Assert.Equal(serve.Port, serve.State.Port);
      Assert.Equal("127.0.0.1", serve.State.Address);
      Assert.True(await AcceptsConnectionAsync(serve.Port));
      Assert.Contains($"http://127.0.0.1:{serve.Port}", serve.Process.StandardOutput);
   }

   [Fact]
   public async Task Port_variable_makes_the_server_listen_on_that_loopback_port()
   {
      using var serve = await StartOnFreePortAsync(port => (PortVariable(port.ToString()), ["serve"]));

      Assert.Equal(serve.Port, serve.State.Port);
      Assert.True(await AcceptsConnectionAsync(serve.Port));
   }

   /// <summary>
   /// The variable names a port that another socket holds, so a server that read the variable would fail to start.
   /// <see cref="StartOnFreePortAsync"/> tries again only when the option's port is in use, so that failure fails the test.
   /// </summary>
   [Fact]
   public async Task Port_option_wins_over_the_variable()
   {
      using var held = HeldPort.Listening();

      using var serve = await StartOnFreePortAsync(port => (PortVariable(held.Port.ToString()), ["serve", "--port", port.ToString()]));

      Assert.Equal(serve.Port, serve.State.Port);
   }

   /// <summary>The option wins, so a variable that is no port number does not stop the start.</summary>
   [Fact]
   public async Task Port_option_ignores_a_variable_that_is_not_a_port_number()
   {
      using var serve = await StartOnFreePortAsync(port => (PortVariable("abc"), ["serve", "--port", port.ToString()]));

      Assert.Equal(serve.Port, serve.State.Port);
   }

   /// <summary>A listener holds the first port that the source returns, as another bind between the probe and the start would.</summary>
   [Fact]
   public async Task Start_on_a_free_port_tries_another_port_when_another_socket_took_the_first()
   {
      using var held = HeldPort.Listening();
      var returned = new List<int>();
      int Ports()
      {
         var port = returned.Count == 0 ? held.Port : FreePort();
         returned.Add(port);
         return port;
      }

      using var started = await StartOnFreePortAsync(port => (null, ["serve", "--port", port.ToString()]), Ports);

      Assert.Equal(held.Port, returned[0]);
      Assert.Equal(returned[^1], started.Port);
      Assert.Equal(started.Port, started.State.Port);
   }

   [Fact]
   public async Task Empty_port_variable_leaves_the_port_to_the_os()
   {
      using var serve = LotsenProcess.Start(_dataDirectory.Path, PortVariable(""), "serve");
      var state = await WaitForStateAsync(serve);

      Assert.NotEqual(0, state.Port);
   }

   [Fact]
   public async Task Port_that_another_socket_holds_exits_1_naming_the_port_and_writes_no_state_file()
   {
      using var held = HeldPort.Listening();

      using var serve = LotsenProcess.Start(_dataDirectory.Path, null, "serve", "--port", held.Port.ToString());
      await serve.WaitForExitAsync(Timeout);

      Assert.Equal(1, serve.ExitCode);
      Assert.Contains($"Port {held.Port}", serve.StandardError);
      Assert.Contains("--port", serve.StandardError);
      Assert.Contains("LOTSEN_PORT", serve.StandardError);
      Assert.Empty(serve.StandardOutput);
      Assert.False(File.Exists(_dataDirectory.StateFilePath));
   }

   /// <summary>The OS refuses an unprivileged bind of a privileged port with a reason other than a port in use.</summary>
   [Fact]
   public async Task Port_that_the_os_refuses_exits_1_naming_the_port_and_writes_no_state_file()
   {
      const int privileged = HeldPort.Privileged;
      Assert.SkipUnless(HeldPort.BindRefusal(privileged) == SocketError.AccessDenied, $"This process may bind port {privileged}, or another socket holds it.");

      using var serve = LotsenProcess.Start(_dataDirectory.Path, PortVariable(privileged.ToString()), "serve");
      await serve.WaitForExitAsync(Timeout);

      Assert.Equal(1, serve.ExitCode);
      Assert.Contains($"Port {privileged}", serve.StandardError);
      Assert.Contains("--port", serve.StandardError);
      Assert.DoesNotContain("Unhandled exception", serve.StandardError);
      Assert.Empty(serve.StandardOutput);
      Assert.False(File.Exists(_dataDirectory.StateFilePath));
   }

   /// <summary>A start that failed on the port releases the claim lock as it exits, so the next start on the same data directory takes it.</summary>
   [Fact]
   public async Task Start_after_a_port_in_use_takes_the_claim_lock()
   {
      using var held = HeldPort.Listening();
      using var failed = LotsenProcess.Start(_dataDirectory.Path, PortVariable(held.Port.ToString()), "serve");
      await failed.WaitForExitAsync(Timeout);
      Assert.Equal(1, failed.ExitCode);

      using var next = LotsenProcess.Start(_dataDirectory.Path, null, "serve");
      var state = await WaitForStateAsync(next);

      Assert.Equal(next.Id, state.Pid);
   }

   [Theory]
   [InlineData("abc")]
   [InlineData("0")]
   [InlineData("65536")]
   [InlineData("-1")]
   [InlineData("+80")]
   [InlineData(" 8080")]
   [InlineData("99999999999999999999")]
   public async Task Port_variable_that_is_not_a_port_number_exits_1_naming_the_variable(string value)
   {
      using var serve = LotsenProcess.Start(_dataDirectory.Path, PortVariable(value), "serve");
      await serve.WaitForExitAsync(Timeout);

      Assert.Equal(1, serve.ExitCode);
      Assert.Contains("LOTSEN_PORT", serve.StandardError);
      Assert.Empty(serve.StandardOutput);
      Assert.False(File.Exists(_dataDirectory.StateFilePath));
   }

   [Fact]
   public async Task Lotsen_stop_ends_a_foreground_server_and_exits_0_once_the_state_file_is_gone()
   {
      using var serve = LotsenProcess.Start(_dataDirectory.Path, null, "serve");
      var state = await WaitForStateAsync(serve);

      var (exitCode, standardOutput, standardError) = await LotsenProcess.RunSeparatelyAsync(_dataDirectory.Path, "stop");
      var stateFileLeft = File.Exists(_dataDirectory.StateFilePath);
      var serverRunning = BackgroundServerProcesses.IsRunning(state.Pid);

      Assert.Equal(0, exitCode);
      Assert.Contains($"PID {state.Pid}", standardOutput);
      Assert.Empty(standardError);
      Assert.False(stateFileLeft, "The state file was there when lotsen stop returned.");
      Assert.False(serverRunning, "The server ran when lotsen stop returned.");
      await serve.WaitForExitAsync(Timeout);
      Assert.Equal(0, serve.ExitCode);
   }

   [Fact]
   public async Task Lotsen_stop_ends_a_background_server_and_exits_0_once_the_state_file_is_gone()
   {
      using var daemon = LotsenProcess.Start(_dataDirectory.Path, AdapterProcess.WithGracePeriod(TimeSpan.FromMinutes(5)), BackgroundServer.Command);
      var state = await WaitForStateAsync(daemon);

      var (exitCode, standardOutput, _) = await LotsenProcess.RunSeparatelyAsync(_dataDirectory.Path, "stop");
      var stateFileLeft = File.Exists(_dataDirectory.StateFilePath);

      Assert.Equal(0, exitCode);
      Assert.Contains($"PID {state.Pid}", standardOutput);
      Assert.False(stateFileLeft, "The state file was there when lotsen stop returned.");
      await daemon.WaitForExitAsync(Timeout);
      Assert.Equal(0, daemon.ExitCode);
      using var claim = ClaimLock.TryTake(_dataDirectory);
      Assert.NotNull(claim);
   }

   [Fact]
   public async Task Lotsen_stop_without_a_running_server_exits_0_saying_that_none_runs()
   {
      var (exitCode, standardOutput, standardError) = await LotsenProcess.RunSeparatelyAsync(_dataDirectory.Path, "stop");

      Assert.Equal(0, exitCode);
      Assert.Contains("No lotsen server is running", standardOutput);
      Assert.Empty(standardError);
   }

   /// <summary>The adapter holds its session on the background server when <c>lotsen stop</c> runs, and Claude calls no tool for a while after it.</summary>
   [Fact]
   public async Task Lotsen_stop_leaves_a_connected_adapter_without_a_server_until_claude_calls_a_tool()
   {
      using var adapter = AdapterProcess.Start(_dataDirectory.Path, AdapterProcess.WithGracePeriod(TimeSpan.FromMinutes(5)));
      await adapter.InitializeAsync();
      await adapter.ReachServerAsync(2);
      var stopped = StateFile.TryRead(_dataDirectory)!;

      using var stoppedProcess = BackgroundServerProcesses.Hold(stopped.Pid);
      var (exitCode, standardOutput, standardError) = await LotsenProcess.RunSeparatelyAsync(_dataDirectory.Path, "stop");
      await Task.Delay(Adapter.ServerConnection.RetryInterval * 4, TestContext.Current.CancellationToken);

      Assert.True(exitCode == 0, $"lotsen stop exited {exitCode}:\n{standardOutput}\n{standardError}");
      Assert.True(stoppedProcess.HasExited, $"The stopped server still runs. lotsen stop wrote:\n{standardOutput}\n{standardError}");
      Assert.Null(StateFile.TryRead(_dataDirectory));
      Assert.Empty(BackgroundServerProcesses.Running(_dataDirectory));
      Assert.False(adapter.HasExited, $"The adapter exited:\n{adapter.Output}");
      await adapter.ReachServerAsync(3);
      Assert.NotEqual(stopped.Pid, StateFile.TryRead(_dataDirectory)!.Pid);
   }

   [Fact]
   public async Task Lotsen_restart_replaces_the_running_server_with_a_new_process_of_this_build_that_an_adapter_reaches()
   {
      using var adapter = AdapterProcess.Start(_dataDirectory.Path, LotsenProcess.WithoutBrowser(AdapterProcess.WithGracePeriod(TimeSpan.FromMinutes(5))));
      var steps = new StepLog(_dataDirectory, adapter);
      await steps.RunAsync("initialize", adapter.InitializeAsync);
      var reached = await steps.RunAsync("the first open_ui", () => adapter.CallToolAsync(2, "open_ui"));
      steps.Expect(!reached.IsError, $"The first open_ui failed: {reached.Text}");
      var replaced = StateFile.TryRead(_dataDirectory);
      steps.Expect(replaced is not null, "The first open_ui left no state file.");
      using var replacedProcess = steps.Run("holding the replaced server's process", () => BackgroundServerProcesses.Hold(replaced.Pid));

      var (exitCode, standardOutput, standardError) = await steps.RunAsync("lotsen restart", RestartWithoutBrowserAsync);
      steps.Attach($"lotsen restart exited {exitCode}. Its standard output", standardOutput);
      steps.Attach("Its standard error", standardError);
      var state = StateFile.TryRead(_dataDirectory);

      steps.Expect(exitCode == 0, $"lotsen restart exited {exitCode}.");
      steps.Expect(state is not null, "lotsen restart left no state file.");
      steps.Expect(replaced.Pid != state.Pid, $"The state file still names the replaced server, PID {replaced.Pid}.");
      steps.Expect(replacedProcess.HasExited, $"The replaced server, PID {replaced.Pid}, still runs.");
      steps.Expect(standardOutput.Contains($"http://127.0.0.1:{state.Port}"), $"lotsen restart did not print the address of port {state.Port}.");
      steps.Expect(standardOutput.Contains($"PID {state.Pid}"), $"lotsen restart did not print the PID {state.Pid}.");
      using (var client = new ServerClient(state))
      {
         var version = await steps.RunAsync("the version request", () => client.GetVersionAsync(TestContext.Current.CancellationToken));
         steps.Expect(version == Program.Version, $"The new server answered version {version}, not {Program.Version}.");
      }

      var result = await steps.RunAsync("the last open_ui", () => adapter.CallToolAsync(3, "open_ui"));
      steps.Expect(!result.IsError, $"The last open_ui failed: {result.Text}");
      var url = steps.Run("reading the last open_ui's URL", () => (string?)JsonNode.Parse(result.Text)?["url"]);
      steps.Expect(url is not null && url.Contains($":{state.Port}/#code="), $"The last open_ui returned {result.Text}, which names no URL on port {state.Port}.");
   }

   [Fact]
   public async Task Lotsen_restart_without_a_running_server_starts_a_background_server_and_prints_its_address()
   {
      var (exitCode, standardOutput, standardError) = await RestartAsync();
      var state = StateFile.TryRead(_dataDirectory);

      Assert.True(exitCode == 0, $"lotsen restart exited {exitCode}:\n{standardOutput}\n{standardError}");
      Assert.NotNull(state);
      Assert.Contains($"http://127.0.0.1:{state.Port}", standardOutput);
      Assert.DoesNotContain("Stopped", standardOutput);
      Assert.Contains(state.Pid, BackgroundServerProcesses.Running(_dataDirectory));
   }

   /// <summary>
   /// Race: an adapter's first tool call starts a server while the restart runs. A restart that finds no server starts a second one, and the claim lock keeps one of the two. A restart that
   /// finds the adapter's server stops it, so the first call's URL can name the stopped server.
   /// </summary>
   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public async Task Lotsen_restart_racing_an_adapters_start_leaves_one_server_at_the_address_that_both_report(bool firstCallEndsFirst)
   {
      using var adapter = AdapterProcess.Start(_dataDirectory.Path, LotsenProcess.WithoutBrowser(AdapterProcess.WithGracePeriod(TimeSpan.FromMinutes(5))));
      var steps = new StepLog(_dataDirectory, adapter);
      await steps.RunAsync("initialize", adapter.InitializeAsync);

      var call = adapter.CallToolAsync(2, "open_ui");
      ToolResult? ended = null;
      ServerState? adapterServer = null;
      if (firstCallEndsFirst)
      {
         ended = await steps.RunAsync("the first open_ui", () => call);
         steps.Expect(!ended.IsError, $"The first open_ui failed: {ended.Text}");
         adapterServer = StateFile.TryRead(_dataDirectory);
         steps.Expect(adapterServer is not null, "The first open_ui left no state file.");
      }

      var (exitCode, standardOutput, standardError) = await steps.RunAsync("lotsen restart", RestartWithoutBrowserAsync);
      steps.Attach($"lotsen restart exited {exitCode}. Its standard output", standardOutput);
      steps.Attach("Its standard error", standardError);
      var result = ended ?? await steps.RunAsync("the first open_ui", () => call);
      steps.Attach("The first open_ui returned", result.Text);
      var state = StateFile.TryRead(_dataDirectory);

      steps.Expect(exitCode == 0, $"lotsen restart exited {exitCode}.");
      steps.Expect(state is not null, "lotsen restart left no state file.");
      steps.Expect(standardOutput.Contains($"http://127.0.0.1:{state.Port}"), $"lotsen restart did not print the address of port {state.Port}.");
      var stopped = CapturedNumber(Regex.Match(standardOutput, @"Stopped the lotsen server \(PID (\d+)\)\."));
      if (adapterServer is not null)
      {
         steps.Expect(stopped == adapterServer.Pid, $"lotsen restart did not report that it stopped the adapter's server, PID {adapterServer.Pid}.");
      }

      if (!result.IsError)
      {
         var url = steps.Run("reading the first open_ui's URL", () => (string?)JsonNode.Parse(result.Text)?["url"]);
         var port = url is null ? null : CapturedNumber(Regex.Match(url, @":(\d+)/#code="));
         steps.Expect(port is not null, "The first open_ui's URL names no port with a code.");

         if (adapterServer is not null)
         {
            steps.Expect(port == adapterServer.Port, $"The first open_ui's URL names port {port}, not the port {adapterServer.Port} of the adapter's server, which lotsen restart stopped.");
         }
         else
         {
            // Only the firstCallEndsFirst row knows the stopped server's port, so this row accepts any port once the restart reports a stop of another server.
            var named = port == state.Port || stopped is { } pid && pid != state.Pid;
            steps.Expect(named, $"The first open_ui's URL names port {port}, which is neither the final server's port {state.Port} nor that of a server that lotsen restart reported it stopped.");
         }
      }

      var again = await steps.RunAsync("the last open_ui", () => adapter.CallToolAsync(3, "open_ui"));
      steps.Expect(!again.IsError, $"The last open_ui failed: {again.Text}");
      var lastUrl = steps.Run("reading the last open_ui's URL", () => (string?)JsonNode.Parse(again.Text)?["url"]);
      steps.Expect(lastUrl is not null && lastUrl.Contains($":{state.Port}/#code="), $"The last open_ui returned {again.Text}, which names no URL on port {state.Port}.");
      if (OperatingSystem.IsLinux())
      {
         await steps.RunAsync(
            "waiting for one background server",
            () => Poll.UntilAsync(
               () => BackgroundServerProcesses.Running(_dataDirectory) is [var only] && only == state.Pid ? "one" : null,
               Timeout,
               () => $"Other background servers still run beside {state.Pid}: {string.Join(", ", BackgroundServerProcesses.Running(_dataDirectory))}"));
      }
   }

   /// <summary>The server runs with .NET's file locking off, so it holds no OS lock on the claim lock file and answers all the same.</summary>
   [Fact]
   public async Task Lotsen_stop_ends_a_server_that_holds_no_claim_lock()
   {
      Assert.SkipWhen(OperatingSystem.IsWindows(), "The variable turns off file locking on Unix only.");
      using var serve = LotsenProcess.Start(_dataDirectory.Path, UnlockedFiles, "serve");
      var state = await WaitForStateAsync(serve);

      var (exitCode, standardOutput, standardError) = await LotsenProcess.RunSeparatelyAsync(_dataDirectory.Path, "stop");
      await serve.WaitForExitAsync(Timeout);

      Assert.True(exitCode == 0, $"lotsen stop exited {exitCode}:\n{standardOutput}\n{standardError}");
      Assert.Contains($"PID {state.Pid}", standardOutput);
      Assert.Null(StateFile.TryRead(_dataDirectory));
   }

   /// <summary>The replaced server runs with .NET's file locking off, so it holds no OS lock, and a second server beside it would find the claim lock free.</summary>
   [Fact]
   public async Task Lotsen_restart_replaces_a_server_that_holds_no_claim_lock_and_leaves_one_server()
   {
      Assert.SkipWhen(OperatingSystem.IsWindows(), "The variable turns off file locking on Unix only.");
      using var serve = LotsenProcess.Start(_dataDirectory.Path, UnlockedFiles, "serve");
      var replaced = await WaitForStateAsync(serve);

      var (exitCode, standardOutput, standardError) = await RestartAsync();
      var state = StateFile.TryRead(_dataDirectory);

      Assert.True(exitCode == 0, $"lotsen restart exited {exitCode}:\n{standardOutput}\n{standardError}");
      Assert.True(serve.HasExited, $"The replaced server still runs:\n{serve.Output}");
      Assert.NotNull(state);
      Assert.NotEqual(replaced.Pid, state.Pid);
      Assert.Contains($"PID {state.Pid}", standardOutput);
      Assert.Equal([state.Pid], BackgroundServerProcesses.Running(_dataDirectory));
   }

   /// <summary>
   /// The state file names the live test process and a port that another HTTP server holds, as when a crashed server's PID and port were both reused. The listener answers after a delay, so
   /// the restart's own server can take the claim lock while an answer is on its way.
   /// </summary>
   [Fact]
   public async Task Lotsen_stop_and_restart_with_a_stale_state_file_whose_port_another_http_server_holds_leave_it_alone_and_start_a_server()
   {
      await using var foreign = new RecordingHttpServer(answer: _ =>
      {
         Thread.Sleep(60);
         return new HttpAnswer(200, "<html></html>", "application/json; charset=utf-8");
      });
      StateFile.Write(_dataDirectory, new ServerState(Environment.ProcessId, "127.0.0.1", foreign.BaseUrl.Port, "stale-token"));

      var (stopExitCode, stopOutput, stopError) = await LotsenProcess.RunSeparatelyAsync(_dataDirectory.Path, "stop");
      var stopRequests = foreign.Requests;
      var (exitCode, standardOutput, standardError) = await RestartAsync();
      var state = StateFile.TryRead(_dataDirectory);

      Assert.True(stopExitCode == 0, $"lotsen stop exited {stopExitCode}:\n{stopOutput}\n{stopError}");
      Assert.Contains("No lotsen server is running", stopOutput);
      Assert.DoesNotContain(stopRequests, request => request.Contains("Authorization:", StringComparison.OrdinalIgnoreCase));
      Assert.True(exitCode == 0, $"lotsen restart exited {exitCode}:\n{standardOutput}\n{standardError}");
      Assert.NotNull(state);
      Assert.Contains(state.Pid, BackgroundServerProcesses.Running(_dataDirectory));
      Assert.Contains($"http://127.0.0.1:{state.Port}", standardOutput);
   }

   /// <summary>
   /// Race: a server of another build takes the data directory between the restart's stop and its start, as an adapter of another install would. The test holds the claim lock, so the started
   /// server loses it. The stopped server is a <c>lotsen serve</c> on another data directory, which the stand-in ends when the shutdown arrives, before it writes the state file of the other build.
   /// </summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public async Task Lotsen_restart_answered_by_another_build_exits_1_naming_that_build_and_the_fix(bool versionsReadable)
   {
      var other = Program.Version with { Build = "0.0.0 (another build)" };
      using var stopped = LotsenProcess.Start(Path.Combine(Path.GetDirectoryName(_dataDirectory.Path)!, "stopped"), null, "serve");
      using var claim = ClaimLock.TryTake(_dataDirectory) ?? throw new InvalidOperationException("The claim lock is held.");
      var port = 0;
      await using var server = new RecordingHttpServer(answer: target =>
      {
         switch (target)
         {
            case "/api/shutdown":
               stopped.Kill();
               StateFile.Write(_dataDirectory, new ServerState(Environment.ProcessId, "127.0.0.1", port, "token"));
               return new HttpAnswer(202);
            case "/api/version":
               return new HttpAnswer(200, versionsReadable ? JsonSerializer.Serialize(other, LotsenJsonContext.Default.VersionInfo) : "<html></html>");
            default:
               return new HttpAnswer(404);
         }
      });
      port = server.BaseUrl.Port;
      StateFile.Write(_dataDirectory, new ServerState(stopped.Id, "127.0.0.1", port, "token"));

      var (exitCode, standardOutput, standardError) = await RestartAsync();

      Assert.True(exitCode == 1, $"lotsen restart exited {exitCode}:\n{standardOutput}\n{standardError}");
      Assert.Contains($"PID {Environment.ProcessId}", standardError);
      Assert.Contains(versionsReadable ? $"build {other.Build}" : "could not be read", standardError);
      Assert.Contains($"build {Program.Version.Build}", standardError);
      Assert.Contains("`lotsen restart`", standardError);
      Assert.DoesNotContain("listening", standardOutput);
   }

   public void Dispose() => BackgroundServerProcesses.CleanUp(_dataDirectory);

   private Task<(int ExitCode, string StandardOutput, string StandardError)> RestartAsync() =>
      LotsenProcess.RunSeparatelyAsync(_dataDirectory.Path, AdapterProcess.WithGracePeriod(RestartedServerGracePeriod), "restart");

   // The server that the restart starts inherits the restart's browser, so its open_ui returns a link to read.
   private Task<(int ExitCode, string StandardOutput, string StandardError)> RestartWithoutBrowserAsync() =>
      LotsenProcess.RunSeparatelyAsync(_dataDirectory.Path, LotsenProcess.WithoutBrowser(AdapterProcess.WithGracePeriod(RestartedServerGracePeriod)), "restart");

   private static int? CapturedNumber(Match match) => match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;

   private static Dictionary<string, string> UnlockedFiles => new() { ["DOTNET_SYSTEM_IO_DISABLEFILELOCKING"] = "1" };

   private static Dictionary<string, string> PortVariable(string value) => new() { ["LOTSEN_PORT"] = value };

   /// <summary>Returns a loopback port that nothing holds now. Another bind to port 0 can still take it before the server binds it, so <see cref="StartOnFreePortAsync"/> then tries another port.</summary>
   private static int FreePort()
   {
      using var probe = HeldPort.Refusing();
      return probe.Port;
   }

   /// <summary>Starts a server on a port from <paramref name="ports"/> and waits for its state file. A start that finds that port in use runs again on the next port.</summary>
   /// <param name="start">The variables and the arguments that start a server on the given port.</param>
   /// <param name="ports">The source of the ports to try, or null for <see cref="FreePort"/>.</param>
   /// <remarks>Any other exit fails the test with the output of the process, and so does a port in use on each of <see cref="PortAttempts"/> starts.</remarks>
   private async Task<StartedServer> StartOnFreePortAsync(Func<int, (IReadOnlyDictionary<string, string>? Environment, string[] Arguments)> start, Func<int>? ports = null)
   {
      var refusals = new StringBuilder();
      for (var attempt = 0; attempt < PortAttempts; attempt++)
      {
         var port = (ports ?? FreePort)();
         var (environment, arguments) = start(port);
         var process = LotsenProcess.Start(_dataDirectory.Path, environment, arguments);
         try
         {
            if (await WaitForStateOrExitAsync(process) is { } state)
            {
               return new StartedServer(process, state, port);
            }
         }
         catch
         {
            process.Dispose();
            throw;
         }

         using (process)
         {
            Assert.True(process.ExitCode == 1 && process.StandardError.Contains($"Port {port} of 127.0.0.1 is in use", StringComparison.Ordinal), $"lotsen exited early:\n{process.Output}");
            refusals.Append(process.StandardError);
         }
      }

      throw FailException.ForFailure($"Each of {PortAttempts} starts found its port in use:\n{refusals}");
   }

   private static async Task<bool> AcceptsConnectionAsync(int port)
   {
      using var client = new TcpClient();
      try
      {
         await client.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
         return true;
      }
      catch (SocketException)
      {
         return false;
      }
   }

   private async Task<ServerState> WaitForStateAsync(LotsenProcess process) =>
      await WaitForStateOrExitAsync(process) ?? throw FailException.ForFailure($"lotsen exited early:\n{process.Output}");

   /// <summary>Returns the state that this process wrote to the state file, or null when the process exited first. Its output is then complete.</summary>
   private async Task<ServerState?> WaitForStateOrExitAsync(LotsenProcess process)
   {
      var deadline = DateTime.UtcNow + Timeout;
      while (DateTime.UtcNow < deadline)
      {
         if (process.HasExited)
         {
            await process.WaitForExitAsync(Timeout);
            return null;
         }

         if (StateFile.TryRead(_dataDirectory) is { } state && state.Pid == process.Id)
         {
            return state;
         }

         await Task.Delay(50, TestContext.Current.CancellationToken);
      }

      throw new TimeoutException($"lotsen wrote no state file:\n{process.Output}");
   }

   private sealed record StartedServer(LotsenProcess Process, ServerState State, int Port) : IDisposable
   {
      public void Dispose() => Process.Dispose();
   }

   /// <summary>Records the steps of a test that drives an adapter and other lotsen processes, and builds its failure messages.</summary>
   /// <remarks>A failure message holds the failure, the time at which each step ended, the attached outputs, the adapter's output and the end of the server log.</remarks>
   private sealed class StepLog
   {
      private const int LogLines = 40;
      private const int LineLength = 1000;

      private readonly Stopwatch _clock = Stopwatch.StartNew();
      private readonly List<string> _steps = [];
      private readonly List<(string Title, string Text)> _attachments = [];
      private readonly DataDirectory _dataDirectory;
      private readonly AdapterProcess _adapter;

      public StepLog(DataDirectory dataDirectory, AdapterProcess adapter)
      {
         _dataDirectory = dataDirectory;
         _adapter = adapter;
      }

      /// <summary>Runs a step and records when it ended. A step that throws fails the test with the exception in the message.</summary>
      public async Task<T> RunAsync<T>(string step, Func<Task<T>> action)
      {
         T result;
         try
         {
            result = await action();
         }
         catch (Exception exception)
         {
            throw Threw(step, exception);
         }

         Ended(step);
         return result;
      }

      /// <inheritdoc cref="RunAsync{T}(string, Func{Task{T}})"/>
      public T Run<T>(string step, Func<T> action)
      {
         T result;
         try
         {
            result = action();
         }
         catch (Exception exception)
         {
            throw Threw(step, exception);
         }

         Ended(step);
         return result;
      }

      /// <summary>Adds a titled text to every later failure message.</summary>
      public void Attach(string title, string text) => _attachments.Add((title, text));

      /// <summary>Fails the test with this failure and the log, unless the condition holds.</summary>
      public void Expect([DoesNotReturnIf(false)] bool condition, string failure)
      {
         if (!condition)
         {
            Assert.Fail(Message(failure));
         }
      }

      private FailException Threw(string step, Exception exception) => FailException.ForFailure(Message($"The step \"{step}\" threw {exception}"));

      private void Ended(string step) => _steps.Add($"{_clock.Elapsed.TotalSeconds,7:0.000} s  {step}");

      private string Message(string failure)
      {
         var message = new StringBuilder()
            .AppendLine(failure)
            .AppendLine(CultureInfo.InvariantCulture, $"The test failed {_clock.Elapsed.TotalSeconds:0.000} s after it started. The steps that ended:");
         foreach (var step in _steps.DefaultIfEmpty("(none)"))
         {
            message.AppendLine(step);
         }

         foreach (var (title, text) in _attachments)
         {
            message.AppendLine(CultureInfo.InvariantCulture, $"{title}:").AppendLine(Shown(text));
         }

         return message
            .AppendLine("The adapter's output:").AppendLine(Shown(_adapter.Output))
            .AppendLine(CultureInfo.InvariantCulture, $"The last {LogLines} lines of the server log, {_dataDirectory.LogFilePath}:").Append(ServerLogEnd())
            .ToString();
      }

      // The adapter's answer to initialize alone holds the whole MCP instructions, which would bury the lines around it.
      private static string Shown(string text) =>
         text.Trim().Length == 0
            ? "(empty)"
            : string.Join('\n', text.TrimEnd().Split('\n').Select(line => line.Length <= LineLength ? line : $"{line[..LineLength]}… ({line.Length - LineLength} more characters)"));

      // The read shares writing and deletion with the server, which keeps the log open for writing while it runs.
      private string ServerLogEnd()
      {
         try
         {
            using var stream = new FileStream(_dataDirectory.LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return Shown(string.Join('\n', reader.ReadToEnd().TrimEnd().Split('\n').TakeLast(LogLines)));
         }
         catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
         {
            return "(no log file)";
         }
         catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
         {
            return $"(the log could not be read: {exception.Message})";
         }
      }
   }
}
