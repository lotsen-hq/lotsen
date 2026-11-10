using System.ComponentModel;
using System.Text.Json.Serialization;
using Lotsen.Pinning;
using Lotsen.Sessions;
using Lotsen.Tours;

namespace Lotsen.Operations;

/// <summary>The input of <c>attach_tour</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AttachTourInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId,
   [property: Description("true to take the tour over from the session it is attached to. Pass it only after the user confirmed the take-over. Omit it otherwise.")]
   bool TakeOver = false);

/// <summary>The input of <c>ack</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AckInput(
   [property: Description("The nonce attribute of the channel event, exactly as the event gave it.")]
   string Nonce);

/// <summary>The output of <c>ack</c>.</summary>
/// <param name="SessionId">The ID of the session that the nonce confirmed.</param>
/// <param name="Level">The session's attachment level, which is confirmed.</param>
internal sealed record AckResult(string SessionId, AttachmentLevel Level);

/// <summary>
/// A session's attachment of tours: <c>attach_tour</c>, which binds the calling session to a tour, and <c>ack</c>, with which a session confirms that its channel notifications reach Claude.
/// </summary>
/// <remarks>
/// The session ledger holds the bindings, the nonce and the confirmation, as <see cref="AgentSessions.Ledger"/> describes. Each operation runs in one body of the writer's Exclusive, so a level
/// changes between two binding events and never during one, and the UI never sees an ack's event before the event of the binding that it changes.
/// </remarks>
internal sealed class SessionOperations
{
   private readonly TourWriter _writer;
   private readonly IterationScheduler _scheduler;

   public SessionOperations(TourWriter writer, IterationScheduler scheduler)
   {
      _writer = writer;
      _scheduler = scheduler;
   }

   /// <summary>Binds the calling session to the tour and returns the tour, or moves the binding from another session when the input asks for a take-over.</summary>
   /// <remarks>
   /// A binding whose session ended moves without a take-over. The tour's pending requests become unanswered whenever the binding moves, and a session that loses the tour to a take-over receives
   /// <see cref="SessionEvents.TourTakenOver"/>. The calling session receives <see cref="SessionEvents.Probe"/> unless it is confirmed or holds its nonce already. A pinned tour gets a
   /// nudge of its iteration check, which the attach does not wait for.
   /// </remarks>
   public OperationResult<Tour> AttachTour(Access.AnySession.Pass pass, AttachTourInput input)
   {
      var session = pass.Recipient;
      var attached = _writer.Exclusive<OperationResult<Tour>>(pass, locked =>
      {
         var read = locked.Read(input.TourId);
         if (read.Value is not { } tour)
         {
            return read;
         }

         switch (locked.Attach(tour.Id, session, input.TakeOver))
         {
            case AttachOutcome.SessionEnded:
               return OperationError.CallerNotAllowed("this agent session has ended. Call the tool again, and the adapter opens a new session.", "tourId");
            case AttachOutcome.HeldByAnother:
               return OperationError.AttachedToAnotherSession(tour.Id);
            case AttachOutcome.Bound bound:
               locked.PublishBinding(tour.Id, bound.Binding);
               if (bound.Previous is { } previous)
               {
                  tour = locked.MarkPendingUnanswered(tour.Id) ?? tour;
                  if (!previous.FromEnded)
                  {
                     locked.Send(previous.From.TakenOver(tour.Id));
                  }
               }

               break;
         }

         locked.Send(session.Probe(tour.Id));
         return AgentView.Of(tour);
      });
      if (attached.Value is { Pin: TourPin.Pinned })
      {
         _scheduler.Nudge(input.TourId, Nudge.SessionAttached);
      }

      return attached;
   }

   /// <summary>Confirms the calling session when the nonce is the session's nonce, and returns the session's level.</summary>
   /// <remarks>Each tour bound to the session gets a <see cref="ServerEvents.TourBindingChanged"/> event with the confirmed level, on the first ack alone.</remarks>
   public OperationResult<AckResult> Ack(Access.AnySession.Pass pass, AckInput input) =>
      _writer.Exclusive<OperationResult<AckResult>>(pass, locked => locked.Confirm(pass.Recipient, input.Nonce) switch
      {
         AckOutcome.SessionEnded => OperationError.CallerNotAllowed("this agent session has ended, so it has nothing to confirm. Call the tool again, and the adapter opens a new session.", null),
         AckOutcome.WrongNonce => OperationError.WrongNonce(),
         AckOutcome.Confirmed confirmed => Published(locked, confirmed),
      });

   // Publishes the new level of each binding that the confirmation changed.
   private static AckResult Published(TourWriter.Locked locked, AckOutcome.Confirmed confirmed)
   {
      foreach (var change in confirmed.Changed)
      {
         locked.PublishBinding(change.Tour, change.Binding);
      }

      return new AckResult(confirmed.SessionId, AttachmentLevel.Confirmed);
   }
}
