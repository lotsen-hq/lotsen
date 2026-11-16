using System.Diagnostics;

namespace Lotsen.Tours;

/// <summary>The rules of the tour actions, one column per member, over every kind.</summary>
/// <remarks>
/// Each column is a switch without a discard arm, its arms in the kind order of <see cref="TourAction"/>, so the file reads as a table of kinds by rules and a new kind does not compile until each
/// column handles it. Only a request has the reviewer's words and an <see cref="ActionRequest"/>, so the feature calls <see cref="Admit"/>, and the table of <see cref="TourThreads"/> calls the
/// columns over the stored action.
/// </remarks>
internal static class ActionRules
{
   // Why each follow-up action has nothing to hand, which the summary page also shows beside its disabled button: nothing sent, or only sent typed threads that it omits by their state.
   public const string NoDecisionSent = "No decision has been sent to Claude yet. Answer a check with a decision and send your answers, or send a note with the type Decision.";
   public const string NoOpenQuestionSent = "No open question has been sent to Claude yet. Answer a check with an open question and send your answers, or send a note with the type Open question.";
   public const string NoConcernSent = "No concern has been sent to Claude yet, and every risk stop is still unwritten or skipped. Answer a check with a concern and send your answers, or send a note with the type Concern.";
   public const string DecisionThreadsOutdated = "Every decision that you sent as a thread is outdated, so Claude gets none of them. Reopen one in its thread view, or answer a check with a decision and send your answers.";
   public const string OpenQuestionThreadsSettled = "Every open question that you sent as a thread is resolved or outdated, so Claude gets none of them. Reopen one in its thread view, or answer a check with an open question and send your answers.";
   public const string ConcernThreadsSettled = "Every concern that you sent as a thread is resolved or outdated, and every risk stop is still unwritten or skipped. Reopen a concern in its thread view, or answer a check with a concern and send your answers.";

   // The sentences that the rules of more than one kind share, served to Claude. DecisionRules uses the public ones.
   public const string ProposedOrderRule = "A proposed order is a request, not an order to copy, because a stop can rely on the stops before it. Keep such a stop after the stops it relies on, give the chapters, and the parts if the plan has any, to the new order, and say in the reply where your order differs from the proposal and why.";
   public const string DepthRule = "The tour's familiarity level sets how deep a stop goes. familiar: go deeper on the why and the design choices, and don't explain the codebase's conventions. some: explain the code that the stop relies on but does not show, with related passages or ref: links. new: define each term the stop relies on and walk through the code step by step.";
   public const string RedoRule = "fetch_question's redo says whether you can still redo this work on the tour as it is now: it is absent while you can, and otherwise holds the reason with what the reviewer can do instead. When a follow-up in this thread asks for the work again and redo holds a reason, do not redo it: quote the reason in your reply, and still answer any question that the follow-up asks. A follow-up that answers your needsAnswer asks for work that you have not done yet, so carry it out then.";
   private const string PlanChangeRule = "with set_plan on the whole plan, its parts and file marks included, because set_plan replaces them, and give every stop that stays the id it has, so it keeps its content, its versions and the reviewer's answers and threads. Then revise with put_stop each kept stop whose content the new plan changes, such as an overview that lists the stops, before you reply.";
   private const string ResolveRule = "Then resolve the action with one short reply: done once the stop or the plan holds the result, with a sentence or two on what changed, needsAnswer when you need the reviewer's answer before you can act, or declined when you will not do it, with the reason in text. The answer to a needsAnswer comes as a follow-up in the same thread, a question about this action: carry the action out then, and reply to the follow-up.";
   private const string PlanResolveRule = $"{ResolveRule} Write the plan's new stops after the reply.";
   private const string AnswerItemFields = "each with its stopId, stopTitle, checkId, the check's question, the reviewer's text and its type";
   private const string ThreadItemFields = "each with its threadId, its anchor, its stopId when the thread is about a stop and its stopTitle when the plan still has that stop, the reviewer's text, which is the thread's first question, its type and its state when the reviewer asked, open or resolved";
   private const string FollowUpItemsRule = "The items are what the reviewer had sent when they asked, and never their draft answers or their unsent notes. lotsen collected them from the review summary, so a later answer or a later change of a thread's type or state does not change them, and a repeat of the action carries the same items. lotsen refuses a repeat that would hand a deleted or outdated thread, or a resolved one other than a decision. The reviewer's words, when there are any, say what to keep in mind.";
   private const string ThreadItemsRule = "A thread item is the reviewer's own finding, in the reviewer's words, not yours: a question that the reviewer sent and filed as a decision, a concern or an open question. Read the replies in its thread with read_state when they matter. lotsen leaves out an outdated thread, as it leaves out an outdated answer, and the action leaves out a list of threads that would be empty.";
   private const string ThreadItemsNowRule = "fetch_question's threadItems say for each thread item, by its threadId, whether it still applies now. An item's applies is false once the reviewer deleted the thread, the thread went outdated, or the reviewer resolved it and this action leaves resolved threads out, and its reason then says which: deleted, outdated or resolved. When a follow-up in this thread asks you to carry out or redo the work, skip each thread item whose applies is false, and say in your reply which items you skipped and why. Answer a follow-up that only asks about the action as any question.";
   private const string NoLotsenActionRule = "lotsen itself creates no work item, posts no comment and changes nothing in the repository, and none of its tools can.";

   private static readonly FieldPath ActionField = "action";
   private static readonly FieldPath TargetField = ActionField.Then("target");
   private static readonly FieldPath AnchorField = "anchor";

   /// <summary>Returns the anchor and the stored action of a new request, or why the request cannot go to the tour now.</summary>
   /// <remarks>
   /// Explain and revise resolve their target with <see cref="StopTargets.Resolve"/>, need a stop with content, and stamp a whole stop's current version on the action. Every other kind takes the
   /// whole tour. Replan, reorder and summarize need a plan, a revise and a replan need words, and a reorder names every stop of the plan once. A follow-up action needs an item in its group of the
   /// summary that an agent session reads, a sent answer or a sent typed thread, and the stored action carries those items. The refusals that the tour's state alone gives come from
   /// <see cref="Offer"/>, before the input's own checks. The waiting rule is the thread table's.
   /// </remarks>
   /// <param name="tour">The latest stored tour.</param>
   /// <param name="request">The request.</param>
   /// <param name="text">The reviewer's words, or empty without words.</param>
   public static OperationResult<Admitted> Admit(Tour tour, ActionRequest request, string text) => Admitted.Of(tour, request, text);

   /// <summary>Returns why a request of the kind cannot go to the tour now, from the tour's state alone, or null when it can.</summary>
   /// <remarks>
   /// It runs the columns of <see cref="Admit"/> that read the tour and not the reviewer's words: the target's stop and its content for explain and revise, the plan for replan, reorder and
   /// summarize, and the items of the summary for a follow-up action. So the tour controls offer a request exactly when <see cref="Admit"/> takes one with valid words, before the waiting rule.
   /// </remarks>
   /// <param name="tour">The latest stored tour.</param>
   /// <param name="request">The request, whose stop target counts and whose reorder order does not.</param>
   public static OperationError? Offer(Tour tour, ActionRequest request) => request switch
   {
      ActionRequest.Explain explain => OfferOnStop(tour, explain.Target, TourAction.ExplainKind),
      ActionRequest.Revise revise => OfferOnStop(tour, revise.Target, TourAction.ReviseKind),
      ActionRequest.Replan => NoPlan(tour, TourAction.ReplanKind),
      ActionRequest.Reorder => NoPlan(tour, TourAction.ReorderKind),
      ActionRequest.Summarize => NoReview(tour),
      ActionRequest.FollowUp followUp => NothingToHand(followUp, AgentView.Summary(tour)) is { } reason ? OperationError.Invalid(ActionField, reason) : null,
   };

   /// <summary>Returns why an action's opening question cannot go to the tour again, checked over the stored anchor, or null when it can.</summary>
   /// <remarks>
   /// The reviewer reads the refusal beside the Re-send button, so a refusal because the tour moved past the action says what changed since the request and what to do in the UI. A whole stop's
   /// action is refused once the stop's version is newer than the stored one, because the server cannot tell the revision that the action asked for from another one. A follow-up action carries
   /// the items that its request stored, as <see cref="CheckThreadsAgain"/> checks them.
   /// </remarks>
   public static OperationError? CheckResend(Tour tour, ThreadAnchor anchor, TourAction action) => action switch
   {
      TourAction.Explain explain => CheckStopAgain(tour, anchor, explain.Kind, explain.StopVersion),
      TourAction.Revise revise => CheckStopAgain(tour, anchor, revise.Kind, revise.StopVersion),
      TourAction.Replan replan => NoPlan(tour, replan.Kind),
      TourAction.Reorder reorder => NoPlan(tour, reorder.Kind)
         ?? (PlanRules.CheckOrder(tour.Plan, reorder.StopIds) is null
            ? null
            : OperationError.Invalid(ActionField.Then("stopIds"), "Claude changed the plan's stops since you proposed this order. Propose an order of the current plan's stops from the Plan panel.")),
      TourAction.Summarize => NoReview(tour),
      TourAction.FollowUp followUp => CheckThreadsAgain(tour, followUp),
   };

   /// <summary>Returns why Claude cannot redo the reorder for a follow-up in its thread, with what the reviewer can do instead, or null while the proposed order names each stop of the plan once.</summary>
   /// <remarks>Carrying out a reorder keeps the plan's stops, so only a later change of the stops makes the order unfit.</remarks>
   public static string? CheckRedo(Tour tour, TourAction.Reorder reorder) => PlanRules.CheckOrder(tour.Plan, reorder.StopIds) is null
      ? null
      : "Claude changed the plan's stops since you proposed this order, so the order no longer names each stop once. Propose an order of the current stops with Reorder… in the Plan panel's ⋯ menu.";

   /// <summary>Returns the group whose waiting threads hold back a new request of the kind, or null when any number of the kind's threads may wait.</summary>
   /// <remarks>A thread's last question decides whether it waits, so a follow-up in it counts, because Claude can do the work again for it.</remarks>
   public static WaitGroup? WaitsAlone(TourAction action) => action switch
   {
      TourAction.Explain or TourAction.Revise => null,
      TourAction.Replan or TourAction.Reorder => WaitGroup.Plan,
      TourAction.Summarize => WaitGroup.Summary,
      TourAction.FollowUp.DraftWorkItems => WaitGroup.WorkItems,
      TourAction.FollowUp.WalkOpenQuestions => WaitGroup.OpenQuestions,
      TourAction.FollowUp.DraftComments => WaitGroup.Comments,
   };

   /// <summary>Returns whether the action hands the review to Claude and adds nothing to it, so the review summary's fingerprint leaves its thread out.</summary>
   public static bool HandsOverReview(TourAction action) => action switch
   {
      TourAction.Explain or TourAction.Revise or TourAction.Replan or TourAction.Reorder => false,
      TourAction.Summarize or TourAction.FollowUp => true,
   };

   /// <summary>Returns whether each thread item of a follow-up action still applies to the tour, in the order of the action's list, or null for an action without thread items.</summary>
   /// <remarks>An item applies exactly when <see cref="CheckResend"/> would let the action go again with it, because both read <see cref="LapseOf"/>.</remarks>
   public static IReadOnlyList<ThreadItemNow>? ThreadItemsNow(Tour tour, TourAction action) => action switch
   {
      TourAction.Explain or TourAction.Revise or TourAction.Replan or TourAction.Reorder or TourAction.Summarize => null,
      TourAction.FollowUp followUp => ThreadListOf(followUp).Threads is { } threads ? [.. threads.Select(thread => new ThreadItemNow(thread.ThreadId, LapseOf(tour, thread, followUp.Kind)))] : null,
   };

   /// <summary>Returns what the action asks of Claude and how to carry it out, as <c>fetch_question</c> serves it for each question of the thread.</summary>
   /// <remarks>A rule stands alone, because a session without the skill reads it, so it never cites a section of the skill. <c>HowToActTests</c> checks that for every kind.</remarks>
   public static string HowToAct(TourAction action) => action switch
   {
      TourAction.Explain => $"The action \"explain\" asks you to explain the anchor's stop, or the selected text of a stopText anchor, more deeply than the stop does now, at the tour's familiarity level. Carry out explain with put_stop on the anchor's stop, so the explanation goes into the stop, never only into your reply. Add the explanation to the stop's own fields, mostly how, and keep the rest as it is. {DepthRule} {ResolveRule}",
      TourAction.Revise => $"The action \"revise\" asks you to revise the anchor's stop as the reviewer's words say. Carry out revise with put_stop on the anchor's stop, so the change goes into the stop, never only into your reply. {DepthRule} {ResolveRule}",
      TourAction.Replan => $"The action \"replan\" asks you to change the tour's plan as the reviewer's words say, such as add, drop, merge or split stops. Carry out replan {PlanChangeRule} {DepthRule} In a replan, a skip says that the reviewer does not need that stop. Keep a skipped stop unless the reviewer's words drop it, because the reviewer can take the skip back. {PlanResolveRule}",
      TourAction.Reorder => $"The action \"reorder\" asks you to put the plan's stops in the reviewer's proposed order, the action's stopIds, which name every stop of the plan once. The reviewer's words, when there are any, say why. Carry out reorder {PlanChangeRule} {ProposedOrderRule} {DepthRule} {RedoRule} {PlanResolveRule}",
      TourAction.Summarize => "The action \"summarize\" asks you to write a synthesis of the review, which the reviewer requested from the summary page. The reviewer's words, when there are any, say what to focus on. Carry out summarize with write_synthesis. Read the review summary with read_summary and the tour with read_state, then write a synthesis of what the review decided, what worries the reviewer and what stays open, with ref: links to the code each point is about. Name the open questions and the concerns that no answer settled yet. The summary's typedThreads are the reviewer's own findings: questions that the reviewer sent and filed as a decision, a concern or an open question. List each in the synthesis as the reviewer's, never in your own voice. Write the synthesis last, after any other change to the tour. Your own writes, such as start_thread or put_stop, change the summary too, so read the summary again after such a write. Pass as basedOn the summaryFingerprint of the summary that you read, so the summary page can tell when the review or the tour's pin changed after that read. The summary page shows the synthesis, and each one replaces the last, so write the whole synthesis each time. Then resolve the action with a reply of one sentence that says you wrote it, never the synthesis itself.",
      TourAction.FollowUp.DraftWorkItems => $"The action \"draftWorkItems\" asks you to propose work items for the review's decisions, which the reviewer requested from the summary page. Its decisions list the decisions that the reviewer had sent when they asked, {AnswerItemFields}, and its decisionThreads list the threads that the reviewer sent and filed as a decision, {ThreadItemFields}. {FollowUpItemsRule} {ThreadItemsRule} {ThreadItemsNowRule} A resolved item of decisionThreads is a decision that the reviewer settled in its thread, and it can still need a work item. Read each decision with its check's question, and each decision thread at its anchor, and propose to the user in the terminal a work item for each decision or decision thread that needs follow-up work, such as a change that the reviewer decided on and the pull request does not make: its title, what it holds, and the decision it comes from. Say which decisions need none. Create a work item with your own tools, such as the hosting platform's CLI, only after the user approves it in the terminal, and create none that the user did not approve. {NoLotsenActionRule} Then resolve the action with a reply of one sentence that names each work item you created, or says that the user approved none.",
      TourAction.FollowUp.WalkOpenQuestions => $"The action \"walkOpenQuestions\" asks you to walk the reviewer through the open questions, which the reviewer requested from the summary page. Its openQuestions list the open questions that the reviewer had sent when they asked, {AnswerItemFields}, and its openQuestionThreads list the threads that the reviewer sent and filed as an open question, {ThreadItemFields}. {FollowUpItemsRule} {ThreadItemsRule} {ThreadItemsNowRule} lotsen also leaves out an open question thread that the reviewer resolved, because the reviewer settled it there. Walk the reviewer through them in the UI, not in the terminal: start one thread with start_thread for each item of openQuestions, and leave the decision to the reviewer, who settles each one in its thread. Anchor the thread on the check's question: stopText with the item's stopId, field checks[<checkId>].question and the item's question as text. When the item has no question, because the stop's content no longer holds the check, anchor it on the stop with stop, and when it has no stopTitle, because the plan dropped the stop, on the tour. An item of openQuestionThreads already is a thread: add to that thread with add_thread_message and its threadId instead of starting a new one. add_thread_message refuses a thread whose last question still waits for your reply, so answer that question with reply instead. Write in each thread what would help the decision, such as the code it rests on with ref: links and the trade-offs, then your recommendation and why. {NoLotsenActionRule} Then resolve the action with a reply of one sentence that says how many threads you started and how many you added to. The reviewer's answers come as follow-ups in those threads: reply to each as to any question.",
      TourAction.FollowUp.DraftComments => $"The action \"draftComments\" asks you to draft comments for the pull request's author from the concerns, which the reviewer requested from the summary page. Its concerns list the concerns that the reviewer had sent when they asked, {AnswerItemFields}, its concernThreads list the threads that the reviewer sent and filed as a concern, {ThreadItemFields}, and its risks the risk stops that you wrote and the reviewer did not skip, a stale one included, which the summary counts as concerns, each with its stopId, title and scenario. lotsen omits a pending risk stop, which has no scenario, and a risk stop that the reviewer skipped, because you post a comment in the reviewer's name. {FollowUpItemsRule} {ThreadItemsRule} {ThreadItemsNowRule} lotsen also leaves out a concern thread that the reviewer resolved, because the discussion settled it. A risk stop with staleOn in read_state still points at an earlier iteration, so read its code again at the tour's pin before you draft its comment. Draft a comment for the pull request's author for each concern, concern thread or risk that needs one: what worries the reviewer, where in the code, such as a concern thread's anchor, and what would settle it, written to the author. Say which concerns need none. Return the drafts as text in your reply, one after another, each naming its file and lines: this reply holds the drafts, so it is longer than a short reply. Then ask the user in the terminal which drafts to post. Post a comment only with your own tools, such as the hosting platform's CLI, after the user approves it in the terminal, and post it as the user approved it. {NoLotsenActionRule}",
   };

   /// <summary>Returns why the summary holds no item for the follow-up action, which the summary page also shows beside its disabled button, or null when it holds one.</summary>
   /// <remarks>
   /// It reads the items that a request would store, so a group that holds only sent typed threads has an item to hand. A group whose sent typed threads the request omits by their state
   /// gets its own reason, which tells the reviewer to reopen one.
   /// </remarks>
   /// <param name="request">The follow-up action.</param>
   /// <param name="agentSummary">The summary that an agent session reads, which the items come from.</param>
   public static string? NothingToHand(ActionRequest.FollowUp request, ReviewSummary agentSummary) => ItemsOf(request, agentSummary) switch
   {
      TourAction.FollowUp.DraftWorkItems work when work.Decisions.Count == 0 && work.DecisionThreads is null =>
         agentSummary.TypedThreads?.Decisions.Count > 0 ? DecisionThreadsOutdated : NoDecisionSent,
      TourAction.FollowUp.WalkOpenQuestions walk when walk.OpenQuestions.Count == 0 && walk.OpenQuestionThreads is null =>
         agentSummary.TypedThreads?.OpenQuestions.Count > 0 ? OpenQuestionThreadsSettled : NoOpenQuestionSent,
      TourAction.FollowUp.DraftComments drafts when drafts.Concerns.Count == 0 && drafts.ConcernThreads is null && drafts.Risks.Count == 0 =>
         agentSummary.TypedThreads?.Concerns.Count > 0 ? ConcernThreadsSettled : NoConcernSent,
      TourAction.FollowUp.DraftWorkItems or TourAction.FollowUp.WalkOpenQuestions or TourAction.FollowUp.DraftComments => null,
   };

   // The admit column, over each kind of request, after the state columns of Offer.
   private static OperationResult<Accepted> Admission(Tour tour, ActionRequest request, string text) => Offer(tour, request) is { } refused
      ? refused
      : request switch
      {
         ActionRequest.Explain explain => OnStop(tour, explain.Target, version => new TourAction.Explain(version)),
         ActionRequest.Revise revise => NoWords(text, "a revise needs the reviewer's words: say what should change in the stop.") is { } noWords ? noWords : OnStop(tour, revise.Target, version => new TourAction.Revise(version)),
         ActionRequest.Replan => NoWords(text, "a replan needs the reviewer's words: say how the plan should change.") is { } noWords ? noWords : OnTour(new TourAction.Replan()),
         ActionRequest.Reorder reorder => BadOrder(tour, reorder.StopIds) is { } badOrder ? badOrder : OnTour(new TourAction.Reorder(reorder.StopIds)),
         ActionRequest.Summarize => OnTour(new TourAction.Summarize()),
         ActionRequest.FollowUp followUp => OnTour(ItemsOf(followUp, AgentView.Summary(tour))),
      };

   // The follow-up action with the items of its group in the summary: the sent answers and the sent threads of its type.
   private static TourAction.FollowUp ItemsOf(ActionRequest.FollowUp request, ReviewSummary agentSummary) => request switch
   {
      ActionRequest.FollowUp.DraftWorkItems => new TourAction.FollowUp.DraftWorkItems(
         FollowUpAnswer.All(agentSummary.Answers.Decisions),
         FollowUpThread.Handed(agentSummary.TypedThreads?.Decisions, HandsResolvedThreads(TourAction.DraftWorkItemsKind))),
      ActionRequest.FollowUp.WalkOpenQuestions => new TourAction.FollowUp.WalkOpenQuestions(
         FollowUpAnswer.All(agentSummary.Answers.OpenQuestions),
         FollowUpThread.Handed(agentSummary.TypedThreads?.OpenQuestions, HandsResolvedThreads(TourAction.WalkOpenQuestionsKind))),
      ActionRequest.FollowUp.DraftComments => new TourAction.FollowUp.DraftComments(
         FollowUpAnswer.All(agentSummary.Answers.Concerns),
         RisksToHand(agentSummary),
         FollowUpThread.Handed(agentSummary.TypedThreads?.Concerns, HandsResolvedThreads(TourAction.DraftCommentsKind))),
   };

   // A settled decision can still need a work item, but a resolved concern or open question is settled, so Claude neither comments on it nor writes in its thread.
   private static bool HandsResolvedThreads(string kind) => kind == TourAction.DraftWorkItemsKind;

   /// <summary>Returns why a follow-up action cannot go again with the threads that it stored, or null when it can.</summary>
   /// <remarks>It refuses the first stored thread that no longer applies, as <see cref="LapseOf"/> decides, so the rules that its howToAct serves hold.</remarks>
   private static OperationError? CheckThreadsAgain(Tour tour, TourAction.FollowUp action)
   {
      var (list, threads, label, item) = ThreadListOf(action);
      foreach (var (thread, index) in (threads ?? []).Select((thread, index) => (thread, index)))
      {
         var field = ActionField.Then(list).At(index);
         switch (LapseOf(tour, thread, action.Kind))
         {
            case ThreadItemLapse.Deleted:
               return OperationError.Invalid(field, $"A thread that this request hands to Claude is deleted since you asked. {AskAgain(label)}.");
            case ThreadItemLapse.Outdated:
               return OperationError.Invalid(field, $"A thread that this request hands to Claude is outdated since you asked. {AskAgain(label)}, which leaves out outdated threads.");
            case ThreadItemLapse.Resolved:
               return OperationError.Invalid(field, $"You resolved this {item} since you asked, so it is settled. {AskAgain(label)}, which leaves out the {item}s you resolved.");
         }
      }

      return null;
   }

   // The action's thread items with the JSON name of their list, the summary page's label of the action and the name of one item's type.
   private static (string List, IReadOnlyList<FollowUpThread>? Threads, string Label, string Item) ThreadListOf(TourAction.FollowUp action) => action switch
   {
      TourAction.FollowUp.DraftWorkItems work => ("decisionThreads", work.DecisionThreads, "Draft work items", "decision"),
      TourAction.FollowUp.WalkOpenQuestions walk => ("openQuestionThreads", walk.OpenQuestionThreads, "Walk through open questions", "open question"),
      TourAction.FollowUp.DraftComments drafts => ("concernThreads", drafts.ConcernThreads, "Draft PR comments", "concern"),
   };

   /// <summary>Returns why a follow-up action of the kind would no longer hand the stored thread, or null while it would.</summary>
   /// <remarks>
   /// A deleted or outdated thread no longer applies, and for walkOpenQuestions and draftComments neither does one that the reviewer resolved since. A thread whose type changed, that the reviewer
   /// reopened, or that the reviewer resolved for draftWorkItems still applies, with the type and state that the action stored.
   /// </remarks>
   private static ThreadItemLapse? LapseOf(Tour tour, FollowUpThread thread, string kind)
   {
      var state = tour.Threads.Find(thread.ThreadId)?.State;
      return state switch
      {
         null => ThreadItemLapse.Deleted,
         QuestionThreadState.Outdated => ThreadItemLapse.Outdated,
         QuestionThreadState.Resolved when !HandsResolvedThreads(kind) => ThreadItemLapse.Resolved,
         QuestionThreadState.Open or QuestionThreadState.Resolved => null,
         _ => throw new UnreachableException($"The thread state {state} has no rule for a follow-up action."),
      };
   }

   // A new request of the kind is refused while this one waits, so the advice names the cancel first.
   private static string AskAgain(string label) => $"Cancel this request if it still waits, then click \"{label}\" again on the summary page";

   /// <summary>Returns the risk stops that a draftComments action hands to Claude.</summary>
   /// <returns>The summary's risk stops that have a scenario, because Claude wrote them, and that the reviewer did not skip, in plan order, a stale one included.</returns>
   /// <remarks>A pending risk stop has only a title to comment on. Claude posts a PR comment in the reviewer's name, so the action hands no stop that the reviewer skipped.</remarks>
   private static IReadOnlyList<FollowUpRisk> RisksToHand(ReviewSummary summary) =>
      [.. summary.Risks
         .Select(risk => risk.Scenario is { } scenario && !summary.SkippedStops.Any(skipped => skipped.StopId == risk.StopId) ? new FollowUpRisk(risk.StopId, risk.Title, scenario) : null)
         .OfType<FollowUpRisk>()];

   private static OperationResult<Accepted> OnTour(TourAction action) => new Accepted(new ThreadAnchor.WholeTour(), action);

   // The stop's content is what the session explains or revises, so a pending stop has nothing to act on. A stop text target already refuses a pending stop.
   private static OperationError? OfferOnStop(Tour tour, StopTarget target, string kind)
   {
      var resolved = StopTargets.Resolve(tour, target, TargetField);
      if (resolved.Value is not { } anchor)
      {
         return resolved.Error!;
      }

      return anchor is ThreadAnchor.WholeStop stop && tour.FindStop(stop.StopId) is not Stop.Written
         ? OperationError.Invalid(TargetField.Then("stopId"), $"stop '{stop.StopId}' has no content yet, so Claude has nothing to {kind}. Wait until Claude writes it.")
         : null;
   }

   // Builds the action on the target that OfferOnStop took, stamped with a whole stop's current version.
   private static OperationResult<Accepted> OnStop(Tour tour, StopTarget target, Func<int?, TourAction> action)
   {
      var anchor = StopTargets.Resolve(tour, target, TargetField).Value!;
      return anchor is ThreadAnchor.WholeStop stop ? new Accepted(stop, action(((Stop.Written)tour.FindStop(stop.StopId)!).Version)) : new Accepted(anchor, action(null));
   }

   private static OperationError? NoPlan(Tour tour, string kind) =>
      tour.Plan is null ? OperationError.Invalid(ActionField, $"the tour has no plan yet, so Claude has no plan to {kind}. Wait until Claude sets the plan.") : null;

   private static OperationError? NoReview(Tour tour) =>
      tour.Plan is null ? OperationError.Invalid(ActionField, "the tour has no plan yet, so Claude has no review to summarize. Wait until Claude sets the plan.") : null;

   private static OperationError? NoWords(string text, string reason) => text.Length == 0 ? OperationError.Invalid("text", reason) : null;

   private static OperationError? BadOrder(Tour tour, IReadOnlyList<string> stopIds) =>
      PlanRules.CheckOrder(tour.Plan, stopIds) is { } fault ? OperationError.Invalid(fault.Under(ActionField.Then("stopIds"))) : null;

   // A stop text anchor holds its own version, which a revision moves only when the anchor's field stays as it was. Admit stores these actions on no other anchor, but a tour file can hold one.
   private static OperationError? CheckStopAgain(Tour tour, ThreadAnchor anchor, string kind, int? stopVersion)
   {
      var (stopId, selectedIn) = anchor switch
      {
         ThreadAnchor.WholeStop stop => (stop.StopId, (int?)null),
         ThreadAnchor.StopText selected => (selected.StopId, selected.Version),
         ThreadAnchor.CodeRange or ThreadAnchor.WholeFile or ThreadAnchor.WholeTour => ((string?)null, (int?)null),
      };
      if (stopId is null)
      {
         return OperationError.Invalid(AnchorField.Then("kind"), $"the {kind} action takes a stop or text in a stop. Request it from the Stop panel or from a selection in a stop's text.");
      }

      return tour.FindStop(stopId) switch
      {
         null => OperationError.Invalid(AnchorField.Then("stopId"), $"Claude dropped this stop from the plan since you asked, so there is no stop to {kind}. Ask again from the Stop panel of a stop in the current plan."),
         Stop.Pending => OperationError.Invalid(AnchorField.Then("stopId"), $"stop '{stopId}' has no content yet, so Claude has nothing to {kind}. Wait until Claude writes it."),
         Stop.Written written when selectedIn is { } version && version != written.Version =>
            OperationError.Invalid(AnchorField.Then("version"), "Claude revised this stop since you asked, and the text you selected is in an earlier version. Read the new version, and select the text again if it still needs it."),
         Stop.Written written when stopVersion is { } asked && asked != written.Version =>
            OperationError.Invalid(ActionField.Then("stopVersion"), "Claude revised this stop since you asked. Read the new version, and ask again from the Stop panel if it still needs it."),
         Stop.Written => null,
      };
   }

   /// <summary>A new request's anchor and stored action, which only <see cref="Admit"/> builds, so no thread holds an action that skipped its rules.</summary>
   internal sealed class Admitted
   {
      private Admitted(ThreadAnchor anchor, TourAction action)
      {
         Anchor = anchor;
         Action = action;
      }

      /// <summary>Where the action applies: the target's stop or stop text for explain and revise, the whole tour for every other kind.</summary>
      public ThreadAnchor Anchor { get; }

      /// <summary>The action as the thread stores it.</summary>
      public TourAction Action { get; }

      // Only this type can call the constructor, and this method runs the admit column first.
      internal static OperationResult<Admitted> Of(Tour tour, ActionRequest request, string text)
      {
         var admission = Admission(tour, request, text);
         return admission.Value is { } admitted ? new Admitted(admitted.Anchor, admitted.Action) : admission.Error!;
      }
   }

   private sealed record Accepted(ThreadAnchor Anchor, TourAction Action);
}
