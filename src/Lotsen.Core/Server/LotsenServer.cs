using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Lotsen.Git;
using Lotsen.Hosting;
using Lotsen.Json;
using Lotsen.Operations;
using Lotsen.Pinning;
using Lotsen.Platforms;
using Lotsen.Sessions;
using Lotsen.Tours;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;

namespace Lotsen.Server;

/// <summary>
/// The settings a server starts with.
/// </summary>
/// <param name="DataDirectory">The directory that receives the state file and the tours.</param>
/// <param name="Version">The versions the server reports to the adapter. Tests set others to stand for a server of another build.</param>
internal sealed record ServerOptions(DataDirectory DataDirectory, VersionInfo Version)
{
   /// <summary>Returns the settings that every server of a lotsen command starts with. Each command adds only what its server does differently.</summary>
   /// <param name="dataDirectory">The data directory that the command resolved.</param>
   /// <param name="version">The versions of the executable.</param>
   /// <param name="platforms">The factories of the platforms that tours can target.</param>
   /// <param name="port">The configured port, or null for one that the OS assigns.</param>
   /// <param name="factCacheSize">The fact cache size that the command resolved.</param>
   public static ServerOptions ForCommand(
      DataDirectory dataDirectory, VersionInfo version, IReadOnlyList<Func<IServiceProvider, IPullRequestPlatform>> platforms, ServerPort? port, FactCacheSize factCacheSize) =>
      new(dataDirectory, version) { Platforms = platforms, Port = port, FactCacheSize = factCacheSize };

   /// <summary>The clock that one-time codes expire by and tours are timestamped with.</summary>
   public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

   /// <summary>What opens the UI in the user's browser.</summary>
   public IBrowserLauncher BrowserLauncher { get; init; } = new SystemBrowserLauncher();

   /// <summary>The factories of the platforms that tours can target, each called once with the server's services.</summary>
   public IReadOnlyList<Func<IServiceProvider, IPullRequestPlatform>> Platforms { get; init; } = [];

   /// <summary>How to run git.</summary>
   public GitOptions Git { get; init; } = new();

   /// <summary>A provider that receives every log line at every level, beside the console, or null for none.</summary>
   public ILoggerProvider? LoggerProvider { get; init; }

   /// <summary>The least severe level that the console prints.</summary>
   public LogLevel MinimumLogLevel { get; init; } = LogLevel.Warning;

   /// <summary>The configured port to listen on, or null for one that the OS assigns.</summary>
   public ServerPort? Port { get; init; }

   /// <summary>The budget of the fact memo.</summary>
   public FactCacheSize FactCacheSize { get; init; } = FactCacheSize.Default;

   /// <summary>How long the server waits with no agent session open before it stops, or null for a server that runs until it is told to stop.</summary>
   /// <remarks>See <see cref="AgentSessions.ExitWhenIdle"/>. The wait starts once the state file is written.</remarks>
   public TimeSpan? IdleGracePeriod { get; init; }

   /// <summary>Whether the server writes its log to <see cref="DataDirectory.LogFilePath"/> through <see cref="ServerLog"/>, with no console log.</summary>
   public bool LogsToFile { get; init; }

   /// <summary>The liveness that every event stream announces in its hello and keeps on the clock of <see cref="LivenessClock"/>.</summary>
   public StreamLiveness Liveness { get; init; } = StreamLiveness.Default;

   /// <summary>The clock that times the keep-alive of every event stream.</summary>
   /// <remarks>It is apart from <see cref="TimeProvider"/> because each reader times its silence limit on a clock of its own, so a test that holds <see cref="TimeProvider"/> still leaves the streams alive.</remarks>
   public TimeProvider LivenessClock { get; init; } = TimeProvider.System;

   /// <summary>When the iteration checks run, which the clock of <see cref="TimeProvider"/> times.</summary>
   public IterationSchedule Iterations { get; init; } = IterationSchedule.Default;

   /// <summary>Called after each try of the claim lock that another holder refuses, or null for none. Tests time a lock probe against the start with it.</summary>
   public Action? ClaimRefused { get; init; }
}

/// <summary>
/// A running lotsen server: the HTTP API and the UI, on a port of 127.0.0.1 that the user configures or the OS assigns.
/// </summary>
/// <remarks>
/// The server holds a token that acts as the user, so it listens on loopback only. Clients find its port and the server token in the state file, which exists while the server runs. The server
/// holds the claim lock of its data directory for its whole run.
/// </remarks>
internal sealed class LotsenServer : IAsyncDisposable
{
   private readonly WebApplication _app;
   private readonly ClaimLock _claim;
   private readonly ServerLog? _log;

   private LotsenServer(WebApplication app, ClaimLock claim, ServerLog? log, int port)
   {
      _app = app;
      _claim = claim;
      _log = log;
      Port = port;
   }

   /// <summary>
   /// The server's <see cref="HostOptions.ShutdownTimeout"/>, the timeout of the host's stop, which covers the requests in flight and the background jobs
   /// (https://learn.microsoft.com/dotnet/api/microsoft.extensions.hosting.hostoptions.shutdowntimeout).
   /// </summary>
   public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

   /// <summary>How long a start tries the claim lock again after another holder refused it, before the start fails with <see cref="ServerAlreadyRunningException"/>.</summary>
   /// <remarks>
   /// A probe of <see cref="ClaimLock.IsHeld"/> holds the lock for the moment of one file open, so a start that collides with a probe takes the lock on a later try, as
   /// <c>ServerDiscoveryTests</c> pins. A server that holds the lock for its whole run still refuses every try.
   /// </remarks>
   public static readonly TimeSpan ClaimWindow = TimeSpan.FromMilliseconds(250);

   private static readonly TimeSpan ClaimRetryInterval = TimeSpan.FromMilliseconds(25);

   /// <summary>The TCP port the server listens on.</summary>
   public int Port { get; }

   /// <summary>The server's services, for tests that wait on its background work.</summary>
   internal IServiceProvider Services => _app.Services;

   /// <summary>Takes the claim lock, opens the log file when the server logs to it, starts the server with new credentials, then writes the state file.</summary>
   /// <exception cref="ServerAlreadyRunningException">Another server holds the data directory's claim lock through the whole <see cref="ClaimWindow"/>. The start then writes no file.</exception>
   /// <exception cref="PortUnavailableException">The OS refuses the bind of the configured port, as when another socket holds it. The start then writes no state file and releases the claim lock.</exception>
   public static async Task<LotsenServer> StartAsync(ServerOptions options, CancellationToken cancellationToken)
   {
      var claim = await TakeClaimAsync(options, cancellationToken);
      ServerLog? log = null;
      try
      {
         claim.DeleteStaleStateFile();
         log = options.LogsToFile ? ServerLog.Open(options.DataDirectory) : null;
         var listener = BindLoopback(options.Port);
         try
         {
            return await StartClaimedAsync(options, claim, log, listener, cancellationToken);
         }
         catch
         {
            listener.Dispose();
            throw;
         }
      }
      catch
      {
         log?.Dispose();
         claim.Dispose();
         throw;
      }
   }

   private static async Task<ClaimLock> TakeClaimAsync(ServerOptions options, CancellationToken cancellationToken)
   {
      var elapsed = Stopwatch.StartNew();
      while (true)
      {
         if (ClaimLock.TryTake(options.DataDirectory) is { } claim)
         {
            return claim;
         }

         options.ClaimRefused?.Invoke();
         if (elapsed.Elapsed >= ClaimWindow)
         {
            throw new ServerAlreadyRunningException(options.DataDirectory, HolderPid(options.DataDirectory));
         }

         await Task.Delay(ClaimRetryInterval, cancellationToken);
      }
   }

   // The holder's state file names it once the holder has written that file.
   private static int? HolderPid(DataDirectory dataDirectory) => StateFile.TryRead(dataDirectory) is { } state && ProcessLiveness.IsAlive(state.Pid) ? state.Pid : null;

   /// <summary>Returns a socket bound to the port of 127.0.0.1, for Kestrel to listen on.</summary>
   /// <remarks>The bind comes before the host starts, so a port in use fails the start without the host's log of a failed start.</remarks>
   private static Socket BindLoopback(ServerPort? port)
   {
      try
      {
         return LoopbackListener.Bind(port);
      }
      catch (SocketException exception) when (port is not null)
      {
         throw new PortUnavailableException(port, exception);
      }
   }

   private static async Task<LotsenServer> StartClaimedAsync(ServerOptions options, ClaimLock claim, ServerLog? log, Socket listener, CancellationToken cancellationToken)
   {
      var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
      {
         ContentRootPath = AppContext.BaseDirectory,
         EnvironmentName = Environments.Production,
      });

      // Settings files and environment variables must not move the listener off loopback; LoopbackPort checks it.
      builder.Configuration.Sources.Clear();

      // The host reads PREFERHOSTINGURLS before the sources are cleared, and resetting it needs a source.
      builder.Configuration.AddInMemoryCollection();
      builder.WebHost.PreferHostingUrls(false);
      builder.Logging.SetMinimumLevel(options.MinimumLogLevel);
      if (log is not null)
      {
         builder.Logging.ClearProviders();
         builder.Logging.AddProvider(log);
      }

      if (options.LoggerProvider is { } loggerProvider)
      {
         builder.Logging.AddProvider(loggerProvider);
         builder.Services.Configure<LoggerFilterOptions>(filters => filters.Rules.Add(new LoggerFilterRule(loggerProvider.GetType().FullName, categoryName: null, LogLevel.Trace, filter: null)));
      }

      var endpoint = (IPEndPoint)listener.LocalEndPoint!;
      LoopbackListener.ListenOn(builder.WebHost, listener);
      builder.WebHost.ConfigureKestrel(kestrel => kestrel.AddServerHeader = false);

      builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = ShutdownTimeout);
      builder.Services.ConfigureHttpJsonOptions(json => LotsenJsonContext.Configure(json.SerializerOptions));
      var credentials = ServerCredentials.CreateRandom();
      builder.Services.AddSingleton(credentials);
      builder.Services.AddSingleton<AgentSessions>();
      builder.Services.AddSingleton(options.TimeProvider);
      builder.Services.AddSingleton(options.BrowserLauncher);
      builder.Services.AddSingleton(options.Version);
      builder.Services.AddSingleton<SessionCodes>();
      builder.Services.AddSingleton(new ServerEndpoint(endpoint.Port));
      builder.Services.AddSingleton<UiLauncher>();
      builder.Services.AddSingleton<EventBroadcaster>();
      builder.Services.AddSingleton(new ServerSentEvents(options.Liveness, options.LivenessClock));
      builder.Services.AddTourOperations();
      builder.Services.AddSingleton(options.DataDirectory);
      builder.Services.AddSingleton(new GitRunner(options.Git));
      foreach (var platform in options.Platforms)
      {
         builder.Services.AddSingleton<IPullRequestPlatform>(platform);
      }

      builder.Services.AddSingleton<PullRequestPlatforms>();
      builder.Services.AddSingleton<ICloneDirectories, CloneDirectories>();
      builder.Services.AddSingleton<CloneCache>();
      builder.Services.AddSingleton<CloneMaintenance>();
      builder.Services.AddSingleton(options.FactCacheSize);
      builder.Services.AddSingleton<IGitFacts, CloneGitFacts>();
      builder.Services.AddSingleton(options.Iterations);
      builder.Services.AddSingleton<IterationLifecycle>();
      builder.Services.AddSingleton<IterationScheduler>();
      builder.Services.AddSingleton<FileOperations>();
      builder.Services.AddSingleton<BackgroundJobs>();
      builder.Services.AddHostedService(services => services.GetRequiredService<BackgroundJobs>());

      var app = builder.Build();
      app.Lifetime.ApplicationStopping.Register(app.Services.GetRequiredService<AgentSessions>().BeginShutdown);
      app.UseRequestGuards();
      app.MapUiCredential();
      app.MapAgentSession();
      app.MapOperations();
      app.MapVersion();
      app.MapShutdown();
      app.MapEventStream();
      UiAssets.LoadEmbedded().MapUi(app);

      try
      {
         await app.StartAsync(cancellationToken);
      }
      catch
      {
         await app.DisposeAsync();
         throw;
      }

      try
      {
         var port = LoopbackPort(app);

         // Clients find the server through the state file, so no request can start a pin before this.
         // The scheduler observes the writer's stores from its construction on, so it exists before any write.
         app.Services.GetRequiredService<IterationScheduler>();
         // The maintenance timer starts with its construction.
         app.Services.GetRequiredService<CloneMaintenance>();
         app.Services.GetRequiredService<TourWriter>().MarkPendingRequestsUnanswered();
         app.Services.GetRequiredService<TourPins>().ResumePins();
         StateFile.Write(options.DataDirectory, new ServerState(Environment.ProcessId, IPAddress.Loopback.ToString(), port, credentials.ServerToken.Value));
         if (options.IdleGracePeriod is { } gracePeriod)
         {
            app.Services.GetRequiredService<AgentSessions>().ExitWhenIdle(gracePeriod, options.TimeProvider, app.Lifetime.StopApplication);
         }

         return new LotsenServer(app, claim, log, port);
      }
      catch
      {
         await app.StopAsync(CancellationToken.None);
         await app.DisposeAsync();
         throw;
      }
   }

   /// <summary>Completes when the host is told to stop, as by Ctrl-C, the shutdown route or the idle exit.</summary>
   public Task WaitForShutdownAsync(CancellationToken cancellationToken = default) => _app.WaitForShutdownAsync(cancellationToken);

   /// <summary>Stops the server, removes its state file, closes its log file, then releases the claim lock.</summary>
   public async ValueTask DisposeAsync()
   {
      try
      {
         await _app.StopAsync(CancellationToken.None);
      }
      finally
      {
         try
         {
            _claim.DeleteOwnStateFile();
            await _app.DisposeAsync();
         }
         finally
         {
            _log?.Dispose();
            _claim.Dispose();
         }
      }
   }

   /// <summary>Returns the port of the server's one listener.</summary>
   /// <exception cref="InvalidOperationException">The server listens anywhere other than one loopback address.</exception>
   private static int LoopbackPort(WebApplication app)
   {
      var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
      if (addresses.Count != 1 || !Uri.TryCreate(addresses.Single(), UriKind.Absolute, out var address) || !IPAddress.TryParse(address.Host, out var ip) || !IPAddress.IsLoopback(ip))
      {
         throw new InvalidOperationException($"lotsen must listen on one loopback address only, but listens on: {string.Join(", ", addresses)}.");
      }

      return address.Port;
   }
}

/// <summary>An error that refuses a server start because another server holds the data directory's claim lock. Its message tells the user what to do.</summary>
internal sealed class ServerAlreadyRunningException : Exception
{
   public ServerAlreadyRunningException(DataDirectory dataDirectory, int? pid)
      : base(Describe(dataDirectory, pid))
   {
      Pid = pid;
   }

   /// <summary>The process ID of the server that holds the lock, or null when that server has not written its state file yet.</summary>
   public int? Pid { get; }

   private static string Describe(DataDirectory dataDirectory, int? pid)
   {
      var server = pid is { } id ? $"Another lotsen server (PID {id})" : "Another lotsen server";
      return $"{server} is running on the data directory {dataDirectory.Path}. Stop it first with `lotsen stop`, or set {DataDirectory.OverrideVariable} to use another data directory.";
   }
}

/// <summary>An error that refuses a server start because the OS refuses the bind of the configured port. Its message tells the user what to do.</summary>
internal sealed class PortUnavailableException : Exception
{
   public PortUnavailableException(ServerPort port, SocketException innerException)
      : base(Describe(port, innerException), innerException)
   {
      Port = port;
   }

   /// <summary>The configured port that the OS refuses.</summary>
   public ServerPort Port { get; }

   private static string Describe(ServerPort port, SocketException exception)
   {
      var problem = exception.SocketErrorCode == SocketError.AddressAlreadyInUse ? "is in use" : $"cannot be used: {exception.Message.TrimEnd('.')}";
      return $"Port {port} of 127.0.0.1 {problem}. Choose another port with --port or the {ServerPort.Variable} environment variable, or set neither for a port that the OS assigns.";
   }
}
