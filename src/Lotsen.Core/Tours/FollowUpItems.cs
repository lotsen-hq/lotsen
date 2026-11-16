using System.Diagnostics;
using System.Text.Json.Serialization;

namespace Lotsen.Tours;

/// <summary>A sent answer that a follow-up action hands to Claude, as the review summary held it when the server stored the action.</summary>
/// <param name="StopId">The ID of the stop whose content holds the check.</param>
/// <param name="CheckId">The check's ID.</param>
/// <param name="Text">The reviewer's answer as plain text.</param>
/// <param name="Type">The answer's type, which the reviewer set.</param>
/// <param name="StopTitle">The stop's title, or null when the plan no longer had the stop. JSON leaves it out when null.</param>
/// <param name="Question">The check's question as prose, or null when the stop's content no longer held the check. JSON leaves it out when null.</param>
internal sealed record FollowUpAnswer(
   string StopId,
   string CheckId,
   string Text,
   AnswerType Type,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StopTitle = null,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Question = null)
{
   /// <summary>Returns the answers of a summary's group, in its order, without their state, which the action outlives.</summary>
   public static IReadOnlyList<FollowUpAnswer> All(IReadOnlyList<SummaryAnswer> answers) =>
      [.. answers.Select(answer => new FollowUpAnswer(answer.StopId, answer.CheckId, answer.Text, answer.Type, answer.StopTitle, answer.Question))];
}

/// <summary>A risk stop that a follow-up action hands to Claude as a concern, as the review summary held it when the server stored the action.</summary>
/// <param name="StopId">The stop's ID.</param>
/// <param name="Title">The stop's title.</param>
/// <param name="Scenario">How the risk could happen, as prose.</param>
internal sealed record FollowUpRisk(string StopId, string Title, string Scenario);

/// <summary>
/// A sent thread with an answer type that a follow-up action hands to Claude, as the review summary held it when the server stored the action. The thread is the reviewer's own finding, in the
/// reviewer's words.
/// </summary>
/// <param name="ThreadId">The thread's ID.</param>
/// <param name="Anchor">The thread's anchor as stored.</param>
/// <param name="Text">The thread's first question, as plain text.</param>
/// <param name="Type">The thread's answer type, which the reviewer set.</param>
/// <param name="State">The thread's state when the server stored the action.</param>
/// <param name="StopId">The stop that the thread is about, as <see cref="SummaryThread.StopId"/> names it, or null for none. JSON leaves it out when null.</param>
/// <param name="StopTitle">The stop's title, or null when the thread is about no stop that the plan has. JSON leaves it out when null.</param>
internal sealed record FollowUpThread(
   string ThreadId,
   ThreadAnchor Anchor,
   string Text,
   AnswerType Type,
   ReviewerThreadState State,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StopId = null,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StopTitle = null)
{
   /// <summary>Returns the threads of a summary's group that a follow-up action hands to Claude, in the group's order.</summary>
   /// <remarks>An outdated thread never goes, as an outdated answer never does, because its anchor or its point may no longer hold.</remarks>
   /// <param name="threads">The summary's group, or null for none.</param>
   /// <param name="withResolved">Whether a thread that the reviewer resolved goes too.</param>
   /// <returns>The threads, or null when none goes.</returns>
   public static IReadOnlyList<FollowUpThread>? Handed(IReadOnlyList<SummaryTypedThread>? threads, bool withResolved) =>
      NoneAsNull(
      [
         .. (threads ?? [])
            .Select(thread => Handable(thread.State, withResolved) is { } state ? new FollowUpThread(thread.Id, thread.Anchor, thread.Text, thread.Type, state, thread.StopId, thread.StopTitle) : null)
            .OfType<FollowUpThread>(),
      ]);

   // The state that an item holds, or null for a thread that does not go.
   private static ReviewerThreadState? Handable(QuestionThreadState state, bool withResolved) => state switch
   {
      QuestionThreadState.Open => ReviewerThreadState.Open,
      QuestionThreadState.Resolved => withResolved ? ReviewerThreadState.Resolved : null,
      QuestionThreadState.Outdated => null,
      _ => throw new UnreachableException($"The thread state {state} has no rule for a follow-up action."),
   };

   /// <summary>Returns the threads, or null when there are none.</summary>
   public static IReadOnlyList<FollowUpThread>? NoneAsNull(IReadOnlyList<FollowUpThread>? threads) => threads is { Count: > 0 } ? threads : null;
}
