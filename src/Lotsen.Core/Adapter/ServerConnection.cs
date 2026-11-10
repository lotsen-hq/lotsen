using System.Text.Json;
using System.Threading.Channels;
using Lotsen.Client;
using Lotsen.Hosting;
using Lotsen.Operations;
using Lotsen.Server;
using Lotsen.Sessions;
using Lotsen.Tours;

namespace Lotsen.Adapter;

/// <summary>Holds the adapter's agent session on the running server, makes each tool call as that session, and opens a new session whenever the connection drops.</summary>
/// <remarks>
/// <para>
/// <see cref="RunAsync"/> is the one owner of the connection, and <see cref="_lock"/> guards the open session and the next outcome that callers wait for. It asks
/// <see cref="ServerDiscovery"/> before each attempt, so it finds a server started or restarted after the adapter. Each attempt sends the adapter's versions, and the server refuses another
/// build before it registers a session.
/// </para>
/// <para>
/// It alone holds a <see cref="ServerClient"/>, one per server, and retires it once the state file names another server. Each call holds a lease on the client of its session, and a retired
/// client is disposed when its last lease ends, so a call in flight on that client fails only when its server is gone.
/// </para>
/// <para>
/// It hands each event on the session's stream to the forward callback, one at a time and in stream order, and reads the next event only once the callback returns.
/// </para>
/// <para>
/// When no server runs, it starts one through the start callback, but only on its first attempt and when a tool call waits for a session. So a failed start does not repeat on every
/// attempt, and a server that the user stopped stays stopped until Claude calls a tool. A server that is starting and a stale state file count as no server. A state file whose
/// port has a listener that does not answer as lotsen does, in time or at all, gets no start, and its failure names the fix. A server that refuses the session because it decided its idle
/// exit counts as no server once it has exited, and the attempt waits at most <see cref="ExitWait"/> for that.
/// </para>
/// </remarks>
internal sealed class ServerConnection
{
   /// <summary>Returns what the user must do when the server refuses a call with the adapter's session and again with a new one.</summary>
   /// <param name="command">The command that runs the adapter's build, which the message names.</param>
   public static string CredentialsRefusedMessage(LotsenCommand command) =>
      $"The lotsen server refused this adapter's credentials, also with a new agent session. Run `{command.With("restart")}`, then call the tool again.";

   /// <summary>What the user must do when the server stopped or cannot be reached.</summary>
   public const string NoServerMessage = "The lotsen server stopped or could not be reached. Call the tool again, and lotsen starts a server when none runs.";

   /// <summary>What Claude must do when a tool call outlasted the request timeout, which the server may still run.</summary>
   /// <remarks>Only a tool that takes a tourId has a tour that read_state can show the call's effect in. A launch of open_ui stores nothing in the tour, and another call opens a second tab.</remarks>
   public const string CallTimedOutMessage =
      "This call to the lotsen server took too long, and the server may still be running it. For a tool that takes a tourId, except open_ui, read the tour with read_state to see whether the call took effect, and call the tool once more only if it did not. For open_ui or any other tool, or when the call takes too long again, tell the user, and stop.";

   /// <summary>The reason of a tool call that waited for a session while the adapter stopped.</summary>
   public const string StoppingMessage = "The adapter is stopping.";

   /// <summary>Returns what the user must do when the answer to a tool call was not lotsen's, as a status and body that no lotsen refusal has.</summary>
   /// <param name="description">The request, the status and the body, as <see cref="WireAnswer{T}.Unexpected"/> holds them.</param>
   /// <param name="command">The command that runs the adapter's build, which the message names.</param>
   public static string UnexpectedAnswerMessage(string description, LotsenCommand command) => $"{description.TrimEnd().TrimEnd('.')}. Run `{command.With("restart")}`, then call the tool again.";

   /// <summary>Returns what the user must do when an attempt to open the session failed in a way that names no other fix.</summary>
   /// <param name="reason">The failure's own message.</param>
   /// <param name="command">The command that runs the adapter's build, which the message names.</param>
   public static string OpenFailedMessage(string reason, LotsenCommand command) =>
      $"The adapter could not open its session on the lotsen server: {reason.TrimEnd().TrimEnd('.')}. Run `{command.With("restart")}`, then call the tool again.";

   /// <summary>Returns what the user must do when the listener on the port that the state file names does not answer as lotsen does, and no server holds the claim lock.</summary>
   /// <param name="port">The port that the state file names.</param>
   /// <param name="pid">The process ID that the state file names.</param>
   /// <param name="command">The command that runs the adapter's build, which the message names.</param>
   public static string ForeignMessage(int port, int pid, LotsenCommand command) =>
      $"The lotsen state file names port {port} and PID {pid}, and the program on that port does not answer as a lotsen server does. If PID {pid} is a lotsen server that hangs, end it. Then run `{command.With("restart")}`, and call the tool again.";

   /// <summary>Returns what the user must do when the server refused the session because it decided its idle exit, and it still ran when the wait for its exit ended.</summary>
   /// <param name="command">The command that runs the adapter's build, which the message names.</param>
   public static string ExitTooSlowMessage(LotsenCommand command) =>
      $"The old lotsen server refused agent sessions because it decided to exit, and it did not exit in time for a new server to start. Run `{command.With("restart")}`, then call the tool again.";

   /// <summary>How long the connection waits between attempts when no tool call asks for a session sooner.</summary>
   public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1);

   /// <summary>How long an attempt waits for the exit of a server that refused the session because it decided its idle exit, before the attempt fails.</summary>
   /// <remarks>A server's host stops within <see cref="LotsenServer.ShutdownTimeout"/>, and the server deletes its state file and releases the claim lock right after it.</remarks>
   public static readonly TimeSpan ExitWait = LotsenServer.ShutdownTimeout * 2;

   private static readonly TimeSpan ExitPollInterval = TimeSpan.FromMilliseconds(50);

   private readonly Lock _lock = new();
   private readonly ServerDiscovery _discovery;
   private readonly VersionInfo _version;
   private readonly LotsenCommand _command;
   private readonly ILogger _logger;
   private readonly Func<SessionEvent, CancellationToken, Task> _forward;
   private readonly Func<CancellationToken, Task>? _startServer;
   private readonly TimeProvider _time;
   private readonly TimeSpan? _requestTimeout;
   private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
   private OpenSession? _open;
   private CancellationTokenSource? _dropOpen;
   private TaskCompletionSource<OpenSession> _next = NewOutcome();

   // Whether the next attempt that finds no server may start one: on the first attempt of a connection that has opened no session, and after a tool call asked.
   private bool _startUnasked = true;
   private bool _startAsked;

   // The outcome of the attempt that took the start turn a call asked for, or null while no attempt has taken it.
   private Task<OpenSession>? _askTakenBy;
   private bool _stopped;

   // The client of the last server that RunAsync found. Only RunAsync reads and replaces it.
   private SharedClient? _client;

   /// <param name="dataDirectory">The data directory to find the server of.</param>
   /// <param name="version">The adapter's versions, which the server's must equal.</param>
   /// <param name="logger">Receives the reasons that no session opens, and the failures of <paramref name="forward"/>.</param>
   /// <param name="forward">Receives each event that the server sends the session, or null to skip every event. An exception it throws is logged, and the next event still reaches it.</param>
   /// <param name="startServer">Starts a server and completes once it answers, or throws a <see cref="ServerStartException"/>. Null leaves the start to the user.</param>
   /// <param name="timeProvider">The clock of the waits between attempts and for a server's exit, or null for the system clock.</param>
   /// <param name="requestTimeout">How long each request to the server waits for its answer, or null for <see cref="ServerClient.RequestTimeout"/>.</param>
   public ServerConnection(
      DataDirectory dataDirectory, VersionInfo version, ILogger logger, Func<SessionEvent, CancellationToken, Task>? forward = null, Func<CancellationToken, Task>? startServer = null,
      TimeProvider? timeProvider = null, TimeSpan? requestTimeout = null)
   {
      _discovery = new ServerDiscovery(dataDirectory);
      _version = version;
      _command = LotsenCommand.Of(version);
      _logger = logger;
      _forward = forward ?? ((_, _) => Task.CompletedTask);
      _startServer = startServer;
      _time = timeProvider ?? TimeProvider.System;
      _requestTimeout = requestTimeout;
   }

   /// <summary>Opens a session, holds it while its connection lasts, and opens a new one after each drop, until the token is canceled.</summary>
   public async Task RunAsync(CancellationToken cancellationToken)
   {
      string? lastFailure = null;
      try
      {
         while (!cancellationToken.IsCancellationRequested)
         {
            try
            {
               await AttemptAsync(cancellationToken);
               lastFailure = null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
               // Only RunAsync settles the outcomes that tool calls wait for, so a failure of any kind must not end it. A canceled run ends in the wait to retry.
               var expected = exception is ServerUnavailableException or ServerStartException or HttpRequestException or OperationCanceledException;
               var failure = exception switch
               {
                  ServerUnavailableException unavailable => unavailable,
                  ServerStartException => new ServerUnavailableException(exception.Message),
                  _ when expected => new ServerUnavailableException(NoServerMessage),
                  _ => new ServerUnavailableException(OpenFailedMessage(exception.Message, _command)),
               };
               Settle(outcome => outcome.TrySetException(failure));
               if (failure.Message != lastFailure)
               {
                  _logger.LogWarning(expected ? null : exception, "{Message}", failure.Message);
                  lastFailure = failure.Message;
               }
            }

            await WaitToRetryAsync(cancellationToken);
         }
      }
      catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
      {
      }
      finally
      {
         lock (_lock)
         {
            _stopped = true;
         }

         Settle(outcome => outcome.TrySetException(new ServerUnavailableException(StoppingMessage)));
         _client?.Retire();
         _client = null;
      }
   }

   /// <summary>Calls the operation as the adapter's session, and opens a session first when none is open.</summary>
   /// <remarks>
   /// A call that the server refuses for its credentials, as for a session that ended, goes again once with a new session, because the server refuses it before the operation runs. A call
   /// that outlasts the request timeout never goes again, because the server may still run it.
   /// </remarks>
   /// <param name="operation">The operation to call.</param>
   /// <param name="arguments">The input properties by name, as the tool call passes them.</param>
   /// <param name="cancellationToken">Cancels the call.</param>
   /// <returns>The operation's output, the server's refusal, or why no server answered the call.</returns>
   public async Task<ToolOutcome> InvokeAsync(Operation operation, IEnumerable<KeyValuePair<string, JsonElement>> arguments, CancellationToken cancellationToken)
   {
      for (var attempt = 1; ; attempt++)
      {
         OpenSession session;
         try
         {
            session = await LeaseAsync(cancellationToken);
         }
         catch (ServerRefusedException refused)
         {
            return new ToolOutcome.Refused(refused.Error);
         }
         catch (ServerUnavailableException unavailable)
         {
            return new ToolOutcome.Unavailable(unavailable.Message);
         }

         WireAnswer<string> answer;
         try
         {
            answer = await session.Client.Client.InvokeAsync(operation, arguments, session.Credential, cancellationToken);
         }
         catch (HttpRequestException)
         {
            return new ToolOutcome.Unavailable(NoServerMessage);
         }
         catch (TimeoutException)
         {
            return new ToolOutcome.Unavailable(CallTimedOutMessage);
         }
         finally
         {
            session.Client.Release();
         }

         var credentialsRefused = answer is WireAnswer<string>.CredentialsRefused || (answer is WireAnswer<string>.Refused { Error.Code: var code } && code == OperationErrorCode.SessionUnknown);
         if (credentialsRefused && attempt == 1)
         {
            Drop(session);
            continue;
         }

         return answer switch
         {
            _ when credentialsRefused => new ToolOutcome.Unavailable(CredentialsRefusedMessage(_command)),
            WireAnswer<string>.Output output => new ToolOutcome.Output(output.Value),
            WireAnswer<string>.Refused refused => new ToolOutcome.Refused(refused.Error),
            WireAnswer<string>.Unexpected unexpected => new ToolOutcome.Unavailable(UnexpectedAnswerMessage(unexpected.Description, _command)),
            WireAnswer<string>.CredentialsRefused => new ToolOutcome.Unavailable(CredentialsRefusedMessage(_command)),
         };
      }
   }

   private static TaskCompletionSource<OpenSession> NewOutcome() => new(TaskCreationOptions.RunContinuationsAsynchronously);

   // Returns the open session with a lease on its client, which the caller releases. A session that ended before the lease gives way to the next one.
   private async Task<OpenSession> LeaseAsync(CancellationToken cancellationToken)
   {
      while (true)
      {
         var session = await ConnectAsync(cancellationToken);
         lock (_lock)
         {
            // RunAsync retires a client only after its session stopped being the open one, which takes this lock.
            if (_open == session)
            {
               session.Client.Lease();
               return session;
            }
         }
      }
   }

   // Returns the open session, or asks for a start when no server runs and waits for the next attempt to open one. A failure is a ServerUnavailableException whose message names the fix.
   private async Task<OpenSession> ConnectAsync(CancellationToken cancellationToken)
   {
      Task<OpenSession> next;
      lock (_lock)
      {
         if (_open is { } open)
         {
            return open;
         }

         next = _next.Task;
         _startAsked = _startServer is not null;
         _askTakenBy = null;
      }

      _wake.Writer.TryWrite(true);
      try
      {
         return await next.WaitAsync(cancellationToken);
      }
      catch (ServerUnavailableException) when (OutcomeOfStartTurn(next) is { } turn)
      {
         // The attempt that failed may have begun before this call asked, so the attempt that takes the call's start turn, or the next one, answers the call. The wake starts that
         // attempt without a wait for the retry interval.
         _wake.Writer.TryWrite(true);
         return await turn.WaitAsync(cancellationToken);
      }
   }

   // Drops the session after the server refused its credential, so the next call waits for a new one. A session that is no longer open is left alone.
   private void Drop(OpenSession session)
   {
      CancellationTokenSource? drop;
      lock (_lock)
      {
         if (_open != session)
         {
            return;
         }

         _open = null;
         drop = _dropOpen;
      }

      // The canceled read may finish on this thread, and its end takes the lock.
      try
      {
         drop?.Cancel();
      }
      catch (ObjectDisposedException)
      {
      }
   }

   // Finds the server, opens a session on it and holds the session until its connection drops. A server that refuses the session because it decided its idle exit gets one replacement.
   private async Task AttemptAsync(CancellationToken cancellationToken)
   {
      var state = await FindAsync(FindServerAsync, cancellationToken);
      try
      {
         await HoldSessionAsync(ClientFor(state), cancellationToken);
      }
      catch (ServerExitingException)
      {
         // A second refusal of the same kind fails the attempt with the server's reason.
         var replacement = await FindAsync(token => ReplaceExitingServerAsync(state, token), cancellationToken);
         await HoldSessionAsync(ClientFor(replacement), cancellationToken);
      }
   }

   // An attempt that finds no server to open the session on retires the client, whose server is gone.
   private async Task<ServerState> FindAsync(Func<CancellationToken, Task<ServerState>> find, CancellationToken cancellationToken)
   {
      try
      {
         return await find(cancellationToken);
      }
      catch
      {
         _client?.Retire();
         _client = null;
         throw;
      }
   }

   private SharedClient ClientFor(ServerState state)
   {
      if (_client is { } client && client.Client.State == state)
      {
         return client;
      }

      _client?.Retire();
      _client = new SharedClient(new ServerClient(state, _requestTimeout));
      return _client;
   }

   // Returns the state file entry of the server to open the session on, after a start when no server runs and the attempt takes the start turn.
   private async Task<ServerState> FindServerAsync(CancellationToken cancellationToken)
   {
      var answer = await _discovery.FindAsync(cancellationToken);
      if (IsNoServer(answer) && TakeStartTurn())
      {
         await _startServer!(cancellationToken);
         answer = await _discovery.FindAsync(cancellationToken);
      }

      return StateOf(answer);
   }

   // The exiting server's refusal stands for no server, so the attempt takes the start turn, and starts once the exiting server is gone.
   private async Task<ServerState> ReplaceExitingServerAsync(ServerState exiting, CancellationToken cancellationToken)
   {
      var mayStart = TakeStartTurn();
      var deadline = _time.GetUtcNow() + ExitWait;
      while (true)
      {
         var answer = await _discovery.FindAsync(cancellationToken);
         var stillExiting = answer switch
         {
            ServerAnswer.Running running => running.State == exiting,
            ServerAnswer.Unreachable unreachable => unreachable.State == exiting,
            _ => false,
         };
         if (!stillExiting)
         {
            if (IsNoServer(answer) && mayStart)
            {
               await _startServer!(cancellationToken);
               answer = await _discovery.FindAsync(cancellationToken);
            }

            return StateOf(answer);
         }

         if (_time.GetUtcNow() >= deadline)
         {
            throw new ServerUnavailableException(ExitTooSlowMessage(_command));
         }

         await Task.Delay(ExitPollInterval, _time, cancellationToken);
      }
   }

   private static bool IsNoServer(ServerAnswer answer) => answer switch
   {
      ServerAnswer.None or ServerAnswer.Starting or ServerAnswer.Stale => true,
      ServerAnswer.Running or ServerAnswer.Unreachable or ServerAnswer.Foreign => false,
   };

   private ServerState StateOf(ServerAnswer answer) => answer switch
   {
      ServerAnswer.Running running => running.State,

      // The holder's own refusal, such as the one of another build, names the fix. A port without an HTTP server behind it fails the attempt as a stopped server does.
      ServerAnswer.Unreachable unreachable => unreachable.State,
      ServerAnswer.Foreign foreign => throw new ServerUnavailableException(ForeignMessage(foreign.State.Port, foreign.State.Pid, _command)),
      ServerAnswer.None or ServerAnswer.Starting or ServerAnswer.Stale => throw new ServerUnavailableException(NoServerMessage),
   };

   private bool TakeStartTurn()
   {
      lock (_lock)
      {
         var turn = _startServer is not null && (_startUnasked || _startAsked);
         if (_startAsked)
         {
            _askTakenBy = _next.Task;
         }

         _startUnasked = false;
         _startAsked = false;
         return turn;
      }
   }

   // Returns the outcome of the attempt that took or takes the call's start turn after the failed one, or null when the failed attempt took it, the connection has no start callback or
   // the run has stopped. The attempt in progress takes the turn when it finds no server, and its outcome also answers the call when it finds one.
   private Task<OpenSession>? OutcomeOfStartTurn(Task<OpenSession> failed)
   {
      lock (_lock)
      {
         if (_stopped || _startServer is null || _askTakenBy == failed)
         {
            return null;
         }

         return _askTakenBy ?? _next.Task;
      }
   }

   private async Task HoldSessionAsync(SharedClient client, CancellationToken cancellationToken)
   {
      var stream = await client.Client.OpenAgentSessionAsync(_version, cancellationToken) switch
      {
         WireAnswer<AgentSessionStream>.Output opened => opened.Value,
         WireAnswer<AgentSessionStream>.Refused { Error.Code: var code } exiting when code == OperationErrorCode.ServerExiting => throw new ServerExitingException(exiting.Error),
         WireAnswer<AgentSessionStream>.Refused refused => throw new ServerRefusedException(refused.Error),
         WireAnswer<AgentSessionStream>.CredentialsRefused => throw new ServerUnavailableException(NoServerMessage),
         WireAnswer<AgentSessionStream>.Unexpected unexpected => throw new ServerUnavailableException(OpenFailedMessage(unexpected.Description, _command)),
      };

      using (stream)
      using (var drop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
      {
         var session = new OpenSession(client, stream.SessionId, stream.Credential);
         Settle(outcome => outcome.TrySetResult(session), session, drop);
         try
         {
            while (await stream.NextAsync(drop.Token) is { } sessionEvent)
            {
               await ForwardAsync(sessionEvent, drop.Token);
            }
         }
         catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
         {
         }
         finally
         {
            lock (_lock)
            {
               if (_open == session)
               {
                  _open = null;
               }

               _dropOpen = null;
            }
         }
      }
   }

   private async Task ForwardAsync(SessionEvent sessionEvent, CancellationToken cancellationToken)
   {
      try
      {
         await _forward(sessionEvent, cancellationToken);
      }
      catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
      {
         // One event that cannot reach Claude must not end the session, which would release its tours.
         _logger.LogWarning(exception, "The {Type} event of the session could not be forwarded.", sessionEvent.Type);
      }
   }

   // Completes the outcome that callers wait for, and starts a new one for the next attempt.
   private void Settle(Action<TaskCompletionSource<OpenSession>> complete, OpenSession? open = null, CancellationTokenSource? drop = null)
   {
      TaskCompletionSource<OpenSession> settled;
      lock (_lock)
      {
         settled = _next;
         _next = NewOutcome();
         _open = open;
         _dropOpen = drop;
         if (open is not null)
         {
            _startUnasked = false;
            _startAsked = false;
         }
      }

      complete(settled);
   }

   private async Task WaitToRetryAsync(CancellationToken cancellationToken)
   {
      using var interval = new CancellationTokenSource(RetryInterval, _time);
      using var delay = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, interval.Token);
      try
      {
         await _wake.Reader.WaitToReadAsync(delay.Token);
         _wake.Reader.TryRead(out _);
      }
      catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
      {
      }
   }

   // The adapter's open agent session: the client of the server it is open on, and the session's ID and credential.
   private sealed record OpenSession(SharedClient Client, string Id, string Credential)
   {
      public override string ToString() => $"OpenSession {{ Id = {Id}, Credential = redacted }}";
   }

   // A client that RunAsync retires and the calls lease. Only a lease taken while the client's session is open counts, so a lease never meets a disposed client.
   private sealed class SharedClient
   {
      private readonly Lock _lock = new();
      private int _leases;
      private bool _retired;

      public SharedClient(ServerClient client)
      {
         Client = client;
      }

      public ServerClient Client { get; }

      public void Lease()
      {
         lock (_lock)
         {
            _leases++;
         }
      }

      public void Release()
      {
         bool dispose;
         lock (_lock)
         {
            _leases--;
            dispose = _retired && _leases == 0;
         }

         if (dispose)
         {
            Client.Dispose();
         }
      }

      public void Retire()
      {
         bool dispose;
         lock (_lock)
         {
            dispose = !_retired && _leases == 0;
            _retired = true;
         }

         if (dispose)
         {
            Client.Dispose();
         }
      }
   }

   // The server refused the session because it decided its idle exit, so a new server can replace it once it has exited.
   private sealed class ServerExitingException : ServerRefusedException
   {
      public ServerExitingException(OperationError error)
         : base(error)
      {
      }
   }
}

/// <summary>What a tool call came to.</summary>
internal closed record ToolOutcome
{
   private ToolOutcome()
   {
   }

   /// <summary>The operation's output.</summary>
   /// <param name="Json">The output as JSON text.</param>
   internal sealed record Output(string Json) : ToolOutcome;

   /// <summary>The server's own refusal of the operation or of the session, whose reason names the fix.</summary>
   internal sealed record Refused(OperationError Error) : ToolOutcome;

   /// <summary>A call that no lotsen server answered, because none could be found, started or reached, the server did not answer in time, or the answer was not lotsen's.</summary>
   /// <param name="Reason">What happened and what the user must do.</param>
   internal sealed record Unavailable(string Reason) : ToolOutcome;
}

/// <summary>An error that keeps the adapter from reaching the server. Its message tells the user what to do.</summary>
internal class ServerUnavailableException : Exception
{
   public ServerUnavailableException(string message)
      : base(message)
   {
   }
}

/// <summary>The server's own refusal of the adapter's session, such as the one of another build.</summary>
internal class ServerRefusedException : ServerUnavailableException
{
   public ServerRefusedException(OperationError error)
      : base(error.Reason)
   {
      Error = error;
   }

   public OperationError Error { get; }
}
