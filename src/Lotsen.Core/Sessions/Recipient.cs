using Lotsen.Tours;

namespace Lotsen.Sessions;

/// <summary>One agent session as the receiver of session events: its ID, and the session events it can receive as values.</summary>
/// <remarks>
/// A recipient has no method that sends. Only the ledger's <see cref="AgentSessions.Ledger.Deliver"/> delivers a <see cref="SessionSend"/>, so only the tour writer, which holds the ledger, sends.
/// Each <see cref="AgentSession"/> has one recipient, so two recipients are equal only when they are the same object.
/// </remarks>
internal sealed class Recipient
{
   private readonly AgentSession _session;

   internal Recipient(AgentSession session)
   {
      _session = session;
   }

   /// <summary>The session's ID.</summary>
   public string Id => _session.Id;

   /// <summary>Returns the question event for the thread's last question and that question's last request.</summary>
   /// <remarks>
   /// The event names the thread's tour action or reviewer decision only for the thread's first question, which holds it. A follow-up is a question about the action or the decision.
   /// </remarks>
   /// <exception cref="ArgumentException">The thread has no question.</exception>
   public SessionSend Question(TourId tourId, QuestionThread.Sent thread)
   {
      var question = thread.LastQuestion ?? throw new ArgumentException($"Thread {thread.Id} has no question to send.", nameof(thread));
      var opens = thread.Messages[0].Id == question.Id;
      var (action, decision) = thread switch
      {
         QuestionThread.Sent.Asked asked when opens => (asked.Action?.Kind, null),
         QuestionThread.Sent.Decided decided when opens => ((string?)null, decided.Decision.Kind),
         _ => (null, null),
      };
      return new SessionSend.Question(this, new QuestionEvent(tourId, thread.Id, question.Id, question.Requests[^1].Id, question.Text, question.StopId, Action: action, Decision: decision));
   }

   /// <summary>Returns the event that tells the session of a batch of answers.</summary>
   public SessionSend Answers(TourId tourId, string batchId) => new SessionSend.Answers(this, new AnswersEvent(tourId, batchId));

   /// <summary>Returns the probe that the session gets when it binds the tour.</summary>
   public SessionSend Probe(TourId tourId) => new SessionSend.Probe(this, tourId);

   /// <summary>Returns the event that tells the session that the reviewer deleted a thread.</summary>
   /// <param name="tourId">The thread's tour.</param>
   /// <param name="threadId">The deleted thread's ID.</param>
   /// <param name="questionId">The question that waited in the thread until the delete canceled it, or null when none waited.</param>
   public SessionSend ThreadDeleted(TourId tourId, string threadId, string? questionId) => new SessionSend.ThreadDeleted(this, new ThreadDeletedEvent(tourId, threadId, questionId));

   /// <summary>Returns the event that tells the session that another session took the tour over.</summary>
   public SessionSend TakenOver(TourId tourId) => new SessionSend.TakenOver(this, tourId);

   public override string ToString() => _session.ToString();

   /// <summary>Returns the session, for the ledger that binds and delivers to it.</summary>
   internal AgentSession SessionFor(AgentSessions.Ledger ledger)
   {
      ArgumentNullException.ThrowIfNull(ledger);
      return _session;
   }
}

/// <summary>A session event for one recipient, as a value that <see cref="AgentSessions.Ledger.Deliver"/> delivers.</summary>
internal closed record SessionSend
{
   private SessionSend(Recipient to)
   {
      To = to;
   }

   /// <summary>The session that the event goes to.</summary>
   public Recipient To { get; }

   /// <summary>A question, with the session's nonce while it is unconfirmed.</summary>
   public sealed record Question : SessionSend
   {
      public Question(Recipient to, QuestionEvent questionEvent)
         : base(to)
      {
         Event = questionEvent;
      }

      public QuestionEvent Event { get; }
   }

   /// <summary>A batch of answers, with the session's nonce while it is unconfirmed.</summary>
   public sealed record Answers : SessionSend
   {
      public Answers(Recipient to, AnswersEvent answersEvent)
         : base(to)
      {
         Event = answersEvent;
      }

      public AnswersEvent Event { get; }
   }

   /// <summary>A probe, which the session gets only while it is unconfirmed and holds no nonce.</summary>
   public sealed record Probe : SessionSend
   {
      public Probe(Recipient to, TourId tourId)
         : base(to)
      {
         TourId = tourId;
      }

      public TourId TourId { get; }
   }

   /// <summary>The news that the reviewer deleted a thread.</summary>
   public sealed record ThreadDeleted : SessionSend
   {
      public ThreadDeleted(Recipient to, ThreadDeletedEvent deletedEvent)
         : base(to)
      {
         Event = deletedEvent;
      }

      public ThreadDeletedEvent Event { get; }
   }

   /// <summary>The news that another session took the tour over.</summary>
   public sealed record TakenOver : SessionSend
   {
      public TakenOver(Recipient to, TourId tourId)
         : base(to)
      {
         TourId = tourId;
      }

      public TourId TourId { get; }
   }
}
