using System.Text.Json.Serialization;
using Lotsen.Json;

namespace Lotsen.Tours;

/// <summary>What the server derives about a thread's questions, which the UI shows and never derives itself: the question that waits, and the outcome of each question.</summary>
/// <param name="Waiting">The thread's question that waits for a reply, with its pending request, or null when none waits. JSON leaves it out when null.</param>
/// <param name="Questions">The outcome of each of the thread's questions, in thread order, empty for a note and for a started thread without a follow-up.</param>
internal sealed record ThreadStatus(
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WaitingQuestion? Waiting,
   IReadOnlyList<QuestionStatus> Questions)
{
   /// <summary>Returns the status of the thread.</summary>
   public static ThreadStatus Of(QuestionThread thread)
   {
      if (thread is not QuestionThread.Sent sent)
      {
         return new ThreadStatus(null, []);
      }

      var waiting = sent.Waiting is { } question ? new WaitingQuestion(question.Id, question.Requests[^1].Id, question.Requests[^1].SessionId) : null;
      return new ThreadStatus(waiting, [.. sent.Messages.OfType<ThreadMessage.Question>().Select(each => new QuestionStatus(each.Id, OutcomeOf(each)))]);
   }

   /// <summary>Returns the outcome of the question: answered once a reply resolved one of its requests, and otherwise the state of its last request.</summary>
   public static QuestionOutcome OutcomeOf(ThreadMessage.Question question) => question.Answered is not null
      ? QuestionOutcome.Answered
      : question.Requests[^1].State switch
      {
         RequestState.Pending => QuestionOutcome.Waiting,
         RequestState.Unanswered => QuestionOutcome.Unanswered,
         _ => QuestionOutcome.Canceled,
      };
}

/// <summary>Where one question of a thread stands.</summary>
/// <param name="QuestionId">The question's ID.</param>
/// <param name="Outcome">The question's outcome.</param>
internal sealed record QuestionStatus(string QuestionId, QuestionOutcome Outcome);

/// <summary>The question of a thread that waits for a reply, with the pending request that the reviewer can cancel or send again.</summary>
/// <param name="QuestionId">The question's ID.</param>
/// <param name="RequestId">The ID of the question's pending request.</param>
/// <param name="SessionId">The agent session that the pending request went to.</param>
internal sealed record WaitingQuestion(string QuestionId, string RequestId, string SessionId);

/// <summary>Where a question stands, as <see cref="ThreadStatus.OutcomeOf"/> derives it from its requests.</summary>
[JsonConverter(typeof(StrictEnumConverter<QuestionOutcome>))]
internal enum QuestionOutcome
{
   /// <summary>A question whose last request is pending and that no reply answered.</summary>
   Waiting,

   /// <summary>A question that a reply answered, whatever its later requests' states.</summary>
   Answered,

   /// <summary>A question that no reply answered, whose last request was canceled.</summary>
   Canceled,

   /// <summary>A question that no reply answered, whose last request went unanswered.</summary>
   Unanswered,
}
