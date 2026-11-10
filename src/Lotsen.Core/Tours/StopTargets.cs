namespace Lotsen.Tours;

/// <summary>Resolves a stop or stop text that an input names to its thread anchor on the tour.</summary>
internal static class StopTargets
{
   /// <summary>Returns the target's anchor on the tour.</summary>
   /// <returns>
   /// The anchor: a whole stop of the plan, or text in a prose field of a written stop, on the stop's current version. The refusal when the target's text is blank, the plan has no such stop, a
   /// stop text target names a stop without content or a field that the content lacks, or the target names another version than the stop's current one.
   /// </returns>
   /// <remarks>A whole-stop target may name a pending stop, and the check then ignores the target's version, because a pending stop has none.</remarks>
   /// <param name="tour">The tour.</param>
   /// <param name="target">The target.</param>
   /// <param name="field">The input's field that holds the target, which each refusal names.</param>
   public static OperationResult<ThreadAnchor> Resolve(Tour tour, StopTarget target, FieldPath field) => target switch
   {
      StopTarget.WholeStop stop => tour.FindStop(stop.StopId) switch
      {
         null => OperationError.Invalid(field.Then("stopId"), NoStop(stop.StopId)),
         Stop.Written written when stop.Version is { } version && version != written.Version => OtherVersion(field, written, version),
         _ => new ThreadAnchor.WholeStop(stop.StopId),
      },
      StopTarget.StopText selected => ResolveText(tour, selected, field),
   };

   /// <summary>Returns the reason that the plan has no stop with the ID, for a refusal of an input that names it.</summary>
   public static string NoStop(string stopId) => $"the plan has no stop '{stopId}'. Read the tour again and ask about a stop that it has.";

   private static OperationResult<ThreadAnchor> ResolveText(Tour tour, StopTarget.StopText selected, FieldPath field)
   {
      if (string.IsNullOrWhiteSpace(selected.Text))
      {
         return OperationError.Invalid(field.Then("text"), "a stopText anchor needs the text that the reviewer selected.");
      }

      if (tour.FindStop(selected.StopId) is not { } found)
      {
         return OperationError.Invalid(field.Then("stopId"), NoStop(selected.StopId));
      }

      if (found is not Stop.Written { Content: var content } written)
      {
         return OperationError.Invalid(field.Then("stopId"), $"stop '{selected.StopId}' has no content yet, so it has no text to select.");
      }

      if (selected.Version is { } selectedIn && selectedIn != written.Version)
      {
         return OtherVersion(field, written, selectedIn);
      }

      return StopProse.Read(content, selected.Field) is null
         ? OperationError.Invalid(
            field.Then("field"),
            $"stop '{selected.StopId}' has no prose field '{selected.Field}'. Give {string.Join(", ", ProseField.Kinds.SkipLast(1))} or {ProseField.Kinds[^1]} for a field that the stop has.")
         : new ThreadAnchor.StopText(selected.StopId, selected.Field, selected.Text, written.Version);
   }

   private static OperationError OtherVersion(FieldPath field, Stop.Written stop, int version) =>
      OperationError.Invalid(field.Then("version"), $"stop '{stop.Id}' is at version {stop.Version}, not {version}. Read the tour again and use the stop's current version.");
}
