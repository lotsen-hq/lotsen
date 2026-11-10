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
