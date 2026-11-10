using System.Diagnostics;
using System.Text.Json.Serialization;
using Lotsen.Json;

namespace Lotsen.Tours;

/// <summary>The reviewer's answer to one check of a stop, which a tour holds at most once for each stop ID and check ID.</summary>
/// <remarks>
/// Only the UI writes an answer, and the reviewer sets its type. An agent session's reads of the tour omit every answer whose state <see cref="ReachesAgents"/> hides from agents. The constructor
/// refuses a state that disagrees with the batch and an outdated version below 1, so every answer, stored or built, has a state that a send and a fetch can read.
/// </remarks>
internal sealed record CheckAnswer
{
   /// <param name="stopId">The ID of the stop whose content holds the check.</param>
   /// <param name="checkId">The <see cref="StopCheck.Id"/> of the check.</param>
   /// <param name="text">The answer as plain text, never rendered as prose.</param>
   /// <param name="type">What kind of answer it is, as the reviewer typed it or took it from a suggestion.</param>
   /// <param name="state">How far the answer has come on its way to Claude.</param>
   /// <param name="savedAt">When the reviewer last changed the text or the type.</param>
   /// <param name="batch">The batch that sent the answer, or null for a draft.</param>
   /// <param name="outdatedOn">The number of the stop's version that the answer stays on, or null while the check is in the stop's current content.</param>
   /// <exception cref="ArgumentException">A draft has a batch, a sent answer's batch is missing or has a received time, a received answer's batch has none, or the outdated version is below 1.</exception>
   public CheckAnswer(string stopId, string checkId, string text, AnswerType type, AnswerState state, DateTimeOffset savedAt, AnswerBatch? batch = null, int? outdatedOn = null)
   {
      var consistent = state switch
      {
         AnswerState.Draft => batch is null,
         AnswerState.Sent => batch is { ReceivedAt: null },
         AnswerState.Received => batch is { ReceivedAt: not null },
         _ => false,
      };
      if (!consistent)
      {
         throw new ArgumentException($"The answer to check '{checkId}' of stop '{stopId}' is {state}, which its batch contradicts.", nameof(batch));
      }

      if (outdatedOn < 1)
      {
         throw new ArgumentException($"The answer to check '{checkId}' of stop '{stopId}' is outdated on version {outdatedOn}, but versions count from 1.", nameof(outdatedOn));
      }

      StopId = stopId;
      CheckId = checkId;
      Text = text;
      Type = type;
      State = state;
      SavedAt = savedAt;
      Batch = batch;
      OutdatedOn = outdatedOn;
   }

   /// <summary>The ID of the stop whose content holds the check.</summary>
   public string StopId { get; }

   /// <summary>The <see cref="StopCheck.Id"/> of the check.</summary>
   public string CheckId { get; }

   /// <summary>The answer as plain text, never rendered as prose.</summary>
   public string Text { get; }

   /// <summary>What kind of answer it is, as the reviewer typed it or took it from a suggestion.</summary>
   public AnswerType Type { get; }

   /// <summary>How far the answer has come on its way to Claude.</summary>
   public AnswerState State { get; }

   /// <summary>When the reviewer last changed the text or the type.</summary>
   public DateTimeOffset SavedAt { get; }

   /// <summary>The batch that sent the answer, or null for a draft. JSON leaves it out when null.</summary>
   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
   public AnswerBatch? Batch { get; }

   /// <summary>
   /// The number of the stop's version that the answer stays on, because the next version dropped its check or gave it a new ID, or null while the check is in the stop's current content. JSON
   /// leaves it out when null.
   /// </summary>
   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
   public int? OutdatedOn { get; }

   /// <summary>Returns whether the answer answers the check of this stop.</summary>
   public bool Answers(string stopId, string checkId) => StopId == stopId && CheckId == checkId;

   /// <summary>Returns the answer with this text and type, as a draft saved at this time, or this answer itself when the text and the type are both the same.</summary>
   /// <remarks>A change to a sent or received answer makes it a draft again without its batch, so the next send carries the change.</remarks>
   public CheckAnswer Revised(string text, AnswerType type, DateTimeOffset now) =>
      text == Text && type == Type ? this : new(StopId, CheckId, text, type, AnswerState.Draft, now, null, OutdatedOn);

   /// <summary>Returns the answer as sent in the batch.</summary>
   public CheckAnswer SentIn(AnswerBatch batch) => new(StopId, CheckId, Text, Type, AnswerState.Sent, SavedAt, batch, OutdatedOn);

   /// <summary>Returns the answer as received at this time, or this answer itself when it is not a sent one.</summary>
   public CheckAnswer AsReceived(DateTimeOffset now) => State == AnswerState.Sent ? new(StopId, CheckId, Text, Type, AnswerState.Received, SavedAt, Batch! with { ReceivedAt = now }, OutdatedOn) : this;

   /// <summary>Returns the answer as outdated on the stop's version with this number, which keeps its state, so a draft stays the reviewer's own.</summary>
   public CheckAnswer OutdatedAt(int version) => new(StopId, CheckId, Text, Type, State, SavedAt, Batch, version);

   /// <summary>Returns the answers of the tour that the next send holds, when the tour is bound to the session with this ID, or the refusal of a send that would hold none.</summary>
   /// <param name="tour">The tour.</param>
   /// <param name="boundSessionId">The bound session's ID, or null when no session holds the tour.</param>
   /// <returns>Each answer to a check of the plan's current content for which <see cref="AwaitsSending"/> holds, in the tour's order.</returns>
   public static OperationResult<CheckAnswer[]> ToSend(Tour tour, string? boundSessionId)
   {
      var checks = (tour.Plan?.Stops ?? []).OfType<Stop.Written>().SelectMany(stop => (stop.Content.Checks ?? []).Select(check => (stop.Id, check.Id))).ToHashSet();
      CheckAnswer[] answers = [.. tour.Answers.Where(answer => checks.Contains((answer.StopId, answer.CheckId)) && answer.AwaitsSending(boundSessionId))];
      return answers.Length == 0 ? OperationError.NoAnswersToSend() : answers;
   }

   /// <summary>Returns whether the next send holds the answer, when the tour is bound to the session with this ID, or to no session for null.</summary>
   /// <remarks>
   /// The send holds a draft. It also holds a sent answer whose session is not the bound session, because that session ended or lost the tour before it received the batch, as a question's
   /// request then becomes unanswered and can be sent again. It never holds a received answer or an outdated one.
   /// </remarks>
   public bool AwaitsSending(string? boundSessionId) => OutdatedOn is null && State switch
   {
      AnswerState.Draft => true,
      AnswerState.Sent => Batch!.SessionId != boundSessionId,
      AnswerState.Received => false,
      _ => throw new UnreachableException($"The answer state {State} does not say whether a send holds it."),
   };

   /// <summary>Returns whether an agent session's reads of the tour show an answer in this state.</summary>
   /// <remarks>
   /// A state that this method does not name throws, so a new state cannot reach an agent session until this method decides it. An outdated answer keeps its state, so this method decides it too.
   /// </remarks>
   public static bool ReachesAgents(AnswerState state) => state switch
   {
      AnswerState.Draft => false,
      AnswerState.Sent or AnswerState.Received => true,
      _ => throw new UnreachableException($"The answer state {state} does not say whether agent sessions see it."),
   };
}

/// <summary>How far a reviewer's answer has come on its way to Claude.</summary>
[JsonConverter(typeof(StrictEnumConverter<AnswerState>))]
internal enum AnswerState
{
   /// <summary>An answer that the reviewer has not sent, which no agent session sees.</summary>
   Draft,

   /// <summary>An answer that the reviewer sent in a batch, which the batch's session has not fetched yet.</summary>
   Sent,

   /// <summary>An answer whose batch the session that it went to fetched with <c>fetch_answers</c>.</summary>
   Received,
}

/// <summary>One sending of answers to an agent session, as each answer of the batch holds it.</summary>
/// <param name="Id">The batch's ID, which the session event and <c>fetch_answers</c> name it by.</param>
/// <param name="SessionId">The ID of the session that was bound when the server stored the batch, which the server sends the batch to.</param>
/// <param name="SentAt">When the server stored the batch.</param>
/// <param name="ReceivedAt">When that session first fetched the batch while bound to the tour, or null before it did. JSON leaves it out when null.</param>
internal sealed record AnswerBatch(string Id, string SessionId, DateTimeOffset SentAt, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ReceivedAt = null);
