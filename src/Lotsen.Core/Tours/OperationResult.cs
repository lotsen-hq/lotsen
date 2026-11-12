using System.Text.Json;
using System.Text.Json.Serialization;
using Lotsen.Json;

namespace Lotsen.Tours;

/// <summary>
/// An operation's refusal. Nothing was stored.
/// </summary>
/// <param name="Code">The kind of refusal, for programs.</param>
/// <param name="Problem">What was wrong, written for the agent or person who sent the input, without the field.</param>
/// <param name="Field">The path of the offending field in the input, such as <c>stops[2].kind</c>, or null when the input as a whole was wrong.</param>
/// <param name="Pin">The tour's pin for <see cref="OperationErrorCode.TourNotPinned"/>, or null for any other code. JSON leaves it out when null.</param>
internal sealed record OperationError(
   OperationErrorCode Code,
   string Problem,
   FieldPath? Field = null,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TourPin? Pin = null)
{
   /// <summary>What was wrong, as the caller reads it: the problem after the field, when there is one.</summary>
   /// <remarks>JSON writes it and ignores it on a read, so a read error renders the same reason again.</remarks>
   public string Reason => Field is { } path ? $"{path}: {Problem}" : Problem;

   /// <summary>Returns an <see cref="OperationErrorCode.InvalidInput"/> error of the field, or of the input as a whole for the root path.</summary>
   public static OperationError Invalid(FieldPath field, string problem) => new(OperationErrorCode.InvalidInput, problem, field.IsRoot ? null : (FieldPath?)field);

   /// <summary>Returns an <see cref="OperationErrorCode.InvalidInput"/> error of the fault, whose path is an input path.</summary>
   public static OperationError Invalid(RuleFault fault) => Invalid(fault.Path, fault.Problem);

   /// <summary>Returns the refusal of a write whose change built a tour that breaks a rule of a stored tour, which is a fault of the server. Nothing was stored.</summary>
   /// <param name="tourId">The tour that the write changed.</param>
   /// <param name="operation">The operation's name, such as <c>put_stop</c>.</param>
   /// <param name="fault">The broken rule, with its path in the tour.</param>
   public static OperationError TourRuleBroken(TourId tourId, string operation, RuleFault fault) => new(
      OperationErrorCode.TourRuleBroken,
      $"{operation} would have stored tour {tourId} with a broken rule, so lotsen stored nothing. {(fault.Path.IsRoot ? "" : $"{fault.Path}: ")}{fault.Problem} This is a fault in lotsen: report it with the server log.");

   /// <summary>Returns the refusal of a preference write while a newer lotsen's preferences are stored.</summary>
   public static OperationError NewerPreferences() => new(
      OperationErrorCode.NewerPreferences,
      "a newer version of lotsen stored the preferences, so this version changes none of them. Run the newer lotsen to change a preference.");

   /// <summary>Returns the refusal of a request whose path names no operation.</summary>
   public static OperationError OperationNotFound(string name) => new(OperationErrorCode.OperationNotFound, $"lotsen has no operation named '{name}'. Name an operation that GET /api/operations lists.");

   public static OperationError TourNotFound(string tourId) => new(OperationErrorCode.TourNotFound, $"no tour has the ID '{tourId}'.", "tourId");

   public static OperationError StopNotFound(string stopId, bool hasPlan) => new(
      OperationErrorCode.StopNotFound,
      hasPlan
         ? $"the plan has no stop '{stopId}'. Give the ID of a stop from read_state. New stops come only from set_plan."
         : $"the tour has no plan yet, so it has no stop '{stopId}'. Set the plan with set_plan first.",
      "stopId");

   /// <summary>Returns the refusal of a write whose tour moved to another pin while the server checked the field against the pinned commits.</summary>
   public static OperationError PinChanged(FieldPath field) => Invalid(field, "the tour's pin changed while the server checked this against the pull request's code. Read the tour again and try again.");

   public static OperationError TourUnreadable(TourId tourId) => new(OperationErrorCode.TourUnreadable, $"tour {tourId} is stored but could not be read.", "tourId");

   /// <summary>Returns the refusal of a prune while stored tours could not be read, because a clone could belong to one of them. It names each tour's file in the data directory's tours directory, so the reviewer can fix or remove it.</summary>
   public static OperationError ToursUnreadable(IReadOnlyList<(TourId Id, string File)> tours) => new(
      OperationErrorCode.TourUnreadable,
      tours.Count == 1
         ? $"no clone was pruned, because tour {tours[0].Id} could not be read, and a clone could belong to it. Make its file {tours[0].File} in the tours directory of lotsen's data directory readable, or move or delete that file, then prune again."
         : $"no clone was pruned, because tours {string.Join(", ", tours.Select(tour => tour.Id))} could not be read, and a clone could belong to them. Make their files in the tours directory of lotsen's data directory readable, or move or delete those files, then prune again: {string.Join(", ", tours.Select(tour => tour.File))}.");

   /// <summary>Returns the refusal of a prune when the stored tours could not be listed.</summary>
   public static OperationError ToursUnlisted() => new(
      OperationErrorCode.TourUnreadable,
      "no clone was pruned, because the stored tours could not be listed. Check that lotsen can read the tours directory in its data directory, then prune again.");

   public static OperationError TourNotPinned(Tour tour) => new(
      OperationErrorCode.TourNotPinned,
      tour.Pin switch
      {
         TourPin.Failed failed => $"tour {tour.Id} has no commits to read files at, because its pin failed: {failed.Reason}",
         _ => $"tour {tour.Id} is still pinning, so it has no commits to read files at yet. Its files can be read once its pin is pinned.",
      },
      "tourId",
      tour.Pin);

   public static OperationError NoIterationToCheck(Tour tour) => new(
      OperationErrorCode.TourNotPinned,
      tour.Pin switch
      {
         TourPin.Failed failed => $"this tour is not pinned, because its pin failed, so lotsen has no update to compare with the pull request's latest one. Pin the tour again first. {failed.Reason}",
         _ => "this tour is still pinning, so lotsen has no update to compare with the pull request's latest one yet. Check again once the tour is pinned.",
      },
      "tourId",
      tour.Pin);

   public static OperationError FileNotFound(string problem, FieldPath? field = null) => new(OperationErrorCode.FileNotFound, problem, field ?? "path");

   public static OperationError RepositoryUnavailable(TourId tourId, string reason) => new(OperationErrorCode.RepositoryUnavailable, $"The repository of tour {tourId} could not be read. {reason}");

   /// <summary>Returns a <see cref="OperationErrorCode.CallerNotAllowed"/> error, which names the field when given, such as <c>tourId</c> for an operation on one tour.</summary>
   public static OperationError CallerNotAllowed(string reason, FieldPath? field) => new(OperationErrorCode.CallerNotAllowed, reason, field);

   /// <summary>Returns the refusal of a write by an agent session that needs the tour's bound session, when no session holds the tour.</summary>
   /// <param name="action">What only the bound session may do, such as "write its plan and stops".</param>
   public static OperationError AttachedToNoSession(TourId tourId, string action) => new(
      OperationErrorCode.NoSessionAttached,
      $"tour {tourId} is attached to no session, so this session cannot {action}. Call attach_tour with the tourId first.",
      "tourId");

   public static OperationError WrongNonce() => new(
      OperationErrorCode.WrongNonce,
      "this is not the nonce that this session received. Pass the nonce attribute of a channel event that this session received, exactly as the event gave it.",
      "nonce");

   /// <param name="tourId">The tour, which the reason names only in its instruction, because Claude's <c>attach_tour</c> takes the ID.</param>
   /// <param name="what">What no session can receive, such as "the question".</param>
   public static OperationError NoSessionAttached(TourId tourId, string what = "the question") => new(
      OperationErrorCode.NoSessionAttached,
      $"this tour is attached to no Claude Code session, so no session can receive {what}. Ask Claude in a Claude Code session that runs lotsen's channel to attach tour {tourId}, then try again.",
      "tourId");

   public static OperationError QuestionNotFound(TourId tourId, string questionId) => new(
      OperationErrorCode.QuestionNotFound,
      $"tour {tourId} has no question '{questionId}'. A question whose thread was deleted is gone and needs no answer.",
      "questionId");

   public static OperationError RequestNotPending(string questionId, QuestionRequest request) => new(
      OperationErrorCode.RequestNotPending,
      $"question '{questionId}' does not wait for an answer, because its last request is {NameOf(request.State)}. The reviewer can send it again instead.",
      "questionId");

   public static OperationError QuestionAnswered(string questionId, QuestionRequest answered) => new(
      OperationErrorCode.QuestionAnswered,
      $"question '{questionId}' already has its answer, from request {answered.Id} as {NameOf(answered.State)}. A question is answered once.",
      "questionId");

   public static OperationError ThreadNotFound(TourId tourId, string threadId) => new(
      OperationErrorCode.ThreadNotFound,
      $"tour {tourId} has no thread '{threadId}'. A thread that was deleted is gone.",
      "threadId");

   public static OperationError QuestionWaiting(string threadId, string questionId) => new(
      OperationErrorCode.QuestionWaiting,
      $"question '{questionId}' of thread '{threadId}' still waits for its answer. Wait for the answer, or cancel the question, then follow up.",
      "threadId");

   /// <summary>Returns the refusal of a command that would make a thread wait while another thread that holds it back waits.</summary>
   /// <param name="waiting">The thread that waits, whose tour action or reviewer decision says what Claude does while it waits.</param>
   /// <param name="field">The input's field that the refusal names.</param>
   /// <exception cref="ArgumentException">The waiting thread is outside every wait group.</exception>
   public static OperationError ActionWaiting(QuestionThread.Sent waiting, FieldPath field)
   {
      var doing = waiting switch
      {
         QuestionThread.Sent.Asked { Action: { } action } => action switch
         {
            TourAction.Summarize => "summarizes the review",
            TourAction.FollowUp.DraftWorkItems => "proposes work items for the decisions",
            TourAction.FollowUp.WalkOpenQuestions => "walks through the open questions",
            TourAction.FollowUp.DraftComments => "drafts comments from the concerns",
            TourAction.Replan => "re-plans the tour as you asked",
            TourAction.Reorder => "reorders the plan as you asked",
            TourAction.Explain or TourAction.Revise => null,
         },
         QuestionThread.Sent.Decided decided => decided.Decision switch
         {
            ReviewerDecision.FamiliarityChanged => "writes stops at the familiarity level that you picked",
            ReviewerDecision.PlanApproved => "writes the stops of the plan that you approved",
            ReviewerDecision.IterationUpdated => "revises the stops that the last update made stale",
         },
         QuestionThread.Sent.Asked or QuestionThread.Sent.Started => null,
      } ?? throw new ArgumentException($"Thread {waiting.Id} is outside every wait group, so it holds back no thread.", nameof(waiting));
      return new(OperationErrorCode.QuestionWaiting, $"Claude still {doing}. Wait for Claude's reply, or cancel that request in its thread, then ask again.", field);
   }

   public static OperationError NoAvailableIteration() => new(
      OperationErrorCode.IterationNotAvailable,
      "this tour has no newer update to move to. Check the pull request for one with the refresh button in the top bar.",
      "tourId");

   /// <summary>Returns the refusal of an update whose tour holds another available iteration than the reviewer's notice showed, before the update or during it.</summary>
   /// <param name="shown">The number of the iteration that the notice showed.</param>
   /// <param name="latest">The number of the iteration that the tour holds now, the same number when only the merge base or the repository changed.</param>
   public static OperationError IterationChangedSinceNotice(int shown, int latest) => new(
      OperationErrorCode.IterationNotAvailable,
      latest == shown
         ? $"update {latest} of the pull request changed since the notice showed it, as when its target branch moves, so the move stored nothing. Read the notice for update {latest}, then move again."
         : $"the pull request moved on to update {latest} since the notice showed update {shown}, so the move stored nothing. Read the notice for update {latest}, then move again.",
      "pin");

   public static OperationError TourChangedDuringUpdate() => new(
      OperationErrorCode.TourChanged,
      "the tour changed during every attempt to move it to the new update, as when Claude wrote stops, so the move stored nothing. Move again.",
      "tourId");

   public static OperationError IterationUnreadable(string reason) => new(
      OperationErrorCode.IterationNotAvailable,
      $"lotsen could not read the new update's commits from the repository, so the tour stays on its update. {reason}",
      "tourId");

   public static OperationError QuestionWaitingToRemove(string threadId, string questionId) => new(
      OperationErrorCode.QuestionWaiting,
      $"question '{questionId}' of thread '{threadId}' still waits for its answer. Cancel the question, then delete the thread.",
      "threadId");

   public static OperationError QuestionWaitingForReply(string threadId, string questionId) => new(
      OperationErrorCode.QuestionWaiting,
      $"question '{questionId}' of thread '{threadId}' waits for your answer. Answer it with reply first, then write the thread again.",
      "threadId");

   public static OperationError QuestionFollowedUp(string questionId, string threadId) => new(
      OperationErrorCode.QuestionFollowedUp,
      $"question '{questionId}' has a follow-up in thread '{threadId}', so it cannot be sent again. Send the thread's last question again instead.",
      "questionId");

   public static OperationError NoteSent(string threadId) => new(
      OperationErrorCode.NoteSent,
      $"thread '{threadId}' is not a note any more, because its question was sent. Only a note that is not sent can be edited, discarded or sent. Read the tour again.",
      "threadId");

   public static OperationError ThreadIsNote(string threadId) => new(
      OperationErrorCode.ThreadIsNote,
      $"thread '{threadId}' is a note that is not sent yet, so it has no question to follow up on. Send the note first, or edit its words.",
      "threadId");

   public static OperationError PlanAwaitsApproval(TourId tourId) => new(
      OperationErrorCode.PlanAwaitsApproval,
      $"the plan of tour {tourId} awaits the reviewer's approval, because the reviewer asked for plan first. Wait for the planApproved decision, then write the stops. set_plan still revises the plan meanwhile.",
      "tourId");

   /// <summary>Returns a <see cref="OperationErrorCode.StopNotFound"/> error for a write of the reviewer's, which names a stop that the UI showed.</summary>
   public static OperationError StopNotShown(string stopId) => new(
      OperationErrorCode.StopNotFound,
      $"the tour's plan has no stop '{stopId}' any more. Read the tour again.",
      "stopId");

   public static OperationError CheckNotFound(string stopId, string checkId) => new(
      OperationErrorCode.CheckNotFound,
      $"stop '{stopId}' has no check '{checkId}' now, because Claude rewrote the stop or has not written it yet. Read the tour again and answer one of the stop's current checks.",
      "checkId");

   public static OperationError AnswerNotFound(string stopId, string checkId) => new(
      OperationErrorCode.AnswerNotFound,
      $"check '{checkId}' of stop '{stopId}' has no answer. Answer the check first.",
      "checkId");

   public static OperationError BatchNotFound(TourId tourId, string batchId) => new(
      OperationErrorCode.BatchNotFound,
      $"tour {tourId} has no answer in batch '{batchId}'. The reviewer may have changed or cleared every answer of the batch, and changed answers come in a later batch. Read the tour to see the answers sent so far.",
      "batchId");

   public static OperationError NoAnswersToSend() => new(
      OperationErrorCode.NoAnswersToSend,
      "this tour has no answer to send. Answer a check, or change a sent answer, then send again.",
      "tourId");

   public static OperationError QuestionNotSentToSession(TourId tourId, string questionId) => new(
      OperationErrorCode.CallerNotAllowed,
      $"question '{questionId}' of tour {tourId} was not sent to this session, so this session cannot reply to it. Reply only to the questions that reach this session.",
      "questionId");

   public static OperationError AttachedToAnotherSession(TourId tourId) => new(
      OperationErrorCode.AttachedToAnotherSession,
      $"tour {tourId} is attached to another session. Ask the user whether to take it over from that session, and only after the user confirms, call attach_tour again with takeOver true.",
      "tourId");

   /// <summary>Returns the refusal of a write by an agent session that needs the tour's bound session, when another session holds the tour.</summary>
   /// <param name="action">What only the bound session may do, such as "write its plan and stops".</param>
   public static OperationError AttachedToAnotherSession(TourId tourId, string action) => new(
      OperationErrorCode.AttachedToAnotherSession,
      $"tour {tourId} is attached to another session, so this session cannot {action}. Ask the user whether to take it over, and only after the user confirms, call attach_tour with takeOver true.",
      "tourId");

   private static string NameOf(RequestState state) => JsonSerializer.Serialize(state, LotsenJsonContext.Default.RequestState).Trim('"');
}

/// <summary>An operation's output, or the error that refused it.</summary>
internal sealed class OperationResult<T>
   where T : class
{
   private OperationResult(T? value, OperationError? error)
   {
      Value = value;
      Error = error;
   }

   /// <summary>The output, or null when the operation was refused.</summary>
   public T? Value { get; }

   /// <summary>The refusal, or null when the operation succeeded.</summary>
   public OperationError? Error { get; }

   /// <summary>Returns the output, as the implicit conversion does for a type that is not an interface.</summary>
   public static OperationResult<T> Of(T value) => new(value, null);

   public static implicit operator OperationResult<T>(T value) => new(value, null);

   public static implicit operator OperationResult<T>(OperationError error) => new(null, error);
}
