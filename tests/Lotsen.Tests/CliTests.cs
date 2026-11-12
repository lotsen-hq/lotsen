using Lotsen.Hosting;

namespace Lotsen.Tests;

/// <summary>Covers how the lotsen executable reads its command line, as separate processes.</summary>
[Trait("Category", "Cli")]
public sealed class CliTests : IDisposable
{
   private readonly DataDirectory _dataDirectory = new(Path.Combine(Directory.CreateTempSubdirectory("lotsen-tests-").FullName, "data"));

   public static TheoryData<string[]> HelpArguments => new()
   {
      new[] { "--help" },
      new[] { "-h" },
      new[] { "serve", "--help" },
      new[] { "serve", "-h" },
      new[] { "open", "--help" },
      new[] { "open", "-h" },
      new[] { "mcp", "--help" },
      new[] { "mcp", "-h" },
      new[] { "stop", "--help" },
      new[] { "stop", "-h" },
      new[] { "restart", "--help" },
      new[] { "restart", "-h" },
   };

   public static TheoryData<string[]> BadArguments => new()
   {
      new[] { "frobnicate" },
      new[] { "help" },
      new[] { "serve", "--frobnicate" },
      new[] { "open", "--frobnicate" },
      new[] { "mcp", "--frobnicate" },
      new[] { "stop", "--frobnicate" },
      new[] { "stop", "now" },
      new[] { "restart", "--frobnicate" },
      new[] { "restart", "--port", "8080" },
      new[] { "restart", "--fact-cache-mb", "8" },
   };

   [Fact]
   public async Task No_arguments_exit_2_with_usage_on_stderr()
   {
      var (exitCode, standardOutput, standardError) = await LotsenProcess.RunSeparatelyAsync(_dataDirectory.Path);

      Assert.Equal(2, exitCode);
      Assert.Empty(standardOutput);
      Assert.Contains("lotsen --help", standardError);
   }

   [Theory]
   [MemberData(nameof(BadArguments))]
   public async Task Bad_arguments_exit_2_with_usage_on_stderr(string[] arguments)
   {
      var (exitCode, standardOutput, standardError) = await LotsenProcess.RunSeparatelyAsync(_dataDirectory.Path, arguments);

      Assert.Equal(2, exitCode);
      Assert.Empty(standardOutput);
      Assert.Contains("lotsen --help", standardError);
   }

   [Theory]
   [MemberData(nameof(HelpArguments))]
   public async Task Help_exits_0_with_usage_on_stdout(string[] arguments)
   {
      var (exitCode, standardOutput, standardError) = await LotsenProcess.RunSeparatelyAsync(_dataDirectory.Path, arguments);

      Assert.Equal(0, exitCode);
      Assert.Contains("Usage:", standardOutput);
      Assert.Empty(standardError);
   }

   [Fact]
   public async Task Version_prints_the_build_identity_on_stdout()
   {
      var (exitCode, standardOutput, standardError) = await LotsenProcess.RunSeparatelyAsync(_dataDirectory.Path, "--version");

      Assert.Equal(0, exitCode);
      Assert.Equal(Program.Version.Build, standardOutput.Trim());
      Assert.Empty(standardError);
   }

   [Fact]
   public async Task Version_of_a_build_without_a_release_version_names_a_development_build_and_its_commit()
   {
      var (_, standardOutput, _) = await LotsenProcess.RunSeparatelyAsync(_dataDirectory.Path, "--version");

      Assert.Matches(@"^0\.0\.0-dev\+[0-9a-f]{40} \([0-9a-f]{32}\)$", standardOutput.Trim());
   }

   [Theory]
   [InlineData("Information")]
   [InlineData("information")]
   public async Task Serve_with_log_level_information_prints_the_hosts_information_lines(string level)
   {
      var output = await ServeAsync("--log-level", level);

      Assert.Contains("Now listening on", output);
   }

   [Fact]
   public async Task Serve_without_log_level_prints_no_information_lines()
   {
      var output = await ServeAsync();

      Assert.DoesNotContain("Now listening on", output);
   }

   [Fact]
   public async Task Serve_with_an_unknown_log_level_exits_2_with_usage_on_stderr()
   {
      var (exitCode, standardOutput, standardError) = await LotsenProcess.RunSeparatelyAsync(_dataDirectory.Path, "serve", "--log-level", "bogus");

      Assert.Equal(2, exitCode);
      Assert.Empty(standardOutput);
      Assert.Contains("bogus", standardError);
      Assert.Contains("lotsen --help", standardError);
      Assert.False(File.Exists(_dataDirectory.StateFilePath));
   }

   [Theory]
   [InlineData("3")]
   [InlineData("Trace,Error")]
   [InlineData("Warning,Error")]
   [InlineData(" Information")]
   [InlineData("Information ")]
   [InlineData("")]
   public async Task Serve_with_a_value_that_is_not_a_log_level_name_exits_2(string value)
   {
      var (exitCode, _, standardError) = await LotsenProcess.RunSeparatelyAsync(_dataDirectory.Path, "serve", "--log-level", value);

      Assert.Equal(2, exitCode);
      Assert.Contains("lotsen --help", standardError);
      Assert.False(File.Exists(_dataDirectory.StateFilePath));
   }

   [Fact]
   public async Task Serve_help_lists_the_log_level_option_and_its_values()
   {
      var (_, standardOutput, _) = await LotsenProcess.RunSeparatelyAsync(_dataDirectory.Path, "serve", "--help");

      Assert.Contains("--log-level", standardOutput);
      foreach (var name in new[] { "Trace", "Debug", "Information", "Warning", "Error", "Critical", "None" })
      {
         Assert.Contains(name, standardOutput);
      }
   }

   [Fact]
   public async Task Help_does_not_list_the_background_server_command()
   {
      var (exitCode, standardOutput, _) = await LotsenProcess.RunSeparatelyAsync(_dataDirectory.Path, "--help");

      Assert.Equal(0, exitCode);
      Assert.Contains("serve", standardOutput);
      Assert.DoesNotContain(BackgroundServer.Command, standardOutput);
   }

   [Fact]
   public async Task Help_lists_stop_and_restart()
   {
      var (exitCode, standardOutput, _) = await LotsenProcess.RunSeparatelyAsync(_dataDirectory.Path, "--help");

      Assert.Equal(0, exitCode);
      Assert.Matches(@"(?m)^\s+stop\s+\S", standardOutput);
      Assert.Matches(@"(?m)^\s+restart\s+\S", standardOutput);
   }

   [Fact]
   public async Task Lotsen_stop_without_a_running_server_exits_0_saying_that_none_runs_and_creates_no_data_directory()
   {
      var (exitCode, standardOutput, standardError) = await LotsenProcess.RunSeparatelyAsync(_dataDirectory.Path, "stop");

      Assert.Equal(0, exitCode);
      Assert.Contains("No lotsen server is running", standardOutput);
      Assert.Empty(standardError);
      Assert.False(Directory.Exists(_dataDirectory.Path), "lotsen stop created the data directory.");
   }

   [Fact]
   public async Task Background_server_command_with_an_unknown_option_exits_2_with_usage_on_stderr()
   {
      var (exitCode, standardOutput, standardError) = await LotsenProcess.RunSeparatelyAsync(_dataDirectory.Path, BackgroundServer.Command, "--frobnicate");

      Assert.Equal(2, exitCode);
      Assert.Empty(standardOutput);
      Assert.Contains("lotsen --help", standardError);
      Assert.False(File.Exists(_dataDirectory.StateFilePath));
   }

   [Fact]
   public async Task Open_without_a_running_server_exits_1_naming_both_ways_to_start_one_and_starts_none()
   {
      var (exitCode, standardOutput, standardError) = await LotsenProcess.RunSeparatelyAsync(_dataDirectory.Path, "open");

      Assert.Equal(1, exitCode);
      Assert.Empty(standardOutput);
      Assert.Contains("`lotsen serve`", standardError);
      Assert.Contains("Claude Code session with the lotsen channel", standardError);
      Assert.False(File.Exists(_dataDirectory.StateFilePath));
   }

   [Fact]
   public async Task Open_with_a_launched_browser_prints_no_link()
   {
      using var serve = LotsenProcess.Start(_dataDirectory.Path, null, "serve");
      await WaitForStateAsync(serve);

      var (exitCode, standardOutput, standardError) = await LotsenProcess.RunSeparatelyAsync(_dataDirectory.Path, "open");

      Assert.Equal(0, exitCode);
      Assert.Contains("Opened lotsen in the browser.", standardOutput);
      Assert.Contains("run 'lotsen open --print-link' and open the link it prints", standardOutput);
      Assert.DoesNotContain("#code=", standardOutput + standardError);
   }

   [Fact]
   public async Task Open_with_print_link_writes_only_the_link_to_stdout()
   {
      using var serve = LotsenProcess.Start(_dataDirectory.Path, null, "serve");
      await WaitForStateAsync(serve);

      var (exitCode, standardOutput, standardError) = await LotsenProcess.RunSeparatelyAsync(_dataDirectory.Path, "open", "--print-link");

      Assert.Equal(0, exitCode);
      var line = Assert.Single(standardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
      Assert.Contains("#code=", line);
      Assert.Contains("works once", standardError);
      Assert.DoesNotContain("#code=", standardError);
   }

   [Fact]
   public async Task Open_with_print_link_without_a_running_server_exits_1_and_prints_no_link()
   {
      var (exitCode, standardOutput, standardError) = await LotsenProcess.RunSeparatelyAsync(_dataDirectory.Path, "open", "--print-link");

      Assert.Equal(1, exitCode);
      Assert.Empty(standardOutput);
      Assert.Contains("No lotsen server is running. Start one with `lotsen serve`", standardError);
      Assert.False(File.Exists(_dataDirectory.StateFilePath));
   }

   [Fact]
   public async Task Open_against_a_server_that_refuses_of_another_build_names_its_reason_and_both_builds()
   {
      var other = Program.Version with { Build = "0.0.0 (another build)" };
      await using var server = await RefusingServer.StartAsync(_dataDirectory, other);

      var (exitCode, standardOutput, standardError) = await LotsenProcess.RunSeparatelyAsync(_dataDirectory.Path, "open", "--print-link");

      Assert.Equal(1, exitCode);
      Assert.Empty(standardOutput);
      Assert.Contains($"The lotsen server (PID {Environment.ProcessId}) refused the request: lotsen has no operation named 'create_ui_link'.", standardError);
      Assert.Contains($"That server runs build {other.Build}, protocol {other.Protocol}, and this executable is build {Program.Version.Build}", standardError);
      Assert.DoesNotContain("No lotsen server is running", standardError);
   }

   [Fact]
   public async Task Open_against_a_server_that_refuses_of_this_build_names_its_reason()
   {
      await using var server = await RefusingServer.StartAsync(_dataDirectory, Program.Version);

      var (exitCode, standardOutput, standardError) = await LotsenProcess.RunSeparatelyAsync(_dataDirectory.Path, "open");

      Assert.Equal(1, exitCode);
      Assert.Empty(standardOutput);
      Assert.Contains($"The lotsen server (PID {Environment.ProcessId}) refused the request: lotsen has no operation named 'open_ui'.", standardError);
      Assert.DoesNotContain("That server runs", standardError);
      Assert.DoesNotContain("No lotsen server is running", standardError);
   }

   /// <summary>The state file names the live test process, as when a crashed server's PID was reused, and a port that refuses connections.</summary>
   [Fact]
   public async Task Open_with_a_state_file_of_a_live_process_that_is_no_server_exits_1_naming_both_ways_to_start_one()
   {
      using var stalePort = HeldPort.Refusing();
      var stale = new ServerState(Environment.ProcessId, "127.0.0.1", stalePort.Port, "stale-token");
      StateFile.Write(_dataDirectory, stale);

      var (exitCode, standardOutput, standardError) = await LotsenProcess.RunSeparatelyAsync(_dataDirectory.Path, "open");

      Assert.Equal(1, exitCode);
      Assert.Empty(standardOutput);
      Assert.Contains("`lotsen serve`", standardError);
      Assert.Contains("Claude Code session with the lotsen channel", standardError);
      Assert.Equal(stale, StateFile.TryRead(_dataDirectory));
   }

   /// <summary>The state file names the live test process and a port that no URL can hold, which no server writes.</summary>
   [Theory]
   [InlineData("stop", 0)]
   [InlineData("open", 1)]
   public async Task Command_with_a_state_file_whose_port_is_out_of_range_finds_no_server(string command, int expectedExitCode)
   {
      var stale = new ServerState(Environment.ProcessId, "127.0.0.1", 70000, "stale-token");
      StateFile.Write(_dataDirectory, stale);

      var (exitCode, standardOutput, standardError) = await LotsenProcess.RunSeparatelyAsync(_dataDirectory.Path, command);

      Assert.Equal(expectedExitCode, exitCode);
      Assert.Contains("No lotsen server is running", standardOutput + standardError);
      Assert.DoesNotContain("Exception", standardError);
   }

   [Theory]
   [InlineData("abc")]
   [InlineData("0")]
   [InlineData("65536")]
   [InlineData("-1")]
   [InlineData("+80")]
   [InlineData("8080 ")]
   [InlineData("")]
   public async Task Serve_with_a_port_that_is_not_a_port_number_exits_2_with_usage_on_stderr(string value)
   {
      var (exitCode, standardOutput, standardError) = await LotsenProcess.RunSeparatelyAsync(_dataDirectory.Path, "serve", "--port", value);

      Assert.Equal(2, exitCode);
      Assert.Empty(standardOutput);
      Assert.Contains("lotsen --help", standardError);
      Assert.False(File.Exists(_dataDirectory.StateFilePath));
   }

   [Theory]
   [InlineData("6000")]
   [InlineData("10080")]
   public async Task Serve_with_a_port_that_browsers_refuse_exits_2_with_the_reason(string value)
   {
      var (exitCode, standardOutput, standardError) = await LotsenProcess.RunSeparatelyAsync(_dataDirectory.Path, "serve", "--port", value);

      Assert.Equal(2, exitCode);
      Assert.Empty(standardOutput);
      Assert.Contains($"Browsers refuse to open port {value}", standardError);
      Assert.False(File.Exists(_dataDirectory.StateFilePath));
   }

   [Fact]
   public async Task Serve_help_lists_the_port_option_and_its_variable()
   {
      var (_, standardOutput, _) = await LotsenProcess.RunSeparatelyAsync(_dataDirectory.Path, "serve", "--help");

      Assert.Contains("--port", standardOutput);
      Assert.Contains("LOTSEN_PORT", standardOutput);
   }

   [Fact]
   public async Task Serve_without_a_fact_cache_size_keeps_up_to_64_mib_of_git_facts()
   {
      var output = await ServeAsync(FactCacheVariable(""), "--log-level", "Information");

      Assert.Contains(FactCacheLine(64), output);
   }

   [Fact]
   public async Task Serve_with_a_fact_cache_size_keeps_up_to_that_many_mib_of_git_facts()
   {
      var output = await ServeAsync(FactCacheVariable(""), "--log-level", "Information", "--fact-cache-mb", "8");

      Assert.Contains(FactCacheLine(8), output);
   }

   [Fact]
   public async Task Serve_takes_the_fact_cache_size_from_its_variable_without_the_option()
   {
      var output = await ServeAsync(FactCacheVariable("12"), "--log-level", "Information");

      Assert.Contains(FactCacheLine(12), output);
   }

   [Theory]
   [InlineData("12")]
   [InlineData("abc")]
   public async Task Fact_cache_option_wins_over_its_variable(string variable)
   {
      var output = await ServeAsync(FactCacheVariable(variable), "--log-level", "Information", "--fact-cache-mb", "8");

      Assert.Contains(FactCacheLine(8), output);
   }

   [Theory]
   [InlineData("abc")]
   [InlineData("0")]
   [InlineData("-1")]
   [InlineData("+8")]
   [InlineData("8 ")]
   [InlineData("1.5")]
   [InlineData("2147483648")]
   [InlineData("")]
   public async Task Serve_with_a_fact_cache_size_that_is_not_a_whole_number_of_mib_exits_2_naming_the_option(string value)
   {
      var (exitCode, standardOutput, standardError) = await LotsenProcess.RunSeparatelyAsync(_dataDirectory.Path, "serve", "--fact-cache-mb", value);

      Assert.Equal(2, exitCode);
      Assert.Empty(standardOutput);
      Assert.Contains("--fact-cache-mb", standardError);
      Assert.Contains("lotsen --help", standardError);
      Assert.False(File.Exists(_dataDirectory.StateFilePath));
   }

   [Theory]
   [InlineData("abc")]
   [InlineData("0")]
   [InlineData(" 8")]
   [InlineData("99999999999999999999")]
   public async Task Fact_cache_variable_that_is_not_a_size_exits_1_naming_the_variable(string value)
   {
      var (exitCode, standardOutput, standardError) = await LotsenProcess.RunSeparatelyAsync(_dataDirectory.Path, FactCacheVariable(value), "serve");

      Assert.Equal(1, exitCode);
      Assert.Empty(standardOutput);
      Assert.Contains(FactCacheSize.Variable, standardError);
      Assert.False(File.Exists(_dataDirectory.StateFilePath));
   }

   [Fact]
   public async Task Serve_help_lists_the_fact_cache_option_its_variable_and_its_default()
   {
      var (_, standardOutput, _) = await LotsenProcess.RunSeparatelyAsync(_dataDirectory.Path, "serve", "--help");

      Assert.Contains("--fact-cache-mb", standardOutput);
      Assert.Contains("LOTSEN_FACT_CACHE_MB", standardOutput);
      Assert.Contains("or else 64 MiB.", standardOutput);
   }

   public void Dispose()
   {
      try
      {
         TestDirectories.Delete(Path.GetDirectoryName(_dataDirectory.Path)!, BackgroundServerProcesses.CleanupWait);
      }
      catch (IOException)
      {
      }
   }

   private static Dictionary<string, string> FactCacheVariable(string value) => new() { [FactCacheSize.Variable] = value };

   private static string FactCacheLine(int mebibytes) => $"The git fact cache keeps up to {mebibytes} MiB.";

   private Task<string> ServeAsync(params string[] options) => ServeAsync(null, options);

   private async Task<string> ServeAsync(IReadOnlyDictionary<string, string>? environment, params string[] options)
   {
      using var serve = LotsenProcess.Start(_dataDirectory.Path, environment, ["serve", .. options]);
      await WaitForStateAsync(serve);

      await serve.StopAsync();
      await serve.WaitForExitAsync(TimeSpan.FromSeconds(30));
      return serve.Output;
   }

   private async Task WaitForStateAsync(LotsenProcess serve)
   {
      var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
      while (StateFile.TryRead(_dataDirectory) is null)
      {
         Assert.False(serve.HasExited, $"lotsen exited early:\n{serve.Output}");
         Assert.True(DateTime.UtcNow < deadline, $"lotsen wrote no state file:\n{serve.Output}");
         await Task.Delay(50, TestContext.Current.CancellationToken);
      }
   }
}
