using System.Text.Json.Serialization;

namespace Lotsen.Operations;

/// <summary>The input of an operation that takes none.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record NoInput;

internal static class OperationInputs
{
   public const string TourIdDescription = "The tour's ID, as create_tour or list_tours returned it, or as the user gave it.";

   public const string AnswerStopIdDescription = "The ID of the stop whose content holds the check.";

   public const string CheckIdDescription = "The ID of a check of the stop's current content.";
}
