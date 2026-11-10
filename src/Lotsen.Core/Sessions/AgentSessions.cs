using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Lotsen.Hosting;
using Lotsen.Server;

namespace Lotsen.Sessions;

/// <summary>
/// Registers the agent sessions that are open now, one for each adapter connection, and finds a session by its ID or by its session credential.
/// </summary>
/// <remarks>
/// <para>
/// The registry's lock guards which sessions are open, the idle exit, and the ledger's bindings and unsettled ends, as <see cref="Ledger"/> describes. A session leaves the registry in the same hold
/// that records its end, so no request authenticates as a session that has ended.
/// </para>
/// <para>
/// A background server's open sessions are its clients. See <see cref="ExitWhenIdle"/> for how it exits once none remains.
/// </para>
/// </remarks>
internal sealed partial class AgentSessions : IDisposable
{
   /// <summary>The request header that carries an agent session's credential, beside the server token.</summary>
   public const string CredentialHeader = "Lotsen-Session";

   /// <summary>The type of the first event on a session's stream, whose payload is an <see cref="AgentSessionOpened"/>.</summary>
   public const string OpenedEvent = "agentSession";

   /// <summary>How many events a session may fall behind before the server ends it.</summary>
   public const int EventCapacity = 256;

   private readonly Lock _lock = new();
   private readonly Dictionary<string, AgentSession> _open = new(StringComparer.Ordinal);
   private IdleExit? _idleExit;
   private ITimer? _idleTimer;

   // Each open and each arm of the timer moves it on, so a timer callback that a later open or drop replaced finds another value.
   private long _idleGeneration;
   private bool _exiting;
   private bool _shuttingDown;

   /// <summary>The sessions that are open now, in no particular order.</summary>
   public IReadOnlyList<AgentSession> Current
   {
      get
      {
         lock (_lock)
         {
            return [.. _open.Values];
         }
      }
   }

   /// <summary>Registers a new session with a new ID and session credential.</summary>
   /// <returns>The session, or null once the idle exit has decided that the server exits.</returns>
   public AgentSession? TryOpen()
   {
      var session = new AgentSession(this, Secrets.NewRandomValue(9), Secrets.NewRandomValue(32));
      lock (_lock)
      {
         if (_exiting)
         {
            return null;
         }

         _open.Add(session.Id, session);
         DisarmIdleTimer();
      }

      return session;
   }

   /// <summary>Calls <paramref name="exit"/> once no session has been open for the grace period, counted from this call or from the drop of the last session.</summary>
   /// <remarks>
   /// The decision takes the lock that <see cref="TryOpen"/> takes and checks again that no session is open. From then on, <see cref="TryOpen"/> refuses every session, so no session opens on
   /// a server that is about to stop. A session that opens before the decision cancels it.
   /// </remarks>
   /// <param name="gracePeriod">How long the registry waits with no session open.</param>
   /// <param name="timeProvider">The clock whose timer counts the grace period.</param>
   /// <param name="exit">Stops the server. The registry calls it once, outside its lock.</param>
   /// <exception cref="InvalidOperationException">The idle exit was started before.</exception>
   public void ExitWhenIdle(TimeSpan gracePeriod, TimeProvider timeProvider, Action exit)
   {
      lock (_lock)
      {
         if (_idleExit is not null)
         {
            throw new InvalidOperationException("The idle exit was started before.");
         }

         _idleExit = new IdleExit(gracePeriod, timeProvider, exit);
         if (_open.Count == 0)
         {
            ArmIdleTimer();
         }
      }
   }

   /// <summary>Decides the idle exit at once when no session is open, without waiting for the grace period.</summary>
   /// <remarks>It decides under the same lock as the idle exit's timer, so from then on <see cref="TryOpen"/> refuses every session.</remarks>
   /// <returns>
   /// Whether the server exits: true when this call decided the exit, or the server was stopping already. False when a session is open, or the server has no idle exit, as a foreground server.
   /// </returns>
   public bool TryExitNowWhenIdle()
   {
      IdleExit exit;
      lock (_lock)
      {
         if (_exiting || _shuttingDown)
         {
            return true;
         }

         if (_idleExit is null || _open.Count != 0)
         {
            return false;
         }

         _exiting = true;
         DisarmIdleTimer();
         exit = _idleExit;
      }

      exit.Exit();
      return true;
   }

   /// <summary>Stops the idle exit's timer for good, because the host is stopping.</summary>
   /// <remarks>The ends of the sessions that the stop closes are still recorded for the ledger, and none of them arms the timer again.</remarks>
   public void BeginShutdown()
   {
      lock (_lock)
      {
         _shuttingDown = true;
         DisarmIdleTimer();
      }
   }

   /// <summary>Stops the idle exit's timer, refuses every session from now on, and completes the ledger's wait for ends.</summary>
   public void Dispose()
   {
      lock (_lock)
      {
         _exiting = true;
         DisarmIdleTimer();
      }

      _endSignals.Writer.TryComplete();
   }

   /// <summary>Returns the open session with this ID, or null when no open session has it.</summary>
   public AgentSession? Find(string id)
   {
      lock (_lock)
      {
         return _open.GetValueOrDefault(id);
      }
   }

   /// <summary>Returns the open session whose session credential this is, or null when the credential names none, as after the session ended.</summary>
   public AgentSession? Authenticate(string presented)
   {
      var separator = presented.IndexOf(AgentSession.CredentialSeparator, StringComparison.Ordinal);
      return separator > 0 && Find(presented[..separator]) is { } session && session.HasKey(presented.AsSpan(separator + 1)) ? session : null;
   }

   /// <summary>Ends the session in one hold of the lock: marks it ended, removes it, and queues its end for the ledger when it holds a binding.</summary>
   /// <returns>Whether this call ended the session, and whether it queued an end.</returns>
   internal (bool Ended, bool Queued) RecordEnd(AgentSession session)
   {
      lock (_lock)
      {
         if (session.IsEnded)
         {
            return (false, false);
         }

         session.MarkEnded();
         _open.Remove(session.Id);
         if (_open.Count == 0 && _idleExit is not null && !_exiting && !_shuttingDown)
         {
            ArmIdleTimer();
         }

         // A session that holds no binding now never gets one, because the ledger binds no ended session.
         var queued = _bindings.ContainsValue(session);
         if (queued)
         {
            _ends.Enqueue(session);
         }

         return (true, queued);
      }
   }

   /// <summary>Wakes the ledger's wait for ends. Call it outside the lock.</summary>
   internal void SignalEnd() => _endSignals.Writer.TryWrite(true);

   // Call these two under the lock.
   private void ArmIdleTimer()
   {
      DisarmIdleTimer();
      var generation = _idleGeneration;
      _idleTimer = _idleExit!.TimeProvider.CreateTimer(_ => ExitIfStillIdle(generation), state: null, _idleExit.GracePeriod, Timeout.InfiniteTimeSpan);
   }

   private void DisarmIdleTimer()
   {
      _idleGeneration++;
      _idleTimer?.Dispose();
      _idleTimer = null;
   }

   // A disposed timer can still run a callback that its thread took before the disposal, so the generation decides.
   private void ExitIfStillIdle(long generation)
   {
      lock (_lock)
      {
         if (_exiting || _shuttingDown || generation != _idleGeneration || _open.Count != 0)
         {
            return;
         }

         _exiting = true;
         DisarmIdleTimer();
      }

      _idleExit!.Exit();
   }

   private sealed record IdleExit(TimeSpan GracePeriod, TimeProvider TimeProvider, Action Exit);
}

/// <summary>
/// One adapter's agent session: its ID, its queue of events for that adapter, whether it has ended, its nonce and whether it confirmed that nonce.
/// </summary>
/// <remarks>
/// <para>
/// The session lasts while the adapter's connection stays open, so the end of the connection ends it. An end runs no code of another module: <see cref="End"/> records it, and the ledger passes it to
/// the tour writer, which settles it, as <see cref="AgentSessions.Ledger"/> describes.
/// </para>
/// <para>
/// The nonce proves that the session's channel notifications reach Claude. The first probe, question or batch of answers that the session queues issues it, and every later event that carries a
/// nonce carries the same one, until <c>ack</c> confirms the session. A confirmed session gets no probe, and its questions and batches carry no nonce.
/// </para>
/// <para>
/// <see cref="_lock"/> guards the nonce and the confirmation. The session holds it only to queue an event that may carry the nonce, to compare a nonce or to confirm, and never while it ends. It
/// comes last in the lock order that <see cref="AgentSessions.Ledger"/> gives. Only the ledger queues an event or confirms, so only the tour writer does, under its lock.
/// </para>
/// </remarks>
internal sealed class AgentSession
{
   internal const char CredentialSeparator = '.';

   // A nonce is 26 characters of an alphabet of 32, 5 bits each, so 26 * 5 = 130 random bits.
   private const string NonceAlphabet = "abcdefghijklmnopqrstuvwxyz234567";
   private const int NonceLength = 26;

   private readonly Lock _lock = new();
   private readonly AgentSessions _owner;
   private readonly string _key;
   private readonly Channel<SessionEvent> _events = System.Threading.Channels.Channel.CreateBounded<SessionEvent>(
      new BoundedChannelOptions(AgentSessions.EventCapacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

   private readonly TaskCompletionSource _whenEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);

   // Set once, under the registry's lock.
   private volatile bool _ended;

   private string? _nonce;
   private bool _confirmed;

   internal AgentSession(AgentSessions owner, string id, string key)
   {
      _owner = owner;
      Id = id;
      _key = key;
      Recipient = new Recipient(this);
   }

   /// <summary>The session's ID, unpadded base64url text. It is no secret, so events and the UI may name it.</summary>
   public string Id { get; }

   /// <summary>The session as the receiver of session events, which an operation holds without the means to send.</summary>
   public Recipient Recipient { get; }

   /// <summary>The session credential that the adapter sends in <see cref="AgentSessions.CredentialHeader"/>: the ID, a dot and the session's secret key.</summary>
   public string Credential => $"{Id}{CredentialSeparator}{_key}";

   /// <summary>Whether the session has ended.</summary>
   public bool IsEnded => _ended;

   /// <summary>The task that completes once the session's end is recorded, after its queue completed.</summary>
   /// <remarks>The tour writer settles the end later, so a wait for what the end releases calls <c>TourWriter.SettleEnds</c> after this task.</remarks>
   public Task WhenEnded => _whenEnded.Task;

   /// <summary>The events for the session's stream. The reader completes when the session ends.</summary>
   public ChannelReader<SessionEvent> Events => _events.Reader;

   /// <summary>Ends the session: records the end, completes its events and then <see cref="WhenEnded"/>. Ending it again does nothing.</summary>
   /// <remarks>It takes only the registry's lock, never the tour writer's, and runs no code of another module, so a delivery under the writer's lock may end the session.</remarks>
   public void End()
   {
      var (ended, queued) = _owner.RecordEnd(this);
      if (!ended)
      {
         return;
      }

      _events.Writer.TryComplete();
      _whenEnded.TrySetResult();
      if (queued)
      {
         _owner.SignalEnd();
      }
   }

   public override string ToString() => $"agent session {Id}";

   internal bool HasKey(ReadOnlySpan<char> presented) => Secrets.FixedTimeEquals(presented, _key);

   internal void MarkEnded() => _ended = true;

   /// <summary>Returns whether an <c>ack</c> with the session's nonce confirmed the session.</summary>
   internal bool IsConfirmed(AgentSessions.Ledger ledger)
   {
      ArgumentNullException.ThrowIfNull(ledger);
      lock (_lock)
      {
         return _confirmed;
      }
   }

   /// <summary>Confirms the session when the nonce is the session's nonce, compared in time that does not depend on where they differ.</summary>
   /// <returns>Null when the session has no nonce yet or the nonce differs, or else whether the session was unconfirmed before.</returns>
   internal bool? Confirm(AgentSessions.Ledger ledger, string presented)
   {
      ArgumentNullException.ThrowIfNull(ledger);
      lock (_lock)
      {
         if (_nonce is not { } nonce || !Secrets.FixedTimeEquals(presented, nonce))
         {
            return null;
         }

         var first = !_confirmed;
         _confirmed = true;
         return first;
      }
   }

   /// <summary>Queues the event, and ends the session when its queue is full.</summary>
   /// <returns>Whether the event was queued. An ended session queues nothing.</returns>
   internal bool TryQueue(AgentSessions.Ledger ledger, SessionEvent sessionEvent)
   {
      ArgumentNullException.ThrowIfNull(ledger);
      if (_events.Writer.TryWrite(sessionEvent))
      {
         return true;
      }

      End();
      return false;
   }

   /// <summary>
   /// Queues the event that <paramref name="build"/> returns for the nonce it carries, or for null once the session is confirmed, and ends the session when its queue is full. A probe is queued
   /// only while no event has carried the nonce yet.
   /// </summary>
   /// <returns>Whether the event was queued.</returns>
   internal bool TryQueueWithNonce(AgentSessions.Ledger ledger, Func<string?, SessionEvent> build, bool probe)
   {
      ArgumentNullException.ThrowIfNull(ledger);
      bool queued;
      lock (_lock)
      {
         if (probe && (_confirmed || _nonce is not null))
         {
            return false;
         }

         var nonce = _confirmed ? null : _nonce ?? RandomNumberGenerator.GetString(NonceAlphabet, NonceLength);
         queued = _events.Writer.TryWrite(build(nonce));
         if (queued && nonce is not null)
         {
            _nonce = nonce;
         }
      }

      // The end takes the registry's lock, which comes before this one.
      if (!queued)
      {
         End();
      }

      return queued;
   }
}

/// <summary>The body of <c>POST /api/agent-session</c>: the adapter's versions.</summary>
/// <remarks>The versions are top-level properties, as a <see cref="VersionInfo"/> holds them, so a server of another build still reads them and refuses with the restart named.</remarks>
/// <param name="Build">The adapter's build identity.</param>
/// <param name="Protocol">The adapter's protocol version.</param>
internal sealed record AgentSessionRequest(string Build, int Protocol)
{
   [JsonIgnore]
   public VersionInfo Version => new(Build, Protocol);

   public static AgentSessionRequest Of(VersionInfo version) => new(version.Build, version.Protocol);
}

/// <summary>The payload of the first event on a session's stream.</summary>
/// <param name="SessionId">The session's ID.</param>
/// <param name="Credential">The session credential, which every request of the session carries.</param>
internal sealed record AgentSessionOpened(string SessionId, string Credential)
{
   public override string ToString() => $"AgentSessionOpened {{ SessionId = {SessionId}, Credential = redacted }}";
}
