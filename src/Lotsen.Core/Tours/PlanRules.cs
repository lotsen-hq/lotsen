using Lotsen.Git;

namespace Lotsen.Tours;

/// <summary>
/// The rules that every plan of a stored tour follows: the chapters, the optional parts with the chapters in part order, the stops in chapter order with the overview first, and the file marks.
/// </summary>
/// <remarks>
/// Each fault's path is relative to the plan, and it names the field as <c>set_plan</c>'s input does, because the plan keeps the input's order of parts, chapters, stops and file marks. The input's own
/// limits, such as the length of a file mark's reason, are the operation's, so a stricter limit never makes a stored tour unreadable.
/// </remarks>
internal static class PlanRules
{
   private static readonly FieldPath Chapters = "chapters";
   private static readonly FieldPath Parts = "parts";
   private static readonly FieldPath Stops = "stops";
   private static readonly FieldPath FileMarks = "fileMarks";

   /// <summary>Returns the first rule that the plan breaks, or null when it follows them all.</summary>
   public static RuleFault? Check(Plan plan) =>
      CheckChapters(plan.Chapters) ?? CheckParts(plan.Parts, plan.Chapters) ?? CheckStops(plan.Stops, plan.Chapters) ?? CheckFileMarks(plan.FileMarks);

   /// <summary>Returns why <paramref name="stopIds"/> is not every stop of the plan once, as a proposed order must be, or null when it is.</summary>
   /// <remarks>It checks a reviewer's proposed order, which no stored plan holds. The fault's path is relative to the list of IDs.</remarks>
   /// <param name="plan">The tour's plan, or null for a tour without one, of which every proposed order leaves nothing out.</param>
   /// <param name="stopIds">The proposed order.</param>
   public static RuleFault? CheckOrder(Plan? plan, IReadOnlyList<string> stopIds)
   {
      var planned = (plan?.Stops ?? []).Select(stop => stop.Id).ToHashSet(StringComparer.Ordinal);
      var seen = new HashSet<string>(StringComparer.Ordinal);
      for (var i = 0; i < stopIds.Count; i++)
      {
         // The serializer checks nullability on properties but not on collection elements.
         if (stopIds[i] is not { } id)
         {
            return new(FieldPath.Root.At(i), "a stop ID cannot be null.");
         }

         if (!planned.Contains(id))
         {
            return new(FieldPath.Root.At(i), $"the plan has no stop '{id}'. The plan may have changed: read the tour again and propose an order of its stops.");
         }

         if (!seen.Add(id))
         {
            return new(FieldPath.Root.At(i), $"stop '{id}' appears more than once. Give each stop of the plan once.");
         }
      }

      return plan?.Stops.FirstOrDefault(stop => !seen.Contains(stop.Id)) is { } left
         ? new(FieldPath.Root, $"the proposed order leaves out stop '{left.Id}'. The plan may have changed: read the tour again and give each of its stops once.")
         : null;
   }

   private static RuleFault? CheckChapters(IReadOnlyList<Chapter> chapters)
   {
      if (chapters.Count == 0)
      {
         return new(Chapters, "a plan needs at least one chapter.");
      }

      var ids = new HashSet<string>(StringComparer.Ordinal);
      for (var i = 0; i < chapters.Count; i++)
      {
         // The serializer checks nullability on properties but not on collection elements.
         if (chapters[i] is not { } chapter)
         {
            return new(Chapters.At(i), "a chapter cannot be null.");
         }

         if (string.IsNullOrWhiteSpace(chapter.Id))
         {
            return new(Chapters.At(i).Then("id"), "a chapter ID cannot be empty.");
         }

         if (string.IsNullOrWhiteSpace(chapter.Title))
         {
            return new(Chapters.At(i).Then("title"), "a chapter title cannot be empty.");
         }

         if (!ids.Add(chapter.Id))
         {
            return new(Chapters.At(i).Then("id"), $"another chapter already has the ID '{chapter.Id}'.");
         }
      }

      return null;
   }

   // Call it after CheckChapters, so no chapter is null.
   private static RuleFault? CheckParts(IReadOnlyList<Part>? parts, IReadOnlyList<Chapter> chapters)
   {
      if (parts is null)
      {
         var named = chapters.ToList().FindIndex(chapter => chapter.Part is not null);
         return named < 0
            ? null
            : new(Chapters.At(named).Then("part"), "the plan has no parts, so no chapter names one. Give parts to group the chapters, or leave out part on every chapter.");
      }

      if (parts.Count == 0)
      {
         return new(Parts, "a plan with parts needs at least one part. Leave out parts for a plan without parts.");
      }

      var ids = new HashSet<string>(StringComparer.Ordinal);
      for (var i = 0; i < parts.Count; i++)
      {
         // The serializer checks nullability on properties but not on collection elements.
         if (parts[i] is not { } part)
         {
            return new(Parts.At(i), "a part cannot be null.");
         }

         if (string.IsNullOrWhiteSpace(part.Id))
         {
            return new(Parts.At(i).Then("id"), "a part ID cannot be empty.");
         }

         if (string.IsNullOrWhiteSpace(part.Title))
         {
            return new(Parts.At(i).Then("title"), "a part title cannot be empty.");
         }

         if (!ids.Add(part.Id))
         {
            return new(Parts.At(i).Then("id"), $"another part already has the ID '{part.Id}'.");
         }
      }

      var partIndex = parts.Select((part, index) => (part.Id, index)).ToDictionary(pair => pair.Id, pair => pair.index, StringComparer.Ordinal);
      var currentPart = -1;
      for (var i = 0; i < chapters.Count; i++)
      {
         var field = Chapters.At(i).Then("part");
         if (chapters[i].Part is not { } id)
         {
            return new(field, $"in a plan with parts, every chapter names its part. The part IDs are {Quoted(parts.Select(part => part.Id))}.");
         }

         if (!partIndex.TryGetValue(id, out var index))
         {
            return new(field, $"no part has the ID '{id}'. The part IDs are {Quoted(parts.Select(part => part.Id))}.");
         }

         if (index != currentPart && index != currentPart + 1)
         {
            var expected = currentPart < 0
               ? $"the first chapter belongs to the first part, '{parts[0].Id}'"
               : currentPart + 1 < parts.Count
                  ? $"after a chapter of '{parts[currentPart].Id}' comes a chapter of '{parts[currentPart].Id}' or '{parts[currentPart + 1].Id}'"
                  : $"after a chapter of the last part, '{parts[currentPart].Id}', every chapter belongs to it";
            return new(field, $"chapters come part by part in the order of parts, so {expected}, not '{id}'.");
         }

         currentPart = index;
      }

      return currentPart < parts.Count - 1 ? new(Parts.At(currentPart + 1), $"part '{parts[currentPart + 1].Id}' has no chapters.") : null;
   }

   // Call it after CheckChapters, so every chapter has an ID that appears once.
   private static RuleFault? CheckStops(IReadOnlyList<Stop> stops, IReadOnlyList<Chapter> chapters)
   {
      if (stops.Count == 0)
      {
         return new(Stops, "a plan needs at least one stop.");
      }

      var chapterIndex = chapters.Select((chapter, index) => (chapter.Id, index)).ToDictionary(pair => pair.Id, pair => pair.index, StringComparer.Ordinal);
      var stopIds = new HashSet<string>(StringComparer.Ordinal);
      var currentChapter = -1;
      for (var i = 0; i < stops.Count; i++)
      {
         var field = Stops.At(i);
         if (stops[i] is not { } stop)
         {
            return new(field, "a stop cannot be null.");
         }

         if (string.IsNullOrWhiteSpace(stop.Title))
         {
            return new(field.Then("title"), "a stop title cannot be empty.");
         }

         if (string.IsNullOrWhiteSpace(stop.Id))
         {
            return new(field.Then("id"), "a stop ID cannot be empty.");
         }

         if (!stopIds.Add(stop.Id))
         {
            return new(field.Then("id"), $"another stop already has the ID '{stop.Id}'.");
         }

         if (stop.Kind == StopKind.Overview && i > 0)
         {
            return new(field.Then("kind"), "only the first stop can be an overview.");
         }

         if (!chapterIndex.TryGetValue(stop.Chapter, out var index))
         {
            return new(field.Then("chapter"), $"no chapter has the ID '{stop.Chapter}'. The chapter IDs are {Quoted(chapters.Select(chapter => chapter.Id))}.");
         }

         if (index != currentChapter && index != currentChapter + 1)
         {
            var expected = currentChapter < 0
               ? $"the first stop belongs to the first chapter, '{chapters[0].Id}'"
               : currentChapter + 1 < chapters.Count
                  ? $"after a stop of '{chapters[currentChapter].Id}' comes a stop of '{chapters[currentChapter].Id}' or '{chapters[currentChapter + 1].Id}'"
                  : $"after a stop of the last chapter, '{chapters[currentChapter].Id}', every stop belongs to it";
            return new(field.Then("chapter"), $"stops come chapter by chapter in the order of chapters, so {expected}, not '{stop.Chapter}'.");
         }

         currentChapter = index;
         if (stop is Stop.Pending { AnchorHint: var hint } && CheckAnchorHint(hint, field.Then("anchorHint")) is { } hintFault)
         {
            return hintFault;
         }
      }

      return currentChapter < chapters.Count - 1 ? new(Chapters.At(currentChapter + 1), $"chapter '{chapters[currentChapter + 1].Id}' has no stops.") : null;
   }

   private static RuleFault? CheckAnchorHint(AnchorHint? hint, FieldPath field)
   {
      if (hint is null)
      {
         return null;
      }

      if (!RepositoryPath.IsValid(hint.Path))
      {
         return new(field.Then("path"), $"give {RepositoryPath.Expected}.");
      }

      return hint.Line < 1 ? new(field.Then("line"), "a line number starts at 1.") : null;
   }

   private static RuleFault? CheckFileMarks(IReadOnlyList<FileMark> marks)
   {
      var paths = new HashSet<string>(StringComparer.Ordinal);
      for (var i = 0; i < marks.Count; i++)
      {
         var field = FileMarks.At(i);
         if (marks[i] is not { } mark)
         {
            return new(field, "a file mark cannot be null.");
         }

         if (!RepositoryPath.IsValid(mark.Path))
         {
            return new(field.Then("path"), $"give {RepositoryPath.Expected}.");
         }

         if (!paths.Add(mark.Path))
         {
            return new(field.Then("path"), $"another file mark already names '{mark.Path}'. Give each file one mark.");
         }

         if (string.IsNullOrWhiteSpace(mark.Reason))
         {
            return new(field.Then("reason"), "a file mark needs a reason.");
         }

         if (mark.Reason.Any(IsLineBreakOrControl))
         {
            return new(field.Then("reason"), "a reason is one line of plain text, without line breaks, tabs or other control characters.");
         }
      }

      return null;
   }

   // Control characters include CR, LF and NEL. The Unicode line and paragraph separators break a line too.
   private static bool IsLineBreakOrControl(char c) => char.IsControl(c) || c is '\u2028' or '\u2029';

   private static string Quoted(IEnumerable<string> ids) => string.Join(", ", ids.Select(id => $"'{id}'"));
}
