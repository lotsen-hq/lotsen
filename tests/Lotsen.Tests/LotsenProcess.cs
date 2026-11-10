using System.Diagnostics;
using System.Text;

namespace Lotsen.Tests;

/// <summary>Runs the lotsen executable from the test output as a child process, with its own data directory.</summary>
internal sealed class LotsenProcess : IDisposable
{
   private readonly Process _process;
   private readonly string _dataDirectory;
   private readonly StringBuilder _output = new();
   private readonly StringBuilder _standardOutput = new();
   private readonly StringBuilder _standardError = new();

   private LotsenProcess(Process process, string dataDirectory)
   {
      _process = process;
      _dataDirectory = dataDirectory;
      process.OutputDataReceived += (_, e) => Append(e.Data, _standardOutput);
      process.ErrorDataReceived += (_, e) => Append(e.Data, _standardError);
      process.BeginOutputReadLine();
      process.BeginErrorReadLine();
   }

   public int Id => _process.Id;

   public bool HasExited => _process.HasExited;

   public int ExitCode => _process.ExitCode;

   /// <summary>Everything the process wrote to standard output and standard error so far.</summary>
   public string Output
   {
      get
      {
         lock (_output)
         {
            return _output.ToString();
         }
      }
   }

   /// <summary>What the process wrote to standard output so far.</summary>
   public string StandardOutput => Read(_standardOutput);

   /// <summary>What the process wrote to standard error so far.</summary>
   public string StandardError => Read(_standardError);

   public static LotsenProcess Start(string dataDirectory, IReadOnlyDictionary<string, string>? environment, params string[] arguments) =>
      new(Process.Start(StartInfo(dataDirectory, environment, arguments)) ?? throw new InvalidOperationException("dotnet did not start."), dataDirectory);

   /// <summary>Returns how to start the lotsen executable with every standard stream redirected.</summary>
   public static ProcessStartInfo StartInfo(string dataDirectory, IReadOnlyDictionary<string, string>? environment, params string[] arguments)
   {
      var startInfo = new ProcessStartInfo("dotnet")
      {
         RedirectStandardOutput = true,
         RedirectStandardError = true,
         RedirectStandardInput = true,
         UseShellExecute = false,
      };
      startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "lotsen.dll"));
      foreach (var argument in arguments)
      {
         startInfo.ArgumentList.Add(argument);
      }

      startInfo.Environment["LOTSEN_DATA_DIR"] = dataDirectory;

      // A background server inherits this from the command that starts it, so no test opens the user's browser. The test child accepts the link on every platform.
      startInfo.Environment["BROWSER"] = TestChild.Executable;
      foreach (var (name, value) in environment ?? new Dictionary<string, string>())
      {
         startInfo.Environment[name] = value;
      }

      return startInfo;
   }

   /// <summary>Returns a copy of these variables with a browser that no launcher can start, so <c>open_ui</c> on a server that inherits them returns its link.</summary>
   public static Dictionary<string, string> WithoutBrowser(IReadOnlyDictionary<string, string>? variables = null) =>
      new(variables ?? new Dictionary<string, string>()) { ["BROWSER"] = "/nonexistent/browser" };

   /// <summary>Runs a lotsen command to its end and returns its exit code and output.</summary>
   public static async Task<(int ExitCode, string Output)> RunAsync(string dataDirectory, IReadOnlyDictionary<string, string>? environment, params string[] arguments)
   {
      using var process = Start(dataDirectory, environment, arguments);
      await process.WaitForExitAsync(TimeSpan.FromSeconds(30));
      return (process.ExitCode, process.Output);
   }

   /// <summary>Runs a lotsen command to its end and returns its exit code and its two output streams.</summary>
   public static Task<(int ExitCode, string StandardOutput, string StandardError)> RunSeparatelyAsync(string dataDirectory, params string[] arguments) =>
      RunSeparatelyAsync(dataDirectory, environment: null, arguments);

   /// <summary>Runs a lotsen command with these variables to its end and returns its exit code and its two output streams.</summary>
   public static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunSeparatelyAsync(string dataDirectory, IReadOnlyDictionary<string, string>? environment, params string[] arguments)
   {
      using var process = Start(dataDirectory, environment, arguments);
      await process.WaitForExitAsync(TimeSpan.FromSeconds(30));
      return (process.ExitCode, process.StandardOutput, process.StandardError);
   }

   /// <summary>Stops the server that this process runs, with SIGINT on Unix and with <c>lotsen stop</c> on Windows.</summary>
   public async Task StopAsync()
   {
      if (!OperatingSystem.IsWindows())
      {
         Interrupt();
         return;
      }

      var (exitCode, output) = await RunAsync(_dataDirectory, null, "stop");
      Assert.True(exitCode == 0, $"lotsen stop exited with {exitCode}:\n{output}");
   }

   /// <summary>Sends SIGINT, the signal a Ctrl-C in the terminal sends.</summary>
   public void Interrupt()
   {
      using var kill = Process.Start("kill", ["-INT", _process.Id.ToString()]);
      kill.WaitForExit();
   }

   /// <summary>Kills the process at once, as a crash would, so it runs no cleanup.</summary>
   public void Kill() => _process.Kill();

   public async Task WaitForExitAsync(TimeSpan timeout)
   {
      using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
      cts.CancelAfter(timeout);
      await _process.WaitForExitAsync(cts.Token);

      // The parameterless wait also drains the redirected output.
      _process.WaitForExit();
   }

   public void Dispose()
   {
      if (!_process.HasExited)
      {
         _process.Kill(entireProcessTree: true);
         _process.WaitForExit();
      }

      _process.Dispose();
   }

   private string Read(StringBuilder stream)
   {
      lock (_output)
      {
         return stream.ToString();
      }
   }

   private void Append(string? line, StringBuilder stream)
   {
      if (line is null)
      {
         return;
      }

      lock (_output)
      {
         _output.AppendLine(line);
         stream.AppendLine(line);
      }
   }
}
