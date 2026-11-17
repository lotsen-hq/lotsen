using Lotsen.Git;
using Lotsen.Platforms;

namespace Lotsen.Tours;

/// <summary>
/// The one definition of a valid stored tour: the rules that span the tour, which <see cref="TourDocument"/> runs on every tour that it reads and on every tour that a write stores.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Check"/> runs, in order: no null element in the tour's own lists, <see cref="CheckPlan"/> under <c>plan</c>, and for each written stop <see cref="CheckContent"/> on its content,
/// a version that counts the earlier versions, each earlier version's kind under <see cref="PurposeRules"/> and <see cref="CheckContent"/> on its content under its kind, check IDs that no other
/// current content of the tour holds, and a stale marker from 1. Then it checks one answer for each stop and check, the fields and the pin that the target's kind allows, and an available
/// iteration compared with the tour's pin. Last it checks that only an archived dive names the tour whose archive took it.
/// </para>
/// <para>
/// A rule that one value can hold lives on that value instead, as an answer's state and batch do in <see cref="CheckAnswer"/>, and the threads' rules in <see cref="TourThreads"/>. Add a rule
/// here only for what no type can hold. An input's own limits, such as a word limit, are the operation's, so a stricter limit never makes a stored tour unreadable.
/// </para>
/// <para>
/// A reference to a stop that the plan dropped stays valid: an answer to its checks, the reading progress and an available iteration's stale stops may name it.
/// </para>
/// </remarks>
internal static class TourRules
{
   /// <summary>The most characters of a repository tour's title.</summary>
   public const int MaxTitleLength = 120;

   private static readonly FieldPath PlanField = "plan";
   private static readonly FieldPath Answers = "answers";
   private static readonly FieldPath SynthesisPinField = ((FieldPath)"synthesis").Then("pin");

   /// <summary>Returns the first rule that the tour breaks, with its path in the tour, or null when it follows them all.</summary>
   public static RuleFault? Check(Tour tour) =>
      CheckNulls(tour)
      ?? (tour.Plan is { } plan ? CheckPlan(tour, plan)?.Under(PlanField) ?? CheckStops(tour, plan) : null)
      ?? CheckAnswers(tour.Answers)
      ?? CheckTargetKind(tour)
      ?? CheckAvailableIteration(tour)
      ?? CheckArchivedWith(tour);

   // The serializer checks nullability on properties but not on collection elements. The plan's own lists are the plan rules'.
   private static RuleFault? CheckNulls(Tour tour) =>
      NullElement(tour.Answers, Answers) ?? NullElement(tour.AvailableIteration?.StaleStops, ((FieldPath)"availableIteration").Then("staleStops"));

   /// <summary>Returns the first rule that a stop's content breaks under the kind in the tour, with its path in the content, or null when it follows them all.</summary>
   /// <remarks>
   /// Only a repository tour's stops hold gotchas and tryIt, and a repository tour's passages are all at the head. Then <see cref="PurposeRules.CheckChecks"/> runs, and last
   /// <see cref="StopContentRules.Check"/>.
   /// </remarks>
   public static RuleFault? CheckContent(Tour tour, StopKind kind, StopContent content) =>
      CheckTargetContent(tour.Target, content) ?? PurposeRules.CheckChecks(PurposeOf(tour), content) ?? StopContentRules.Check(kind, content);

   /// <summary>
   /// Returns the first rule that a plan breaks in the tour, with its path in the plan, or null when it follows them all: <see cref="PlanRules"/>, then no file marks in a repository tour, then
   /// each stop's kind under <see cref="PurposeRules"/>.
   /// </summary>
   public static RuleFault? CheckPlan(Tour tour, Plan plan)
   {
      if (PlanRules.Check(plan) is { } fault)
      {
         return fault;
      }

      if (tour.Target is RepositoryTarget && plan.FileMarks.Count > 0)
      {
         return new("fileMarks", "a repository tour has no changed files to mark. Leave fileMarks out.");
      }

      for (var i = 0; i < plan.Stops.Count; i++)
      {
         if (PurposeRules.CheckKind(PurposeOf(tour), plan.Stops[i].Kind, FieldPath.Root.Then("stops").At(i).Then("kind")) is { } kindFault)
         {
            return kindFault;
         }
      }

      return null;
   }

   // A pull request's tour with a purpose breaks a rule of its own, so the purpose binds only a repository tour's stops.
   private static TourPurpose? PurposeOf(Tour tour) => tour.Target is RepositoryTarget ? tour.Purpose : null;

   private static RuleFault? CheckTargetContent(TourTarget target, StopContent content)
   {
      if (target is not RepositoryTarget)
      {
         return StopContentRules.RepositoryOnlyField(content) is { } field ? new(field, $"only a repository tour's stops take {field}. Leave {field} out.") : null;
      }

      var index = content.Passages.ToList().FindIndex(passage => passage.Side != FileSide.Head);
      return index < 0 ? null : new(((FieldPath)"passages").At(index).Then("side"), $"{TourPin.Snapshot.HeadOnly} Point the passage at head.");
   }

   // Call it after CheckPlan, so no stop is null.
   private static RuleFault? CheckStops(Tour tour, Plan plan)
   {
      var checkIds = new HashSet<string>(StringComparer.Ordinal);
      for (var i = 0; i < plan.Stops.Count; i++)
      {
         if (plan.Stops[i] is not Stop.Written stop)
         {
            continue;
         }

         var field = PlanField.Then("stops").At(i);
         if ((CheckContent(tour, stop.Kind, stop.Content)?.Under(field.Then("content")) ?? CheckEarlier(tour, stop, field.Then("earlier"))) is { } fault)
         {
            return fault;
         }

         // The UI keys a stop's checks by ID and an answer finds its check by ID, so a check ID does not repeat within the tour's current content.
         var checks = stop.Content.Checks ?? [];
         for (var j = 0; j < checks.Count; j++)
         {
            if (!checkIds.Add(checks[j].Id))
            {
               return new(field.Then("content").Then("checks").At(j).Then("id"), $"another check of the tour already has the ID '{checks[j].Id}'.");
            }
         }

         // An update marks only content stale, and the marker names the iteration that the update moved to.
         if (stop.StaleOn < 1)
         {
            return new(field.Then("staleOn"), "an iteration counts from 1.");
         }
      }

      return null;
   }

   // The stop holds every version before its own, so a version's number is its index in Earlier plus 1.
   private static RuleFault? CheckEarlier(Tour tour, Stop.Written stop, FieldPath field)
   {
      if (NullElement(stop.Earlier, field) is { } nullFault)
      {
         return nullFault;
      }

      if (stop.Version != stop.Earlier.Count + 1)
      {
         return new(field, $"stop '{stop.Id}' is at version {stop.Version}, so it holds {stop.Version - 1} earlier versions, not {stop.Earlier.Count}.");
      }

      for (var j = 0; j < stop.Earlier.Count; j++)
      {
         var version = stop.Earlier[j];
         if ((PurposeRules.CheckKind(PurposeOf(tour), version.Kind, field.At(j).Then("kind")) ?? CheckContent(tour, version.Kind, version.Content)?.Under(field.At(j).Then("content"))) is { } fault)
         {
            return fault;
         }
      }

      return null;
   }

   // An operation finds a check's answer by its stop ID and check ID, so a second answer under the same IDs would be one that no operation reaches.
   private static RuleFault? CheckAnswers(IReadOnlyList<CheckAnswer> answers)
   {
      var answered = new HashSet<(string StopId, string CheckId)>();
      for (var i = 0; i < answers.Count; i++)
      {
         if (!answered.Add((answers[i].StopId, answers[i].CheckId)))
         {
            return new(Answers.At(i), $"another answer already answers check '{answers[i].CheckId}' of stop '{answers[i].StopId}'.");
         }
      }

      return null;
   }

   // A pull request's tour has none of a repository tour's fields, and each kind has its own kind of pin, for the tour and for its synthesis.
   private static RuleFault? CheckTargetKind(Tour tour) => tour.Target switch
   {
      RepositoryTarget => CheckRepositoryTour(tour),
      _ when tour.Purpose is not null => new("purpose", "only a repository tour has a purpose."),
      _ when tour.Words is not null => new("words", "only a repository tour has words."),
      _ when tour.Title is not null => new("title", "only a repository tour has a title."),
      _ when tour.DiveOrigin is not null => new("diveOrigin", "only a repository tour is a dive."),
      _ when tour.Pin is TourPin.Snapshot => new("pin", "a pull request's tour is pinned to an iteration of the pull request, not to a snapshot."),
      _ when tour.Synthesis?.Pin is SynthesisPin.Repository => new(SynthesisPinField, "a pull request's tour has a synthesis written at an iteration of the pull request, not at a snapshot."),
      _ => null,
   };

   private static RuleFault? CheckRepositoryTour(Tour tour)
   {
      if (tour.Purpose is null)
      {
         return new("purpose", "a repository tour needs a purpose: orient, prepare or evaluate.");
      }

      if (string.IsNullOrWhiteSpace(tour.Title))
      {
         return new("title", "a repository tour needs a title.");
      }

      if (tour.Title.Any(PlanRules.IsLineBreakOrControl))
      {
         return new("title", "a title is one line of plain text, without line breaks, tabs or other control characters.");
      }

      if (tour.Title.Length > MaxTitleLength)
      {
         return new("title", $"a title holds at most {MaxTitleLength} characters, and this one holds {tour.Title.Length}.");
      }

      // Null names no words and a dive from the whole tour, so an empty value would be a second way to say it.
      if (tour.Words is { } words && string.IsNullOrWhiteSpace(words))
      {
         return new("words", "words are null when the reviewer gave none, never empty.");
      }

      if (tour.DiveOrigin?.StopId is { } stopId && string.IsNullOrWhiteSpace(stopId))
      {
         return new(((FieldPath)"diveOrigin").Then("stopId"), "a dive's stop ID is null for a dive from the whole tour, never empty.");
      }

      if (tour.Pin is TourPin.Pinned)
      {
         return new("pin", "a repository tour is pinned to a snapshot of one commit, not to an iteration of a pull request.");
      }

      if (tour.Synthesis?.Pin is SynthesisPin.PullRequest)
      {
         return new(SynthesisPinField, "a repository tour has a synthesis written at a snapshot, not at an iteration of a pull request.");
      }

      return tour.PullRequestState != PullRequestState.Active ? new("pullRequestState", "only a pull request's tour has a pull request state.") : null;
   }

   // Unarchiving the tour that ArchivedWith names restores the dive, so only an archived dive holds it.
   private static RuleFault? CheckArchivedWith(Tour tour) =>
      tour.ArchivedWith is not null && (!tour.Archived || tour.DiveOrigin is null) ? new("archivedWith", "only an archived dive names the tour whose archive took it.") : null;

   // An available iteration is relative to the tour's pin, so only a pinned tour holds one.
   private static RuleFault? CheckAvailableIteration(Tour tour) =>
      tour.AvailableIteration is not null && tour.Pin is not TourPin.Pinned ? new("availableIteration", "only a pinned tour has an available iteration.") : null;

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

/// <summary>The rules of the review marks that a tour's file holds beside the tour.</summary>
internal static class ReviewMarkRules
{
   private static readonly FieldPath Marks = "reviewMarks";

   /// <summary>Returns the first rule that the marks break, with its path in the tour's file, or null when they follow them all: each mark names a repository path that no other mark names.</summary>
   public static RuleFault? Check(IReadOnlyList<ReviewMark> marks)
   {
      var paths = new HashSet<string>(StringComparer.Ordinal);
      for (var i = 0; i < marks.Count; i++)
      {
         if (marks[i] is not { } mark)
         {
            return new(Marks.At(i), "an element cannot be null.");
         }

         if (!RepositoryPath.IsValid(mark.Path))
         {
            return new(Marks.At(i).Then("path"), $"give {RepositoryPath.Expected}.");
         }

         if (!paths.Add(mark.Path))
         {
            return new(Marks.At(i).Then("path"), $"another review mark already names '{mark.Path}'.");
         }
      }

      return null;
   }
}
