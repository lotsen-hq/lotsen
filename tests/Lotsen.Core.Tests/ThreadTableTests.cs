using System.Text.Json;
using Lotsen.Git;
using Lotsen.Json;
using Lotsen.Operations;
using Lotsen.Pinning;
using Lotsen.Sessions;
using Lotsen.Tours;

namespace Lotsen.Tests;

/// <summary>
/// Covers each cell of the table in <see cref="TourThreads"/>: the refusal, or the changed threads, their states and request states, and the sends, of one command or fact on a tour that earlier
/// commands built.
/// </summary>
/// <remarks>
/// A row reads the outcome in short forms. <c>Changes</c> lists each change as <c>created:t1</c>, <c>updated:t1</c> or <c>removed:t1</c>, in change order, and empty means the write stores
/// nothing. <c>Thread</c> is the probed thread's kind and state, such as <c>asked open</c>. <c>Requests</c> lists the request states of each question of the probed thread, the questions apart
/// by <c>;</c>. <c>Sends</c> lists the threads of the queued questions.
/// </remarks>
[Trait("Category", "Threads")]
public sealed class ThreadTableTests
{
   private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
   private static readonly AgentSessions Sessions = new();
   private static readonly Recipient Bound = Sessions.TryOpen()!.Recipient;
   private static readonly Recipient Other = Sessions.TryOpen()!.Recipient;

   private static readonly StopContent Before = new("Old.", [], How: "Kept.", Why: "Old why.");
   private static readonly Stop.Written Replaced = new("s1", "Rounding", StopKind.Inspect, "c1", Before, 1, []);
   private static readonly Stop.Written Written = new("s1", "Rounding", StopKind.Inspect, "c1", new StopContent("New.", [], How: "Kept.", Why: "New why."), 2, [new StopVersion("Rounding", StopKind.Inspect, Before, Now)]);
   private static readonly ThreadAnchor How = new ThreadAnchor.StopText("s1", "how", "Kept", 1);
   private static readonly ThreadAnchor Why = new ThreadAnchor.StopText("s1", "why", "Old", 1);
   private static readonly TourPin.Pinned Pin = new(CommitId.Parse(new string('a', 40)), CommitId.Parse(new string('b', 40)), 2, new Uri("https://example.test/repo"));
   private static readonly ThreadAnchor.CodeRange Code = new(FileSide.Head, "src/Rates.cs", 3, 4, Pin.Head, "a\nb");
   private static readonly ThreadAnchor.CodeRange EarlierCode = Code with { Commit = CommitId.Parse(new string('e', 40)) };
   private static readonly ThreadAnchor.WholeFile EarlierFile = new(FileSide.Head, "src/Rates.cs", CommitId.Parse(new string('e', 40)));

   private static readonly IReadOnlyDictionary<string, Cell> Cells = new Dictionary<string, Cell>
   {
      ["Ask opens an asked thread with a pending request and sends it"] = new([], Command(AskTour(), "t1", "q1", "r1"), new(Changes: "created:t1", Thread: "asked open", Requests: "pending", Sends: "t1")),
      ["Request opens an asked thread at the admitted anchor with the stored action and sends it"] = new(
         [],
         RequestCommand(new ActionRequest.Explain(new StopTarget.StopText("s1", "how", "Kept")), "t1", "q1", "r1"),
         new(Changes: "created:t1", Thread: "asked open", Requests: "pending", Sends: "t1", Action: "explain", AnchorVersion: 2)),
      ["Request of a summarize while another summarize waits is refused"] = new([RequestStep(Summarize)], RequestCommand(Summarize, "t2", "q2", "r2"), Refused(OperationErrorCode.QuestionWaiting, "action: Claude still summarizes the review. Wait")),
      ["Request of a summarize after the other summarize got its reply opens a thread"] = new([RequestStep(Summarize), Reply("q1")], RequestCommand(Summarize, "t2", "q2", "r2"), new(Changes: "created:t2"), Probe: "t2"),
      ["Request of an action outside every wait group opens a thread while another of its kind waits"] = new([RequestStep(ExplainStop)], RequestCommand(ExplainStop, "t2", "q2", "r2"), new(Changes: "created:t2", Action: "explain"), Probe: "t2"),
      ["Request of a reorder while another reorder waits is refused"] = new([RequestStep(Reorder)], RequestCommand(Reorder, "t2", "q2", "r2"), Refused(OperationErrorCode.QuestionWaiting, "action: Claude still reorders the plan as you asked. Wait")),
      ["Request of a reorder while a level change waits is refused"] = new(
         [ReportStep(LevelChange)],
         RequestCommand(Reorder, "t2", "q2", "r2"),
         Refused(OperationErrorCode.QuestionWaiting, "action: Claude still writes stops at the familiarity level that you picked. Wait")),
      ["Request of a reorder while an iteration update waits is refused"] = new(
         [ReportStep(Update)],
         RequestCommand(Reorder, "t2", "q2", "r2"),
         Refused(OperationErrorCode.QuestionWaiting, "action: Claude still revises the stops that the last update made stale. Wait")),
      ["Request of a reorder while the plan approval waits is refused"] = new(
         [ReportStep(Approval)],
         RequestCommand(Reorder, "t2", "q2", "r2"),
         Refused(OperationErrorCode.QuestionWaiting, "action: Claude still writes the stops of the plan that you approved. Wait")),
      ["Request of a reorder while a replan waits is refused"] = new(
         [RequestStep(Replan)],
         RequestCommand(Reorder, "t2", "q2", "r2"),
         Refused(OperationErrorCode.QuestionWaiting, "action: Claude still re-plans the tour as you asked. Wait")),
      ["Request of a replan while another replan waits is refused"] = new(
         [RequestStep(Replan)],
         RequestCommand(Replan, "t2", "q2", "r2"),
         Refused(OperationErrorCode.QuestionWaiting, "action: Claude still re-plans the tour as you asked. Wait")),
      ["Request of a replan while a reorder waits is refused"] = new(
         [RequestStep(Reorder)],
         RequestCommand(Replan, "t2", "q2", "r2"),
         Refused(OperationErrorCode.QuestionWaiting, "action: Claude still reorders the plan as you asked. Wait")),
      ["Request of a replan while a level change waits is refused"] = new(
         [ReportStep(LevelChange)],
         RequestCommand(Replan, "t2", "q2", "r2"),
         Refused(OperationErrorCode.QuestionWaiting, "action: Claude still writes stops at the familiarity level that you picked. Wait")),
      ["Request of a replan while the plan approval waits is refused"] = new(
         [ReportStep(Approval)],
         RequestCommand(Replan, "t2", "q2", "r2"),
         Refused(OperationErrorCode.QuestionWaiting, "action: Claude still writes the stops of the plan that you approved. Wait")),
      ["Request of a replan while an iteration update waits is refused"] = new(
         [ReportStep(Update)],
         RequestCommand(Replan, "t2", "q2", "r2"),
         Refused(OperationErrorCode.QuestionWaiting, "action: Claude still revises the stops that the last update made stale. Wait")),
      ["Request of a reorder while a follow-up in a replan thread waits is refused"] = new(
         [RequestStep(Replan), Reply("q1"), FollowUpStep()],
         RequestCommand(Reorder, "t2", "q3", "r3"),
         Refused(OperationErrorCode.QuestionWaiting, "action: Claude still re-plans the tour as you asked. Wait")),
      ["Request of a reorder after the replan got its reply opens a thread"] = new(
         [RequestStep(Replan), Reply("q1")],
         RequestCommand(Reorder, "t2", "q2", "r2"),
         new(Changes: "created:t2", Action: "reorder"), Probe: "t2"),
      ["Request of a summarize while a replan waits opens a thread"] = new(
         [RequestStep(Replan)],
         RequestCommand(Summarize, "t2", "q2", "r2"),
         new(Changes: "created:t2", Action: "summarize"), Probe: "t2"),
      ["Report of a plan approval while a replan waits opens a thread"] = new(
         [RequestStep(Replan)],
         ReportCommand(Approval, "t2", "q2", "r2"),
         new(Changes: "created:t2", Decision: "planApproved"), Probe: "t2"),
      ["Report of a level change while a reorder waits opens a thread"] = new([RequestStep(Reorder)], ReportCommand(LevelChange, "t2", "q2", "r2"), new(Changes: "created:t2", Decision: "familiarityChanged"), Probe: "t2"),
      ["Report opens a decided thread on the tour with the decision and sends it"] = new([], ReportCommand(LevelChange, "t1", "q1", "r1"), new(Changes: "created:t1", Thread: "decided open", Requests: "pending", Sends: "t1", Decision: "familiarityChanged")),
      ["Report of a level change while the plan approval waits is refused"] = new([ReportStep(Approval)], ReportCommand(LevelChange, "t2", "q2", "r2"), Refused(OperationErrorCode.QuestionWaiting, "familiarity: Claude still writes the stops of the plan that you approved. Wait")),
      ["Report of a plan approval while a level change waits is refused"] = new([ReportStep(LevelChange)], ReportCommand(Approval, "t2", "q2", "r2"), Refused(OperationErrorCode.QuestionWaiting, "Claude still writes stops at the familiarity level that you picked. Wait")),
      ["Report of an iteration update while an earlier one waits is refused"] = new([ReportStep(Update)], ReportCommand(Update, "t2", "q2", "r2"), Refused(OperationErrorCode.QuestionWaiting, "tourId: Claude still revises the stops that the last update made stale")),
      ["Report of an iteration update while a level change waits opens a thread"] = new([ReportStep(LevelChange)], ReportCommand(Update, "t2", "q2", "r2"), new(Changes: "created:t2", Decision: "iterationUpdated"), Probe: "t2"),
      ["Start opens a started thread with the author's agent message and sends nothing"] = new([], Command(StartTour(), "t1", "m1"), new(Changes: "created:t1", Thread: "started open", Requests: "", Sends: "")),
      ["SaveNote opens a note and sends nothing"] = new([], Command(Note(), "t1"), new(Changes: "created:t1", Thread: "note open", Sends: "")),

      ["EditNote on a missing thread is refused"] = new([], Command(new ThreadCommand.EditNote("t1", "New.")), Refused(OperationErrorCode.ThreadNotFound)),
      ["EditNote on a sent thread is refused"] = new([Ask()], Command(new ThreadCommand.EditNote("t1", "New.")), Refused(OperationErrorCode.NoteSent)),
      ["EditNote with the same words stores nothing"] = new([NoteStep()], Command(new ThreadCommand.EditNote("t1", "My words.")), new(Changes: "", Thread: "note open")),
      ["EditNote with new words updates the note and its save time"] = new([NoteStep()], Command(new ThreadCommand.EditNote("t1", "New.")), new(Changes: "updated:t1", Thread: "note open", NoteText: "New.")),
      ["DiscardNote on a missing thread is refused"] = new([], Command(new ThreadCommand.DiscardNote("t1")), Refused(OperationErrorCode.ThreadNotFound)),
      ["DiscardNote on a sent thread is refused"] = new([Ask()], Command(new ThreadCommand.DiscardNote("t1")), Refused(OperationErrorCode.NoteSent)),
      ["DiscardNote removes the note"] = new([NoteStep()], Command(new ThreadCommand.DiscardNote("t1")), new(Changes: "removed:t1", Sends: "")),

      ["SendNotes with a missing thread refuses the whole batch"] = new([NoteStep()], Command(new ThreadCommand.SendNotes(["t1", "gone"], Bound), "q1", "r1"), Refused(OperationErrorCode.ThreadNotFound)),
      ["SendNotes with a sent thread refuses the whole batch"] = new([NoteStep(), Ask("t2", "q2", "r2")], Command(new ThreadCommand.SendNotes(["t1", "t2"], Bound), "q1", "r1"), Refused(OperationErrorCode.NoteSent)),
      ["SendNotes turns each note into an open asked thread in input order and sends each"] = new(
         [NoteStep(), NoteStep("t2")],
         Command(new ThreadCommand.SendNotes(["t2", "t1"], Bound), "q2", "r2", "q1", "r1"),
         new(Changes: "updated:t2 updated:t1", Thread: "asked open", Requests: "pending", Sends: "t2 t1")),
      ["SendNotes opens a resolved note"] = new([NoteStep(), SetState(ReviewerThreadState.Resolved)], Command(new ThreadCommand.SendNotes(["t1"], Bound), "q1", "r1"), new(Thread: "asked open")),

      ["FollowUp on a missing thread is refused"] = new([], Command(FollowUp()), Refused(OperationErrorCode.ThreadNotFound)),
      ["FollowUp on a note is refused"] = new([NoteStep()], Command(FollowUp()), Refused(OperationErrorCode.ThreadIsNote)),
      ["FollowUp while the last question waits is refused"] = new([Ask()], Command(FollowUp()), Refused(OperationErrorCode.QuestionWaiting, "still waits")),
      ["FollowUp in an action's thread while another of its kind waits is refused"] = new(
         [RequestStep(Summarize), Reply("q1"), RequestStep(Summarize, "t2", "q2", "r2")],
         Command(FollowUp()),
         Refused(OperationErrorCode.QuestionWaiting, "threadId: Claude still summarizes the review. Wait")),
      ["FollowUp in an older familiarity thread while a newer change waits is refused"] = new(
         [ReportStep(LevelChange), Reply("q1"), ReportStep(LevelChange, "t2", "q2", "r2")],
         Command(FollowUp()),
         Refused(OperationErrorCode.QuestionWaiting, "threadId: Claude still writes stops at the familiarity level that you picked. Wait")),
      ["FollowUp in the plan approval's thread while a level change waits is refused"] = new(
         [ReportStep(Approval), Reply("q1"), ReportStep(LevelChange, "t2", "q2", "r2")],
         Command(FollowUp()),
         Refused(OperationErrorCode.QuestionWaiting, "threadId: Claude still writes stops at the familiarity level that you picked. Wait")),
      ["FollowUp in an older iteration update's thread while a level change waits adds the question"] = new(
         [ReportStep(Update), Reply("q1"), ReportStep(LevelChange, "t2", "q2", "r2")],
         Command(FollowUp(), "q3", "r3"),
         new(Thread: "decided open", Requests: "done;pending", Sends: "t1")),
      ["FollowUp in a replan thread while another replan waits adds the question"] = new(
         [RequestStep(Replan), Reply("q1"), RequestStep(Replan, "t2", "q2", "r2")],
         Command(FollowUp(), "q3", "r3"),
         new(Thread: "asked open", Requests: "done;pending", Sends: "t1")),
      ["FollowUp in a replan thread while a reorder waits adds the question"] = new(
         [RequestStep(Replan), Reply("q1"), RequestStep(Reorder, "t2", "q2", "r2")],
         Command(FollowUp(), "q3", "r3"),
         new(Thread: "asked open", Requests: "done;pending", Sends: "t1")),
      ["FollowUp in a reorder thread while a level change waits adds the question"] = new(
         [RequestStep(Reorder), Reply("q1"), ReportStep(LevelChange, "t2", "q2", "r2")],
         Command(FollowUp(), "q3", "r3"),
         new(Thread: "asked open", Requests: "done;pending", Sends: "t1")),
      ["FollowUp in a replan thread while the plan approval waits adds the question"] = new(
         [RequestStep(Replan), Reply("q1"), ReportStep(Approval, "t2", "q2", "r2")],
         Command(FollowUp(), "q3", "r3"),
         new(Thread: "asked open", Requests: "done;pending", Sends: "t1")),
      ["FollowUp in a reorder thread while an iteration update waits adds the question"] = new(
         [RequestStep(Reorder), Reply("q1"), ReportStep(Update, "t2", "q2", "r2")],
         Command(FollowUp(), "q3", "r3"),
         new(Thread: "asked open", Requests: "done;pending", Sends: "t1")),
      ["FollowUp in a familiarity thread while a replan waits adds the question"] = new(
         [ReportStep(LevelChange), Reply("q1"), RequestStep(Replan, "t2", "q2", "r2")],
         Command(FollowUp(), "q3", "r3"),
         new(Thread: "decided open", Requests: "done;pending", Sends: "t1")),
      ["FollowUp adds a pending question, opens a resolved thread and sends it"] = new([Ask(), Reply("q1"), SetState(ReviewerThreadState.Resolved)], Command(FollowUp(), "q2", "r2"), new(Changes: "updated:t1", Thread: "asked open", Requests: "done;pending", Sends: "t1")),
      ["FollowUp opens a code thread that an iteration update left behind"] = new([Start(EarlierCode), Outdate()], Command(FollowUp(), "q2", "r2"), new(Thread: "started open")),
      ["FollowUp in a started thread adds its first question"] = new([Start()], Command(FollowUp(), "q2", "r2"), new(Thread: "started open", Requests: "pending", Sends: "t1")),

      ["Resend of a missing question is refused"] = new([], Command(new ThreadCommand.Resend("q1", Bound)), Refused(OperationErrorCode.QuestionNotFound)),
      ["Resend of an answered question is refused"] = new([Ask(), Reply("q1")], Command(new ThreadCommand.Resend("q1", Bound)), Refused(OperationErrorCode.QuestionAnswered)),
      ["Resend of a question with a follow-up is refused"] = new([Ask(), Cancel("q1"), FollowUpStep()], Command(new ThreadCommand.Resend("q1", Bound)), Refused(OperationErrorCode.QuestionFollowedUp)),
      ["Resend of a decision that its rule refuses is refused"] = new([ReportStep(LevelChange), Cancel("q1")], Command(new ThreadCommand.Resend("q1", Bound)), Refused(OperationErrorCode.InvalidInput, "decision.kind: a familiarity change does not go again")),
      ["Resend of a follow-up in a decided thread skips the decision's own check"] = new([ReportStep(LevelChange), Cancel("q1"), FollowUpStep()], Command(new ThreadCommand.Resend("q2", Bound), "r3"), new(Requests: "canceled;canceled,pending", Sends: "t1")),
      ["Resend of a tour action that its rule refuses is refused"] = new(
         [RequestStep(ExplainStop), Cancel("q1")],
         Command(new ThreadCommand.Resend("q1", Bound)),
         Refused(OperationErrorCode.InvalidInput, "action.stopVersion: Claude revised this stop since you asked"),
         Revised: true),
      ["Resend of a summarize while another summarize waits is refused"] = new(
         [RequestStep(Summarize), Cancel("q1"), RequestStep(Summarize, "t2", "q2", "r2")],
         Command(new ThreadCommand.Resend("q1", Bound)),
         Refused(OperationErrorCode.QuestionWaiting, "questionId: Claude still summarizes the review. Wait")),
      ["Resend of a canceled replan while another replan waits is refused"] = new(
         [RequestStep(Replan), Cancel("q1"), RequestStep(Replan, "t2", "q2", "r2")],
         Command(new ThreadCommand.Resend("q1", Bound)),
         Refused(OperationErrorCode.QuestionWaiting, "questionId: Claude still re-plans the tour as you asked. Wait")),
      ["Resend of a canceled reorder while a replan waits is refused"] = new(
         [RequestStep(Reorder), Cancel("q1"), RequestStep(Replan, "t2", "q2", "r2")],
         Command(new ThreadCommand.Resend("q1", Bound)),
         Refused(OperationErrorCode.QuestionWaiting, "questionId: Claude still re-plans the tour as you asked. Wait")),
      ["Resend of a canceled reorder while a level change waits is refused"] = new(
         [RequestStep(Reorder), Cancel("q1"), ReportStep(LevelChange, "t2", "q2", "r2")],
         Command(new ThreadCommand.Resend("q1", Bound)),
         Refused(OperationErrorCode.QuestionWaiting, "questionId: Claude still writes stops at the familiarity level that you picked. Wait")),
      ["Resend of a canceled replan while the plan approval waits is refused"] = new(
         [RequestStep(Replan), Cancel("q1"), ReportStep(Approval, "t2", "q2", "r2")],
         Command(new ThreadCommand.Resend("q1", Bound)),
         Refused(OperationErrorCode.QuestionWaiting, "questionId: Claude still writes the stops of the plan that you approved. Wait")),
      ["Resend of a canceled reorder while an iteration update waits is refused"] = new(
         [RequestStep(Reorder), Cancel("q1"), ReportStep(Update, "t2", "q2", "r2")],
         Command(new ThreadCommand.Resend("q1", Bound)),
         Refused(OperationErrorCode.QuestionWaiting, "questionId: Claude still revises the stops that the last update made stale. Wait")),
      ["Resend of an unanswered replan while a reorder waits is refused"] = new(
         [RequestStep(Replan), Released(), RequestStep(Reorder, "t2", "q2", "r2")],
         Command(new ThreadCommand.Resend("q1", Bound)),
         Refused(OperationErrorCode.QuestionWaiting, "questionId: Claude still reorders the plan as you asked. Wait")),
      ["Resend of a pending replan while a level change waits is refused"] = new(
         [RequestStep(Replan), ReportStep(LevelChange, "t2", "q2", "r2")],
         Command(new ThreadCommand.Resend("q1", Bound)),
         Refused(OperationErrorCode.QuestionWaiting, "questionId: Claude still writes stops at the familiarity level that you picked. Wait")),
      ["Resend of a pending replan while no other thread waits sends it again"] = new(
         [RequestStep(Replan)],
         Command(new ThreadCommand.Resend("q1", Bound), "r2"),
         new(Requests: "canceled,pending", Sends: "t1")),
      ["Resend of a canceled replan sends it again while a summarize waits"] = new(
         [RequestStep(Replan), Cancel("q1"), RequestStep(Summarize, "t2", "q2", "r2")],
         Command(new ThreadCommand.Resend("q1", Bound), "r3"),
         new(Requests: "canceled,pending", Sends: "t1")),
      ["Resend of a follow-up in a replan thread while a reorder waits sends it again"] = new(
         [RequestStep(Replan), Reply("q1"), FollowUpStep(), Cancel("q2"), RequestStep(Reorder, "t2", "q3", "r3")],
         Command(new ThreadCommand.Resend("q2", Bound), "r4"),
         new(Requests: "done;canceled,pending", Sends: "t1")),
      ["Resend cancels the pending request, adds a pending one, opens the thread and sends it"] = new(
         [Ask(), SetState(ReviewerThreadState.Resolved)],
         Command(new ThreadCommand.Resend("q1", Bound), "r2"),
         new(Changes: "updated:t1", Thread: "asked open", Requests: "canceled,pending", Sends: "t1")),
      ["Resend opens a code thread that an iteration update left behind"] = new([AskStep(new ThreadCommand.Ask(EarlierCode, "Why?", null, Bound)), Cancel("q1"), Outdate()], Command(new ThreadCommand.Resend("q1", Bound), "r2"), new(Thread: "asked open")),
      ["Resend after a cancel adds a pending request"] = new([Ask(), Cancel("q1")], Command(new ThreadCommand.Resend("q1", Bound), "r2"), new(Requests: "canceled,pending")),

      ["Cancel of a missing question is refused"] = new([], Command(new ThreadCommand.Cancel("q1")), Refused(OperationErrorCode.QuestionNotFound)),
      ["Cancel of an answered question is refused"] = new([Ask(), Reply("q1")], Command(new ThreadCommand.Cancel("q1")), Refused(OperationErrorCode.QuestionAnswered)),
      ["Cancel of a question whose last request is not pending is refused"] = new([Ask(), Cancel("q1")], Command(new ThreadCommand.Cancel("q1")), Refused(OperationErrorCode.RequestNotPending)),
      ["Cancel of a follow-up leaves the earlier question alone"] = new([Ask(), Reply("q1"), FollowUpStep()], Command(new ThreadCommand.Cancel("q2")), new(Requests: "done;canceled")),
      ["Cancel cancels the pending request, keeps the state and sends nothing"] = new([Ask(), SetState(ReviewerThreadState.Resolved)], Command(new ThreadCommand.Cancel("q1")), new(Changes: "updated:t1", Thread: "asked resolved", Requests: "canceled", Sends: "")),

      ["SetState on a missing thread is refused"] = new([], Command(SetStateCommand(ReviewerThreadState.Resolved)), Refused(OperationErrorCode.ThreadNotFound)),
      ["SetState to the thread's state stores nothing"] = new([Ask()], Command(SetStateCommand(ReviewerThreadState.Open)), new(Changes: "", Thread: "asked open")),
      ["SetState resolves a note and sends nothing"] = new([NoteStep()], Command(SetStateCommand(ReviewerThreadState.Resolved)), new(Changes: "updated:t1", Thread: "note resolved", Sends: "")),
      ["SetState opens a resolved note"] = new([NoteStep(), SetState(ReviewerThreadState.Resolved)], Command(SetStateCommand(ReviewerThreadState.Open)), new(Changes: "updated:t1", Thread: "note open")),
      ["SetState opens an outdated thread"] = new([Start(), Outdate()], Command(SetStateCommand(ReviewerThreadState.Open)), new(Changes: "updated:t1", Thread: "started open")),
      ["SetState resolves a thread whose question waits and keeps it waiting"] = new([Ask()], Command(SetStateCommand(ReviewerThreadState.Resolved)), new(Thread: "asked resolved", Requests: "pending", Sends: "")),

      ["AgentWrote on a missing thread is refused"] = new([], Command(AgentWrote(), "m2"), Refused(OperationErrorCode.ThreadNotFound)),
      ["AgentWrote on a note is refused as a missing thread"] = new([NoteStep()], Command(AgentWrote(), "m2"), Refused(OperationErrorCode.ThreadNotFound)),
      ["AgentWrote while the last question waits is refused"] = new([Ask()], Command(AgentWrote(), "m2"), Refused(OperationErrorCode.QuestionWaiting, "with reply")),
      ["AgentWrote opens an outdated thread"] = new([Start(), Outdate()], Command(AgentWrote(), "m2"), new(Changes: "updated:t1", Thread: "started open", Messages: "agentMessage agentMessage")),
      ["AgentWrote keeps a resolved thread resolved"] = new([Start(), SetState(ReviewerThreadState.Resolved)], Command(AgentWrote(), "m2"), new(Thread: "started resolved")),
      ["AgentWrote keeps a stop text thread that a revision left behind outdated"] = new([Start(How), Outdate()], Command(AgentWrote(), "m2"), new(Thread: "started outdated")),
      ["AgentWrote opens an outdated stop text thread on the stop's current version"] = new([Start(new ThreadAnchor.StopText("s1", "how", "Kept", 2)), Outdate()], Command(AgentWrote(), "m2"), new(Thread: "started open")),
      ["AgentWrote keeps a code thread that an iteration update left behind outdated"] = new([Start(EarlierCode), Outdate()], Command(AgentWrote(), "m2"), new(Thread: "started outdated")),
      ["AgentWrote keeps a file thread that an iteration update left behind outdated"] = new([Start(EarlierFile), Outdate()], Command(AgentWrote(), "m2"), new(Thread: "started outdated")),
      ["AgentWrote opens an outdated thread whose code anchor is at the pin"] = new([Start(Code), Outdate()], Command(AgentWrote(), "m2"), new(Thread: "started open")),
      ["AgentWrote opens an outdated thread whose base code anchor is at the merge base"] = new([Start(Code with { Side = FileSide.Base, Commit = Pin.MergeBase }), Outdate()], Command(AgentWrote(), "m2"), new(Thread: "started open")),
      ["AgentWrote opens an outdated code thread of an unpinned tour"] = new([Start(EarlierCode), Outdate()], Command(AgentWrote(), "m2"), new(Thread: "started open"), Pin: new TourPin.Pinning()),

      ["Outdate on a missing thread is refused"] = new([], Command(OutdateCommand()), Refused(OperationErrorCode.ThreadNotFound)),
      ["Outdate of a thread whose question was canceled marks it outdated"] = new([Ask(), Cancel("q1")], Command(OutdateCommand()), new(Changes: "updated:t1", Thread: "asked outdated")),
      ["Outdate on a note is refused as a missing thread"] = new([NoteStep()], Command(OutdateCommand()), Refused(OperationErrorCode.ThreadNotFound)),
      ["Outdate while the last question waits is refused"] = new([Ask()], Command(OutdateCommand()), Refused(OperationErrorCode.QuestionWaiting, "with reply")),
      ["Outdate marks an open thread outdated"] = new([Start()], Command(OutdateCommand()), new(Changes: "updated:t1", Thread: "started outdated")),
      ["Outdate keeps a resolved thread and stores nothing"] = new([Start(), SetState(ReviewerThreadState.Resolved)], Command(OutdateCommand()), new(Changes: "", Thread: "started resolved")),
      ["Delete on a missing thread is refused"] = new([], Command(new ThreadCommand.Delete("t1", Bound)), Refused(OperationErrorCode.ThreadNotFound)),
      ["Delete while the last question waits is refused"] = new([Ask()], Command(new ThreadCommand.Delete("t1", Bound)), Refused(OperationErrorCode.QuestionWaiting, "with reply")),
      ["Delete on a note is refused as a missing thread"] = new([NoteStep()], Command(new ThreadCommand.Delete("t1", Bound)), Refused(OperationErrorCode.ThreadNotFound)),
      ["Delete removes the thread"] = new([Ask(), Reply("q1")], Command(new ThreadCommand.Delete("t1", Bound)), new(Changes: "removed:t1")),
      ["Remove on a missing thread is refused"] = new([], Command(new ThreadCommand.Remove("t1")), Refused(OperationErrorCode.ThreadNotFound)),
      ["Remove while the last question waits is refused"] = new([Ask()], Command(new ThreadCommand.Remove("t1")), Refused(OperationErrorCode.QuestionWaiting, "Cancel the question")),
      ["Remove after the cancel of the waiting question removes the thread"] = new([Ask(), Cancel("q1")], Command(new ThreadCommand.Remove("t1")), new(Changes: "removed:t1", Sends: "")),
      ["Remove removes an answered thread"] = new([Ask(), Reply("q1")], Command(new ThreadCommand.Remove("t1")), new(Changes: "removed:t1", Sends: "")),
      ["Remove removes a note"] = new([NoteStep()], Command(new ThreadCommand.Remove("t1")), new(Changes: "removed:t1", Sends: "")),

      ["Reply to a missing question is refused"] = new([], Command(ReplyCommand("q1"), "a1"), Refused(OperationErrorCode.QuestionNotFound)),
      ["Reply to an answered question is refused"] = new([Ask(), Reply("q1")], Command(ReplyCommand("q1"), "a2"), Refused(OperationErrorCode.QuestionAnswered)),
      ["Reply from a session the question never went to is refused"] = new([Ask()], Command(ReplyCommand("q1", Other), "a1"), Refused(OperationErrorCode.CallerNotAllowed)),
      ["Reply resolves the pending request and adds the reply"] = new([Ask()], Command(ReplyCommand("q1"), "a1"), new(Changes: "updated:t1", Thread: "asked open", Requests: "done", Messages: "question reply", Sends: "")),
      ["Reply resolves the first request to its session and cancels a later pending one"] = new([Ask(), ResendStep("r2")], Command(ReplyCommand("q1", resolution: ReplyResolution.Declined, text: null), "a1"), new(Requests: "declined,canceled")),
      ["Reply to a canceled request opens an outdated thread"] = new([Ask(), Cancel("q1"), Outdate()], Command(ReplyCommand("q1"), "a1"), new(Thread: "asked open", Requests: "done")),
      ["Reply from the session of a later request resolves that request"] = new([Ask(), new(new ThreadCommand.Resend("q1", Other), "r2")], Command(ReplyCommand("q1", Other), "a1"), new(Requests: "canceled,done")),
      ["Reply to an earlier question after a follow-up resolves only that question"] = new([Ask(), Cancel("q1"), FollowUpStep()], Command(ReplyCommand("q1"), "a1"), new(Requests: "done;pending", Messages: "question question reply")),
      ["Reply keeps a stop text thread that a revision left behind outdated"] = new([AskStep(new ThreadCommand.Ask(How, "Why?", null, Bound)), Cancel("q1"), Outdate()], Command(ReplyCommand("q1"), "a1"), new(Thread: "asked outdated")),
      ["Reply keeps a code thread that an iteration update left behind outdated"] = new([AskStep(new ThreadCommand.Ask(EarlierCode, "Why?", null, Bound)), Cancel("q1"), Outdate()], Command(ReplyCommand("q1"), "a1"), new(Thread: "asked outdated")),
      ["Reply keeps a file thread that an iteration update left behind outdated"] = new([AskStep(new ThreadCommand.Ask(EarlierFile, "Why?", null, Bound)), Cancel("q1"), Outdate()], Command(ReplyCommand("q1"), "a1"), new(Thread: "asked outdated")),
      ["Reply keeps a resolved thread resolved"] = new([Ask(), SetState(ReviewerThreadState.Resolved)], Command(ReplyCommand("q1"), "a1"), new(Thread: "asked resolved")),

      ["MarkReceived of a missing question is refused"] = new([], Command(new ThreadCommand.MarkReceived("q1", Bound)), Refused(OperationErrorCode.QuestionNotFound)),
      ["MarkReceived marks the requests to the session and keeps the others"] = new([Ask()], Command(new ThreadCommand.MarkReceived("q1", Bound)), new(Changes: "updated:t1", Received: "r1")),
      ["MarkReceived of a received question stores nothing"] = new([Ask(), Received("q1")], Command(new ThreadCommand.MarkReceived("q1", Bound)), new(Changes: "")),
      ["MarkReceived by a session no request went to stores nothing"] = new([Ask()], Command(new ThreadCommand.MarkReceived("q1", Other)), new(Changes: "", Received: "")),

      ["StopRevised moves a stop text anchor whose field stayed the same, carried by the revision's event"] = new([Start(How)], Fact(new ThreadFact.StopRevised(Replaced, Written)), new(Changes: "updated:t1", Thread: "started open", AnchorVersion: 2, Carried: true)),
      ["StopRevised outdates an open thread whose field changed"] = new([Start(Why)], Fact(new ThreadFact.StopRevised(Replaced, Written)), new(Changes: "updated:t1", Thread: "started outdated", AnchorVersion: 1, Carried: true)),
      ["StopRevised outdates an open note whose field changed"] = new([NoteStep("t1", Why)], Fact(new ThreadFact.StopRevised(Replaced, Written)), new(Thread: "note outdated")),
      ["StopRevised keeps a waiting thread open"] = new([AskStep(new ThreadCommand.Ask(Why, "Why?", null, Bound))], Fact(new ThreadFact.StopRevised(Replaced, Written)), new(Changes: "", Thread: "asked open")),
      ["StopRevised keeps a resolved thread resolved"] = new([Start(Why), SetState(ReviewerThreadState.Resolved)], Fact(new ThreadFact.StopRevised(Replaced, Written)), new(Changes: "", Thread: "started resolved")),
      ["StopRevised leaves a thread on another version as it is"] = new([Start(new ThreadAnchor.StopText("s1", "why", "Old", 2))], Fact(new ThreadFact.StopRevised(Replaced, Written)), new(Changes: "")),

      ["Repinned moves an anchor to its new place and publishes it"] = new([Start(Code)], Fact(Repinned(new Remapped<ThreadAnchor>.Moved(Code with { StartLine = 5, EndLine = 6 }))), new(Changes: "updated:t1", Thread: "started open", Carried: false)),
      ["Repinned keeps an anchor that moved to its own place"] = new([Start(Code)], Fact(Repinned(new Remapped<ThreadAnchor>.Moved(Code))), new(Changes: "")),
      ["Repinned outdates an open thread whose anchor is stale"] = new([Start(Code)], Fact(Repinned(new Remapped<ThreadAnchor>.Stale(StaleReason.LinesChanged))), new(Changes: "updated:t1", Thread: "started outdated")),
      ["Repinned keeps an outdated thread outdated when it moves its anchor back to the pin"] = new([Start(EarlierCode), Outdate()], Fact(Repinned(new Remapped<ThreadAnchor>.Moved(Code))), new(Changes: "updated:t1", Thread: "started outdated")),
      ["Repinned keeps a waiting thread whose anchor is stale open"] = new([AskStep(new ThreadCommand.Ask(Code, "Why?", null, Bound))], Fact(Repinned(new Remapped<ThreadAnchor>.Stale(StaleReason.FileGone))), new(Changes: "")),

      ["Released marks each pending request unanswered"] = new([Ask(), Ask("t2", "q2", "r2"), ReplyStep("q2", "a2")], Fact(new ThreadFact.Released()), new(Changes: "updated:t1", Requests: "unanswered", Carried: false)),
      ["Released keeps a resolved thread resolved"] = new([Ask(), SetState(ReviewerThreadState.Resolved)], Fact(new ThreadFact.Released()), new(Thread: "asked resolved", Requests: "unanswered")),
      ["Released without a pending request stores nothing"] = new([Ask(), Reply("q1"), NoteStep("t2")], Fact(new ThreadFact.Released()), new(Changes: "")),
   };

   public static TheoryData<string> CellNames => [.. Cells.Keys];

   [Theory]
   [MemberData(nameof(CellNames))]
   public void Thread_table_cell(string name)
   {
      var cell = Cells[name];
      var tour = Given(cell.Given, cell.Pin ?? Pin, cell.Revised);

      var result = cell.When(tour);

      var expect = cell.Expect;
      if (expect.Refused is { } code)
      {
         Assert.Null(result.Value);
         Assert.Equal(code, result.Error!.Code);
         Assert.Contains(expect.Reason ?? "", result.Error.Reason, StringComparison.Ordinal);
         return;
      }

      var write = Assert.IsType<ThreadWrite>(result.Value);
      var probed = write.Next.Threads.Find(cell.Probe);
      if (expect.Changes is { } changes)
      {
         Assert.Equal(changes, string.Join(" ", write.Changes.Select(change => $"{change.Kind.ToString().ToLowerInvariant()}:{change.Thread.Id}")));
         Assert.Equal(changes.Length == 0, ReferenceEquals(write.Next, tour));
      }

      Check(expect.Sends, () => string.Join(" ", write.Sends.Select(send => send.Thread.Id)));
      Check(expect.Thread, () => $"{KindOf(probed!)} {probed!.State.ToString().ToLowerInvariant()}");
      Check(expect.Requests, () => Requests(probed!));
      Check(expect.Messages, () => string.Join(" ", ((QuestionThread.Sent)probed!).Messages.Select(KindOf)));
      Check(expect.Action, () => ((QuestionThread.Sent.Asked)probed!).Action!.Kind);
      Check(expect.Decision, () => ((QuestionThread.Sent.Decided)probed!).Decision.Kind);
      Check(expect.NoteText, () => ((QuestionThread.Note)probed!).Text);
      if (expect.NoteText is not null)
      {
         Assert.Equal(Now, ((QuestionThread.Note)probed!).SavedAt);
      }

      Check(expect.Received, () => string.Join(" ", ((QuestionThread.Sent)probed!).Messages.OfType<ThreadMessage.Question>().SelectMany(q => q.Requests).Where(r => r.ReceivedAt == Now).Select(r => r.Id)));
      if (expect.AnchorVersion is { } version)
      {
         Assert.Equal(version, Assert.IsType<ThreadAnchor.StopText>(probed!.Anchor).Version);
      }

      if (expect.Carried is { } carried)
      {
         Assert.Equal(carried, write.IsCarried);
      }

      // Every state that the table reaches loads again through the stored form's checks.
      var stored = JsonSerializer.Serialize(write.Next.Threads, LotsenJsonContext.Default.TourThreads);
      Assert.Equal(stored, JsonSerializer.Serialize(JsonSerializer.Deserialize(stored, LotsenJsonContext.Default.TourThreads)!, LotsenJsonContext.Default.TourThreads));
   }

   [Fact]
   public void Thread_check_gives_the_refusal_of_apply_and_builds_no_tour()
   {
      var tour = Given([Ask()]);

      Assert.Equal(TourThreads.Apply(tour, FollowUp(), Now, TourIds.Random).Error, TourThreads.Check(tour, FollowUp()));
      Assert.Null(TourThreads.Check(tour, ReplyCommand("q1")));
      Assert.Equal(OperationErrorCode.QuestionNotFound, TourThreads.Check(tour, ReplyCommand("q9"))?.Code);
   }

   [Fact]
   public void Thread_write_names_the_thread_after_the_change_or_none_after_a_removal_a_batch_or_a_fact()
   {
      var tour = Given([Ask(), Reply("q1"), NoteStep("t2"), NoteStep("t3")]);

      Assert.Same(tour.Threads.Find("t1"), TourThreads.Apply(tour, SetStateCommand(ReviewerThreadState.Open), Now, TourIds.Random).Value!.Thread);
      Assert.Equal(QuestionThreadState.Resolved, TourThreads.Apply(tour, SetStateCommand(ReviewerThreadState.Resolved), Now, TourIds.Random).Value!.Thread!.State);
      Assert.Null(TourThreads.Apply(tour, new ThreadCommand.Delete("t1", Bound), Now, TourIds.Random).Value!.Thread);
      Assert.Null(TourThreads.Apply(tour, new ThreadCommand.SendNotes(["t2", "t3"], Bound), Now, TourIds.Random).Value!.Thread);
      Assert.Null(TourThreads.Settle(tour, new ThreadFact.Released()).Thread);
   }

   [Fact]
   public void Thread_write_chained_with_then_keeps_one_change_per_thread_in_order_and_draws_unique_ids()
   {
      var tour = Given([Start(Code)]);
      var settled = TourThreads.Settle(tour, Repinned(new Remapped<ThreadAnchor>.Stale(StaleReason.LinesChanged)), TourIds.Sequence("t2", "q2", "r2", "q2"));

      var chained = settled.Then(AskTour(), Now).Value!;

      Assert.Equal("updated:t1 created:t2", string.Join(" ", chained.Changes.Select(change => $"{change.Kind.ToString().ToLowerInvariant()}:{change.Thread.Id}")));
      Assert.Equal("t2", chained.Thread!.Id);
      Assert.Same(tour, chained.From);
      Assert.Equal(["t2"], chained.Sends.Select(send => send.Thread.Id));
      Assert.Contains("in use", Assert.Throws<InvalidOperationException>(() => chained.Then(new ThreadCommand.FollowUp("t1", "And?", null, Bound), Now)).Message, StringComparison.Ordinal);
   }

   [Fact]
   public void Thread_write_chained_with_then_drops_the_sends_of_a_thread_that_a_later_command_removes()
   {
      var asked = TourThreads.Apply(Given([]), AskTour(), Now, TourIds.Sequence("t1", "q1", "r1")).Value!;
      var created = asked.Then(new ThreadCommand.Cancel("q1"), Now).Value!.Then(new ThreadCommand.Delete("t1", Bound), Now).Value!;

      Assert.Empty(created.Changes);
      Assert.Empty(created.Sends);

      var followed = TourThreads.Apply(Given([Ask(), Reply("q1")]), FollowUp(), Now, TourIds.Sequence("q2", "r2")).Value!;
      var existing = followed.Then(new ThreadCommand.Cancel("q2"), Now).Value!.Then(new ThreadCommand.Delete("t1", Bound), Now).Value!;

      Assert.Equal("removed:t1", string.Join(" ", existing.Changes.Select(change => $"{change.Kind.ToString().ToLowerInvariant()}:{change.Thread.Id}")));
      Assert.Empty(existing.Sends);
   }

   [Fact]
   public void Thread_ids_sequence_refuses_an_id_the_tour_holds()
   {
      var tour = Given([Ask()]);

      Assert.Throws<InvalidOperationException>(() => TourThreads.Apply(tour, AskTour(), Now, TourIds.Sequence("t1", "q9", "r9")));
   }

   /// <summary>The plan holds stop s1 with check c9, and the other IDs name a stop that a set_plan dropped: an answer's stop, the reading progress's last stop and an available iteration's stale stop.</summary>
   [Theory]
   [InlineData("s1")]
   [InlineData("c9")]
   [InlineData("gone")]
   [InlineData("read")]
   [InlineData("stale")]
   public void Thread_ids_sequence_refuses_an_id_that_the_tour_names_outside_its_threads(string id)
   {
      var tour = Given([]);
      var checkedStop = Written with { Content = Written.Content with { Checks = [new StopCheck("c9", "Is it right?")] } };
      tour = tour with
      {
         Plan = tour.Plan! with { Stops = [checkedStop] },
         Answers = [new CheckAnswer("gone", "c8", "Yes.", AnswerType.Decision, AnswerState.Draft, Now)],
         Progress = new ReadingProgress(finished: false, "read"),
         AvailableIteration = new AvailableIteration(Pin with { Head = CommitId.Parse(new string('c', 40)), Iteration = 3 }, ["stale"], Now),
      };

      Assert.Contains("in use", Assert.Throws<InvalidOperationException>(() => TourThreads.Apply(tour, AskTour(), Now, TourIds.Sequence(id, "q9", "r9"))).Message, StringComparison.Ordinal);
   }

   // A revised tour's stop gets a version after the steps, as a put_stop between them and the probed command would.
   private static Tour Given(IEnumerable<Step> steps, TourPin? pin = null, bool revised = false)
   {
      var tour = SummaryTours.Empty() with { Pin = pin ?? Pin, Plan = new Plan([new Chapter("c1", "Pricing")], [Written], []) };
      foreach (var step in steps)
      {
         tour = step.Run(tour);
      }

      return revised ? tour with { Plan = tour.Plan! with { Stops = [Written with { Version = 3, Earlier = [.. Written.Earlier, new StopVersion("Rounding", StopKind.Inspect, Written.Content, Now)] }] } } : tour;
   }

   private static void Check(string? expected, Func<string> actual)
   {
      if (expected is not null)
      {
         Assert.Equal(expected, actual());
      }
   }

   private static string KindOf(QuestionThread thread) => thread switch
   {
      QuestionThread.Note => "note",
      QuestionThread.Sent.Asked => "asked",
      QuestionThread.Sent.Decided => "decided",
      QuestionThread.Sent.Started => "started",
   };

   private static string KindOf(ThreadMessage message) => message switch
   {
      ThreadMessage.Question => "question",
      ThreadMessage.Question.Reply => "reply",
      ThreadMessage.AgentMessage => "agentMessage",
   };

   private static string Requests(QuestionThread thread) =>
      string.Join(";", ((QuestionThread.Sent)thread).Messages.OfType<ThreadMessage.Question>().Select(question => string.Join(",", question.Requests.Select(request => JsonNamingPolicy.CamelCase.ConvertName(request.State.ToString())))));

   private static Func<Tour, OperationResult<ThreadWrite>> Command(ThreadCommand command, params string[] ids) => tour => TourThreads.Apply(tour, command, Now, TourIds.Sequence(ids));

   private static Func<Tour, OperationResult<ThreadWrite>> Fact(ThreadFact fact) => tour => TourThreads.Settle(tour, fact);

   private static ThreadFact.Repinned Repinned(Remapped<ThreadAnchor> outcome) => new([new AnchorRemap("t1", Code, outcome)]);

   private static Outcome Refused(OperationErrorCode code, string? reason = null) => new(Refused: code, Reason: reason);

   private static ThreadCommand.Ask AskTour() => new(new ThreadAnchor.WholeTour(), "Why?", null, Bound);

   private static ActionRequest Summarize => new ActionRequest.Summarize();

   private static ActionRequest ExplainStop => new ActionRequest.Explain(new StopTarget.WholeStop("s1"));

   private static ActionRequest Reorder => new ActionRequest.Reorder(["s1"]);

   private static ActionRequest Replan => new ActionRequest.Replan();

   private static Func<Tour, ReviewerDecision> LevelChange => tour => ReviewerDecision.FamiliarityChanged.For(tour, Familiarity.Some, Familiarity.New, null);

   private static Func<Tour, ReviewerDecision> Approval => tour => ReviewerDecision.PlanApproved.For(tour, ["s1"]);

   private static Func<Tour, ReviewerDecision> Update => _ => ReviewerDecision.IterationUpdated.For(2, ["s1"]);

   // The action that the tour admits for the request, built from the tour that the command applies to.
   // A revise and a replan need the reviewer's words, and every other request goes without them.
   private static ThreadCommand Requested(Tour tour, ActionRequest request)
   {
      var text = request is ActionRequest.Replan or ActionRequest.Revise ? "Fewer stops." : "";
      return new ThreadCommand.Request(ActionRules.Admit(tour, request, text).Value ?? throw new InvalidOperationException($"The tour does not admit {request}."), text, null, Bound);
   }

   private static ThreadCommand Reported(Tour tour, Func<Tour, ReviewerDecision> decision) => new ThreadCommand.Report(decision(tour), "", null, Bound, WaitField(decision(tour)));

   private static FieldPath WaitField(ReviewerDecision decision) => decision is ReviewerDecision.IterationUpdated ? "tourId" : "familiarity";

   private static Func<Tour, OperationResult<ThreadWrite>> RequestCommand(ActionRequest request, params string[] ids) => tour => TourThreads.Apply(tour, Requested(tour, request), Now, TourIds.Sequence(ids));

   private static Func<Tour, OperationResult<ThreadWrite>> ReportCommand(Func<Tour, ReviewerDecision> decision, params string[] ids) =>
      tour => TourThreads.Apply(tour, Reported(tour, decision), Now, TourIds.Sequence(ids));

   private static Step RequestStep(ActionRequest request, string threadId = "t1", string questionId = "q1", string requestId = "r1") => new(tour => Requested(tour, request), threadId, questionId, requestId);

   private static Step ReportStep(Func<Tour, ReviewerDecision> decision, string threadId = "t1", string questionId = "q1", string requestId = "r1") =>
      new(tour => Reported(tour, decision), threadId, questionId, requestId);

   private static ThreadCommand.Start StartTour(ThreadAnchor? anchor = null) => new(anchor ?? new ThreadAnchor.WholeTour(), "Look here.", Bound);

   private static ThreadCommand.SaveNote Note(ThreadAnchor? anchor = null) => new(anchor ?? new ThreadAnchor.WholeTour(), "My words.", null);

   private static ThreadCommand.FollowUp FollowUp() => new("t1", "And?", null, Bound);

   private static ThreadCommand.SetState SetStateCommand(ReviewerThreadState state) => new("t1", state);

   private static ThreadCommand.AgentWrote AgentWrote() => new("t1", "More.", Bound);

   private static ThreadCommand.Outdate OutdateCommand() => new("t1", Bound);

   private static ThreadCommand.Reply ReplyCommand(string questionId, Recipient? author = null, ReplyResolution resolution = ReplyResolution.Done, string? text = "Here.") =>
      new(questionId, resolution, text, author ?? Bound);

   private static Step Ask(string threadId = "t1", string questionId = "q1", string requestId = "r1") => new(AskTour(), threadId, questionId, requestId);

   private static Step AskStep(ThreadCommand.Ask ask, string threadId = "t1", string questionId = "q1", string requestId = "r1") => new(ask, threadId, questionId, requestId);

   private static Step Start(ThreadAnchor? anchor = null) => new(StartTour(anchor), "t1", "m1");

   private static Step NoteStep(string threadId = "t1", ThreadAnchor? anchor = null) => new(Note(anchor), threadId);

   private static Step FollowUpStep() => new(FollowUp(), "q2", "r2");

   private static Step Reply(string questionId) => ReplyStep(questionId, "a1");

   private static Step ReplyStep(string questionId, string replyId) => new(ReplyCommand(questionId), replyId);

   private static Step ResendStep(string requestId) => new(new ThreadCommand.Resend("q1", Bound), requestId);

   private static Step Cancel(string questionId) => new(new ThreadCommand.Cancel(questionId));

   private static Step SetState(ReviewerThreadState state) => new(SetStateCommand(state));

   private static Step Outdate() => new(OutdateCommand());

   private static Step Received(string questionId) => new(new ThreadCommand.MarkReceived(questionId, Bound));

   private static Step Released() => Step.Settled(new ThreadFact.Released());

   // A setup step applies a command, or settles a fact.
   private sealed record Step
   {
      private readonly Func<Tour, Tour> _run;

      public Step(Func<Tour, ThreadCommand> build, params string[] ids)
      {
         _run = tour =>
         {
            var command = build(tour);
            var applied = TourThreads.Apply(tour, command, Now.AddMinutes(-1), TourIds.Sequence(ids));
            return applied.Value?.Next ?? throw new InvalidOperationException($"The setup step {command} was refused: {applied.Error!.Reason}");
         };
      }

      public Step(ThreadCommand command, params string[] ids)
         : this(_ => command, ids)
      {
      }

      private Step(ThreadFact fact)
      {
         _run = tour => TourThreads.Settle(tour, fact).Next;
      }

      public static Step Settled(ThreadFact fact) => new(fact);

      public Tour Run(Tour tour) => _run(tour);
   }

   private sealed record Cell(IReadOnlyList<Step> Given, Func<Tour, OperationResult<ThreadWrite>> When, Outcome Expect, string Probe = "t1", TourPin? Pin = null, bool Revised = false);

   private sealed record Outcome(
      OperationErrorCode? Refused = null,
      string? Reason = null,
      string? Changes = null,
      string? Thread = null,
      string? Requests = null,
      string? Sends = null,
      string? Messages = null,
      string? Action = null,
      string? Decision = null,
      string? NoteText = null,
      string? Received = null,
      int? AnchorVersion = null,
      bool? Carried = null);
}
