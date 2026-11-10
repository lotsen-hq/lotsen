using System.Text.Json.Serialization;
using System.Threading.Channels;
using Lotsen.Json;
using Lotsen.Tours;

namespace Lotsen.Sessions;

internal sealed partial class AgentSessions
{
   // The ledger's state, which the registry's lock guards. A binding whose session ended stays until the ledger settles it.
   private readonly Dictionary<TourId, AgentSession> _bindings = [];
   private readonly Queue<AgentSession> _ends = new();

   // The ending thread writes, and no waiter runs on it, because a channel runs continuations asynchronously by default.
   private readonly Channel<bool> _endSignals = Channel.CreateUnbounded<bool>(new UnboundedChannelOptions { SingleReader = true });
   private bool _ledgerClaimed;

   /// <summary>Returns the registry's one ledger, for the one owner that settles session ends: the tour writer.</summary>
   /// <exception cref="InvalidOperationException">The ledger was claimed before.</exception>
   public Ledger ClaimLedger() => new(this);

   /// <summary>
   /// Holds which session holds which tour, each binding's attachment level, and the ends of sessions that the ledger's owner has not settled yet.
   /// </summary>
   /// <remarks>
   /// <para>
   /// A tour has at most one binding. A binding whose session ended reads as none at once, in <see cref="Holder"/>, <see cref="BindingOf"/> and <see cref="Snapshot"/>. It is settled exactly once:
   /// by the bind that replaces it, whose <see cref="Released"/> has <see cref="Released.FromEnded"/>, or else by <see cref="TakeEnd"/>.
   /// </para>
   /// <para>
   /// The ledger is nested in the registry, so it alone shares the registry's lock. Locks nest in one order: the tour writer's lock, then the registry's, then the session's own. An end takes only
   /// the registry's lock, so a delivery under the writer's lock may end a session. Only the one owner holds the ledger, so only that owner binds, confirms and delivers session events.
   /// </para>
   /// </remarks>
   internal sealed class Ledger
   {
      private readonly AgentSessions _owner;

      /// <summary>Claims the registry's ledger. Use <see cref="ClaimLedger"/>.</summary>
      /// <exception cref="InvalidOperationException">The ledger was claimed before.</exception>
      internal Ledger(AgentSessions owner)
      {
         lock (owner._lock)
         {
            if (owner._ledgerClaimed)
            {
               throw new InvalidOperationException("The ledger of the agent sessions was claimed before.");
            }

            owner._ledgerClaimed = true;
         }

         _owner = owner;
      }

      /// <summary>Returns the session that holds the tour, or null when none does.</summary>
      public Recipient? Holder(TourId tour)
      {
         lock (_owner._lock)
         {
            return Live(tour)?.Recipient;
         }
      }

      /// <summary>Returns the tour's binding as events and outputs carry it, or null when no session holds the tour.</summary>
      public TourBinding? BindingOf(TourId tour)
      {
         lock (_owner._lock)
         {
            return Live(tour) is { } session ? Binding(session) : null;
         }
      }

      /// <summary>Returns the binding of every tour that a session holds.</summary>
      public IReadOnlyDictionary<TourId, TourBinding> Snapshot()
      {
         lock (_owner._lock)
         {
            return _owner._bindings.Where(binding => !binding.Value.IsEnded).ToDictionary(binding => binding.Key, binding => Binding(binding.Value));
         }
      }

      /// <summary>Binds the caller to the tour, or moves the binding to the caller when it may.</summary>
      /// <param name="tour">The tour to attach.</param>
      /// <param name="caller">The session that attaches it.</param>
      /// <param name="takeOver">Whether the caller may take the tour from a session that holds it.</param>
      /// <returns>
      /// What the attach did. A binding whose session ended moves without <paramref name="takeOver"/>, and <see cref="AttachOutcome.Bound.Previous"/> then says so with
      /// <see cref="Released.FromEnded"/>.
      /// </returns>
      public AttachOutcome Attach(TourId tour, Recipient caller, bool takeOver)
      {
         var session = caller.SessionFor(this);
         lock (_owner._lock)
         {
            if (session.IsEnded)
            {
               return new AttachOutcome.SessionEnded();
            }

            var current = _owner._bindings.GetValueOrDefault(tour);
            if (current == session)
            {
               return new AttachOutcome.Unchanged(Binding(session));
            }

            if (current is { IsEnded: false } && !takeOver)
            {
               return new AttachOutcome.HeldByAnother();
            }

            _owner._bindings[tour] = session;
            return new AttachOutcome.Bound(Binding(session), current is null ? null : new Released(tour, current.Recipient, current.IsEnded));
         }
      }

      /// <summary>Binds the creator to the new tour, which no session holds.</summary>
      /// <returns>The binding, or null when the creator has ended. Nothing was bound then.</returns>
      /// <exception cref="InvalidOperationException">A session holds the tour already.</exception>
      public TourBinding? BindNew(TourId tour, Recipient creator)
      {
         var session = creator.SessionFor(this);
         lock (_owner._lock)
         {
            if (session.IsEnded)
            {
               return null;
            }

            if (!_owner._bindings.TryAdd(tour, session))
            {
               throw new InvalidOperationException($"Tour {tour} is new, but a session holds it already.");
            }

            return Binding(session);
         }
      }

      /// <summary>Confirms the caller when the nonce is its nonce, and returns the bindings that the confirmation changed.</summary>
      /// <returns>What the confirmation did. Only the first confirmation of a session changes a binding.</returns>
      public AckOutcome Confirm(Recipient caller, string nonce)
      {
         var session = caller.SessionFor(this);
         lock (_owner._lock)
         {
            if (session.IsEnded)
            {
               return new AckOutcome.SessionEnded();
            }

            return session.Confirm(this, nonce) switch
            {
               null => new AckOutcome.WrongNonce(),
               true => new AckOutcome.Confirmed(session.Id, [.. _owner._bindings.Where(binding => binding.Value == session).Select(binding => new BindingChange(binding.Key, Binding(session)))]),
               false => new AckOutcome.Confirmed(session.Id, []),
            };
         }
      }

      /// <summary>Removes the tour's binding, as when the tour is deleted, without settling it.</summary>
      public void Forget(TourId tour)
      {
         lock (_owner._lock)
         {
            _owner._bindings.Remove(tour);
         }
      }

      /// <summary>Removes the next unsettled end and the bindings that its session still holds, which the caller settles.</summary>
      /// <returns>The end with the tours it released, or null when no end waits.</returns>
      public SessionEnd? TakeEnd()
      {
         lock (_owner._lock)
         {
            if (!_owner._ends.TryDequeue(out var session))
            {
               return null;
            }

            List<TourId> released = [.. _owner._bindings.Where(binding => binding.Value == session).Select(binding => binding.Key)];
            foreach (var tour in released)
            {
               _owner._bindings.Remove(tour);
            }

            return new SessionEnd(session.Id, released);
         }
      }

      /// <summary>Waits until an end may wait for <see cref="TakeEnd"/>.</summary>
      /// <returns>True when an end may wait, or false once the registry is disposed.</returns>
      /// <exception cref="OperationCanceledException">The token was canceled.</exception>
      public async ValueTask<bool> WaitForEndAsync(CancellationToken cancellationToken)
      {
         var open = await _owner._endSignals.Reader.WaitToReadAsync(cancellationToken);
         while (_owner._endSignals.Reader.TryRead(out _))
         {
         }

         return open;
      }

      /// <summary>Queues the event for its session. A full queue ends the session, which records its end and calls no other code.</summary>
      /// <returns>Whether the event was queued. A probe is not queued for a confirmed session or one whose nonce is out, and an ended session queues nothing.</returns>
      public bool Deliver(SessionSend send)
      {
         var session = send.To.SessionFor(this);
         return send switch
         {
            SessionSend.Question question => session.TryQueueWithNonce(this, nonce => SessionEvents.ForQuestion(question.Event with { Nonce = nonce }), probe: false),
            SessionSend.Answers answers => session.TryQueueWithNonce(this, nonce => SessionEvents.ForAnswers(answers.Event with { Nonce = nonce }), probe: false),
            SessionSend.Probe => session.TryQueueWithNonce(this, nonce => SessionEvents.ForProbe(new ProbeEvent(nonce!)), probe: true),
            SessionSend.TakenOver takenOver => session.TryQueue(this, SessionEvents.ForTourTakenOver(takenOver.TourId)),
            SessionSend.ThreadDeleted deleted => session.TryQueue(this, SessionEvents.ForThreadDeleted(deleted.Event)),
         };
      }

      // Call these two under the registry's lock.
      private AgentSession? Live(TourId tour) => _owner._bindings.GetValueOrDefault(tour) is { IsEnded: false } session ? session : null;

      private TourBinding Binding(AgentSession session) => new(session.Id, session.IsConfirmed(this) ? AttachmentLevel.Confirmed : AttachmentLevel.Unconfirmed);
   }
}

/// <summary>The agent session bound to a tour: the one session that writes the tour's plan and stops.</summary>
/// <param name="SessionId">The session's ID.</param>
/// <param name="Level">Whether the session can receive the reviewer's questions, as far as lotsen knows.</param>
internal sealed record TourBinding(string SessionId, AttachmentLevel Level);

/// <summary>How far lotsen knows that a bound session receives the reviewer's questions, which Claude Code delivers as channel notifications.</summary>
/// <remarks>Nothing tells whether a session receives them before its <c>ack</c>, and a session stays unconfirmed until it acks, however long that takes.</remarks>
[JsonConverter(typeof(StrictEnumConverter<AttachmentLevel>))]
internal enum AttachmentLevel
{
   /// <summary>The session has not passed <c>ack</c> its nonce yet.</summary>
   Unconfirmed,

   /// <summary>The session passed <c>ack</c> its nonce, so a channel notification reached Claude.</summary>
   Confirmed,
}

/// <summary>What <see cref="AgentSessions.Ledger.Attach"/> did.</summary>
internal closed record AttachOutcome
{
   /// <summary>The caller has ended, so nothing was bound.</summary>
   internal sealed record SessionEnded : AttachOutcome;

   /// <summary>Another session holds the tour and the caller asked for no take-over, so nothing was bound.</summary>
   internal sealed record HeldByAnother : AttachOutcome;

   /// <summary>The caller holds the tour already.</summary>
   /// <param name="Binding">The caller's binding.</param>
   internal sealed record Unchanged(TourBinding Binding) : AttachOutcome;

   /// <summary>The caller holds the tour now.</summary>
   /// <param name="Binding">The caller's new binding.</param>
   /// <param name="Previous">The binding that the caller's replaced, or null when no session held the tour.</param>
   internal sealed record Bound(TourBinding Binding, Released? Previous) : AttachOutcome;
}

/// <summary>A binding that a bind replaced, which the binder settles.</summary>
/// <param name="Tour">The tour.</param>
/// <param name="From">The session that held the tour.</param>
/// <param name="FromEnded">Whether that session had ended, so it gets no <see cref="SessionEvents.TourTakenOver"/>.</param>
internal sealed record Released(TourId Tour, Recipient From, bool FromEnded);

/// <summary>A tour's binding after a change.</summary>
/// <param name="Tour">The tour.</param>
/// <param name="Binding">Its binding now.</param>
internal sealed record BindingChange(TourId Tour, TourBinding Binding);

/// <summary>What <see cref="AgentSessions.Ledger.Confirm"/> did.</summary>
internal closed record AckOutcome
{
   /// <summary>The caller has ended, so nothing was confirmed.</summary>
   internal sealed record SessionEnded : AckOutcome;

   /// <summary>The nonce is not the caller's, or the caller has none yet, so nothing was confirmed.</summary>
   internal sealed record WrongNonce : AckOutcome;

   /// <summary>The calling session is confirmed.</summary>
   /// <param name="SessionId">The caller's ID.</param>
   /// <param name="Changed">The bindings whose level the confirmation changed, empty when the caller was confirmed before.</param>
   internal sealed record Confirmed(string SessionId, IReadOnlyList<BindingChange> Changed) : AckOutcome;
}

/// <summary>A session's end, as <see cref="AgentSessions.Ledger.TakeEnd"/> returns it to be settled.</summary>
/// <param name="SessionId">The session's ID.</param>
/// <param name="Released">The tours that the session still held, whose bindings the end released.</param>
internal sealed record SessionEnd(string SessionId, IReadOnlyList<TourId> Released);
