using System.Diagnostics;
using System.Globalization;
using System.Text;
using Lotsen.Hosting;
using Lotsen.Processes;

namespace Lotsen.Tests;

/// <summary>
/// Covers how <see cref="ProcessRunner"/> runs a process to its end: the exit code, both outputs, the input, the environment and the working directory, and the kill of the whole process tree at the
/// timeout or a cancellation.
/// </summary>
[Trait("Category", "Process")]
public sealed class ProcessRunnerTests : IDisposable
{
   private static readonly TimeSpan Long = TimeSpan.FromMinutes(1);

   private readonly string _scratch = Directory.CreateTempSubdirectory("lotsen-process-").FullName;
   private readonly List<int> _spawned = [];

   private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

   [Theory]
   [InlineData(0)]
   [InlineData(1)]
   [InlineData(42)]
   public async Task Exit_code_and_both_outputs_are_captured(int code)
   {
      var outcome = await ProcessRunner.RunAsync(TestChild.Command(Long, "print", code.ToString(CultureInfo.InvariantCulture), "the output", "the error"), Cancellation);

      var exited = Assert.IsType<ProcessOutcome.Exited>(outcome);
      Assert.Equal(code, exited.Code);
      Assert.Equal("the output", Encoding.UTF8.GetString(exited.Output));
      Assert.Equal("the error", exited.Error);
   }

   /// <summary>Each output is many times the size of a pipe's buffer, and the child writes them in turns.</summary>
   [Fact]
   public async Task Large_output_on_both_pipes_ends_without_a_deadlock()
   {
      const int Size = 4 * 1024 * 1024;

      var outcome = await ProcessRunner.RunAsync(TestChild.Command(Long, "flood", Size.ToString(CultureInfo.InvariantCulture)), Cancellation);

      var exited = Assert.IsType<ProcessOutcome.Exited>(outcome);
      Assert.Equal(0, exited.Code);
      Assert.Equal(Size, exited.Output.Length);
      Assert.All(exited.Output, b => Assert.Equal((byte)'o', b));
      Assert.Equal(new string('e', Size), exited.Error);
   }

   /// <summary>The input is larger than a pipe's buffer, so the child echoes part of it while the runner still writes the rest.</summary>
   [Fact]
   public async Task Input_reaches_the_process_and_then_closes()
   {
      var input = Enumerable.Range(0, 1024 * 1024).Select(i => (byte)(i % 251)).ToArray();

      var outcome = await ProcessRunner.RunAsync(TestChild.Command(Long, "echo") with { Input = input }, Cancellation);

      var exited = Assert.IsType<ProcessOutcome.Exited>(outcome);
      Assert.Equal(0, exited.Code);
      Assert.Equal(input, exited.Output);
   }

   [Fact]
   public async Task Process_without_input_reads_an_empty_closed_input()
   {
      var outcome = await ProcessRunner.RunAsync(TestChild.Command(Long, "echo"), Cancellation);

      var exited = Assert.IsType<ProcessOutcome.Exited>(outcome);
      Assert.Equal(0, exited.Code);
      Assert.Empty(exited.Output);
   }

   [Fact]
   public async Task Environment_sets_and_removes_variables_and_keeps_the_rest_of_this_process_environment()
   {
      var suffix = Guid.NewGuid().ToString("N");
      var (removed, kept, set) = ($"LOTSEN_TEST_REMOVED_{suffix}", $"LOTSEN_TEST_KEPT_{suffix}", $"LOTSEN_TEST_SET_{suffix}");
      Environment.SetEnvironmentVariable(removed, "inherited");
      Environment.SetEnvironmentVariable(kept, "inherited");
      try
      {
         var environment = new Dictionary<string, string?> { [removed] = null, [set] = "a value\nover two lines" };

         var values = await Task.WhenAll(new[] { removed, kept, set }.Select(name => ProcessRunner.RunAsync(TestChild.Command(Long, "env", name) with { Environment = environment }, Cancellation)));

         Assert.Equal(3, Assert.IsType<ProcessOutcome.Exited>(values[0]).Code);
         Assert.Equal("inherited", Encoding.UTF8.GetString(Assert.IsType<ProcessOutcome.Exited>(values[1]).Output));
         Assert.Equal("a value\nover two lines", Encoding.UTF8.GetString(Assert.IsType<ProcessOutcome.Exited>(values[2]).Output));
      }
      finally
      {
         Environment.SetEnvironmentVariable(removed, null);
         Environment.SetEnvironmentVariable(kept, null);
      }
   }

   /// <summary>A marker file identifies the directory, whatever form of its path the system reports.</summary>
   [Fact]
   public async Task Process_starts_in_the_working_directory()
   {
      var marker = $"marker-{Guid.NewGuid():N}";
      File.WriteAllText(Path.Combine(_scratch, marker), "");

      var outcome = await ProcessRunner.RunAsync(TestChild.Command(Long, "cwd") with { WorkingDirectory = _scratch }, Cancellation);

      var exited = Assert.IsType<ProcessOutcome.Exited>(outcome);
      Assert.True(File.Exists(Path.Combine(Encoding.UTF8.GetString(exited.Output), marker)), $"The process started in {Encoding.UTF8.GetString(exited.Output)}.");
   }

   [Fact]
   public async Task Timeout_kills_the_process_and_its_child()
   {
      var pidFile = Path.Combine(_scratch, "pids");

      var outcome = await ProcessRunner.RunAsync(TestChild.Command(TimeSpan.FromSeconds(5), "spawn", pidFile), Cancellation);

      Assert.IsType<ProcessOutcome.TimedOut>(outcome);
      await AssertTreeEndsAsync(ReadPids(pidFile) ?? throw new InvalidOperationException("The child wrote no process IDs within the timeout."));
   }

   [Fact]
   public async Task Cancellation_kills_the_process_and_its_child()
   {
      var pidFile = Path.Combine(_scratch, "pids");
      using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);

      var run = ProcessRunner.RunAsync(TestChild.Command(Long, "spawn", pidFile), cancel.Token);
      var pids = await Poll.UntilAsync(() => ReadPids(pidFile), TimeSpan.FromSeconds(30), () => "The child wrote no process IDs.");
      await cancel.CancelAsync();

      Assert.IsType<ProcessOutcome.Canceled>(await run.WaitAsync(TimeSpan.FromSeconds(30), Cancellation));
      await AssertTreeEndsAsync(pids);
   }

   [Fact]
   public async Task Missing_executable_does_not_start()
   {
      var outcome = await ProcessRunner.RunAsync(new ProcessCommand(Path.Combine(_scratch, "no-such-program"), [], Long), Cancellation);

      Assert.Equal(StartFailure.ExecutableMissing, Assert.IsType<ProcessOutcome.NotStarted>(outcome).Failure);
   }

   [Fact]
   public async Task Missing_working_directory_does_not_start()
   {
      var outcome = await ProcessRunner.RunAsync(TestChild.Command(Long, "cwd") with { WorkingDirectory = Path.Combine(_scratch, "no-such-directory") }, Cancellation);

      Assert.Equal(StartFailure.WorkingDirectoryMissing, Assert.IsType<ProcessOutcome.NotStarted>(outcome).Failure);
   }

   [Fact]
   public void Text_of_a_command_or_an_exit_holds_no_argument_variable_input_or_output()
   {
      const string Secret = "lotsen-test-secret";
      var command = new ProcessCommand("tool", [Secret], Long) { Environment = new Dictionary<string, string?> { ["VALUE"] = Secret }, Input = Encoding.UTF8.GetBytes(Secret) };
      var exited = new ProcessOutcome.Exited(0, Encoding.UTF8.GetBytes(Secret), Secret);

      Assert.Equal("ProcessCommand { FileName = tool }", command.ToString());
      Assert.Equal("Exited { Code = 0 }", exited.ToString());
   }

   public void Dispose()
   {
      foreach (var pid in _spawned.Where(IsRunning))
      {
         try
         {
            using var process = ProcessLiveness.TryGet(pid);
            process?.Kill();
         }
         catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
         {
         }
      }

      Directory.Delete(_scratch, recursive: true);
   }

   /// <summary>Returns whether a process with this ID runs.</summary>
   /// <remarks>On Linux a killed process stays a zombie until its parent or a reaper collects it, so a zombie counts as ended.</remarks>
   private static bool IsRunning(int pid)
   {
      if (!ProcessLiveness.IsAlive(pid))
      {
         return false;
      }

      if (!OperatingSystem.IsLinux())
      {
         return true;
      }

      try
      {
         // The command name in /proc/<pid>/stat is in parentheses and may hold spaces, so the state is the first field after the last one.
         var stat = File.ReadAllText($"/proc/{pid}/stat");
         return stat[(stat.LastIndexOf(')') + 2)..][0] is not ('Z' or 'X');
      }
      catch (IOException)
      {
         return false;
      }
   }

   /// <summary>Returns the child's and the grandchild's process IDs from the file that <c>spawn</c> writes, or null while the file does not exist.</summary>
   private int[]? ReadPids(string pidFile)
   {
      if (!File.Exists(pidFile))
      {
         return null;
      }

      int[] pids = [.. File.ReadAllLines(pidFile).Select(line => int.Parse(line, CultureInfo.InvariantCulture))];
      _spawned.AddRange(pids.Except(_spawned));
      return pids;
   }

   private static async Task AssertTreeEndsAsync(int[] pids)
   {
      Assert.Equal(2, pids.Length);
      var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
      while (pids.Any(IsRunning))
      {
         Assert.True(DateTime.UtcNow < deadline, $"These processes of the tree still run: {string.Join(", ", pids.Where(IsRunning))}.");
         await Task.Delay(50, Cancellation);
      }
   }
}
