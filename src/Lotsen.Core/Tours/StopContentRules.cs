using Lotsen.Git;

namespace Lotsen.Tours;

/// <summary>The rules that a stop's content follows under its kind, whether it is the stop's current content or an earlier version: a headline, one primary passage, the kind's needs and whole lines.</summary>
/// <remarks>
/// Each fault's path is relative to the content, and it names the field as <c>put_stop</c>'s input does, because the content keeps the input's order of passages and checks. The checks against
/// the pinned commits, such as whether a primary passage touches the diff, need git, so <c>put_stop</c> runs them apart.
/// </remarks>
internal static class StopContentRules
{
   private static readonly FieldPath Passages = "passages";
   private static readonly FieldPath Checks = "checks";
   private static readonly FieldPath Tags = "tags";

   /// <summary>Returns the first rule that the content breaks under the kind, or null when it follows them all.</summary>
   public static RuleFault? Check(StopKind kind, StopContent content) => CheckFields(content) ?? CheckKind(kind, content);

   /// <summary>Returns the first rule that the content breaks whatever its kind, or null when it follows them all: the headline, the elements, the checks and the passages' lines.</summary>
   public static RuleFault? CheckFields(StopContent content)
   {
      if (string.IsNullOrWhiteSpace(content.Headline))
      {
         return new("headline", "a stop needs a headline: its main point, which the reviewer reads first.");
      }

      if ((NullElement(content.Passages, Passages) ?? NullElement(content.Checks, Checks) ?? NullElement(content.Tags, Tags) ?? CheckChecks(content.Checks ?? [])) is { } fault)
      {
         return fault;
      }

      for (var i = 0; i < content.Passages.Count; i++)
      {
         var passage = content.Passages[i];
         if (CheckLines(Passages.At(i), passage.Path, passage.StartLine, passage.EndLine) is { } linesFault)
         {
            return linesFault;
         }
      }

      var primaries = content.Passages.Count(passage => passage.Role == PassageRole.Primary);
      return content.Passages.Count > 0 && primaries != 1
         ? new(Passages, $"exactly one passage must be primary, but {primaries} are. Make the passage the stop is about primary and the others related.")
         : null;
   }

   /// <summary>Returns the first rule that whole lines of a file break: the path is a repository path, the lines start at 1, and the end is not before the start.</summary>
   /// <param name="field">The field of the lines, which each fault names with its own property, as in <c>passages[0].path</c>.</param>
   public static RuleFault? CheckLines(FieldPath field, string path, int startLine, int endLine)
   {
      if (!RepositoryPath.IsValid(path))
      {
         return new(field.Then("path"), $"give {RepositoryPath.Expected}.");
      }

      if (startLine < 1)
      {
         return new(field.Then("startLine"), "a line number starts at 1.");
      }

      return endLine < startLine ? new(field.Then("endLine"), $"the lines end at line {endLine}, before the start line {startLine}.") : null;
   }

   /// <summary>Returns whether the primary passage of a stop of this kind must touch the diff: an overview and a context stop may point anywhere.</summary>
   /// <remarks><c>put_stop</c> checks the rule against the pinned commits, and the remap makes a stop stale whose primary passage breaks it on a new pin.</remarks>
   public static bool PrimaryTouchesDiff(StopKind kind) => kind is StopKind.Inspect or StopKind.Risk or StopKind.Question;

   /// <summary>Returns the kind's name as the JSON contract writes it, after its indefinite article, as in "an inspect".</summary>
   public static string KindWithArticle(StopKind kind) => kind switch
   {
      StopKind.Overview or StopKind.Inspect => $"an {kind.ToString().ToLowerInvariant()}",
      _ => $"a {kind.ToString().ToLowerInvariant()}",
   };

   // An overview must be the first stop. That is a plan rule, so PlanRules checks it.
   private static RuleFault? CheckKind(StopKind kind, StopContent content)
   {
      if (kind == StopKind.Risk && string.IsNullOrWhiteSpace(content.Scenario))
      {
         return new("scenario", "a risk stop needs a scenario: how the risk could happen.");
      }

      if (kind == StopKind.Question && content.Checks is null or [])
      {
         return new(Checks, "a question stop needs at least one check for the reviewer to answer.");
      }

      return kind != StopKind.Overview && content.Passages.Count == 0 ? new(Passages, $"{KindWithArticle(kind)} stop needs at least one passage. Only an overview may have none.") : null;
   }

   // A check's ID names it within the tour, and an answer and a stop text anchor find the check by it.
   private static RuleFault? CheckChecks(IReadOnlyList<StopCheck> checks)
   {
      var ids = new HashSet<string>(StringComparer.Ordinal);
      for (var i = 0; i < checks.Count; i++)
      {
         var field = Checks.At(i);
         if (string.IsNullOrWhiteSpace(checks[i].Question))
         {
            return new(field.Then("question"), "a check needs a question: the one decision that the reviewer makes.");
         }

         if (NullElement(checks[i].Suggestions, field.Then("suggestions")) is { } fault)
         {
            return fault;
         }

         if (string.IsNullOrEmpty(checks[i].Id))
         {
            return new(field.Then("id"), "a check needs an ID.");
         }

         if (!ids.Add(checks[i].Id))
         {
            return new(field.Then("id"), $"the check '{checks[i].Id}' appears twice. Give each check's ID once.");
         }
      }

      return null;
   }

   // The serializer checks nullability on properties but not on collection elements.
   private static RuleFault? NullElement<T>(IReadOnlyList<T>? items, FieldPath field)
      where T : class
   {
      for (var i = 0; i < (items?.Count ?? 0); i++)
      {
         if (items![i] is null)
         {
            return new(field.At(i), "an element cannot be null.");
         }
      }

      return null;
   }
}
