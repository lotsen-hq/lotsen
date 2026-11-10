using Lotsen.Client;
using Lotsen.Git;
using Lotsen.Hosting;
using Lotsen.Operations;
using Lotsen.Platforms;
using Lotsen.Server;
using Lotsen.Tests;
using Microsoft.Extensions.DependencyInjection;

namespace Lotsen.TestHost;

/// <summary>One real server and what it stands on: its own data directory, manual clock and test platform, over the host's shared origin.</summary>
/// <remarks>
/// The world provides only what no operation can: commits, the pull request that the platform reports, the clock and restarts. Everything else goes through the server's operations, so the
/// rules that a test meets are the server's own.
/// </remarks>
internal sealed class World : IAsyncDisposable
{
   private readonly SharedOrigin _origin;
   private readonly Lock _lock = new();
   private readonly Dictionary<string, ClaudeSession> _sessions = [];
   private Func<CancellationToken, Task<ResolvedPullRequest>>? _answer;
   private Hold? _held;
   private int _unanswered;
   private TaskCompletionSource? _resolveWaits;
   private int _nextSession;

   private World(string id, DataDirectory dataDirectory, SharedOrigin origin, Func<CancellationToken, Task<ResolvedPullRequest>>? answer)
   {
      Id = id;
      DataDirectory = dataDirectory;
      _origin = origin;
      _answer = answer;
      Platform = new TestPlatform();
      Platform.Answer(ResolveAsync);
   }

   public string Id { get; }

   public DataDirectory DataDirectory { get; }

   public ManualTimeProvider Clock { get; } = new();

   public TestPlatform Platform { get; }

   public LotsenServer? Server { get; private set; }

   /// <summary>The server's base URL, such as <c>http://127.0.0.1:5123</c>.</summary>
   public string Url => $"http://127.0.0.1:{RunningServer.Port}";

   /// <summary>The UI credential of the server's current run.</summary>
   public string UiCredential => RunningServer.Services.GetRequiredService<ServerCredentials>().Ui.Value;

   private LotsenServer RunningServer => Server ?? throw new InvalidOperationException($"World {Id} has no running server.");

   /// <summary>What every resolve of a pull request returns now, or null while the test has set no pull request, which a template passes on to its copies.</summary>
   public Func<CancellationToken, Task<ResolvedPullRequest>>? PullRequestAnswer
   {
      get
      {
         lock (_lock)
         {
            return _answer;
         }
      }
   }

   /// <summary>Starts a world on its data directory, which may hold a template's copy.</summary>
   /// <param name="clockStart">The time the world's clock starts at, or null for the clock's own start.</param>
   /// <param name="answer">The pull request answer of the template that the data directory copies, so a check of the copied tour resolves as the template's did, or null for none.</param>
   public static async Task<World> StartAsync(
      string id, DataDirectory dataDirectory, SharedOrigin origin, DateTimeOffset? clockStart, Func<CancellationToken, Task<ResolvedPullRequest>>? answer, CancellationToken cancellationToken)
   {
      var world = new World(id, dataDirectory, origin, answer);
      if (clockStart is { } start)
      {
         world.Clock.Advance(start - world.Clock.GetUtcNow());
      }

      await world.StartServerAsync(cancellationToken);
      return world;
   }

   /// <summary>Starts the server on the world's data directory, with new credentials.</summary>
   public async Task StartServerAsync(CancellationToken cancellationToken)
   {
      var options = new ServerOptions(DataDirectory, VersionInfo.FromAssemblies(typeof(LotsenServer).Assembly))
      {
         TimeProvider = Clock,
         BrowserLauncher = new RecordingBrowserLauncher(launches: false),
         Iterations = ServerHarness.ImmediateIterations,
         Platforms = [_ => Platform],
      };
      Server = await LotsenServer.StartAsync(options, cancellationToken);
   }

   /// <summary>Drops every agent session, then stops the server, which removes its state file.</summary>
   public async Task StopServerAsync()
   {
      foreach (var session in TakeSessions())
      {
         await session.DisposeAsync();
      }

      if (Server is { } server)
      {
         Server = null;
         await server.DisposeAsync();
      }
   }

   /// <inheritdoc cref="SharedOrigin.Commit"/>
   public CommitId Commit(string branch, CommitId? parent, string message, IReadOnlyDictionary<string, string?> files) => _origin.Commit(Id, branch, parent, message, files);

   /// <summary>Makes every resolve of a pull request return the shared origin with these commits, iteration and state.</summary>
   public void SetPullRequest(CommitId source, CommitId target, int iteration, PullRequestState state) =>
      SetAnswer(_ => Task.FromResult(new ResolvedPullRequest(new Uri(_origin.Url.AbsoluteUri), source, target, iteration, state)));

   /// <summary>Makes every resolve fail with this reason, as the platform's API would.</summary>
   public void FailPullRequest(string reason) => SetAnswer(_ => Task.FromException<ResolvedPullRequest>(new PlatformException(reason)));

   /// <summary>Makes every later resolve wait until <see cref="ReleasePullRequest"/>.</summary>
   public void HoldPullRequest()
   {
      lock (_lock)
      {
         _held ??= new Hold();
      }
   }

   /// <summary>Lets every held resolve continue.</summary>
   public void ReleasePullRequest()
   {
      Hold? held;
      lock (_lock)
      {
         held = _held;
         _held = null;
      }

      held?.Released.TrySetResult();
   }

   /// <summary>Completes when no background job of the server runs, such as a pin or an iteration check.</summary>
   public Task IdleAsync() => RunningServer.Services.GetRequiredService<BackgroundJobs>().WhenIdleAsync();

   /// <summary>Completes once no background job of the server runs, or once a resolve of the pull request waits on the test: on a hold, or on a pull request that the test has not set.</summary>
   /// <remarks>
   /// A job that waits on the test runs until the test acts, so the jobs are never idle then. A release ends the hold's wait at once, so a call after the release waits for the jobs that the
   /// held resolves continue.
   /// </remarks>
   public Task QuietAsync()
   {
      Task waits;
      lock (_lock)
      {
         if (_held is { Waiting: > 0 } || _unanswered > 0)
         {
            return Task.CompletedTask;
         }

         waits = (_resolveWaits ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
      }

      return Task.WhenAny(IdleAsync(), waits);
   }

   /// <summary>Writes a comment line of the text on every open event stream of the server, after every event that the server published before the call.</summary>
   /// <exception cref="ArgumentException">The text holds a line break.</exception>
   public void Comment(string text) => RunningServer.Services.GetRequiredService<EventBroadcaster>().Publish(ServerEvent.Comment(text));

   /// <summary>Returns a new one-time code for the UI, from the link that <c>open_ui</c> builds.</summary>
   public async Task<string> IssueCodeAsync(CancellationToken cancellationToken)
   {
      using var client = new ServerClient(StateFile.TryRead(DataDirectory) ?? throw new InvalidOperationException($"World {Id} has no running server."));
      var url = (await client.OpenUiAsync(cancellationToken)).Url ?? throw new InvalidOperationException($"World {Id}'s server launched a browser and returned no link.");
      const string marker = "#code=";
      return url[(url.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..];
   }

   /// <summary>Opens a real agent session on the world's server, as an adapter does.</summary>
   /// <param name="confirm">Whether the session acks each probe at once, so its bindings become confirmed.</param>
   public async Task<ClaudeSession> OpenSessionAsync(bool confirm, CancellationToken cancellationToken)
   {
      var name = $"{Id}-s{Interlocked.Increment(ref _nextSession)}";
      var session = await ClaudeSession.OpenAsync(name, DataDirectory, RunningServer, confirm, cancellationToken);
      lock (_lock)
      {
         _sessions[name] = session;
      }

      return session;
   }

   /// <summary>Returns the world's open session of this name.</summary>
   /// <exception cref="KeyNotFoundException">The world has no open session of the name.</exception>
   public ClaudeSession Session(string name)
   {
      lock (_lock)
      {
         return _sessions.TryGetValue(name, out var session) ? session : throw new KeyNotFoundException($"World {Id} has no open session {name}.");
      }
   }

   /// <summary>Drops the session's connection, as an adapter that exits does, and completes once the server settled the session's end.</summary>
   public async Task DropSessionAsync(string name)
   {
      ClaudeSession? session;
      lock (_lock)
      {
         _sessions.Remove(name, out session);
      }

      if (session is not null)
      {
         await session.DisposeAsync();
         await session.WhenSettledAsync(RunningServer);
      }
   }

   public async ValueTask DisposeAsync()
   {
      ReleasePullRequest();
      await StopServerAsync();
   }

   private List<ClaudeSession> TakeSessions()
   {
      lock (_lock)
      {
         var sessions = _sessions.Values.ToList();
         _sessions.Clear();
         return sessions;
      }
   }

   private void SetAnswer(Func<CancellationToken, Task<ResolvedPullRequest>> answer)
   {
      lock (_lock)
      {
         _answer = answer;
      }
   }

   private async Task<ResolvedPullRequest> ResolveAsync(CancellationToken cancellationToken)
   {
      Func<CancellationToken, Task<ResolvedPullRequest>>? answer;
      Hold? held;
      TaskCompletionSource? waits = null;
      lock (_lock)
      {
         held = _held;
         if (held is not null)
         {
            held.Waiting++;
            waits = TakeResolveWaits();
         }
      }

      if (held is not null)
      {
         waits?.TrySetResult();
         await held.Released.Task.WaitAsync(cancellationToken);
      }

      lock (_lock)
      {
         answer = _answer;
         if (answer is null)
         {
            _unanswered++;
            waits = TakeResolveWaits();
         }
      }

      if (answer is null)
      {
         waits?.TrySetResult();
         try
         {
            // A pull request that the test has not set keeps its pin in progress, as a platform that has not answered yet.
            await Task.Delay(Timeout.Infinite, cancellationToken);
         }
         finally
         {
            lock (_lock)
            {
               _unanswered--;
            }
         }
      }

      return await answer!(cancellationToken);
   }

   // Call it under the lock, when a resolve starts to wait on the test. Returns the signal of the QuietAsync calls that wait for one, which the caller sets outside the lock.
   private TaskCompletionSource? TakeResolveWaits()
   {
      var waits = _resolveWaits;
      _resolveWaits = null;
      return waits;
   }

   // A hold of the pull request, with the resolves that wait on it. A release drops the hold, so the count needs no decrement.
   private sealed class Hold
   {
      public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

      public int Waiting { get; set; }
   }
}
