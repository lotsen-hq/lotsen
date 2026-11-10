using Lotsen.Git;
using Lotsen.Pinning;
using Lotsen.Sessions;

namespace Lotsen.Tours;

/// <summary>The table that decides every change of a tour's threads.</summary>
/// <remarks>
/// <para>
/// One <c>Decide</c> holds the cell of each command for each kind of thread it names and each waiting phase, and the other the cell of each fact for each thread. <see cref="Step"/> decides
/// what each command and fact does to a question's requests. <see cref="Apply"/>, <see cref="Check"/> and <see cref="Settle(Tour, ThreadFact, TourIds)"/> all go
/// through them.
/// </para>
/// <para>
/// The state rules are these. A question that the reviewer sends opens its thread: a new question, a follow-up, a re-send or a sent note. A reply or an agent message opens an outdated thread and
/// leaves the other states as they are, unless <see cref="IsLeftBehind"/> holds for the thread. Only an open thread that does not wait becomes outdated, so the reviewer's resolve stays and a
/// question that waits for its reply stays in view. The reviewer may resolve and open any thread, a note included.
/// </para>
/// <para>
/// The request rules are these. A re-send cancels the question's pending request and adds a new one. A reply resolves the first request of the question that went to the replying session,
/// whatever its state, and cancels every other pending request. The reply goes at the end of the thread, after any follow-up of its question. The end of a binding marks every pending request
/// unanswered. A session's fetch marks its requests received once.
/// </para>
/// <para>
/// The waiting rule is this. A request, a report, a follow-up and a re-send that would make a thread of a <see cref="WaitGroup"/> wait are refused while another thread that holds back that
/// group waits, as <see cref="CheckWaits"/> says. A re-send of a thread's first question waits for the thread's own group, as a new request or report does. A follow-up and a re-send of a
/// follow-up wait for the group that <see cref="FollowUpGroupOf"/> gives.
/// </para>
/// </remarks>
internal sealed partial class TourThreads
{
   /// <summary>Returns the write that the command makes to the tour's threads, or the refusal.</summary>
   /// <param name="tour">The latest stored tour, or that tour with the write's other fields changed.</param>
   /// <param name="command">The command.</param>
   /// <param name="now">The time of the write, for the timestamps that the change stores.</param>
   /// <param name="ids">Where new IDs come from.</param>
   public static OperationResult<ThreadWrite> Apply(Tour tour, ThreadCommand command, DateTimeOffset now, TourIds ids)
   {
      var draft = new Draft(tour, now, ids);
      return Run(draft, command) is { } refused ? refused : draft.Written(named: command is not ThreadCommand.SendNotes, carried: false);
   }

   /// <summary>Returns the refusal that <see cref="Apply"/> gives the command on the tour, or null when it applies. It builds no tour, and checks no input.</summary>
   public static OperationError? Check(Tour tour, ThreadCommand command) => Run(new Draft(tour, default, TourIds.Random), command);

   /// <summary>Returns the write that the fact makes to the tour's threads, in tour order.</summary>
   /// <param name="tour">The latest stored tour, or that tour with the write's other fields changed.</param>
   /// <param name="fact">What happened to the tour.</param>
   /// <param name="ids">Where the IDs of a chained command come from, or null for random ones.</param>
   /// <returns>The write. A <see cref="ThreadFact.StopRevised"/> write is carried, because the revision's own event carries its threads.</returns>
   public static ThreadWrite Settle(Tour tour, ThreadFact fact, TourIds? ids = null)
   {
      var draft = new Draft(tour, default, ids ?? TourIds.Random);
      foreach (var thread in tour.Threads)
      {
         draft.Take(Decide(thread, fact), thread);
      }

      return draft.Written(named: false, carried: fact is ThreadFact.StopRevised);
   }

   /// <summary>Returns a write that changes no thread, for a write that changes only the tour's other fields.</summary>
   public static ThreadWrite Unchanged(Tour tour) => new Draft(tour, default, TourIds.Random).Written(named: false, carried: false);

   /// <summary>Returns whether the thread's anchor stays where a revision or an iteration update left it.</summary>
   /// <returns>
   /// True for stop text on an earlier version of its stop, and for a code or file anchor of a pinned tour on a commit other than the pin's commit of its side. An iteration update gives a moved
   /// anchor the new pin's commit, so only an anchor that it left stale is off the pin.
   /// </returns>
   /// <remarks>A reply or an agent message keeps such a thread outdated, because neither brings the anchor to the stop's current text or to the pinned code.</remarks>
   public static bool IsLeftBehind(Tour tour, QuestionThread thread) => thread.Anchor switch
   {
      ThreadAnchor.StopText selected => tour.FindStop(selected.StopId) is Stop.Written stop && selected.Version < stop.Version,
      ThreadAnchor.CodeRange code => IsOffPin(tour, code.Side, code.Commit),
      ThreadAnchor.WholeFile file => IsOffPin(tour, file.Side, file.Commit),
      _ => false,
   };

   private static bool IsOffPin(Tour tour, FileSide side, CommitId commit) => tour.Pin is TourPin.Pinned pin && commit != pin.CommitOf(side);

   private static OperationError? Run(Draft draft, ThreadCommand command)
   {
      var threads = draft.Tour.Threads;
      if (command is ThreadCommand.SendNotes batch)
      {
         foreach (var threadId in batch.ThreadIds)
         {
            if (draft.Take(Decide(draft, new Named(threadId, threads.Find(threadId), null), command), threads.Find(threadId)) is { } refused)
            {
               return refused;
            }
         }

         return null;
      }

      var named = command switch
      {
         ThreadCommand.EditNote edit => ThreadNamed(threads, edit.ThreadId),
         ThreadCommand.DiscardNote discard => ThreadNamed(threads, discard.ThreadId),
         ThreadCommand.FollowUp followUp => ThreadNamed(threads, followUp.ThreadId),
         ThreadCommand.SetState set => ThreadNamed(threads, set.ThreadId),
         ThreadCommand.AgentWrote wrote => ThreadNamed(threads, wrote.ThreadId),
         ThreadCommand.Outdate outdate => ThreadNamed(threads, outdate.ThreadId),
         ThreadCommand.Delete delete => ThreadNamed(threads, delete.ThreadId),
         ThreadCommand.Remove remove => ThreadNamed(threads, remove.ThreadId),
         ThreadCommand.Resend resend => QuestionNamed(threads, resend.QuestionId),
         ThreadCommand.Cancel cancel => QuestionNamed(threads, cancel.QuestionId),
         ThreadCommand.Reply reply => QuestionNamed(threads, reply.QuestionId),
         ThreadCommand.MarkReceived mark => QuestionNamed(threads, mark.QuestionId),
         ThreadCommand.Ask or ThreadCommand.Request or ThreadCommand.Report or ThreadCommand.Start or ThreadCommand.SaveNote or ThreadCommand.SendNotes => new Named("", null, null),
      };
      return draft.Take(Decide(draft, named, command), named.Thread);
   }

   private static Named ThreadNamed(TourThreads threads, string threadId) => new(threadId, threads.Find(threadId), null);

   private static Named QuestionNamed(TourThreads threads, string questionId) => threads.FindQuestion(questionId) is var (thread, question) ? new(questionId, thread, question) : new(questionId, null, null);

   // The cells of the commands. A command that names a question finds a sent thread or none, so its cells on a note refuse as for a missing question.
   private static Decision Decide(Draft draft, Named named, ThreadCommand command)
   {
      var tour = draft.Tour;
      var question = named.Question;
      return (named.Thread, command) switch
      {
         (_, ThreadCommand.Ask ask) => new Decision.Send(QuestionThread.Sent.Asked.Open(draft.NewId(), ask.Anchor, draft.NewQuestion(ask.Text, ask.StopId, ask.Recipient), null), ask.Recipient),
         (_, ThreadCommand.Request request) when CheckWaits(tour, ActionRules.WaitsAlone(request.Admitted.Action), null, "action") is { } waits => new Decision.Refuse(waits),
         (_, ThreadCommand.Request request) => new Decision.Send(
            QuestionThread.Sent.Asked.Open(draft.NewId(), request.Admitted.Anchor, draft.NewQuestion(request.Text, request.StopId, request.Recipient), request.Admitted.Action),
            request.Recipient),
         (_, ThreadCommand.Report report) when CheckWaits(tour, DecisionRules.WaitsAlone(report.Decision), null, report.WaitField) is { } waits => new Decision.Refuse(waits),
         (_, ThreadCommand.Report report) => new Decision.Send(QuestionThread.Sent.Decided.Open(draft.NewId(), draft.NewQuestion(report.Text, report.StopId, report.Recipient), report.Decision), report.Recipient),
         (_, ThreadCommand.Start start) => new Decision.Put(QuestionThread.Sent.Started.Open(draft.NewId(), start.Anchor, ThreadMessage.AgentMessage.Written(draft.NewId(), draft.Now, start.Author.Id, start.Text))),
         (_, ThreadCommand.SaveNote save) => new Decision.Put(QuestionThread.Note.Saved(draft.NewId(), save.Anchor, save.Text, draft.Now, save.StopId)),

         (null, ThreadCommand.EditNote or ThreadCommand.DiscardNote or ThreadCommand.SendNotes or ThreadCommand.FollowUp or ThreadCommand.SetState or ThreadCommand.Remove) =>
            new Decision.Refuse(OperationError.ThreadNotFound(tour.Id, named.Id)),
         (null or QuestionThread.Note, ThreadCommand.AgentWrote or ThreadCommand.Outdate or ThreadCommand.Delete) => new Decision.Refuse(OperationError.ThreadNotFound(tour.Id, named.Id)),
         (null or QuestionThread.Note, ThreadCommand.Resend or ThreadCommand.Cancel or ThreadCommand.Reply or ThreadCommand.MarkReceived) =>
            new Decision.Refuse(OperationError.QuestionNotFound(tour.Id, named.Id)),

         (QuestionThread.Note note, ThreadCommand.EditNote edit) when note.Text == edit.Text => new Decision.Keep(),
         (QuestionThread.Note note, ThreadCommand.EditNote edit) => new Decision.Put(note.Edited(edit.Text, draft.Now)),
         (QuestionThread.Note, ThreadCommand.DiscardNote) => new Decision.Remove(),
         (QuestionThread.Note note, ThreadCommand.SendNotes send) => new Decision.Send(QuestionThread.Sent.Asked.From(note, draft.NewQuestion(note.Text, note.StopId, send.Recipient)), send.Recipient),
         (QuestionThread.Sent sent, ThreadCommand.EditNote or ThreadCommand.DiscardNote or ThreadCommand.SendNotes) => new Decision.Refuse(OperationError.NoteSent(sent.Id)),

         (QuestionThread.Note note, ThreadCommand.FollowUp) => new Decision.Refuse(OperationError.ThreadIsNote(note.Id)),
         (QuestionThread.Sent { Waiting: { } waiting } sent, ThreadCommand.FollowUp) => new Decision.Refuse(OperationError.QuestionWaiting(sent.Id, waiting.Id)),
         (QuestionThread.Sent sent, ThreadCommand.FollowUp) when CheckWaits(tour, TourThreads.FollowUpGroupOf(sent), sent, "threadId") is { } waits => new Decision.Refuse(waits),
         (QuestionThread.Sent sent, ThreadCommand.FollowUp followUp) =>
            new Decision.Send(sent.With(QuestionThreadState.Open, [.. sent.Messages, draft.NewQuestion(followUp.Text, followUp.StopId, followUp.Recipient)]), followUp.Recipient),

         (QuestionThread thread, ThreadCommand.SetState set) when thread.State == StateOf(set.State) => new Decision.Keep(),
         (QuestionThread thread, ThreadCommand.SetState set) => new Decision.Put(thread.WithState(StateOf(set.State))),

         (QuestionThread.Sent { Waiting: { } waiting } sent, ThreadCommand.AgentWrote or ThreadCommand.Outdate or ThreadCommand.Delete) =>
            new Decision.Refuse(OperationError.QuestionWaitingForReply(sent.Id, waiting.Id)),
         (QuestionThread.Sent sent, ThreadCommand.AgentWrote wrote) =>
            new Decision.Put(sent.With(AfterAgentMessage(tour, sent), [.. sent.Messages, ThreadMessage.AgentMessage.Written(draft.NewId(), draft.Now, wrote.Author.Id, wrote.Text)])),
         (QuestionThread.Sent { State: QuestionThreadState.Open } sent, ThreadCommand.Outdate) => new Decision.Put(sent.WithState(QuestionThreadState.Outdated)),
         (QuestionThread.Sent, ThreadCommand.Outdate) => new Decision.Keep(),
         (QuestionThread.Sent, ThreadCommand.Delete) => new Decision.Remove(),
         (QuestionThread.Sent { Waiting: { } waiting } sent, ThreadCommand.Remove) => new Decision.Refuse(OperationError.QuestionWaitingToRemove(sent.Id, waiting.Id)),
         (QuestionThread, ThreadCommand.Remove) => new Decision.Remove(),

         (QuestionThread.Sent, ThreadCommand.Resend or ThreadCommand.Cancel or ThreadCommand.Reply) when question!.Answered is { } answered =>
            new Decision.Refuse(OperationError.QuestionAnswered(question.Id, answered)),
         (QuestionThread.Sent sent, ThreadCommand.Resend) when sent.LastQuestion?.Id != question!.Id => new Decision.Refuse(OperationError.QuestionFollowedUp(question.Id, sent.Id)),
         (QuestionThread.Sent sent, ThreadCommand.Resend) when CheckResend(tour, sent, question!) is { } refused => new Decision.Refuse(refused),
         (QuestionThread.Sent sent, ThreadCommand.Resend resend) =>
            new Decision.Send(Stepped(sent, question!, new RequestStep.Superseded(), QuestionRequest.Pending(draft.NewId(), resend.Recipient.Id, draft.Now), QuestionThreadState.Open), resend.Recipient),
         (QuestionThread.Sent, ThreadCommand.Cancel) when question!.Requests[^1] is { State: not RequestState.Pending } last => new Decision.Refuse(OperationError.RequestNotPending(question.Id, last)),
         (QuestionThread.Sent sent, ThreadCommand.Cancel) => new Decision.Put(Stepped(sent, question!, new RequestStep.Withdrawn(), null, sent.State)),
         (QuestionThread.Sent, ThreadCommand.Reply reply) when FirstRequestTo(question!, reply.Author) < 0 => new Decision.Refuse(OperationError.QuestionNotSentToSession(tour.Id, question!.Id)),
         (QuestionThread.Sent sent, ThreadCommand.Reply reply) => new Decision.Put(Replied(draft, sent, question!, reply)),
         (QuestionThread.Sent, ThreadCommand.MarkReceived mark) when !question!.IsUnreceivedBy(mark.Session.Id) => new Decision.Keep(),
         (QuestionThread.Sent sent, ThreadCommand.MarkReceived mark) => new Decision.Put(Stepped(sent, question!, new RequestStep.Receipt(mark.Session.Id, draft.Now), null, sent.State)),
      };
   }

   // The cells of the facts.
   private static Decision Decide(QuestionThread thread, ThreadFact fact) => (thread, fact) switch
   {
      ({ Anchor: ThreadAnchor.StopText selected }, ThreadFact.StopRevised revised) when selected.StopId == revised.Replaced.Id && selected.Version == revised.Replaced.Version =>
         StopProse.Read(revised.Replaced.Content, selected.Field) is { } before && before == StopProse.Read(revised.Written.Content, selected.Field)
            ? new Decision.Put(thread.WithAnchor(selected with { Version = revised.Written.Version }))
            : Outdated(thread),
      (_, ThreadFact.StopRevised) => new Decision.Keep(),
      (_, ThreadFact.Repinned repinned) => repinned.Anchors.FirstOrDefault(anchor => anchor.ThreadId == thread.Id)?.Outcome switch
      {
         Remapped<ThreadAnchor>.Moved { To: var to } when to != thread.Anchor => new Decision.Put(thread.WithAnchor(to)),
         Remapped<ThreadAnchor>.Stale => Outdated(thread),
         _ => new Decision.Keep(),
      },
      (QuestionThread.Sent sent, ThreadFact.Released) when sent.Messages.Any(message => message is ThreadMessage.Question { IsWaiting: true }) => new Decision.Put(Released(sent)),
      (_, ThreadFact.Released) => new Decision.Keep(),
   };

   // What a command or a fact does to one request of a question, at its index among the question's requests.
   private static QuestionRequest Step(QuestionRequest request, int index, int count, RequestStep step) => (request.State, step) switch
   {
      (RequestState.Pending, RequestStep.Superseded) => request.Canceled(),
      (_, RequestStep.Superseded) => request,
      (RequestState.Pending, RequestStep.Withdrawn) when index == count - 1 => request.Canceled(),
      (_, RequestStep.Withdrawn) => request,
      (_, RequestStep.Answered answered) when index == answered.Index => request.Resolved(answered.Resolution),
      (RequestState.Pending, RequestStep.Answered) => request.Canceled(),
      (_, RequestStep.Answered) => request,
      (RequestState.Pending, RequestStep.Released) => request.Unanswered(),
      (_, RequestStep.Released) => request,
      (_, RequestStep.Receipt receipt) when request.SessionId == receipt.SessionId && request.ReceivedAt is null => request.Received(receipt.Now),
      (_, RequestStep.Receipt) => request,
   };

   // Only an open thread that does not wait becomes outdated, so the reviewer's resolve stays and a waiting question stays in view.
   private static Decision Outdated(QuestionThread thread) =>
      thread is { State: QuestionThreadState.Open } and not QuestionThread.Sent { Waiting: not null } ? new Decision.Put(thread.WithState(QuestionThreadState.Outdated)) : new Decision.Keep();

   private static QuestionThreadState AfterAgentMessage(Tour tour, QuestionThread thread) =>
      thread.State == QuestionThreadState.Outdated && !IsLeftBehind(tour, thread) ? QuestionThreadState.Open : thread.State;

   /// <summary>Returns why a command cannot make a thread of the wait group wait, or null when it can: the waiting rule of every cell that sends a question.</summary>
   /// <param name="tour">The tour.</param>
   /// <param name="group">The group of the thread that would wait, or null for a thread outside every group, which nothing holds back.</param>
   /// <param name="thread">The thread that would wait, which never holds itself back, or null for a new one.</param>
   /// <param name="field">The input's field that the refusal names.</param>
   public static OperationError? CheckWaits(Tour tour, WaitGroup? group, QuestionThread? thread, FieldPath field) =>
      group is { } waitGroup && tour.Threads.Waiting(waitGroup, except: thread) is { } waiting ? OperationError.ActionWaiting(waiting, field) : null;

   // A tour action or a reviewer decision goes again only with the thread's first question, which holds it. A re-send of that question waits like a new request, and of a later one as a follow-up.
   private static OperationError? CheckResend(Tour tour, QuestionThread.Sent thread, ThreadMessage.Question question) =>
      thread.Messages[0].Id != question.Id
         ? CheckWaits(tour, TourThreads.FollowUpGroupOf(thread), thread, "questionId")
         : thread switch
         {
            QuestionThread.Sent.Asked asked => asked.Action is { } action ? ActionRules.CheckResend(tour, asked.Anchor, action) : null,
            QuestionThread.Sent.Decided decided => DecisionRules.CheckResend(tour, decided),
            QuestionThread.Sent.Started => null,
         }
         ?? CheckWaits(tour, WaitGroupOf(thread), thread, "questionId");

   private static int FirstRequestTo(ThreadMessage.Question question, Recipient session)
   {
      for (var i = 0; i < question.Requests.Count; i++)
      {
         if (question.Requests[i].SessionId == session.Id)
         {
            return i;
         }
      }

      return -1;
   }

   private static QuestionThread.Sent Replied(Draft draft, QuestionThread.Sent thread, ThreadMessage.Question question, ThreadCommand.Reply reply)
   {
      var resolution = reply.Resolution switch
      {
         ReplyResolution.Done => RequestState.Done,
         ReplyResolution.NeedsAnswer => RequestState.NeedsAnswer,
         _ => RequestState.Declined,
      };
      var resolved = StepQuestion(question, new RequestStep.Answered(FirstRequestTo(question, reply.Author), resolution), null);
      var answer = ThreadMessage.Question.Reply.For(resolved, draft.NewId(), draft.Now, reply.Text);
      return thread.With(AfterAgentMessage(draft.Tour, thread), [.. thread.Messages.Select(message => ReferenceEquals(message, question) ? resolved : message), answer]);
   }

   private static QuestionThread.Sent Released(QuestionThread.Sent thread) => thread.With(
      thread.State,
      [.. thread.Messages.Select(message => message is ThreadMessage.Question { IsWaiting: true } question ? StepQuestion(question, new RequestStep.Released(), null) : message)]);

   private static QuestionThread.Sent Stepped(QuestionThread.Sent thread, ThreadMessage.Question question, RequestStep step, QuestionRequest? added, QuestionThreadState state) =>
      thread.With(state, [.. thread.Messages.Select(message => ReferenceEquals(message, question) ? StepQuestion(question, step, added) : message)]);

   private static ThreadMessage.Question StepQuestion(ThreadMessage.Question question, RequestStep step, QuestionRequest? added)
   {
      var requests = question.Requests.Select((request, i) => Step(request, i, question.Requests.Count, step));
      return question.WithRequests(added is null ? [.. requests] : [.. requests, added]);
   }

   private static QuestionThreadState StateOf(ReviewerThreadState state) => state == ReviewerThreadState.Resolved ? QuestionThreadState.Resolved : QuestionThreadState.Open;

   // The ID that a command names, with the thread and question that the tour holds under it.
   private sealed record Named(string Id, QuestionThread? Thread, ThreadMessage.Question? Question);

   private closed record Decision
   {
      private Decision()
      {
      }

      internal sealed record Refuse(OperationError Error) : Decision;

      internal sealed record Keep : Decision;

      internal sealed record Put(QuestionThread Thread) : Decision;

      internal sealed record Send(QuestionThread.Sent Thread, Recipient To) : Decision;

      internal sealed record Remove : Decision;
   }

   private closed record RequestStep
   {
      private RequestStep()
      {
      }

      // A re-send: a pending request becomes canceled.
      internal sealed record Superseded : RequestStep;

      // The reviewer's cancel: the last request, while pending, becomes canceled.
      internal sealed record Withdrawn : RequestStep;

      // A reply: the request at the index takes the resolution, and every other pending request becomes canceled.
      internal sealed record Answered(int Index, RequestState Resolution) : RequestStep;

      // The end of a binding: a pending request becomes unanswered.
      internal sealed record Released : RequestStep;

      // A fetch: each request to the session without a mark becomes received.
      internal sealed record Receipt(string SessionId, DateTimeOffset Now) : RequestStep;
   }

   // The threads of one write while the table decides it, with the IDs it drew, its changes and its sends.
   private sealed class Draft
   {
      private readonly TourIds _ids;
      private readonly HashSet<string> _used;
      private readonly List<QuestionThread> _threads;
      private readonly List<ThreadChange> _changes = [];
      private readonly List<ThreadSend> _sends = [];
      private QuestionThread? _named;

      public Draft(Tour tour, DateTimeOffset now, TourIds ids)
      {
         Tour = tour;
         Now = now;
         _ids = ids;
         _used = TourIds.NamedBy(tour);
         _threads = [.. tour.Threads];
      }

      public Tour Tour { get; }

      public DateTimeOffset Now { get; }

      public string NewId() => _ids.Next(_used);

      public ThreadMessage.Question NewQuestion(string text, string? stopId, Recipient recipient) =>
         ThreadMessage.Question.Asked(NewId(), Now, text, stopId, QuestionRequest.Pending(NewId(), recipient.Id, Now));

      // Applies the decision to the thread that it is about, or to a new thread when that is null. Returns the refusal.
      public OperationError? Take(Decision decision, QuestionThread? thread)
      {
         switch (decision)
         {
            case Decision.Refuse refuse:
               return refuse.Error;
            case Decision.Keep:
               _named = thread;
               break;
            case Decision.Put put:
               Change(thread, put.Thread);
               break;
            case Decision.Send send:
               Change(thread, send.Thread);
               _sends.Add(new ThreadSend(send.To, send.Thread));
               break;
            case Decision.Remove:
               _threads.RemoveAll(candidate => candidate.Id == thread!.Id);
               _changes.Add(new ThreadChange(ThreadChangeKind.Removed, thread!, Carried: false));
               _named = null;
               break;
         }

         return null;
      }

      public ThreadWrite Written(bool named, bool carried)
      {
         var changes = carried ? _changes.Select(change => change with { Carried = true }) : _changes;
         var next = _changes.Count == 0 ? Tour : Tour with { Threads = new TourThreads([.. _threads]) };
         return ThreadWrite.Of(Tour, next, named ? _named : null, [.. changes], [.. _sends], _ids);
      }

      private void Change(QuestionThread? before, QuestionThread after)
      {
         var index = before is null ? -1 : _threads.FindIndex(candidate => candidate.Id == before.Id);
         if (index < 0)
         {
            _threads.Add(after);
         }
         else
         {
            _threads[index] = after;
         }

         _changes.Add(new ThreadChange(index < 0 ? ThreadChangeKind.Created : ThreadChangeKind.Updated, after, Carried: false));
         _named = after;
      }
   }
}
