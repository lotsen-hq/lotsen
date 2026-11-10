using Lotsen.Git;
using Lotsen.Tours;

namespace Lotsen.Operations;

/// <summary>The checks of a thread's anchor and of the stop that the reviewer views, against the tour's plan.</summary>
internal static class AnchorChecks
{
   private static readonly FieldPath Anchor = "anchor";

   /// <summary>Returns why the anchor's own fields are invalid, or null when they are valid.</summary>
   public static OperationError? CheckFields(AnchorInput anchor) => anchor switch
   {
      AnchorInput.CodeRange code => StopContentRules.CheckLines(Anchor, code.Path, code.StartLine, code.EndLine) is { } fault ? OperationError.Invalid(fault) : null,
      AnchorInput.WholeFile file when !RepositoryPath.IsValid(file.Path) => OperationError.Invalid(Anchor.Then("path"), $"give {RepositoryPath.Expected}."),
      _ => null,
   };

   /// <summary>Returns why the plan does not hold the stops that the anchor and the viewed stop name, or null when it holds them.</summary>
   /// <remarks>A stop or stop text anchor follows <see cref="StopTargets.Resolve"/>.</remarks>
   public static OperationError? CheckPlanReferences(Tour tour, AnchorInput anchor, string? stopId) => CheckViewedStop(tour, stopId) ?? anchor switch
   {
      AnchorInput.WholeStop stop => StopTargets.Resolve(tour, new StopTarget.WholeStop(stop.StopId), Anchor).Error,
      AnchorInput.StopText selected => StopTargets.Resolve(tour, new StopTarget.StopText(selected.StopId, selected.Field, selected.Text, selected.Version), Anchor).Error,
      _ => null,
   };

   /// <summary>Returns why the plan has no stop that the reviewer views, or null when it has the stop or none is viewed.</summary>
   public static OperationError? CheckViewedStop(Tour tour, string? stopId) =>
      stopId is { } viewed && tour.FindStop(viewed) is null ? OperationError.Invalid("stopId", StopTargets.NoStop(viewed)) : null;
}
