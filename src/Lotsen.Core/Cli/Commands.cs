using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text.Json;
using Lotsen.Adapter;
using Lotsen.Client;
using Lotsen.Hosting;
using Lotsen.Platforms;
using Lotsen.Server;
using Microsoft.Extensions.Logging;

namespace Lotsen.Cli;

/// <summary>The commands of the lotsen executable. No command prints the server token.</summary>
public static class Commands
{
   private const string FactCacheOption = "--fact-cache-mb";

   /// <summary>Parses the arguments and runs the command that they name.</summary>
   /// <param name="args">The command line arguments.</param>
   /// <param name="version">The versions of this executable, which the server reports and the adapter compares with the server's.</param>
   /// <param name="platforms">The factories of the platforms that the server's tours can target, each called once with the server's services.</param>
   /// <returns>The process exit code: 0 on success, 1 on failure, 2 for arguments that do not parse. Help goes to standard output, parse errors to standard error.</returns>
   public static Task<int> RunAsync(string[] args, VersionInfo version, IReadOnlyList<Func<IServiceProvider, IPullRequestPlatform>> platforms)
   {
      var root = Build(version, platforms);

      // A path may start with '@', so no argument is read as a response file.
      var parseResult = root.Parse(args, new ParserConfiguration { ResponseFileTokenReplacer = null });
      if (parseResult.Action is ParseErrorAction || args.Length == 0)
      {
         // ParseErrorAction writes its report to stdout, where a script would take it for a result.
         foreach (var error in parseResult.Errors)
         {
            Console.Error.WriteLine(error.Message);
         }

         Console.Error.WriteLine("Run 'lotsen --help' or 'lotsen <command> --help' for usage.");
         return Task.FromResult(2);
      }

      // Both the server and the adapter register their own signal handling.
      var configuration = new InvocationConfiguration { EnableDefaultExceptionHandler = false, ProcessTerminationTimeout = null };
      return parseResult.InvokeAsync(configuration);
   }

   private static RootCommand Build(VersionInfo version, IReadOnlyList<Func<IServiceProvider, IPullRequestPlatform>> platforms)
   {
      var serve = new Command("serve", "Run the lotsen server in the foreground until Ctrl-C or `lotsen stop`.");
      var logLevel = new Option<LogLevel>("--log-level")
      {
         Description = "The least severe level the server logs: Trace, Debug, Information, Warning, Error, Critical or None. The default is Warning.",
         DefaultValueFactory = _ => LogLevel.Warning,
         CustomParser = ParseLogLevel,
      };
      var port = new Option<ServerPort?>("--port")
      {
         Description = $"The port of 127.0.0.1 to listen on, from 1 to 65535. The default is the {ServerPort.Variable} environment variable, or else a port that the OS assigns.",
         CustomParser = ParsePort,
      };
      var factCacheSize = new Option<FactCacheSize?>(FactCacheOption)
      {
         Description = $"The size of the git fact cache, which keeps the server's answers from git in memory, as the server estimates sizes: a whole number of MiB from {FactCacheSize.Minimum} to {int.MaxValue}. The default is the {FactCacheSize.Variable} environment variable, or else {FactCacheSize.Default} MiB.",
         CustomParser = ParseFactCacheSize,
      };
      serve.Options.Add(logLevel);
      serve.Options.Add(port);
      serve.Options.Add(factCacheSize);
      serve.SetAction(parseResult => ServeAsync(version, platforms, parseResult.GetValue(logLevel), parseResult.GetValue(port), parseResult.GetValue(factCacheSize)));
      var open = new Command("open", "Open the running server's UI in the browser.");
      var printLink = new Option<bool>("--print-link")
      {
         Description = "Launch no browser, and print a fresh link to the UI that works once.",
      };
      open.Options.Add(printLink);
      open.SetAction(parseResult => OpenAsync(version, parseResult.GetValue(printLink)));
      var mcp = new Command("mcp", "Serve MCP over stdin and stdout for Claude Code, forwarding to the running server, which it starts in the background when none runs.");
      mcp.SetAction(_ => McpAsync(version));
      var stop = new Command("stop", "Stop the running lotsen server, in the foreground or the background, and wait until it has exited.");
      stop.SetAction(_ => StopAsync());
      var restart = new Command("restart", "Stop the running lotsen server, if any, start a background server of this executable, and print its address.");
      restart.SetAction(_ => RestartAsync(version));
      var daemon = new Command(BackgroundServer.Command, "Run the lotsen server in the background until no agent session has used it for its grace period.") { Hidden = true };
      daemon.SetAction(_ => DaemonAsync(version, platforms));

      var root = new RootCommand("lotsen: a guided tour over a pull request.") { serve, open, stop, restart, mcp, daemon };

      // The built-in action prints the entry assembly's version, not the build identity that the server reports.
      root.Options.OfType<VersionOption>().Single().Action = new PrintVersion(version);
      return root;
   }

   private static LogLevel ParseLogLevel(ArgumentResult result)
   {
      // Enum.TryParse also takes numbers, lists such as "Trace,Error" and padded names; the option takes one name.
      var value = result.Tokens.Single().Value;
      var name = Enum.GetNames<LogLevel>().FirstOrDefault(candidate => candidate.Equals(value, StringComparison.OrdinalIgnoreCase));
      if (name is not null)
      {
         return Enum.Parse<LogLevel>(name);
      }

      result.AddError($"'{value}' is not a log level. Use Trace, Debug, Information, Warning, Error, Critical or None.");
      return LogLevel.Warning;
   }

   private static ServerPort? ParsePort(ArgumentResult result)
   {
      var value = result.Tokens.Single().Value;
      if (ServerPort.TryParse(value, out var port))
      {
         return port;
      }

      result.AddError(
         ServerPort.IsBlockedNumber(value)
            ? $"Browsers refuse to open port {value}, so lotsen's UI could not open there. Use another port."
            : $"'{value}' is not a port number. Use a number from 1 to 65535.");
      return null;
   }

   private static FactCacheSize? ParseFactCacheSize(ArgumentResult result)
   {
      var value = result.Tokens.Single().Value;
      if (FactCacheSize.TryParse(value, out var size))
      {
         return size;
      }

      result.AddError($"'{value}' is not a size for {FactCacheOption}. Use a whole number of MiB from {FactCacheSize.Minimum} to {int.MaxValue}.");
      return null;
   }

   /// <summary>Returns the port that the option configures, or else the one that the environment variable configures.</summary>
   /// <returns>Whether the port resolved. A null <paramref name="port"/> then leaves the port to the OS. Otherwise <paramref name="error"/> tells the user what to fix.</returns>
   private static bool TryResolvePort(ServerPort? option, out ServerPort? port, [NotNullWhen(false)] out string? error) =>
      TryResolve(
         option,
         ServerPort.Variable,
         ServerPort.TryParse,
         value => ServerPort.IsBlockedNumber(value)
            ? $"{ServerPort.Variable} is '{value}', a port that browsers refuse to open, so lotsen's UI could not open there. Set it to another port, or unset it for a port that the OS assigns."
            : $"{ServerPort.Variable} is '{value}', which is not a port number. Set it to a number from 1 to 65535, or unset it for a port that the OS assigns.",
         out port,
         out error);

   /// <summary>Returns the fact cache size that the option configures, or else the one that the environment variable configures, or else the default.</summary>
   /// <returns>Whether the size resolved. Otherwise <paramref name="error"/> tells the user what to fix.</returns>
   private static bool TryResolveFactCacheSize(FactCacheSize? option, [NotNullWhen(true)] out FactCacheSize? size, [NotNullWhen(false)] out string? error)
   {
      if (!TryResolve(
         option,
         FactCacheSize.Variable,
         FactCacheSize.TryParse,
         value => $"{FactCacheSize.Variable} is '{value}', which is not a size in MiB. Set it to a whole number from {FactCacheSize.Minimum} to {int.MaxValue}, or unset it for {FactCacheSize.Default} MiB.",
         out var configured,
         out error))
      {
         size = null;
         return false;
      }

      size = configured ?? FactCacheSize.Default;
      return true;
   }

   /// <summary>Returns the value that the option sets, or else the one that the environment variable sets. An empty variable sets none.</summary>
   /// <returns>
   /// Whether the value resolved. A null <paramref name="value"/> then means that neither sets one. Otherwise <paramref name="error"/> holds the message that <paramref name="invalid"/>
   /// returns for the variable's value.
   /// </returns>
   private static bool TryResolve<T>(T? option, string variable, TryParseSetting<T> parse, Func<string, string> invalid, out T? value, [NotNullWhen(false)] out string? error)
      where T : class
   {
      value = option;
      error = null;
      if (option is not null || Environment.GetEnvironmentVariable(variable) is not { Length: > 0 } text)
      {
         return true;
      }

      if (parse(text, out value))
      {
         return true;
      }

      error = invalid(text);
      return false;
   }

   private delegate bool TryParseSetting<T>(string text, [NotNullWhen(true)] out T? value)
      where T : class;

   private sealed class PrintVersion : SynchronousCommandLineAction
   {
      private readonly VersionInfo _version;

      public PrintVersion(VersionInfo version)
      {
         _version = version;
      }

      public override int Invoke(ParseResult parseResult)
      {
         parseResult.InvocationConfiguration.Output.WriteLine(_version.Build);
         return 0;
      }
   }

   /// <summary>Returns the options of the server that <c>lotsen serve</c> runs, whose console prints the lines at the log level and above.</summary>
   internal static ServerOptions ServeOptions(
      DataDirectory dataDirectory, VersionInfo version, IReadOnlyList<Func<IServiceProvider, IPullRequestPlatform>> platforms, LogLevel logLevel, ServerPort? port, FactCacheSize factCacheSize) =>
      ServerOptions.ForCommand(dataDirectory, version, platforms, port, factCacheSize) with { MinimumLogLevel = logLevel };

   /// <summary>Returns the options of a background server, which logs to the log file and stops after its grace period without an agent session.</summary>
   /// <remarks>The background server command takes no options, so the environment that it inherits sets its port and its fact cache size.</remarks>
   /// <returns>Whether the environment's settings resolved. Otherwise <paramref name="error"/> tells the user what to fix.</returns>
   internal static bool TryBackgroundOptions(
      DataDirectory dataDirectory,
      VersionInfo version,
      IReadOnlyList<Func<IServiceProvider, IPullRequestPlatform>> platforms,
      [NotNullWhen(true)] out ServerOptions? options,
      [NotNullWhen(false)] out string? error)
   {
      if (!TryResolvePort(option: null, out var port, out error) || !TryResolveFactCacheSize(option: null, out var factCacheSize, out error))
      {
         options = null;
         return false;
      }

      options = ServerOptions.ForCommand(dataDirectory, version, platforms, port, factCacheSize) with { IdleGracePeriod = BackgroundServer.GracePeriodFromEnvironment(), LogsToFile = true };
      return true;
   }

   private static async Task<int> ServeAsync(
      VersionInfo version, IReadOnlyList<Func<IServiceProvider, IPullRequestPlatform>> platforms, LogLevel logLevel, ServerPort? portOption, FactCacheSize? factCacheSizeOption)
   {
      if (!TryResolvePort(portOption, out var port, out var error) || !TryResolveFactCacheSize(factCacheSizeOption, out var factCacheSize, out error))
      {
         await Console.Error.WriteLineAsync(error);
         return 1;
      }

      var options = ServeOptions(DataDirectory.Resolve(), version, platforms, logLevel, port, factCacheSize);
      LotsenServer started;
      try
      {
         started = await LotsenServer.StartAsync(options, CancellationToken.None);
      }
      catch (Exception exception) when (exception is ServerAlreadyRunningException or PortUnavailableException)
      {
         await Console.Error.WriteLineAsync(exception.Message);
         return 1;
      }

      await using var server = started;
      Console.WriteLine($"lotsen is listening on http://127.0.0.1:{server.Port}");
      Console.WriteLine("Run `lotsen open` to open it in the browser. Press Ctrl-C or run `lotsen stop` to stop it.");
      await server.WaitForShutdownAsync();
      return 0;
   }

   // A background server has no console. It writes the reason it stopped to its log, which the start that waits for it names.
   private static async Task<int> DaemonAsync(VersionInfo version, IReadOnlyList<Func<IServiceProvider, IPullRequestPlatform>> platforms)
   {
      BackgroundServer.DetachThisProcess();
      var dataDirectory = DataDirectory.Resolve();
      try
      {
         if (!TryBackgroundOptions(dataDirectory, version, platforms, out var options, out var error))
         {
            ServerLog.Append(dataDirectory, error);
            return 1;
         }

         LotsenServer started;
         try
         {
            started = await LotsenServer.StartAsync(options, CancellationToken.None);
         }
         catch (ServerAlreadyRunningException)
         {
            // The server that holds the claim lock is the one that the start waits for.
            return 0;
         }

         await using var server = started;
         await server.WaitForShutdownAsync();
         return 0;
      }
      catch (PortUnavailableException exception)
      {
         ServerLog.Append(dataDirectory, exception.Message);
         return 1;
      }
      catch (Exception exception)
      {
         ServerLog.Append(dataDirectory, exception.ToString());
         ServerLog.Append(dataDirectory, $"The lotsen server stopped on an error: {exception.Message}");
         return 1;
      }
   }

   private static async Task<int> StopAsync()
   {
      int? stopped;
      try
      {
         stopped = await ServerShutdown.StopAsync(DataDirectory.Resolve(), ServerShutdown.Timeout, CancellationToken.None);
      }
      catch (ServerShutdownException exception)
      {
         await Console.Error.WriteLineAsync(exception.Message);
         return 1;
      }

      Console.WriteLine(stopped is { } pid ? $"Stopped the lotsen server (PID {pid})." : "No lotsen server is running.");
      return 0;
   }

   // An adapter can start a server during the restart. The claim lock keeps one of the two, and the start returns the one that answers, so the address printed is the running server's.
   // That adapter can be of another install, so the restart compares the answering server's build with its own.
   private static async Task<int> RestartAsync(VersionInfo version)
   {
      var dataDirectory = DataDirectory.Resolve();
      StartedServer started;
      try
      {
         if (await ServerShutdown.StopAsync(dataDirectory, ServerShutdown.Timeout, CancellationToken.None) is { } stopped)
         {
            Console.WriteLine($"Stopped the lotsen server (PID {stopped}).");
         }

         started = await BackgroundServer.StartAsync(dataDirectory, LotsenCommand.Installed, CancellationToken.None);
      }
      catch (Exception exception) when (exception is ServerShutdownException or ServerStartException)
      {
         await Console.Error.WriteLineAsync(exception.Message);
         return 1;
      }

      var state = started.State;
      if (started.Versions != version)
      {
         var other = started.Versions is { } versions ? $"build {versions.Build}, protocol {versions.Protocol}" : "a build whose versions could not be read";
         await Console.Error.WriteLineAsync(
            $"The lotsen server that answers (PID {state.Pid}) runs {other}, and this executable is build {version.Build}, protocol {version.Protocol}. A Claude Code session whose lotsen is another install, or a `lotsen serve` of another build, started it before this restart's server. Close that session or stop that `lotsen serve`, then run `lotsen restart` again.");
         return 1;
      }

      Console.WriteLine($"lotsen is listening on http://{state.Address}:{state.Port} (PID {state.Pid}).");
      Console.WriteLine($"Run `lotsen open` to open it in the browser. A background server stops once no Claude Code session has used it for {BackgroundServer.GracePeriod.TotalSeconds:0} seconds.");
      return 0;
   }

   private static string LinkNotice => $"Open this link within {(int)SessionCodes.Lifetime.TotalMinutes} minutes; it works once:";

   private static async Task<int> OpenAsync(VersionInfo version, bool printLink)
   {
      const string noServer = "No lotsen server is running. Start one with `lotsen serve`, or start a Claude Code session with the lotsen channel.";
      var state = await new ServerDiscovery(DataDirectory.Resolve()).FindAsync(CancellationToken.None) switch
      {
         ServerAnswer.Running running => running.State,
         ServerAnswer.Unreachable unreachable => unreachable.State,
         ServerAnswer.None or ServerAnswer.Starting or ServerAnswer.Stale or ServerAnswer.Foreign => null,
      };
      if (state is null)
      {
         await Console.Error.WriteLineAsync(noServer);
         return 1;
      }

      using var client = new ServerClient(state);
      try
      {
         if (printLink)
         {
            PrintLink(await client.CreateUiLinkAsync(CancellationToken.None));
         }
         else
         {
            ReportOpened(await client.OpenUiAsync(CancellationToken.None));
         }
      }
      catch (RefusedRequestException refused)
      {
         await Console.Error.WriteLineAsync(await OpenRefusalAsync(client, version, refused.Message));
         return 1;
      }
      catch (HttpRequestException)
      {
         await Console.Error.WriteLineAsync(noServer);
         return 1;
      }

      return 0;
   }

   // The page comes from the server, so it matches the server whatever the server's build. A server of another build opens as this build's does, and only its refusal names the builds.
   private static async Task<string> OpenRefusalAsync(ServerClient client, VersionInfo version, string reason)
   {
      var refused = $"The lotsen server (PID {client.State.Pid}) refused the request: {reason}";
      VersionInfo? server;
      try
      {
         server = await client.GetVersionAsync(CancellationToken.None);
      }
      catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
      {
         server = null;
      }

      if (server == version)
      {
         return refused;
      }

      var other = server is { } versions ? $"build {versions.Build}, protocol {versions.Protocol}" : "a build whose versions could not be read";
      return $"{refused}{Environment.NewLine}That server runs {other}, and this executable is build {version.Build}, protocol {version.Protocol}. Run `lotsen open` from that server's install. To use this build, stop that server with `lotsen stop`, which stops it for every session that uses it, or give this build another data directory with {DataDirectory.OverrideVariable}.";
   }

   // Standard output holds only the link, so a shell can pass the output to a browser.
   private static void PrintLink(UiLinkResult link)
   {
      Console.Error.WriteLine(LinkNotice);
      Console.WriteLine(link.Url);
   }

   private static void ReportOpened(OpenUiResult result)
   {
      if (result.Url is { } url)
      {
         Console.WriteLine($"Could not launch a browser. {LinkNotice}");
         Console.WriteLine(url);
      }
      else
      {
         Console.WriteLine("Opened lotsen in the browser. If no browser window opened, run 'lotsen open --print-link' and open the link it prints.");
      }
   }

   // Claude Code ends the adapter by closing its standard input, and a terminal or service manager by a signal.
   private static async Task<int> McpAsync(VersionInfo version)
   {
      using var stop = new CancellationTokenSource();
      void Stop(PosixSignalContext context)
      {
         context.Cancel = true;
         stop.Cancel();
      }

      using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);
      using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);
      try
      {
         await McpAdapter.RunAsync(DataDirectory.Resolve(), version, stop.Token);
      }
      catch (OperationCanceledException) when (stop.IsCancellationRequested)
      {
      }

      return 0;
   }
}
