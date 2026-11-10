using System.Text.Json.Serialization;
using Lotsen.Json;

namespace Lotsen.Tours;

/// <summary>One decision that a stop asks the reviewer to make, with Claude's recommendation and the answers Claude expects.</summary>
/// <param name="Id">The check's ID, which the server gives it, unique within the tour and kept while Claude writes the check with it.</param>
/// <param name="Question">What the reviewer decides, as prose.</param>
/// <param name="Recommendation">Claude's recommended answer and its reason, as prose, or null when Claude has none. JSON leaves it out when null.</param>
/// <param name="Suggestions">The likely answers in Claude's order, or null when there are none. JSON leaves it out when null.</param>
internal sealed record StopCheck(
   string Id,
   string Question,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Recommendation = null,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<SuggestedAnswer>? Suggestions = null);

/// <summary>An answer to a check that Claude suggests.</summary>
/// <param name="Text">The answer as plain text, never rendered as prose.</param>
/// <param name="Type">What kind of answer it is, as Claude typed it.</param>
internal sealed record SuggestedAnswer(string Text, AnswerType Type);

/// <summary>What kind of answer the reviewer gives to a check.</summary>
[JsonConverter(typeof(StrictEnumConverter<AnswerType>))]
internal enum AnswerType
{
   /// <summary>A decision about the change.</summary>
   Decision,

   /// <summary>A concern about the change.</summary>
   Concern,

   /// <summary>A question that the reviewer cannot settle yet.</summary>
   OpenQuestion,
}

/// <summary>The HTML of the prose of one check, safe to insert into the UI as it is. The suggestions are plain text and have none.</summary>
/// <param name="Question">The HTML of <see cref="StopCheck.Question"/>.</param>
/// <param name="Recommendation">The HTML of <see cref="StopCheck.Recommendation"/>, or null when the check has none. JSON leaves it out when null.</param>
internal sealed record CheckHtml(string Question, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Recommendation = null);
