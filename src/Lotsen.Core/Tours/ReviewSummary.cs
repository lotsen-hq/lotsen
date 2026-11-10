using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lotsen.Json;

namespace Lotsen.Tours;

/// <summary>What the review of a tour has produced so far, built from the tour's state alone: the output of <c>read_summary</c>.</summary>
/// <param name="TourId">The tour's ID.</param>
/// <param name="UpdatedAt">The tour's update time when the summary was built.</param>
/// <param name="Answers">The reviewer's answers, grouped by their type, with the outdated answers apart.</param>
/// <param name="Risks">The plan's risk stops, in plan order, which the summary lists as concerns.</param>
/// <param name="Threads">The threads, notes and tour actions included, grouped by their state.</param>
/// <param name="SkippedStops">The stops that the reviewer skipped, in plan order.</param>
/// <param name="StaleStops">The stops with a stale marker, in plan order.</param>
/// <param name="Synthesis">The bound session's last synthesis of the review, or null when none has been written. JSON leaves it out when null.</param>
/// <param name="SummaryFingerprint">
/// The <see cref="Fingerprint"/> of the summary that an agent session reads, which <c>write_synthesis</c> takes as the summary that the synthesis is based on, or null on a summary that
/// <see cref="Of"/> built. JSON leaves it out when null.
/// </param>
/// <param name="FollowUps">
/// Why each follow-up action has nothing to hand to Claude, from the summary that an agent session reads, or null on a summary that <see cref="Of"/> built. JSON leaves it out when null.
/// </param>
internal sealed record ReviewSummary(
   TourId TourId,
   DateTimeOffset UpdatedAt,
   SummaryAnswers Answers,
   IReadOnlyList<SummaryRisk> Risks,
   SummaryThreads Threads,
   IReadOnlyList<SummaryStop> SkippedStops,
   IReadOnlyList<SummaryStaleStop> StaleStops,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SummarySynthesis? Synthesis = null,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SummaryFingerprint? SummaryFingerprint = null,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SummaryFollowUps? FollowUps = null)
{
   /// <summary>Returns the review summary of the tour.</summary>
   /// <remarks>
   /// Pass the tour as <see cref="AgentView.For"/> shows it to the viewer, so an agent session's summary holds none of the reviewer's draft answers or notes. Within a group, answers follow
   /// plan order and then check order, an answer whose stop the plan no longer has comes last, and threads keep the order they were started in.
   /// </remarks>
   /// <param name="shown">The tour as the viewer reads it.</param>
   /// <param name="stored">The stored tour, whose stops' earlier versions hold the questions of outdated answers. Claude wrote those questions, so they hold none of the reviewer's words.</param>
   public static ReviewSummary Of(Tour shown, Tour stored)
   {
      var stops = shown.Plan?.Stops ?? [];
      var history = stored.Plan?.Stops ?? [];

      // The summary must list every answer and thread, so a type or state without a group throws instead of leaving one out.
      List<SummaryAnswer> decisions = [], concerns = [], openQuestions = [], outdatedAnswers = [];
      foreach (var answer in shown.Answers.Select(answer => AnswerOf(answer, stops, history)).OrderBy(entry => entry.Order).Select(entry => entry.Answer))
      {
         var group = answer switch
         {
            { OutdatedOn: not null } => outdatedAnswers,
            { Type: AnswerType.Decision } => decisions,
            { Type: AnswerType.Concern } => concerns,
            { Type: AnswerType.OpenQuestion } => openQuestions,
            _ => throw new UnreachableException($"The answer type {answer.Type} has no group in the review summary."),
         };
         group.Add(answer);
      }

      List<SummaryThread> open = [], resolved = [], outdated = [];
      foreach (var thread in shown.Threads)
      {
         var group = thread.State switch
         {
            QuestionThreadState.Open => open,
            QuestionThreadState.Resolved => resolved,
            QuestionThreadState.Outdated => outdated,
            _ => throw new UnreachableException($"The thread state {thread.State} has no group in the review summary."),
         };
         group.Add(ThreadOf(thread, stops));
      }

      return new ReviewSummary(
         shown.Id,
         shown.UpdatedAt,
         new SummaryAnswers(decisions, concerns, openQuestions, outdatedAnswers),
         [.. stops.Where(stop => stop.Kind == StopKind.Risk).Select(stop => new SummaryRisk(stop.Id, stop.Title, (stop as Stop.Written)?.Content.Scenario))],
         new SummaryThreads(open, resolved, outdated),
         [.. stops.Where(stop => stop.Skipped).Select(stop => new SummaryStop(stop.Id, stop.Title))],
         [.. stops.OfType<Stop.Written>().Where(stop => stop.StaleOn is not null).Select(stop => new SummaryStaleStop(stop.Id, stop.Title, stop.StaleOn!.Value))]);
   }

   /// <summary>Returns a digest of what the summary says at the tour's pin, which tells whether a synthesis predates the summary.</summary>
   /// <remarks>
   /// The digest leaves out the update time, which every write of the tour moves, the synthesis, the summary's own fingerprint and follow-up reasons, and the threads of summarize and follow-up
   /// actions, which hand the review to Claude and add nothing to it. It counts a received answer as sent, because Claude's fetch of a batch adds nothing to the review either. It holds the
   /// pin's merge base and head, because Claude reads the code at them, so the same summary at another pin has another digest.
   /// </remarks>
   /// <param name="pin">The tour's pin, whose commits count only once it is pinned.</param>
   /// <returns>The SHA-256 of the summary's JSON and the pin's commits, in lowercase hexadecimal.</returns>
   public SummaryFingerprint Fingerprint(TourPin pin)
   {
      static IReadOnlyList<SummaryAnswer> Sent(IReadOnlyList<SummaryAnswer> answers) =>
         [.. answers.Select(answer => answer.State == AnswerState.Received ? answer with { State = AnswerState.Sent } : answer)];

      static IReadOnlyList<SummaryThread> Reviewed(IReadOnlyList<SummaryThread> threads) => [.. threads.Where(thread => !thread.HandsOverReview)];

      var compared = this with
      {
         UpdatedAt = default,
         Synthesis = null,
         SummaryFingerprint = null,
         FollowUps = null,
         Answers = new SummaryAnswers(Sent(Answers.Decisions), Sent(Answers.Concerns), Sent(Answers.OpenQuestions), Sent(Answers.Outdated)),
         Threads = new SummaryThreads(Reviewed(Threads.Open), Reviewed(Threads.Resolved), Reviewed(Threads.Outdated)),
      };
      using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
      hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(compared, LotsenJsonContext.Default.ReviewSummary));
      if (pin is TourPin.Pinned pinned)
      {
         // The JSON ends with its closing brace, so the line breaks keep the commits apart from it and from each other.
         hash.AppendData(Encoding.UTF8.GetBytes($"\n{pinned.MergeBase}\n{pinned.Head}"));
      }

      return Tours.SummaryFingerprint.Of(hash.GetHashAndReset());
   }

   // An answer to a check that the stop's content no longer holds sorts after the stop's other answers.
   private static (SummaryAnswer Answer, (int Stop, int Check) Order) AnswerOf(CheckAnswer answer, IReadOnlyList<Stop> stops, IReadOnlyList<Stop> history)
   {
      var stopIndex = IndexOf(stops, stop => stop.Id == answer.StopId);
      var stop = stopIndex < int.MaxValue ? stops[stopIndex] : null;
      var content = answer.OutdatedOn is { } number
         ? (history.FirstOrDefault(kept => kept.Id == answer.StopId) as Stop.Written)?.ContentOf(number)
         : (stop as Stop.Written)?.Content;
      var checks = content?.Checks ?? [];
      var checkIndex = IndexOf(checks, check => check.Id == answer.CheckId);
      var question = checkIndex < int.MaxValue ? checks[checkIndex].Question : null;
      return (new SummaryAnswer(answer.StopId, stop?.Title, answer.CheckId, question, answer.Text, answer.Type, answer.State, answer.OutdatedOn), (stopIndex, checkIndex));
   }

   // The UI's thread filter finds a thread's stop with the same rule.
   private static SummaryThread ThreadOf(QuestionThread thread, IReadOnlyList<Stop> stops)
   {
      var stopId = thread.Anchor switch
      {
         ThreadAnchor.StopText selected => selected.StopId,
         ThreadAnchor.WholeStop stop => stop.StopId,
         _ => thread switch
         {
            QuestionThread.Note note => note.StopId,
            QuestionThread.Sent sent => (sent.Messages[0] as ThreadMessage.Question)?.StopId,
         },
      };
      var title = stopId is null ? null : stops.FirstOrDefault(stop => stop.Id == stopId)?.Title;
      var action = (thread as QuestionThread.Sent.Asked)?.Action;
      return new SummaryThread(thread.Id, thread.Anchor, stopId, title, action?.Kind, (thread as QuestionThread.Sent.Decided)?.Decision.Kind, thread is QuestionThread.Note)
      {
         HandsOverReview = action is not null && ActionRules.HandsOverReview(action),
      };
   }

   // Returns int.MaxValue when no item matches, so an unknown item sorts last.
   private static int IndexOf<T>(IReadOnlyList<T> items, Func<T, bool> match)
   {
      for (var i = 0; i < items.Count; i++)
      {
         if (match(items[i]))
         {
            return i;
         }
      }

      return int.MaxValue;
   }
}

/// <summary>The reviewer's answers of a review summary, each group in plan order.</summary>
/// <param name="Decisions">The answers of type decision to checks of the stops' current content.</param>
/// <param name="Concerns">The answers of type concern to checks of the stops' current content.</param>
/// <param name="OpenQuestions">The answers of type open question to checks of the stops' current content.</param>
/// <param name="Outdated">The outdated answers, of every type.</param>
internal sealed record SummaryAnswers(IReadOnlyList<SummaryAnswer> Decisions, IReadOnlyList<SummaryAnswer> Concerns, IReadOnlyList<SummaryAnswer> OpenQuestions, IReadOnlyList<SummaryAnswer> Outdated);

/// <summary>One answer of a review summary, with the stop and the check that it answers.</summary>
/// <param name="StopId">The ID of the stop whose content holds the check.</param>
/// <param name="StopTitle">The stop's title, or null when the plan no longer has the stop. JSON leaves it out when null.</param>
/// <param name="CheckId">The check's ID.</param>
/// <param name="Question">
/// The check's question as prose, from the stop's current content or, for an outdated answer, from the version that the answer stays on, or null when neither holds the check. JSON leaves it out
/// when null.
/// </param>
/// <param name="Text">The reviewer's answer as plain text.</param>
/// <param name="Type">The answer's type, which the reviewer set.</param>
/// <param name="State">The answer's state.</param>
/// <param name="OutdatedOn">The <see cref="CheckAnswer.OutdatedOn"/> of the answer. JSON leaves it out when null.</param>
internal sealed record SummaryAnswer(
   string StopId,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StopTitle,
   string CheckId,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Question,
   string Text,
   AnswerType Type,
   AnswerState State,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? OutdatedOn = null);

/// <summary>One risk stop of a review summary.</summary>
/// <param name="StopId">The stop's ID.</param>
/// <param name="Title">The stop's title.</param>
/// <param name="Scenario">How the risk could happen, as prose, or null while the stop has no content. JSON leaves it out when null.</param>
internal sealed record SummaryRisk(string StopId, string Title, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Scenario);

/// <summary>The threads of a review summary, each group in the order the threads were started.</summary>
/// <param name="Open">The open threads.</param>
/// <param name="Resolved">The resolved threads.</param>
/// <param name="Outdated">The outdated threads.</param>
internal sealed record SummaryThreads(IReadOnlyList<SummaryThread> Open, IReadOnlyList<SummaryThread> Resolved, IReadOnlyList<SummaryThread> Outdated);

/// <summary>One thread of a review summary, with what its anchor is about.</summary>
/// <param name="Id">The thread's ID.</param>
/// <param name="Anchor">The thread's anchor as stored.</param>
/// <param name="StopId">
/// The stop that the thread is about: the anchor's stop for a stop or stop text anchor, otherwise the stop that the reviewer viewed while asking or writing the note, or null for none. JSON leaves
/// it out when null.
/// </param>
/// <param name="StopTitle">The stop's title, or null when the thread is about no stop or the plan no longer has the stop. JSON leaves it out when null.</param>
/// <param name="Action">The kind of the tour action that the thread's first question requests, or null for a thread without one. JSON leaves it out when null.</param>
/// <param name="Decision">The kind of the reviewer decision that a decided thread reports, or null for any other thread. JSON leaves it out when null.</param>
/// <param name="Note">Whether the thread is a note that the reviewer has not sent. JSON leaves it out when false.</param>
internal sealed record SummaryThread(
   string Id,
   ThreadAnchor Anchor,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StopId,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StopTitle,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Action = null,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Decision = null,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Note = false)
{
   /// <summary>Whether the thread's tour action hands the review to Claude, as <see cref="ActionRules.HandsOverReview"/> says, so the summary's fingerprint leaves the thread out.</summary>
   [JsonIgnore]
   public bool HandsOverReview { get; init; }
}

/// <summary>One stop of the plan, named in a review summary.</summary>
/// <param name="StopId">The stop's ID.</param>
/// <param name="Title">The stop's title.</param>
internal sealed record SummaryStop(string StopId, string Title);

/// <summary>One stop with a stale marker, named in a review summary.</summary>
/// <param name="StopId">The stop's ID.</param>
/// <param name="Title">The stop's title.</param>
/// <param name="StaleOn">The stop's <see cref="Stop.Written.StaleOn"/>.</param>
internal sealed record SummaryStaleStop(string StopId, string Title, int StaleOn);

/// <summary>The bound session's last synthesis of the review, as a review summary returns it.</summary>
/// <param name="Text">The synthesis as prose.</param>
/// <param name="WrittenAt">When the session wrote it.</param>
/// <param name="Pin">The tour's pin when the session wrote it, which the synthesis's <c>ref:</c> links read at.</param>
/// <param name="PredatesSummary">
/// Whether the summary changed since the one that the synthesis is based on, as an agent session reads the summary: an answer that was sent or changed, a thread that was started or changed
/// state, a skip, a stale stop, a risk stop or its scenario, a stop title or check question that the summary names, or another merge base or head. Claude's fetch of a batch of answers is no
/// change.
/// </param>
internal sealed record SummarySynthesis(string Text, DateTimeOffset WrittenAt, TourPin.Pinned Pin, bool PredatesSummary);

/// <summary>Why each follow-up action has nothing to hand to Claude, the reason that a request of it gets, so the summary page disables its button with that reason.</summary>
/// <param name="DraftWorkItems">The reason for <c>draftWorkItems</c>, or null when the summary holds a decision. JSON leaves it out when null.</param>
/// <param name="WalkOpenQuestions">The reason for <c>walkOpenQuestions</c>, or null when the summary holds an open question. JSON leaves it out when null.</param>
/// <param name="DraftComments">
/// The reason for <c>draftComments</c>, or null when the summary holds a concern or a risk stop that <see cref="TourAction.FollowUp.DraftComments"/> hands to Claude. JSON leaves it out when null.
/// </param>
internal sealed record SummaryFollowUps(
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DraftWorkItems,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WalkOpenQuestions,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DraftComments)
{
   /// <summary>Returns the reasons from the summary that an agent session reads, which the follow-up actions collect their items from.</summary>
   public static SummaryFollowUps Of(ReviewSummary agentSummary) => new(
      ActionRules.NothingToHand(new ActionRequest.FollowUp.DraftWorkItems(), agentSummary),
      ActionRules.NothingToHand(new ActionRequest.FollowUp.WalkOpenQuestions(), agentSummary),
      ActionRules.NothingToHand(new ActionRequest.FollowUp.DraftComments(), agentSummary));
}
