namespace Lotsen.Tours;

/// <summary>One prose field of a stop's content, named as a stop text anchor names it.</summary>
/// <remarks>A check's field names the check by its ID, so a revision that moves a check within the content leaves the field's name as it was.</remarks>
internal closed record ProseField
{
   /// <summary>The description of a property that names a prose field, for an input schema.</summary>
   public const string SchemaDescription =
      $"The prose field of the stop's content: {HeadlineName}, {HowName}, {WhyName}, {DesignChoicesName}, {PinnedByName}, {ScenarioName}, or {ChecksName}[<id>].{QuestionName} or "
      + $"{ChecksName}[<id>].{RecommendationName} for the check with that id.";

   private const string HeadlineName = "headline";
   private const string HowName = "how";
   private const string WhyName = "why";
   private const string DesignChoicesName = "designChoices";
   private const string PinnedByName = "pinnedBy";
   private const string ScenarioName = "scenario";
   private const string ChecksName = "checks";
   private const string QuestionName = "question";
   private const string RecommendationName = "recommendation";

   private static readonly FieldPath Checks = ChecksName;

   // Each field outside a check, from the one deconstruction in StopProse, so a field that StopProse handles joins Parse and Kinds.
   private static readonly IReadOnlyList<ProseField> Simple = StopProse.Slots(new StopContent("", []));

   private ProseField()
   {
   }

   /// <summary>The path of each kind of prose field, with a check's ID written as <c>&lt;id&gt;</c>, in the order that served text lists them.</summary>
   public static IReadOnlyList<FieldPath> Kinds { get; } =
      [.. StopProse.Slots(new StopContent("", [], Checks: [new StopCheck("<id>", "")])).OrderBy(field => field is CheckQuestion or CheckRecommendation).Select(field => field.Path)];

   /// <summary>The field's path in the content, such as <c>how</c> or <c>checks[&lt;check ID&gt;].question</c>.</summary>
   public FieldPath Path => PathOf(Checks.Key);

   /// <summary>Returns the field's path, with the field's check at the path that <paramref name="check"/> gives the check's ID.</summary>
   /// <remarks><c>put_stop</c>'s input names a check by its index, as in <c>checks[2].question</c>, so its faults name a check's field through this.</remarks>
   public FieldPath PathOf(Func<string, FieldPath> check) => this switch
   {
      Headline => HeadlineName,
      How => HowName,
      Why => WhyName,
      DesignChoices => DesignChoicesName,
      PinnedBy => PinnedByName,
      Scenario => ScenarioName,
      CheckQuestion(var id) => check(id).Then(QuestionName),
      CheckRecommendation(var id) => check(id).Then(RecommendationName),
   };

   /// <summary>Returns the prose field that the path names, as <see cref="Path"/> renders it, or null when the path names no prose field.</summary>
   public static ProseField? Parse(FieldPath path)
   {
      if (Simple.FirstOrDefault(field => field.Path == path) is { } simple)
      {
         return simple;
      }

      // A check's ID may hold any character, so the ID runs to the last "]." of the path.
      var text = path.ToString();
      var prefix = $"{ChecksName}[";
      var close = text.LastIndexOf("].", StringComparison.Ordinal);
      if (!text.StartsWith(prefix, StringComparison.Ordinal) || close < prefix.Length)
      {
         return null;
      }

      var id = text[prefix.Length..close];
      ProseField[] candidates = [new CheckQuestion(id), new CheckRecommendation(id)];
      return candidates.FirstOrDefault(field => field.Path == path);
   }

   /// <summary>The headline, which every content has.</summary>
   internal sealed record Headline : ProseField;

   /// <summary>How the code works.</summary>
   internal sealed record How : ProseField;

   /// <summary>Why the change looks the way it does.</summary>
   internal sealed record Why : ProseField;

   /// <summary>The design choices behind the change.</summary>
   internal sealed record DesignChoices : ProseField;

   /// <summary>The tests that pin the behavior.</summary>
   internal sealed record PinnedBy : ProseField;

   /// <summary>How the risk could happen.</summary>
   internal sealed record Scenario : ProseField;

   /// <summary>The question of one check, which every check has.</summary>
   /// <param name="CheckId">The check's ID.</param>
   internal sealed record CheckQuestion(string CheckId) : ProseField;

   /// <summary>The recommendation of one check.</summary>
   /// <param name="CheckId">The check's ID.</param>
   internal sealed record CheckRecommendation(string CheckId) : ProseField;
}

/// <summary>The prose fields of a stop's content: the one definition that <c>put_stop</c>'s reference checks, the remap, the repin, stop text anchors and the HTML read them by.</summary>
internal static class StopProse
{
   /// <summary>Returns the content with each prose field that is not null mapped.</summary>
   /// <remarks>It maps the fields in this order: the headline, how, why, the design choices, pinned by, each check's question and recommendation, then the scenario.</remarks>
   public static StopContent Map(StopContent content, Func<ProseField, string, string> map) => Visit(content, (field, prose) => prose is null ? null : map(field, prose));

   /// <summary>Returns each prose field that the content can hold, null or not, in the order of <see cref="Map"/>: every field outside a check, and the fields of each check that it has.</summary>
   public static IReadOnlyList<ProseField> Slots(StopContent content)
   {
      var fields = new List<ProseField>();
      Visit(content, (field, prose) =>
      {
         fields.Add(field);
         return prose;
      });
      return fields;
   }

   /// <summary>Returns each prose field of the content that is not null, with its prose, in the order of <see cref="Map"/>.</summary>
   public static IReadOnlyList<(ProseField Field, string Prose)> Fields(StopContent content)
   {
      var fields = new List<(ProseField Field, string Prose)>();
      Map(content, (field, prose) =>
      {
         fields.Add((field, prose));
         return prose;
      });
      return fields;
   }

   /// <summary>Returns the prose of one field of the content.</summary>
   /// <returns>The field's Markdown, or null when the field is null or names a check that the content lacks.</returns>
   public static string? Read(StopContent content, ProseField field) => Fields(content).FirstOrDefault(entry => entry.Field == field).Prose;

   /// <summary>Returns the prose of the field that a stop text anchor names by its path.</summary>
   /// <returns>The field's Markdown, or null when the path names no prose field, the field is null, or the path names a check that the content lacks.</returns>
   public static string? Read(StopContent content, string field) => ProseField.Parse(FieldPath.Parse(field)) is { } prose ? Read(content, prose) : null;

   // Rebuilds the content with each prose field, null or not, passed through the function. This is the one deconstruction of StopContent and of StopCheck. A deconstruction takes one
   // variable for each parameter of the record, so a new parameter breaks it until this method handles the new field.
   private static StopContent Visit(StopContent content, Func<ProseField, string?, string?> visit)
   {
      var (headline, passages, how, why, designChoices, pinnedBy, checks, scenario, tags, placement) = content;
      return new StopContent(
         visit(new ProseField.Headline(), headline)!,
         passages,
         visit(new ProseField.How(), how),
         visit(new ProseField.Why(), why),
         visit(new ProseField.DesignChoices(), designChoices),
         visit(new ProseField.PinnedBy(), pinnedBy),
         checks?.Select(check =>
         {
            var (id, question, recommendation, suggestions) = check;
            return new StopCheck(id, visit(new ProseField.CheckQuestion(id), question)!, visit(new ProseField.CheckRecommendation(id), recommendation), suggestions);
         }).ToList(),
         visit(new ProseField.Scenario(), scenario),
         tags,
         placement);
   }
}
