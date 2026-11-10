using System.Text.Json.Serialization;
using Lotsen.Json;
using Lotsen.Pinning;
using Lotsen.Sessions;

namespace Lotsen.Tours;

/// <summary>A write of the reviewer or of an agent session to a tour's threads, which <see cref="TourThreads.Apply"/> may refuse.</summary>
/// <remarks>
/// A command that adds a pending request names its <see cref="Recipient"/>, and an agent session's command names its author, so neither can be left out. The other commands implement
/// <see cref="ISendsNothing"/>.
/// </remarks>
internal closed record ThreadCommand
{
   private ThreadCommand()
   {
   }

   /// <summary>A command that queues no question for any session.</summary>
   internal interface ISendsNothing;

   /// <summary>The reviewer's question as a new thread, with a pending request to the recipient.</summary>
   /// <param name="Anchor">Where the reviewer asked, as the feature read it.</param>
   /// <param name="Text">The reviewer's words.</param>
   /// <param name="StopId">The stop that the reviewer viewed, or null for none.</param>
   /// <param name="Recipient">The bound session that the question goes to.</param>
   internal sealed record Ask(ThreadAnchor Anchor, string Text, string? StopId, Recipient Recipient) : ThreadCommand;

   /// <summary>The reviewer's tour action as a new asked thread at its anchor, with a pending request to the recipient.</summary>
   /// <param name="Admitted">The action and its anchor, as <see cref="ActionRules.Admit"/> built them.</param>
   /// <param name="Text">The reviewer's words, or empty without words.</param>
   /// <param name="StopId">The stop that the reviewer viewed, or null for none.</param>
   /// <param name="Recipient">The bound session that the question goes to.</param>
   internal sealed record Request(ActionRules.Admitted Admitted, string Text, string? StopId, Recipient Recipient) : ThreadCommand;

   /// <summary>The report of the reviewer's decision as a new decided thread, with a pending request to the recipient.</summary>
   /// <param name="Decision">The decision, from its kind's factory.</param>
   /// <param name="Text">The reviewer's words, or empty without words.</param>
   /// <param name="StopId">The stop that the reviewer viewed, or null for none.</param>
   /// <param name="Recipient">The bound session that the question goes to.</param>
   /// <param name="WaitField">The input's field that a refusal names, while another thread of the decision's wait group waits.</param>
   internal sealed record Report(ReviewerDecision Decision, string Text, string? StopId, Recipient Recipient, FieldPath WaitField) : ThreadCommand;

   /// <summary>The bound session's agent message as a new thread.</summary>
   /// <param name="Anchor">Where the session starts the thread, as the feature read it.</param>
   /// <param name="Text">The message as prose.</param>
   /// <param name="Author">The session that writes it.</param>
   internal sealed record Start(ThreadAnchor Anchor, string Text, Recipient Author) : ThreadCommand, ISendsNothing;

   /// <summary>The reviewer's words as a new note.</summary>
   /// <param name="Anchor">Where the reviewer wrote the note, as the feature read it.</param>
   /// <param name="Text">The reviewer's words.</param>
   /// <param name="StopId">The stop that the reviewer viewed, or null for none.</param>
   internal sealed record SaveNote(ThreadAnchor Anchor, string Text, string? StopId) : ThreadCommand, ISendsNothing;

   /// <summary>New words for a note.</summary>
   internal sealed record EditNote(string ThreadId, string Text) : ThreadCommand, ISendsNothing;

   /// <summary>The removal of a note.</summary>
   internal sealed record DiscardNote(string ThreadId) : ThreadCommand, ISendsNothing;

   /// <summary>The sending of notes, each as its thread's first question with a pending request to the recipient, in the order of <paramref name="ThreadIds"/>.</summary>
   /// <remarks>Each question takes the stop that its note holds, which no check reads against the current plan.</remarks>
   /// <param name="ThreadIds">The IDs of the notes' threads, each once.</param>
   /// <param name="Recipient">The bound session that the questions go to.</param>
   internal sealed record SendNotes(IReadOnlyList<string> ThreadIds, Recipient Recipient) : ThreadCommand;

   /// <summary>The reviewer's follow-up, a new question in a thread with a pending request to the recipient.</summary>
   internal sealed record FollowUp(string ThreadId, string Text, string? StopId, Recipient Recipient) : ThreadCommand;

   /// <summary>The sending of a thread's last question again, as a new pending request to the recipient.</summary>
   internal sealed record Resend(string QuestionId, Recipient Recipient) : ThreadCommand;

   /// <summary>The reviewer's cancel of a question's pending request.</summary>
   internal sealed record Cancel(string QuestionId) : ThreadCommand, ISendsNothing;

   /// <summary>The reviewer's open or resolve of a thread.</summary>
   internal sealed record SetState(string ThreadId, ReviewerThreadState State) : ThreadCommand, ISendsNothing;

   /// <summary>The bound session's agent message at the end of a thread.</summary>
   internal sealed record AgentWrote(string ThreadId, string Text, Recipient Author) : ThreadCommand, ISendsNothing;

   /// <summary>A session's reply to a question, which resolves the first request of the question that went to the author.</summary>
   /// <param name="QuestionId">The question's ID.</param>
   /// <param name="Resolution">How the reply resolves the request.</param>
   /// <param name="Text">The answer as prose, or null for a decline without words.</param>
   /// <param name="Author">The session that replies, which need not be the bound session.</param>
   internal sealed record Reply(string QuestionId, ReplyResolution Resolution, string? Text, Recipient Author) : ThreadCommand, ISendsNothing;

   /// <summary>The bound session's mark that a thread is outdated.</summary>
   internal sealed record Outdate(string ThreadId, Recipient Author) : ThreadCommand, ISendsNothing;

   /// <summary>The bound session's removal of a thread.</summary>
   internal sealed record Delete(string ThreadId, Recipient Author) : ThreadCommand, ISendsNothing;

   /// <summary>The reviewer's removal of any thread, a note included. It refuses a thread whose last question waits, so the caller cancels that question first.</summary>
   internal sealed record Remove(string ThreadId) : ThreadCommand, ISendsNothing;

   /// <summary>A session's fetch of a question, which marks the session's requests of it received.</summary>
   internal sealed record MarkReceived(string QuestionId, Recipient Session) : ThreadCommand, ISendsNothing;
}

/// <summary>What happened to a tour that its threads follow, which <see cref="TourThreads.Settle(Tour, ThreadFact, TourIds)"/> never refuses and may ignore for a thread.</summary>
internal closed record ThreadFact
{
   private ThreadFact()
   {
   }

   /// <summary>A <c>put_stop</c> revision of a stop.</summary>
   /// <param name="Replaced">The stop as it was, at the version that the revision replaced.</param>
   /// <param name="Written">The stop with its new content.</param>
   internal sealed record StopRevised(Stop.Written Replaced, Stop.Written Written) : ThreadFact;

   /// <summary>An iteration update, with where the remap put each code and file anchor.</summary>
   internal sealed record Repinned(IReadOnlyList<AnchorRemap> Anchors) : ThreadFact;

   /// <summary>The end of the binding that the tour's pending requests went to, or a start of the server.</summary>
   internal sealed record Released : ThreadFact;
}

/// <summary>The states of a thread that the reviewer sets with <c>set_thread_state</c>. Only the bound session marks a thread <see cref="QuestionThreadState.Outdated"/>.</summary>
[JsonConverter(typeof(StrictEnumConverter<ReviewerThreadState>))]
internal enum ReviewerThreadState
{
   /// <summary>The state <see cref="QuestionThreadState.Open"/>.</summary>
   Open,

   /// <summary>The state <see cref="QuestionThreadState.Resolved"/>.</summary>
   Resolved,
}

/// <summary>How a reply resolves a request: the states of <see cref="RequestState"/> that a reply may give.</summary>
[JsonConverter(typeof(StrictEnumConverter<ReplyResolution>))]
internal enum ReplyResolution
{
   /// <summary>The answer is complete, or the session did what the reviewer asked.</summary>
   Done,

   /// <summary>The session needs the reviewer's answer before it can continue.</summary>
   NeedsAnswer,

   /// <summary>The session will not do what the reviewer asked.</summary>
   Declined,
}
