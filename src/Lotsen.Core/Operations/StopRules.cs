using System.Globalization;
using Lotsen.Git;
using Lotsen.Prose;
using Lotsen.Tours;

namespace Lotsen.Operations;

/// <summary>
/// The input policy of <c>put_stop</c> and the content that it builds. The rules of the content itself are <see cref="StopContentRules"/>, and the passage checks against the commits run in
/// <see cref="StopPassages"/>.
/// </summary>
internal static class StopRules
{
   // The commit of a draft's passages, which git has not read yet. No rule of the content reads a passage's commit or text.
   private static readonly CommitId Unread = CommitId.Parse(new string('0', 40));

   private static readonly FieldPath ChecksField = "checks";

   /// <summary>Returns the first rule that the input breaks without the tour, or null when it follows them all.</summary>
   /// <remarks>The input's own limits come first: no null element, suggestions within the word limits and each check's ID once. Then <see cref="StopContentRules.CheckFields"/> runs on the draft.</remarks>
   public static OperationError? CheckFields(PutStopInput input)
   {
      if ((NullElement(input.Checks, "checks") ?? NullElement(input.Tags, "tags") ?? NullElement(input.Passages, "passages") ?? CheckChecks(input.Checks ?? [])) is { } refused)
      {
         return refused;
      }

      // A check without an ID gets a stand-in, which no check of the input names. Checks refuses an empty ID that the stop does not have later, as it refuses any other.
      var given = (input.Checks ?? []).Select(check => check.Id).OfType<string>().ToHashSet(StringComparer.Ordinal);
      var checks = (input.Checks ?? []).Select(check => new StopCheck(string.IsNullOrEmpty(check.Id) ? TourIds.Random.Next(given) : check.Id, check.Question)).ToList();
      return StopContentRules.CheckFields(Draft(input, checks)) is { } fault ? OperationError.Invalid(fault) : null;
   }

   /// <summary>Returns the content that the input would store, with each passage on a commit that git has not read and with empty text, so the content's rules can refuse it before git runs.</summary>
   /// <param name="tour">The tour as read.</param>
   /// <param name="current">The stop of the tour's plan that the input writes.</param>
   /// <param name="input">The input, which follows <see cref="CheckFields"/>.</param>
   /// <returns>The content, or the refusal of a check ID that the stop does not have.</returns>
   public static OperationResult<StopContent> Draft(Tour tour, Stop current, PutStopInput input)
   {
      var checks = Checks(tour, current, input);
      return checks.Value is { } built ? Draft(input, built) : checks.Error!;
   }

   /// <summary>Returns the code references of the <c>ref:</c> links in every prose field of the input, each under the field's input path, such as <c>checks[2].question</c>.</summary>
   /// <param name="input">The input, without a null check. Its other fields need not follow <see cref="CheckFields"/>.</param>
   /// <returns>The references in field order, or the error of the first field whose prose does not read, as <see cref="ProseReferences.Read"/> finds it.</returns>
   public static OperationResult<IReadOnlyList<ProseReference>> ReadReferences(PutStopInput input)
   {
      // The draft names each check by its index in the input, so a check's prose field leads back to that index whatever IDs the input gives. No prose field reads a passage.
      var checks = (input.Checks ?? []).Select((check, i) => new StopCheck(i.ToString(CultureInfo.InvariantCulture), check.Question, Text(check.Recommendation))).ToList();
      var fields = StopProse.Fields(Content(input, [], checks)).Select(entry => (entry.Field.PathOf(id => ChecksField.At(int.Parse(id, CultureInfo.InvariantCulture))), entry.Prose));
      var read = ProseReferences.Read(fields);
      return read.Value is { } references ? OperationResult<IReadOnlyList<ProseReference>>.Of(references) : OperationError.Invalid(read.Fault!);
   }

   /// <summary>Applies the input's title, kind and chapter to the stop in the tour's plan, and checks the plan's rules on the result.</summary>
   /// <returns>
   /// The changed plan, whose stop keeps its anchor hint, status and content. The error is <see cref="OperationErrorCode.StopNotFound"/> when the plan has no stop with the input's ID, or the broken
   /// plan rule under the input's field.
   /// </returns>
   public static OperationResult<PlanChange> ApplyPlanChange(Tour tour, PutStopInput input)
   {
      var index = tour.Plan?.Stops.ToList().FindIndex(stop => stop.Id == input.StopId) ?? -1;
      if (tour.Plan is not { } plan || index < 0)
      {
         return OperationError.StopNotFound(input.StopId, hasPlan: tour.Plan is not null);
      }

      var current = plan.Stops[index];
      var changed = current with { Title = input.Title ?? current.Title, Kind = input.Kind ?? current.Kind, Chapter = input.Chapter ?? current.Chapter };
      var next = plan with { Stops = [.. plan.Stops.Select((stop, i) => i == index ? changed : stop)] };
      if (PlanRules.Check(next) is not { } fault)
      {
         return new PlanChange(next, index);
      }

      // The stored plan follows every rule and only this stop changed, so a fault under the stop names the input's own field.
      if (fault.Path.After(FieldPath.Root.Then("stops").At(index)) is { IsRoot: false } own)
      {
         return OperationError.Invalid(own, fault.Problem);
      }

      // Only a change of chapter can break a rule of another stop or of a chapter, because PlanRules checks a stop's title and kind on the stop alone.
      return OperationError.Invalid("chapter", $"moving the stop to chapter '{changed.Chapter}' breaks a rule of the plan. {fault.Path}: {fault.Problem}");
   }

   /// <summary>Returns the content to store, without each empty or whitespace-only optional field, tag and label.</summary>
   /// <param name="input">The input, which follows <see cref="CheckFields"/>.</param>
   /// <param name="passages">The checked passages.</param>
   /// <param name="checks">The input's checks with their IDs, as <see cref="Checks"/> returns them.</param>
   public static StopContent Content(PutStopInput input, IReadOnlyList<StopPassage> passages, IReadOnlyList<StopCheck> checks) => new(
      input.Headline,
      passages,
      Text(input.How),
      Text(input.Why),
      Text(input.DesignChoices),
      Text(input.PinnedBy),
      checks.Count > 0 ? checks : null,
      Text(input.Scenario),
      Texts(input.Tags),
      input.Placement);

   /// <summary>Returns the input's checks as the stop stores them, each with its ID and without an empty recommendation or suggestion.</summary>
   /// <remarks>
   /// A check that Claude writes with an ID keeps it, which the stop's current content must have. A check without one gets a new ID that the tour does not name, as
   /// <see cref="TourIds.NamedBy"/> returns them. Call it again inside the store's update, because the stop's content may change after an earlier call.
   /// </remarks>
   /// <param name="tour">The tour as stored.</param>
   /// <param name="current">The stop of the tour's plan that the input writes, with its current content.</param>
   /// <param name="input">The input, which follows <see cref="CheckFields"/>.</param>
   /// <returns>The checks in the input's order, or the error of the first check whose ID the stop does not have.</returns>
   public static OperationResult<IReadOnlyList<StopCheck>> Checks(Tour tour, Stop current, PutStopInput input)
   {
      var kept = (current as Stop.Written)?.Content.Checks?.Select(check => check.Id).ToHashSet(StringComparer.Ordinal) ?? [];
      var used = TourIds.NamedBy(tour);
      var checks = new List<StopCheck>();
      foreach (var (check, i) in (input.Checks ?? []).Select((check, i) => (check, i)))
      {
         if (check.Id is { } id && !kept.Contains(id))
         {
            return OperationError.Invalid(
               FieldPath.Root.Then("checks").At(i).Then("id"),
               $"stop '{current.Id}' has no check '{id}'. Give the ID of one of the stop's checks from read_state to keep it, or omit id for a new check.");
         }

         var suggestions = check.Suggestions?.Where(suggestion => !string.IsNullOrWhiteSpace(suggestion.Text)).Select(suggestion => new SuggestedAnswer(suggestion.Text, suggestion.Type)).ToList();
         checks.Add(new StopCheck(check.Id ?? TourIds.Random.Next(used), check.Question, Text(check.Recommendation), suggestions is { Count: > 0 } ? suggestions : null));
      }

      return checks;
   }

   private static StopContent Draft(PutStopInput input, IReadOnlyList<StopCheck> checks) =>
      Content(input, [.. (input.Passages ?? []).Select(passage => new StopPassage(passage.Side, passage.Path, passage.StartLine, passage.EndLine, passage.Role, Unread, "", Label(passage.Label)))], checks);

   /// <summary>Returns the label of a passage, or null when it is empty or whitespace-only.</summary>
   public static string? Label(string? label) => Text(label);

   // Checks what the serializer does not: no null suggestion, suggestions that the reviewer can pick and each ID at most once.
   private static OperationError? CheckChecks(IReadOnlyList<CheckInput> checks)
   {
      var ids = new HashSet<string>(StringComparer.Ordinal);
      for (var i = 0; i < checks.Count; i++)
      {
         if (NullElement(checks[i].Suggestions, FieldPath.Root.Then("checks").At(i).Then("suggestions")) is { } nullError)
         {
            return nullError;
         }

         var suggestions = checks[i].Suggestions ?? [];
         for (var j = 0; j < suggestions.Count; j++)
         {
            // A whitespace-only suggestion is dropped, so only the stored ones are held to the limits.
            if (!string.IsNullOrWhiteSpace(suggestions[j].Text) && ReviewerWords.Limit(suggestions[j].Text, "suggestion") is { } limit)
            {
               return OperationError.Invalid(FieldPath.Root.Then("checks").At(i).Then("suggestions").At(j).Then("text"), limit);
            }
         }

         if (checks[i].Id is { } id && !ids.Add(id))
         {
            return OperationError.Invalid(FieldPath.Root.Then("checks").At(i).Then("id"), $"the check '{id}' appears twice. Give each check's ID once.");
         }
      }

      return null;
   }

   private static string? Text(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

   private static IReadOnlyList<string>? Texts(IReadOnlyList<string>? texts) => texts?.Where(text => !string.IsNullOrWhiteSpace(text)).ToList() is { Count: > 0 } kept ? kept : null;

   private static OperationError? NullElement<T>(IReadOnlyList<T>? items, FieldPath field)
      where T : class
   {
      var index = items?.ToList().FindIndex(item => item is null) ?? -1;
      return index < 0 ? null : OperationError.Invalid(field.At(index), "an element cannot be null.");
   }
}

/// <summary>A plan with one stop changed, as <see cref="StopRules.ApplyPlanChange"/> returns it.</summary>
/// <param name="Plan">The whole plan after the change.</param>
/// <param name="Index">The index of the changed stop in the plan's stops.</param>
internal sealed record PlanChange(Plan Plan, int Index)
{
   public Stop Stop => Plan.Stops[Index];
}
