namespace Lotsen.Tours;

/// <summary>
/// The one table of the stop kinds and the checks that a repository tour's purpose allows: a risk stop in a prepare or an evaluate tour, a question stop and checks only in an evaluate tour.
/// </summary>
/// <remarks>
/// <see cref="TourRules"/> holds every stored tour to the table, and <c>set_plan</c> and <c>put_stop</c> reach it through <see cref="TourRules.CheckPlan"/> and <see cref="TourRules.CheckContent"/>.
/// A pull request's tour has no purpose, so the table allows it every kind and checks on any stop.
/// </remarks>
internal static class PurposeRules
{
   /// <summary>Returns whether a tour of the purpose holds stops of the kind.</summary>
   public static bool Allows(TourPurpose purpose, StopKind kind) => kind switch
   {
      StopKind.Risk => purpose is TourPurpose.Prepare or TourPurpose.Evaluate,
      StopKind.Question => purpose is TourPurpose.Evaluate,
      _ => true,
   };

   /// <summary>Returns whether the stops of a tour of the purpose ask the reviewer checks, on a stop of any kind.</summary>
   public static bool AsksChecks(TourPurpose purpose) => purpose is TourPurpose.Evaluate;

   /// <summary>Returns the fault of a stop of the kind at the field in a tour of the purpose, or null when the purpose allows the kind or the tour has no purpose.</summary>
   public static RuleFault? CheckKind(TourPurpose? purpose, StopKind kind, FieldPath field)
   {
      if (purpose is not { } of || Allows(of, kind))
      {
         return null;
      }

      var stop = StopContentRules.KindWithArticle(kind);
      var belongs = Join(Enum.GetValues<TourPurpose>().Where(other => Allows(other, kind)));
      return new(field, $"{WithArticle(of)} tour has no {Name(kind)} stops. {char.ToUpperInvariant(stop[0])}{stop[1..]} stop belongs in {belongs} tour, so make it a context or an inspect stop.");
   }

   /// <summary>Returns the fault of stop content with checks in a tour of the purpose, or null when the content has none, the purpose asks checks or the tour has no purpose.</summary>
   public static RuleFault? CheckChecks(TourPurpose? purpose, StopContent content) =>
      purpose is { } of && !AsksChecks(of) && content.Checks is { Count: > 0 }
         ? new("checks", $"{WithArticle(of)} tour asks no checks. Only {Join(Enum.GetValues<TourPurpose>().Where(AsksChecks))} tour does, so leave checks out.")
         : null;

   // The purposes as the JSON contract names them, each after its indefinite article, as in "a prepare or an evaluate".
   private static string Join(IEnumerable<TourPurpose> purposes)
   {
      var named = purposes.Select(WithArticle).ToList();
      return named.Count == 1 ? named[0] : $"{string.Join(", ", named[..^1])} or {named[^1]}";
   }

   private static string WithArticle(TourPurpose purpose) => purpose is TourPurpose.Orient or TourPurpose.Evaluate ? $"an {Name(purpose)}" : $"a {Name(purpose)}";

   private static string Name<T>(T value)
      where T : struct, Enum => value.ToString().ToLowerInvariant();
}
