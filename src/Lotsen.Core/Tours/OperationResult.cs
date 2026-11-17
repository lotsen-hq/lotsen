using System.Text.Json;
using System.Text.Json.Serialization;
using Lotsen.Git;
using Lotsen.Json;
using Lotsen.Platforms;

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

   /// <summary>
   /// Returns the refusal of a delete or an archive of a tour while stored tours could not be read that are, or could be, dives below it. It names each tour's file in the data directory's
   /// tours directory. Nothing was changed.
   /// </summary>
   /// <remarks>A file that a newer lotsen wrote holds a tour that the newer version can read, so its advice never says to delete the file.</remarks>
   /// <param name="tourId">The tour that the operation names.</param>
   /// <param name="unreadable">Each tour whose file is damaged, with the name of its file.</param>
   /// <param name="newer">Each tour whose file a newer lotsen wrote, with the name of its file.</param>
   public static OperationError DivesUnreadable(TourId tourId, IReadOnlyList<(TourId Id, string File)> unreadable, IReadOnlyList<(TourId Id, string File)> newer)
   {
      var reason = $"tour {tourId} and its dives stay as they are.";
      if (unreadable.Count > 0)
      {
         reason += unreadable.Count == 1
            ? $" Tour {unreadable[0].Id} could not be read, and it may be a dive below tour {tourId}. Make its file {unreadable[0].File} in the tours directory of lotsen's data directory readable, or move or delete that file, then try again."
            : $" Tours {string.Join(", ", unreadable.Select(tour => tour.Id))} could not be read, and each may be a dive below tour {tourId}. Make their files in the tours directory of lotsen's data directory readable, or move or delete those files, then try again: {string.Join(", ", unreadable.Select(tour => tour.File))}.";
      }

      if (newer.Count > 0)
      {
         reason += newer.Count == 1
            ? $" Tour {newer[0].Id} could not be read, because a newer version of lotsen wrote it, and it may be a dive below tour {tourId}. Delete or archive tour {tourId} with that newer version, or move the file {newer[0].File} out of the tours directory of lotsen's data directory and try again."
            : $" Tours {string.Join(", ", newer.Select(tour => tour.Id))} could not be read, because a newer version of lotsen wrote them, and each may be a dive below tour {tourId}. Delete or archive tour {tourId} with that newer version, or move their files out of the tours directory of lotsen's data directory and try again: {string.Join(", ", newer.Select(tour => tour.File))}.";
      }

      return new(OperationErrorCode.TourUnreadable, reason, "tourId");
   }

   /// <summary>Returns the refusal of a delete or an archive of a repository tour when the stored tours could not be listed to find its dives. Nothing was changed.</summary>
   public static OperationError DivesUnlisted(TourId tourId) => new(
      OperationErrorCode.TourUnreadable,
      $"tour {tourId} and its dives stay as they are, because the stored tours could not be listed to find its dives. Check that lotsen can read the tours directory in its data directory, then try again.",
      "tourId");

   /// <summary>Returns the refusal of an operation that reads the commits of a pull request's pinned tour, for a tour that has none.</summary>
   /// <returns><see cref="OperationErrorCode.NotPullRequestTour"/> for a repository tour in any pin state, or <see cref="PinNotReady"/>'s refusal for a pull request's tour.</returns>
   public static OperationError TourNotPinned(Tour tour) => tour.Target is RepositoryTarget ? NotPullRequestTour(tour, "merge base and pull request head to read files at") : PinNotReady(tour);

   /// <summary>Returns the <see cref="OperationErrorCode.TourNotPinned"/> refusal of an operation that reads files at a tour's pin, for a tour that pins or whose pin failed.</summary>
   public static OperationError PinNotReady(Tour tour) => tour.Pin switch
   {
      TourPin.Failed failed => new(OperationErrorCode.TourNotPinned, $"tour {tour.Id} has no commits to read files at, because its pin failed: {failed.Reason}", "tourId", tour.Pin),
      TourPin.Pinning or TourPin.Pinned or TourPin.Snapshot => new(
         OperationErrorCode.TourNotPinned,
         $"tour {tour.Id} is still pinning, so it has no commits to read files at yet. Its files can be read once the pin is done.",
         "tourId",
         tour.Pin),
   };

   /// <summary>Returns the refusal of an iteration check of a tour that is not pinned to an iteration.</summary>
   /// <returns><see cref="OperationErrorCode.NotPullRequestTour"/> for a repository tour in any pin state, or <see cref="OperationErrorCode.TourNotPinned"/> while a pull request's tour pins or after its pin failed.</returns>
   public static OperationError NoIterationToCheck(Tour tour) => tour.Target is RepositoryTarget
      ? NotPullRequestTour(tour, "pull request to check for a new update")
      : tour.Pin switch
      {
         TourPin.Failed failed => new(
            OperationErrorCode.TourNotPinned,
            $"this tour is not pinned, because its pin failed, so lotsen has no update to compare with the pull request's latest one. Pin the tour again first. {failed.Reason}",
            "tourId",
            tour.Pin),
         TourPin.Pinning or TourPin.Pinned or TourPin.Snapshot => new(
            OperationErrorCode.TourNotPinned,
            "this tour is still pinning, so lotsen has no update to compare with the pull request's latest one yet. Check again once the tour is pinned.",
            "tourId",
            tour.Pin),
      };

   /// <summary>Returns the refusal of an operation that acts only on a pull request's tour, for a repository tour.</summary>
   /// <param name="tour">The repository tour.</param>
   /// <param name="what">What a repository tour has none of, such as <c>pull request to check for a new update</c>.</param>
   public static OperationError NotPullRequestTour(Tour tour, string what) => new(OperationErrorCode.NotPullRequestTour, $"tour {tour.Id} is a repository tour, so it has no {what}.", "tourId");

   /// <summary>Returns the refusal of a move of a pull request's tour, which moves to a new update of its pull request instead.</summary>
   public static OperationError NoMoveForPullRequest(Tour tour) => new(
      OperationErrorCode.MoveNotAvailable,
      $"tour {tour.Id} tours a pull request, so it has no ref to move along. Move it to a new update of the pull request from the notice in the top bar.",
      "tourId");

   /// <summary>Returns the refusal of a move of a repository tour that is not pinned to a snapshot, while it pins or after its pin failed.</summary>
   public static OperationError NoSnapshotToMove(Tour tour) => tour.Pin is TourPin.Failed failed
      ? new(OperationErrorCode.TourNotPinned, $"tour {tour.Id} has no commit to move from, because its pin failed. Pin the tour again first. {failed.Reason}", "tourId", tour.Pin)
      : new(OperationErrorCode.TourNotPinned, $"tour {tour.Id} is still pinning, so it has no commit to move from yet. Check for a move once the tour is pinned.", "tourId", tour.Pin);

   /// <summary>Returns the refusal of a move of a tour whose ref is a tag or a commit ID, which names one commit for good.</summary>
   /// <param name="kind">What the ref is: a tag or a commit ID.</param>
   /// <param name="ref">The ref, as the platform or git resolved it.</param>
   public static OperationError NoMoveOfRef(RepositoryRefKind kind, string @ref) => new(
      OperationErrorCode.MoveNotAvailable,
      $"the tour shows the {(kind == RepositoryRefKind.Tag ? "tag" : "commit")} {@ref}, which names one commit for good, so the tour has no move.",
      "tourId");

   /// <summary>Returns the refusal of a move whose tour, and each dive that it ticks, is at the latest commit of the ref already.</summary>
   /// <param name="commit">The latest commit.</param>
   /// <param name="behind">The dives below the tour at another commit, which the move did not tick, or none.</param>
   public static OperationError NothingToMove(CommitId commit, IReadOnlyList<TourId> behind) => new(
      OperationErrorCode.MoveNotAvailable,
      $"the tour and each dive that diveIds names are at commit {commit}, the latest commit of its ref, so nothing moves. "
         + (behind.Count == 0 ? "Check for a move again once the ref moves on." : $"Tick the dives that are at another commit to move them: {string.Join(", ", behind)}."),
      "tourId");

   /// <summary>Returns the failure of a move whose store of a tour failed on the disk or was refused, after the move stored the tours before it.</summary>
   /// <param name="moved">The tours that the move stored, each dive after its parent, or none.</param>
   /// <param name="notMoved">The tours that the move did not store, the failed one first.</param>
   /// <param name="commit">The commit that the stored tours moved to.</param>
   /// <param name="reason">Why the store failed.</param>
   /// <param name="onDisk">Whether the disk failed, which the fix names, or false for a store that a refusal stopped.</param>
   public static OperationError MoveStoredInPart(IReadOnlyList<TourId> moved, IReadOnlyList<TourId> notMoved, CommitId commit, string reason, bool onDisk) => new(
      OperationErrorCode.TourNotStored,
      $"lotsen could not store the move of tour {notMoved[0]}: {reason} "
         + (moved.Count == 0 ? "No tour moved." : $"{Tours(moved)} moved to commit {commit}.")
         + $" {Tours(notMoved)} {(notMoved.Count == 1 ? "stays" : "stay")} at {(notMoved.Count == 1 ? "its commit" : "their commits")}. {(onDisk ? "Check that lotsen can write its data directory, then check for a move again." : "Check for a move again.")}",
      "tourId");

   // Names the tours as a sentence's subject, such as "Tour a" or "Tours a, b".
   private static string Tours(IReadOnlyList<TourId> tours) => $"{(tours.Count == 1 ? "Tour" : "Tours")} {string.Join(", ", tours)}";

   /// <summary>Returns the refusal of a move whose ref names another commit than the check showed, because the ref moved since.</summary>
   /// <param name="shown">The commit that the check showed, which the move names.</param>
   /// <param name="latest">The commit that the ref names now.</param>
   public static OperationError RefMovedSinceCheck(CommitId shown, CommitId latest) => new(
      OperationErrorCode.MoveNotAvailable,
      $"the ref moved on to commit {latest} since the check showed commit {shown}, so the move stored nothing. Check for a move again, then move.",
      "commit");

   public static OperationError TourChangedDuringMove() => new(
      OperationErrorCode.TourChanged,
      "the tour or one of its dives changed during every attempt to move it to the latest commit, as when Claude wrote stops, so the move stored nothing. Move again.",
      "tourId");

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
            TourAction.Explain or TourAction.Revise or TourAction.Dive => null,
         },
         QuestionThread.Sent.Decided decided => decided.Decision switch
         {
            ReviewerDecision.FamiliarityChanged => "writes stops at the familiarity level that you picked",
            ReviewerDecision.PlanApproved => "writes the stops of the plan that you approved",
            ReviewerDecision.IterationUpdated => "revises the stops that the last update made stale",
            ReviewerDecision.TourMoved => "revises the stops that the last move made stale",
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

   public static OperationError ThreadTakesNoType(QuestionThread.Sent thread)
   {
      var what = thread switch
      {
         QuestionThread.Sent.Asked => $"thread '{thread.Id}' holds a tour action",
         QuestionThread.Sent.Decided => $"thread '{thread.Id}' reports a decision of yours",
         QuestionThread.Sent.Started => $"Claude started thread '{thread.Id}'",
      };
      return new(OperationErrorCode.ThreadTakesNoType, $"{what}, so it takes no type. Give a type to a note or to a thread that starts with your question.", "threadId");
   }

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
