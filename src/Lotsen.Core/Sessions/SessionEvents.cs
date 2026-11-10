using System.Text.Json;
using System.Text.Json.Serialization;
using Lotsen.Json;
using Lotsen.Server;
using Lotsen.Tours;

namespace Lotsen.Sessions;

/// <summary>One session event: its type name and its payload as JSON text.</summary>
internal sealed record SessionEvent(string Type, string Data) : IStreamEvent;

/// <summary>The payload of a <see cref="SessionEvents.TourTakenOver"/> event.</summary>
/// <param name="TourId">The tour that another session took over.</param>
internal sealed record TourTakenOverEvent(TourId TourId);

/// <summary>The payload of a <see cref="SessionEvents.Probe"/> event.</summary>
/// <param name="Nonce">The session's nonce, which Claude passes to <c>ack</c>.</param>
internal sealed record ProbeEvent(string Nonce);

/// <summary>The payload of a <see cref="SessionEvents.Question"/> event: the reviewer's words and the IDs that <c>fetch_question</c> needs, and nothing read from the tour or its code.</summary>
/// <param name="TourId">The tour that the question is about.</param>
/// <param name="ThreadId">The ID of the question's thread, which holds the earlier messages when the question is a follow-up.</param>
/// <param name="QuestionId">The question's ID.</param>
/// <param name="RequestId">The ID of this sending of the question.</param>
/// <param name="Text">The reviewer's words, or empty for a tour action or a reviewer decision without words.</param>
/// <param name="StopId">The stop that the reviewer viewed while asking, or null for none. JSON leaves it out when null.</param>
/// <param name="Nonce">
/// The session's nonce, which Claude passes to <c>ack</c>, while the session is unconfirmed, as <see cref="AgentSessions.Ledger.Deliver"/> sets it. Null for a confirmed session, and JSON then
/// leaves it out.
/// </param>
/// <param name="Action">The kind of the tour action that the question requests, as <see cref="TourAction.Kind"/> names it, or null for any other question. JSON leaves it out when null.</param>
/// <param name="Decision">
/// The kind of the reviewer decision that the question reports, as <see cref="ReviewerDecision.Kind"/> names it, or null for any other question. JSON leaves it out when null. A question names an
/// action or a decision, never both, because only the first question of an asked or a decided thread holds one.
/// </param>
internal sealed record QuestionEvent(
   TourId TourId,
   string ThreadId,
   string QuestionId,
   string RequestId,
   string Text,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StopId = null,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Nonce = null,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Action = null,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Decision = null);

/// <summary>The payload of a <see cref="SessionEvents.Answers"/> event: the IDs that <c>fetch_answers</c> needs, and none of the reviewer's words.</summary>
/// <param name="TourId">The tour whose checks the answers answer.</param>
/// <param name="BatchId">The batch's ID.</param>
/// <param name="Nonce">The session's nonce while the session is unconfirmed, as for a <see cref="QuestionEvent"/>. Null for a confirmed session, and JSON then leaves it out.</param>
internal sealed record AnswersEvent(TourId TourId, string BatchId, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Nonce = null);

/// <summary>The payload of a <see cref="SessionEvents.ThreadDeleted"/> event: the IDs of the deleted thread and of its canceled question, and none of the thread's words.</summary>
/// <param name="TourId">The thread's tour.</param>
/// <param name="ThreadId">The deleted thread's ID.</param>
/// <param name="QuestionId">The question of the thread that waited for its reply until the delete canceled it, or null when none waited. JSON leaves it out when null.</param>
internal sealed record ThreadDeletedEvent(TourId TourId, string ThreadId, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? QuestionId = null);

/// <summary>The event types that the server sends one agent session on its stream, and how each payload is built. The adapter turns each into a message for Claude.</summary>
internal static class SessionEvents
{
   /// <summary>Another session took over a tour that this session was bound to. The payload is a <see cref="TourTakenOverEvent"/>.</summary>
   public const string TourTakenOver = "tourTakenOver";

   /// <summary>The reviewer asked a question about a tour that this session is bound to, requested a tour action or made a decision. The payload is a <see cref="QuestionEvent"/>.</summary>
   public const string Question = "question";

   /// <summary>A check that the session's channel notifications reach Claude, which an unconfirmed session gets when it attaches a tour. The payload is a <see cref="ProbeEvent"/>.</summary>
   public const string Probe = "probe";

   /// <summary>The reviewer sent a batch of answers to checks of a tour that this session is bound to. The payload is an <see cref="AnswersEvent"/>.</summary>
   public const string Answers = "answers";

   /// <summary>The reviewer deleted a thread of a tour that this session is bound to. The payload is a <see cref="ThreadDeletedEvent"/>.</summary>
   public const string ThreadDeleted = "threadDeleted";

   public static SessionEvent ForTourTakenOver(TourId tourId) => new(TourTakenOver, JsonSerializer.Serialize(new TourTakenOverEvent(tourId), LotsenJsonContext.Default.TourTakenOverEvent));

   public static SessionEvent ForQuestion(QuestionEvent question) => new(Question, JsonSerializer.Serialize(question, LotsenJsonContext.Default.QuestionEvent));

   public static SessionEvent ForAnswers(AnswersEvent answers) => new(Answers, JsonSerializer.Serialize(answers, LotsenJsonContext.Default.AnswersEvent));

   public static SessionEvent ForThreadDeleted(ThreadDeletedEvent deleted) => new(ThreadDeleted, JsonSerializer.Serialize(deleted, LotsenJsonContext.Default.ThreadDeletedEvent));

   public static SessionEvent ForProbe(ProbeEvent probe) => new(Probe, JsonSerializer.Serialize(probe, LotsenJsonContext.Default.ProbeEvent));
}
