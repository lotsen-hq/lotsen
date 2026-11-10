using System.ComponentModel;
using System.Text.Json.Serialization;
using Lotsen.Pinning;
using Lotsen.Sessions;
using Lotsen.Tours;

namespace Lotsen.Operations;

/// <summary>The input of <c>ask_question</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AskQuestionInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId,
   [property: Description("The reviewer's question as plain text, with no control characters but tabs and line breaks.")]
   string Text,
   [property: Description("Where the reviewer asked: code for whole lines at one side, stopText for text selected in a prose field of a stop, file, stop, or tour.")]
   AnchorInput Anchor,
   [property: Description("The ID of the stop that the reviewer views while asking. Omit it when no stop is open.")]
   string? StopId = null);

/// <summary>The anchor in the input of <c>ask_question</c>. The <c>kind</c> property in JSON names the derived type.</summary>
/// <remarks>The private constructor keeps the kinds those of <see cref="ThreadAnchor"/>.</remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(CodeRange), "code")]
[JsonDerivedType(typeof(StopText), "stopText")]
[JsonDerivedType(typeof(WholeFile), "file")]
[JsonDerivedType(typeof(WholeStop), "stop")]
[JsonDerivedType(typeof(WholeTour), "tour")]
internal abstract record AnchorInput
{
   private AnchorInput()
   {
   }

   [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
   internal sealed record CodeRange(
      [property: Description("The side the lines are on: base for the merge base, head for the head.")]
      FileSide Side,
      [property: Description("The file's repository path at that side, relative, with forward slashes.")]
      string Path,
      [property: Description("The first line, from 1.")]
      int StartLine,
      [property: Description("The last line, inclusive, not before startLine.")]
      int EndLine) : AnchorInput;

   [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
   internal sealed record StopText(
      [property: Description("The ID of the stop whose text the reviewer selected.")]
      string StopId,
      [property: Description(ProseField.SchemaDescription)]
      string Field,
      [property: Description("The selected text.")]
      string Text,
      [property: Description("The stop's version that the text was selected in, as the stop's version in the tour. A version that is not the stop's current one is refused. Omit it for the current version.")]
      int? Version = null) : AnchorInput;

   [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
   internal sealed record WholeFile(
      [property: Description("The side the file is on: base for the merge base, head for the head.")]
      FileSide Side,
      [property: Description("The file's repository path at that side, relative, with forward slashes.")]
      string Path) : AnchorInput;

   [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
   internal sealed record WholeStop(
      [property: Description("The ID of the stop.")]
      string StopId) : AnchorInput;

   [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
   internal sealed record WholeTour : AnchorInput;
}

/// <summary>The input of <c>request_tour_action</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RequestTourActionInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId,
   [property: Description("What the reviewer asks Claude to do: explain to explain a stop or text selected in it more deeply, or revise to revise the stop, each with its target, replan to change the plan as the words say, reorder to put the plan's stops in the order of stopIds, which names every stop of the plan once, summarize to write a synthesis of the review, or a follow-up action: draftWorkItems to propose work items for the sent decisions, walkOpenQuestions to start a thread on each sent open question, or draftComments to draft comments for the author from the sent concerns and the risk stops that Claude wrote and the reviewer did not skip. Only explain and revise take a target, and every other kind applies to the whole tour. The server collects a follow-up action's items, so the kind is all that a follow-up action holds. A reviewer decision is no tour action: change_familiarity, approve_plan and update_iteration report each.")]
   ActionRequest Action,
   [property: Description("The reviewer's words as plain text, with no control characters but tabs and line breaks. Omit them, or leave them empty, for an action without words. A revise and a replan need them.")]
   string? Text = null,
   [property: Description("The ID of the stop that the reviewer views while asking. Omit it when no stop is open.")]
   string? StopId = null);

/// <summary>The input of <c>ask_follow_up</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AskFollowUpInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId,
   [property: Description("The ID of the thread to follow up in.")]
   string ThreadId,
   [property: Description("The reviewer's follow-up as plain text, with no control characters but tabs and line breaks.")]
   string Text,
   [property: Description("The ID of the stop that the reviewer views while asking. Omit it when no stop is open.")]
   string? StopId = null);

/// <summary>The input of <c>fetch_question</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record FetchQuestionInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId,
   [property: Description("The question's ID, as the question's channel event gave it.")]
   string QuestionId);

/// <summary>The output of <c>fetch_question</c>.</summary>
/// <param name="TourId">The question's tour.</param>
/// <param name="QuestionId">The question's ID.</param>
/// <param name="Thread">The question's thread as stored, with its anchor and every message so far.</param>
/// <param name="Stop">
/// The current state of the stop the question is about, without its earlier versions, as <see cref="AgentView.Of(Tours.Stop)"/> shows it: the anchor's stop for a stop or stop text anchor,
/// otherwise the stop the reviewer viewed. Null when there is none or the plan no longer has it, and JSON then leaves it out.
/// </param>
/// <param name="HowToAct">What the thread's tour action or reviewer decision asks of Claude and how to carry it out, for each question of a thread that holds one. Null otherwise, and JSON then leaves it out.</param>
internal sealed record FetchedQuestion(
   TourId TourId,
   string QuestionId,
   QuestionThread Thread,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Stop? Stop,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? HowToAct);

/// <summary>The input of <c>resend_question</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ResendQuestionInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId,
   [property: Description("The ID of the question that has no answer yet.")]
   string QuestionId);

/// <summary>The input of <c>cancel_question</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CancelQuestionInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId,
   [property: Description("The ID of the question that waits for an answer.")]
   string QuestionId);

/// <summary>
/// The reviewer's questions: <c>ask_question</c>, <c>request_tour_action</c> and <c>ask_follow_up</c>, which store a question and send it to the tour's bound session, <c>resend_question</c> and
/// <c>cancel_question</c>, and <c>fetch_question</c>, which returns a question with its context and marks its requests received.
/// </summary>
/// <remarks>
/// A question goes to the session that was bound when the writer stored it, which the request names, after the writer releases its lock. A binding that ends or moves after the store therefore
/// finds the request stored.
/// </remarks>
internal sealed class QuestionOperations
{
   private readonly TourWriter _writer;
   private readonly FileOperations _files;
   private readonly IterationScheduler _scheduler;

   public QuestionOperations(TourWriter writer, FileOperations files, IterationScheduler scheduler)
   {
      _writer = writer;
      _files = files;
      _scheduler = scheduler;
   }

   /// <summary>Stores the UI's question as a new thread with a pending request for the tour's bound session, sends the session the question, and returns the thread.</summary>
   /// <remarks>
   /// A tour with no bound session is refused, because no session would receive the question. The anchor's checks are those of <see cref="ThreadOpener.StoreQuestionAsync"/>.
   /// </remarks>
   public async Task<OperationResult<QuestionThread>> AskQuestionAsync(Access.ReviewerToSession.Pass pass, AskQuestionInput input, CancellationToken cancellationToken)
   {
      _scheduler.Nudge(input.TourId, Nudge.ReviewerAction);
      var created = await ThreadOpener.StoreQuestionAsync(_writer, _files, pass, NewThreadInput.Of(input), cancellationToken);
      return created.Value is { } stored ? stored.Thread : created.Error!;
   }

   /// <summary>
   /// Stores the UI's tour action as a new thread whose question holds the action, with a pending request for the tour's bound session, sends the session the question, and returns the
   /// thread.
   /// </summary>
   /// <remarks>
   /// A tour with no bound session is refused, as for <see cref="AskQuestionAsync"/>. The writer refuses words over the limit before it reads the tour. The viewed stop and <see
   /// cref="ActionRules.Admit"/> then run on the latest tour, under the writer's lock. Words that hold only whitespace count as none, and the question then holds empty text, which <see cref="ActionRules.Admit"/> refuses for a kind that needs words.
   /// </remarks>
   public OperationResult<QuestionThread> RequestTourAction(Access.ReviewerToSession.Pass pass, RequestTourActionInput input)
   {
      _scheduler.Nudge(input.TourId, Nudge.ReviewerAction);
      var text = string.IsNullOrWhiteSpace(input.Text) ? "" : input.Text;
      var words = text.Length > 0 && ReviewerWords.Limit(text, "tour action") is { } limit ? OperationError.Invalid("text", limit) : null;
      return _writer.Write(pass, input.TourId, edit =>
      {
         if (AnchorChecks.CheckViewedStop(edit.Latest, input.StopId) is { } invalid)
         {
            return invalid;
         }

         var admitted = ActionRules.Admit(edit.Latest, input.Action, text);
         return admitted.Value is not { } action ? admitted.Error! : edit.Store(edit.Threads.Request(action, text, input.StopId), (_, written) => written.Thread!);
      }, words);
   }

   /// <summary>Adds the UI's follow-up to a thread as a new question with a pending request for the tour's bound session, sends the session the question, and returns the thread.</summary>
   /// <remarks>
   /// A tour with no bound session is refused, as for <see cref="AskQuestionAsync"/>. The earlier questions keep their requests and their answers. A thread whose last question waits is refused, so
   /// only a thread's last question can wait.
   /// </remarks>
   public OperationResult<QuestionThread> AskFollowUp(Access.ReviewerToSession.Pass pass, AskFollowUpInput input)
   {
      _scheduler.Nudge(input.TourId, Nudge.ReviewerAction);
      return _writer.Write(pass, input.TourId, edit =>
         (ReviewerWords.Check(input.Text) ?? AnchorChecks.CheckViewedStop(edit.Latest, input.StopId)) is { } invalid
            ? invalid
            : edit.Store(edit.Threads.FollowUp(input.ThreadId, input.Text, input.StopId), (_, written) => written.Thread!));
   }

   /// <summary>Returns a question of the tour with its thread and the current state of the stop it is about.</summary>
   /// <remarks>
   /// A fetch by an agent session marks each request of the question that went to that session and has no mark yet as received, so the reviewer sees that Claude has the question. The mark is set
   /// once, inside the store's update, so two fetches that race set it once, and a later fetch changes nothing.
   /// </remarks>
   public OperationResult<FetchedQuestion> FetchQuestion(Access.AnySession.Pass pass, FetchQuestionInput input)
   {
      var read = _writer.Read(input.TourId);
      if (read.Value is not { } tour)
      {
         return read.Error!;
      }

      if (tour.Threads.FindQuestion(input.QuestionId) is not (_, var asked))
      {
         return OperationError.QuestionNotFound(tour.Id, input.QuestionId);
      }

      if (asked.IsUnreceivedBy(pass.Recipient.Id))
      {
         var marked = MarkReceived(pass, tour.Id, input.QuestionId);
         if (marked.Value is not { } stored)
         {
            return marked.Error!;
         }

         tour = stored;
      }

      if (tour.Threads.FindQuestion(input.QuestionId) is not (var thread, var question))
      {
         return OperationError.QuestionNotFound(tour.Id, input.QuestionId);
      }

      var stopId = thread.Anchor switch
      {
         ThreadAnchor.StopText selected => selected.StopId,
         ThreadAnchor.WholeStop stop => stop.StopId,
         _ => question.StopId,
      };
      return new FetchedQuestion(tour.Id, question.Id, thread, stopId is null || tour.FindStop(stopId) is not { } found ? null : AgentView.Of(found), HowToAct(thread));
   }

   private static string? HowToAct(QuestionThread.Sent thread) => thread switch
   {
      QuestionThread.Sent.Asked asked => asked.Action is { } action ? ActionRules.HowToAct(action) : null,
      QuestionThread.Sent.Decided decided => DecisionRules.HowToAct(decided.Decision),
      QuestionThread.Sent.Started => null,
   };

   /// <summary>Sends a question that has no answer yet again to the tour's bound session, as a new pending request, and returns the thread.</summary>
   /// <remarks>
   /// The question's last request may be pending, canceled or unanswered, and the same update cancels a pending one. The earlier requests' sessions may still answer, and the first answer wins. Only
   /// the thread's last question can be sent again, so only that question can wait. A tour action goes again only while <see cref="ActionRules.CheckResend"/> passes against the latest tour, and a
   /// reviewer decision while <see cref="DecisionRules.CheckResend"/> does. A question in a thread of a wait group goes again under the waiting rule of <see cref="TourThreads"/>.
   /// </remarks>
   public OperationResult<QuestionThread> ResendQuestion(Access.ReviewerToSession.Pass pass, ResendQuestionInput input)
   {
      _scheduler.Nudge(input.TourId, Nudge.ReviewerAction);
      return _writer.Write(pass, input.TourId, edit => edit.Store(edit.Threads.Resend(input.QuestionId), (_, written) => written.Thread!));
   }

   /// <summary>Stops a question waiting for its answer: its pending request becomes canceled. Returns the thread.</summary>
   /// <remarks>
   /// Nothing reaches the session, which may still answer the canceled request. The tour needs no bound session, because a session can end or lose its connection while the question waits.
   /// </remarks>
   public OperationResult<QuestionThread> CancelQuestion(Access.Reviewer.Pass pass, CancelQuestionInput input) =>
      _writer.Write(pass, input.TourId, edit =>
         edit.Store(edit.Threads.Apply(new ThreadCommand.Cancel(input.QuestionId)), (_, written) => written.Thread!));

   // Marks the question's requests to the session that have no mark yet as received, and publishes the thread when the write marked one. Returns the stored tour.
   private OperationResult<Tour> MarkReceived(Access.AnySession.Pass pass, TourId tourId, string questionId) =>
      _writer.Write(pass, tourId, edit => edit.Store(edit.Threads.Apply(new ThreadCommand.MarkReceived(questionId, pass.Recipient)), (stored, _) => stored.Tour));
}
