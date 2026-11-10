using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Lotsen.Client;

namespace Lotsen.Hosting;

/// <summary>
/// Starts a background server: this executable's hidden <see cref="Command"/>, which outlives the process that started it and stops once no agent session has used it for its grace period.
/// </summary>
/// <remarks>
/// <para>
/// The background server runs in a session and a process group of its own, as <see cref="DetachThisProcess"/> makes it on Unix, so a Ctrl-C in the terminal of the process that started it does
/// not reach it. On Windows the start asks for a process group of its own and no console window. The server's standard streams are pipes that the starter closes at once.
/// </para>
/// <para>
/// The server inherits no handle of the starter, so the reader of the starter's standard output sees it end when the starter exits, as
/// <c>BackgroundServerTests.Background_server_exits_after_the_grace_period_once_its_adapter_closed_and_removes_its_state_file</c> pins.
/// </para>
/// <para>
/// The server gets no credential from the starter. It makes its own credentials and writes them to the state file.
/// </para>
/// <para>
/// Two starts at once each start a process, and the claim lock leaves one server running. The other process exits 0 without a word, and both starts then find the one server.
/// </para>
/// </remarks>
internal static partial class BackgroundServer
{
   /// <summary>The hidden subcommand that runs a background server.</summary>
   public const string Command = "daemon";

   /// <summary>The environment variable through which tests shorten the grace period, in milliseconds. Only the background server reads it, and help does not name it.</summary>
   public const string TestGracePeriodVariable = "LOTSEN_TEST_GRACE_PERIOD_MS";

   /// <summary>The environment variable through which tests shorten the start timeout, in milliseconds. Only <see cref="StartAsync"/> reads it, and help does not name it.</summary>
   public const string TestStartTimeoutVariable = "LOTSEN_TEST_START_TIMEOUT_MS";

   /// <summary>How long a background server waits with no agent session open before it stops.</summary>
   public static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(60);

   /// <summary>How long <see cref="StartAsync"/> waits for a started server to answer, unless <see cref="TestStartTimeoutVariable"/> sets another.</summary>
   public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);

   private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
   private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(2);

   // A start that lost the claim lock to a server that is stopping finds no server, so the start tries again, at most this often.
   private static readonly TimeSpan RestartInterval = TimeSpan.FromSeconds(1);

   /// <summary>Returns the reason of a start whose server process could not be started, with the reason that the system gave, if any.</summary>
   public static string SpawnFailedMessage(string? reason) => reason is null ? "The lotsen server could not start." : $"The lotsen server could not start: {reason}";

   /// <summary>Returns the reason of a start whose server exited with an error, with the last line of its log since the start, if any.</summary>
   public static string ExitedMessage(DataDirectory dataDirectory, string? lastLogLine) => lastLogLine is not null
      ? $"The lotsen server could not start. Its log, {dataDirectory.LogFilePath}, ends: {lastLogLine}"
      : $"The lotsen server could not start. Its log is {dataDirectory.LogFilePath}.";

   /// <summary>Returns the reason of a start whose server did not answer within the start timeout.</summary>
   /// <param name="dataDirectory">The data directory, whose log file the reason names.</param>
   /// <param name="timeout">The start timeout in force, which the reason names in seconds, to the millisecond.</param>
   /// <param name="command">The command that the reason's reader runs lotsen with.</param>
   public static string StartTimeoutMessage(DataDirectory dataDirectory, TimeSpan timeout, LotsenCommand command) => string.Create(
      CultureInfo.InvariantCulture,
      $"The lotsen server did not answer within {timeout.TotalSeconds:0.###} seconds of its start. Its log is {dataDirectory.LogFilePath}. Run `{command.With("serve")}` to start it in the foreground and see why.");

   /// <summary>Returns how long <see cref="StartAsync"/> waits for a started server to answer: <see cref="StartTimeout"/>, or the one that <see cref="TestStartTimeoutVariable"/> sets.</summary>
   public static TimeSpan StartTimeoutFromEnvironment() => StartTimeoutFrom(Environment.GetEnvironmentVariable(TestStartTimeoutVariable));

   /// <summary>Returns the start timeout that a value of <see cref="TestStartTimeoutVariable"/> sets.</summary>
   /// <param name="milliseconds">The variable's value, or null when it is not set.</param>
   /// <returns>The value as a timeout, or <see cref="StartTimeout"/> when the value is no positive whole number of milliseconds that fits an <see cref="int"/>.</returns>
   public static TimeSpan StartTimeoutFrom(string? milliseconds) =>
      int.TryParse(milliseconds, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0 ? TimeSpan.FromMilliseconds(value) : StartTimeout;

   /// <summary>Returns the grace period of a background server: <see cref="GracePeriod"/>, or the one that <see cref="TestGracePeriodVariable"/> sets.</summary>
   public static TimeSpan GracePeriodFromEnvironment() =>
      int.TryParse(Environment.GetEnvironmentVariable(TestGracePeriodVariable), NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds) && milliseconds > 0
         ? TimeSpan.FromMilliseconds(milliseconds)
         : GracePeriod;

   /// <summary>Moves this process, a background server, into a session and a process group of its own on Unix, away from the terminal of the process that started it.</summary>
   /// <remarks>A signal that the terminal sends to its process group can still reach the server in the moment between its start and this call.</remarks>
   public static void DetachThisProcess()
   {
      if (!OperatingSystem.IsWindows())
      {
         // It fails only for a process group leader, which a process that another one started is not.
         _ = SetSid();
      }
   }

   /// <summary>Starts a background server of this executable on the data directory, and waits until a server there answers.</summary>
   /// <param name="dataDirectory">The data directory to start the server on.</param>
   /// <param name="command">The command that a failure's reader runs lotsen with: <see cref="LotsenCommand.Installed"/> for the CLI, and the adapter's own for the adapter.</param>
   /// <param name="cancellationToken">Cancels the wait.</param>
   /// <returns>The server that answers, which is another one, possibly of another build, when another start or a <c>lotsen serve</c> won the claim lock.</returns>
   /// <exception cref="ServerStartException">
   /// The server did not start, or no server answered within the start timeout, <see cref="StartTimeoutFromEnvironment"/>. The message names the log file.
   /// </exception>
   public static async Task<StartedServer> StartAsync(DataDirectory dataDirectory, LotsenCommand command, CancellationToken cancellationToken)
   {
      dataDirectory.EnsureExists();
      var timeout = StartTimeoutFromEnvironment();
      var discovery = new ServerDiscovery(dataDirectory);
      var elapsed = Stopwatch.StartNew();
      var started = Stopwatch.StartNew();
      var spawnedAt = DateTime.UtcNow;
      var process = Spawn(dataDirectory);
      try
      {
         while (true)
         {
            if (await TryReachAsync(discovery, cancellationToken) is { } state)
            {
               return state;
            }

            if (process.HasExited)
            {
               if (process.ExitCode != 0)
               {
                  throw new ServerStartException(ExitedMessage(dataDirectory, ServerLog.TryReadLastLine(dataDirectory, spawnedAt)));
               }

               if (started.Elapsed >= RestartInterval)
               {
                  process.Dispose();
                  spawnedAt = DateTime.UtcNow;
                  process = Spawn(dataDirectory);
                  started.Restart();
               }
            }

            if (elapsed.Elapsed >= timeout)
            {
               throw new ServerStartException(StartTimeoutMessage(dataDirectory, timeout, command));
            }

            await Task.Delay(PollInterval, cancellationToken);
         }
      }
      finally
      {
         process.Dispose();
      }
   }

   // A stale state file stays until the started server replaces it, so the start sends the token only to a server that answers as lotsen or to the claim lock's holder.
   private static async Task<StartedServer?> TryReachAsync(ServerDiscovery discovery, CancellationToken cancellationToken)
   {
      var state = await discovery.FindAsync(cancellationToken) switch
      {
         ServerAnswer.Running running => running.State,
         ServerAnswer.Unreachable unreachable => unreachable.State,
         ServerAnswer.None or ServerAnswer.Starting or ServerAnswer.Stale or ServerAnswer.Foreign => null,
      };
      if (state is null)
      {
         return null;
      }

      using var client = new ServerClient(state);
      using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      timeout.CancelAfter(AnswerTimeout);
      try
      {
         return new StartedServer(state, await client.GetVersionAsync(timeout.Token));
      }
      catch (Exception exception) when (exception is JsonException or InvalidOperationException)
      {
         return new StartedServer(state, Versions: null);
      }
      catch (Exception exception) when (exception is HttpRequestException || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
      {
         return null;
      }
   }

   private static Process Spawn(DataDirectory dataDirectory)
   {
      var (fileName, arguments) = ThisExecutable();
      var startInfo = new ProcessStartInfo(fileName)
      {
         UseShellExecute = false,
         RedirectStandardInput = true,
         RedirectStandardOutput = true,
         RedirectStandardError = true,
         CreateNoWindow = true,

         // The server outlives the starter, so it must not keep the starter's working directory in use.
         WorkingDirectory = dataDirectory.Path,

         // An empty list lets the server inherit only its own standard handles, so it holds none of the starter's, such as the adapter's MCP pipes.
         InheritedHandles = [],
      };
      foreach (var argument in arguments.Append(Command))
      {
         startInfo.ArgumentList.Add(argument);
      }

      if (OperatingSystem.IsWindows())
      {
         startInfo.CreateNewProcessGroup = true;
      }

      startInfo.Environment[DataDirectory.OverrideVariable] = dataDirectory.Path;
      Process process;
      try
      {
         process = Process.Start(startInfo) ?? throw new ServerStartException(SpawnFailedMessage(null));
      }
      catch (System.ComponentModel.Win32Exception exception)
      {
         throw new ServerStartException(SpawnFailedMessage(exception.Message));
      }

      process.StandardInput.Close();
      process.StandardOutput.Close();
      process.StandardError.Close();
      return process;
   }

   [LibraryImport("libc.so.6", EntryPoint = "setsid")]
   private static partial int SetSid();

   // Under `dotnet lotsen.dll` the process is the dotnet host, and the first argument is the application.
   private static (string FileName, IReadOnlyList<string> Arguments) ThisExecutable()
   {
      var processPath = Environment.ProcessPath ?? throw new ServerStartException(SpawnFailedMessage("the path of this executable is unknown."));
      return Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
         ? (processPath, [Environment.GetCommandLineArgs()[0]])
         : (processPath, []);
   }
}

/// <summary>The server that answered after a start of a background server.</summary>
/// <param name="State">The server's state file entry.</param>
/// <param name="Versions">The versions that the server reports, or null when its answer to the version request could not be read.</param>
internal sealed record StartedServer(ServerState State, VersionInfo? Versions);

/// <summary>An error that keeps a background server from starting or answering. Its message tells the user where to look.</summary>
internal sealed class ServerStartException : Exception
{
   public ServerStartException(string message)
      : base(message)
   {
   }
}
