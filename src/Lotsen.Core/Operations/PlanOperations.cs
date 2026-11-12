using System.ComponentModel;
using System.Text.Json.Serialization;
using Lotsen.Prose;
using Lotsen.Sessions;
using Lotsen.Tours;

namespace Lotsen.Operations;

/// <summary>The input of <c>set_plan</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SetPlanInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId,
   [property: Description("The chapters in tour order. Every chapter needs at least one stop.")]
   IReadOnlyList<ChapterInput> Chapters,
   [property: Description("The stops in tour order, chapter by chapter in the order of chapters. At most one stop is an overview, and it must be the first stop.")]
   IReadOnlyList<StopInput> Stops,
   [property: Description("Marks on changed files that need little review, each path at most once. The UI shows a marked file collapsed, with its mark and reason. The plan's marks replace any earlier ones, so give them again with each plan. Omit it for a plan without marks.")]
   IReadOnlyList<FileMarkInput>? FileMarks = null,
   [property: Description("The parts in tour order, each a named group of consecutive chapters, which the reviewer can fold in the UI. Every part needs at least one chapter, and with parts every chapter names its part, chapters coming part by part in the order of parts. The plan's parts replace any earlier ones, so give them again with each plan. Omit it, or give an empty list, for a plan without parts.")]
   IReadOnlyList<PartInput>? Parts = null);

/// <summary>A chapter in the input of <c>set_plan</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ChapterInput(
   [property: Description("The chapter's ID, unique within the plan, which stops name in their chapter field.")]
   string Id,
   [property: Description("The chapter's title.")]
   string Title,
   [property: Description("The ID of the part the chapter belongs to. Give it for every chapter of a plan with parts, and omit it in a plan without parts.")]
   string? Part = null);

/// <summary>A part in the input of <c>set_plan</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PartInput(
   [property: Description("The part's ID, unique within the plan, which chapters name in their part field.")]
   string Id,
   [property: Description("The part's title, which names the area of the change that its chapters cover.")]
   string Title);

/// <summary>A stop in the input of <c>set_plan</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record StopInput(
   [property: Description("The stop's title.")]
   string Title,
   [property: Description("What the stop does for the reviewer: overview (a map of the whole change), context (background), inspect (a walk through changed code), risk (a concern) or question (a question for the reviewer).")]
   StopKind Kind,
   [property: Description("The ID of the chapter the stop belongs to.")]
   string Chapter,
   [property: Description("Where the stop is expected to point, when known. A kept stop with content ignores it.")]
   AnchorHintInput? AnchorHint = null,
   [property: Description("To keep a stop from the current plan, its ID from read_state. Omit it for a new stop; the server assigns one.")]
   string? Id = null);

/// <summary>An anchor hint in the input of <c>set_plan</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AnchorHintInput(
   [property: Description("The repository path of the file, relative, with forward slashes.")]
   string Path,
   [property: Description("The 1-based line in that file, when known.")]
   int? Line = null);

/// <summary>A file mark in the input of <c>set_plan</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record FileMarkInput(
   [property: Description("The file's repository path, relative, with forward slashes. Name a renamed file by its path at the head.")]
   string Path,
   [property: Description("The kind of change: mechanical for a change that follows from another change and adds no logic of its own, such as a renamed namespace or an updated import, or generated for a file that a tool writes, such as a lock file or generated client code.")]
   FileMarkKind Mark,
   [property: Description("Why the file has the mark, for the reviewer: one line of plain text, not empty.")]
   string Reason);

/// <summary>The input of <c>put_stop</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PutStopInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId,
   [property: Description("The ID of a stop in the tour's plan, as set_plan or read_state returned it.")]
   string StopId,
   [property: Description("The stop's main point, which the reviewer reads first, in Markdown. It cannot be empty.")]
   string Headline,
   [property: Description("How the code works, in Markdown.")]
   string? How = null,
   [property: Description("Why the change looks the way it does, in Markdown.")]
   string? Why = null,
   [property: Description("The design choices behind the change and their trade-offs, in Markdown.")]
   string? DesignChoices = null,
   [property: Description("The tests that pin the behavior, in Markdown.")]
   string? PinnedBy = null,
   [property: Description("Checks for the reviewer to answer, one decision each, in the order the reviewer should answer them. A question stop needs at least one. The checks replace the stop's earlier checks, so give every check to keep again with its id.")]
   IReadOnlyList<CheckInput>? Checks = null,
   [property: Description("How the risk could happen, in Markdown. A risk stop needs one.")]
   string? Scenario = null,
   [property: Description("The lines the stop points at, in the order the reviewer should read them. Exactly one passage is primary when there are any. Every kind but overview needs at least one. The primary passage of an inspect, risk or question stop must touch the diff. Related passages may point at unchanged code on either side.")]
   IReadOnlyList<PassageInput>? Passages = null,
   [property: Description("Free descriptive tags, such as security, performance, public-api or breaking-change.")]
   IReadOnlyList<string>? Tags = null,
   [property: Description("Where the UI should show the stop's text: inline at the primary passage, or side for the side panel. Omit it to let the UI decide.")]
   StopPlacement? Placement = null,
   [property: Description("A new title for the stop. Omit it to keep the plan's title.")]
   string? Title = null,
   [property: Description("A new kind for the stop, which the plan's rules then check. Omit it to keep the plan's kind.")]
   StopKind? Kind = null,
   [property: Description("The ID of a new chapter for the stop, which the plan's rules then check. Omit it to keep the plan's chapter.")]
   string? Chapter = null);

/// <summary>A check in the input of <c>put_stop</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CheckInput(
   [property: Description("The one decision the reviewer makes, as a question in Markdown. It cannot be empty.")]
   string Question,
   [property: Description("Your recommended answer and its reason, in Markdown. Omit it when you have none.")]
   string? Recommendation = null,
   [property: Description("The answers the reviewer is likely to give, each one the reviewer can pick. Omit it when you expect none.")]
   IReadOnlyList<SuggestionInput>? Suggestions = null,
   [property: Description("To keep a check that the stop has now, its ID from read_state. Omit it for a new check; the server assigns one.")]
   string? Id = null);

/// <summary>A suggested answer in the input of <c>put_stop</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SuggestionInput(
   [property: Description("The answer in the reviewer's words, as short plain text, not Markdown, with no control characters but tabs and line breaks.")]
   string Text,
   [property: Description("What the answer is: decision for a decision about the change, concern for a concern about it, or openQuestion for a question the reviewer cannot settle yet.")]
   AnswerType Type);

/// <summary>A passage in the input of <c>put_stop</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PassageInput(
   [property: Description("The side the lines are on: base for the merge base, head for the head.")]
   FileSide Side,
   [property: Description("The file's repository path at that side, relative, with forward slashes.")]
   string Path,
   [property: Description("The first line, from 1.")]
   int StartLine,
   [property: Description("The last line, inclusive, not before startLine.")]
   int EndLine,
   [property: Description("primary for the passage the stop is about, related for a passage that supports it, such as a caller or a test.")]
   PassageRole Role,
   [property: Description("A short name for the passage, such as interface, caller or test.")]
   string? Label = null,
   [property: Description("The text you expect on startLine. The server compares it with the line at that side, without leading and trailing whitespace, and rejects the stop when they differ.")]
   string? ExpectedFirstLine = null);

/// <summary>The bound session's plan and stops: <c>set_plan</c> and <c>put_stop</c>.</summary>
internal sealed class PlanOperations
{
   private static readonly OperationError PlanChangedWhileChecked = OperationError.Invalid("stopId", "the plan changed while the stop was checked. Read the state and write the stop again.");

   private readonly TourWriter _writer;
   private readonly FileOperations _files;

   public PlanOperations(TourWriter writer, FileOperations files)
   {
      _writer = writer;
      _files = files;
   }

   /// <summary>Sets the tour's plan for the tour's bound session, and returns the tour.</summary>
   public OperationResult<Tour> SetPlan(Access.BoundSession.Pass pass, SetPlanInput input) =>
      _writer.Write(pass, input.TourId, edit =>
      {
         var latest = edit.Latest;
         var built = PlanDrafts.Build(input, latest);
         if (built.Value is not { } plan)
         {
            return built.Error!;
         }

         return edit.Store(latest with { Plan = plan }, stored =>
         {
            stored.Publish(ServerEvents.ForPlanSet(stored.Tour, stored.Mentions));
            return AgentView.Of(stored.Tour);
         });
      });

   /// <summary>
   /// Writes the content of a stop in the plan for the tour's bound session, after the stop passes <see cref="StopRules"/> and its passages and code references pass <see cref="StopPassages"/>,
   /// and returns the stored stop without its earlier versions, as <see cref="AgentView.Of(Stop)"/> shows it.
   /// </summary>
   /// <remarks>
   /// The passage checks run git, so they run on the tour as read before them. The write applies the plan change and the checks' IDs again to the latest stored tour, so a plan that
   /// <c>set_plan</c> replaced, or a check that another <c>put_stop</c> removed, in the meantime is checked too. The stored stop's anchor hint is cleared, and its content becomes the stop's next
   /// version, as <see cref="StopVersions.Write"/> describes. The first version publishes <see cref="ServerEvents.StopPut"/>, and a later one <see cref="ServerEvents.StopRevised"/>. A tour whose
   /// plan waits for the reviewer's approval, as <see cref="Tour.PlanWaits"/> says, is refused.
   /// </remarks>
   public Task<OperationResult<Stop>> PutStopAsync(Access.BoundSession.Pass pass, PutStopInput input, CancellationToken cancellationToken) =>
      _writer.WriteAfterGitAsync<IReadOnlyList<StopPassage>, Stop>(
         pass,
         input.TourId,
         tour => CheckStop(tour, input),
         async (tour, git, token) =>
         {
            // The kind decides which passage checks run.
            var kind = StopRules.ApplyPlanChange(tour, input).Value!.Stop.Kind;
            var references = StopRules.ReadReferences(input).Value!;
            var reads = (input.Passages ?? []).Select(passage => (passage.Side, passage.Path)).Concat(references.Select(link => (link.Reference.Side, link.Reference.Path)));
            await git.DownloadAsync(_files, reads);

            var passages = await git.OnRepositoryAsync(
               _files,
               "passages",
               (repository, pin) => new StopPassages(repository, pin).CheckAsync(input.Passages ?? [], kind, references, token));
            return passages.Value is { } checkedPassages
               ? Gathered.Of(checkedPassages).Unless(latest => StopRules.ApplyPlanChange(latest, input).Value?.Stop.Kind != kind, PlanChangedWhileChecked)
               : passages.Error!;
         },
         (edit, passages) =>
         {
            var latest = edit.Latest;
            var applied = StopRules.ApplyPlanChange(latest, input).Value!;
            var write = StopVersions.Write(latest, applied, StopRules.Content(input, passages, StopRules.Checks(latest, applied.Stop, input).Value!), edit.Now);
            return edit.Store(write.Threads, (stored, _) =>
            {
               stored.Publish(write.Replaced is null ? ServerEvents.ForStopPut(stored.Tour, write.Stop, stored.Mentions) : ServerEvents.ForStopRevised(stored.Tour, write, stored.Mentions));
               return AgentView.Of(write.Stop);
            });
         },
         cancellationToken);

   // Returns the refusal of the stop's input against the tour, which put_stop runs on the read tour and again on the latest one.
   private static OperationError? CheckStop(Tour tour, PutStopInput input)
   {
      if ((CheckPlanApproved(tour) ?? StopRules.CheckFields(input) ?? StopRules.ReadReferences(input).Error) is { } refused)
      {
         return refused;
      }

      if (tour.Pin is not TourPin.Pinned)
      {
         return OperationError.TourNotPinned(tour);
      }

      var change = StopRules.ApplyPlanChange(tour, input);
      if (change.Value is not { } planned)
      {
         return change.Error!;
      }

      var draft = StopRules.Draft(tour, planned.Stop, input);
      if (draft.Value is not { } content)
      {
         return draft.Error!;
      }

      // The same rules run again on the stored content, which holds the passages that git read.
      return StopContentRules.Check(planned.Stop.Kind, content) is { } fault ? OperationError.Invalid(fault) : null;
   }

   private static OperationError? CheckPlanApproved(Tour tour) => tour.PlanWaits ? OperationError.PlanAwaitsApproval(tour.Id) : null;
}

/// <summary>Builds a plan from <c>set_plan</c>'s input against the latest stored tour, and checks it.</summary>
internal static class PlanDrafts
{
   /// <summary>The most UTF-16 code units that a file mark's reason may hold, which keeps a reason to one short sentence.</summary>
   public const int MaxReasonLength = 200;

   /// <summary>Returns the plan that the input sets: kept stops keep their content, and each new stop gets an ID that the tour does not name.</summary>
   /// <remarks>
   /// The input's own limits and its kept stops come first, then <see cref="PlanRules"/> on the built plan, whose fault paths are the input's, because the plan keeps the input's order. A null
   /// element of the input stays null in the plan, so the plan rules name it. An empty list of parts builds a plan without parts.
   /// </remarks>
   /// <param name="input">The input.</param>
   /// <param name="latest">The latest stored tour, whose plan holds the stops that the input keeps.</param>
   /// <param name="ids">Where the new stops' IDs come from, or null for <see cref="TourIds.Random"/>.</param>
   /// <returns>The plan, or the refusal of the first input limit, kept stop or plan rule that the input breaks.</returns>
   public static OperationResult<Plan> Build(SetPlanInput input, Tour latest, TourIds? ids = null)
   {
      var stopsField = FieldPath.Root.Then("stops");
      var currentStops = latest.Plan?.Stops.ToDictionary(stop => stop.Id, StringComparer.Ordinal) ?? [];
      var usedIds = TourIds.NamedBy(latest);
      var stops = new List<Stop>(input.Stops.Count);
      for (var i = 0; i < input.Stops.Count; i++)
      {
         if (input.Stops[i] is not { } stop)
         {
            stops.Add(null!);
            continue;
         }

         var anchorHint = stop.AnchorHint is { } hint ? new AnchorHint(hint.Path, hint.Line) : null;
         if (stop.Id is null)
         {
            stops.Add(new Stop.Pending((ids ?? TourIds.Random).Next(usedIds), stop.Title, stop.Kind, stop.Chapter, anchorHint));
         }
         else if (currentStops.TryGetValue(stop.Id, out var kept))
         {
            switch (kept)
            {
               // The content passed the checks of its kind, which need git, so only put_stop can change the kind of a stop with content.
               case Stop.Written written when stop.Kind != written.Kind:
                  return OperationError.Invalid(
                     stopsField.At(i).Then("kind"),
                     $"stop '{kept.Id}' already has content, which was checked as {StopContentRules.KindWithArticle(kept.Kind)} stop. Keep its kind here, and change the kind with put_stop, which checks the content against the new kind.");

               // A stop with content points where its passages point, so it takes no anchor hint.
               case Stop.Written written:
                  stops.Add(written with { Title = stop.Title, Chapter = stop.Chapter });
                  break;
               case Stop.Pending pending:
                  stops.Add(pending with { Title = stop.Title, Kind = stop.Kind, Chapter = stop.Chapter, AnchorHint = anchorHint });
                  break;
            }
         }
         else
         {
            return OperationError.Invalid(
               stopsField.At(i).Then("id"),
               $"the current plan has no stop '{stop.Id}'. Give the ID of a stop from read_state to keep it, or omit id for a new stop.");
         }
      }

      var marks = input.FileMarks ?? [];
      for (var i = 0; i < marks.Count; i++)
      {
         if (marks[i]?.Reason is { Length: > MaxReasonLength } reason)
         {
            return OperationError.Invalid(
               FieldPath.Root.Then("fileMarks").At(i).Then("reason"),
               $"a reason holds at most {MaxReasonLength} characters, and this one holds {reason.Length}. Shorten it to one short sentence.");
         }
      }

      var plan = new Plan(
         [.. input.Chapters.Select(chapter => chapter is null ? null! : new Chapter(chapter.Id, chapter.Title, chapter.Part))],
         stops,
         [.. marks.Select(mark => mark is null ? null! : new FileMark(mark.Path, mark.Mark, mark.Reason))],
         input.Parts is { Count: > 0 } parts ? [.. parts.Select(part => part is null ? null! : new Part(part.Id, part.Title))] : null);
      return PlanRules.Check(plan) is { } fault ? OperationError.Invalid(fault) : plan;
   }
}
