using System.ComponentModel;
using System.Text.Json.Serialization;
using Lotsen.Pinning;
using Lotsen.Sessions;
using Lotsen.Tours;

namespace Lotsen.Operations;

/// <summary>The input of <c>answer_check</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AnswerCheckInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId,
   [property: Description(OperationInputs.AnswerStopIdDescription)]
   string StopId,
   [property: Description(OperationInputs.CheckIdDescription)]
   string CheckId,
   [property: Description("The answer as plain text, with no control characters but tabs and line breaks.")]
   string Text,
   [property: Description("true when the reviewer picked one of the check's suggested answers, whose text this text repeats exactly. The answer then takes that suggestion's type. Omit it for the reviewer's own words, which keep the type of the check's earlier answer, or start as a decision when the check has no answer.")]
   bool FromSuggestion = false);

/// <summary>The input of <c>set_answer_type</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SetAnswerTypeInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId,
   [property: Description(OperationInputs.AnswerStopIdDescription)]
   string StopId,
   [property: Description(OperationInputs.CheckIdDescription)]
   string CheckId,
   [property: Description("The answer's new type: decision, concern or openQuestion.")]
   AnswerType Type);

/// <summary>The input of <c>clear_answer</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ClearAnswerInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId,
   [property: Description(OperationInputs.AnswerStopIdDescription)]
   string StopId,
   [property: Description(OperationInputs.CheckIdDescription)]
   string CheckId);

/// <summary>An answer as one write stored it, with the tour's update time after that write, so the UI orders it against the events and loads in flight.</summary>
/// <param name="TourId">The answer's tour.</param>
/// <param name="UpdatedAt">The tour's update time after the write, or the tour's current update time when the write changed nothing.</param>
/// <param name="Answer">The answer as stored.</param>
internal sealed record UpdatedAnswer(TourId TourId, DateTimeOffset UpdatedAt, CheckAnswer Answer);

/// <summary>An answer that one write removed, with the tour's update time after that write, so the UI orders the removal against the events and loads in flight.</summary>
/// <param name="TourId">The answer's tour.</param>
/// <param name="UpdatedAt">The tour's update time after the write.</param>
/// <param name="StopId">The ID of the stop whose check lost its answer.</param>
/// <param name="CheckId">The ID of the check that lost its answer.</param>
internal sealed record ClearedAnswer(TourId TourId, DateTimeOffset UpdatedAt, string StopId, string CheckId);

/// <summary>The input of <c>send_answers</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SendAnswersInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId);

/// <summary>The input of <c>fetch_answers</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record FetchAnswersInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId,
   [property: Description("The batch's ID, as the batch's channel event gave it.")]
   string BatchId);

/// <summary>The answers of one batch as one write stored them, with the tour's update time after that write, so the UI orders them against the events and loads in flight.</summary>
/// <param name="TourId">The answers' tour.</param>
/// <param name="UpdatedAt">The tour's update time after the write.</param>
/// <param name="BatchId">The batch's ID.</param>
/// <param name="Answers">The batch's answers as stored, in the tour's order of answers.</param>
internal sealed record UpdatedBatch(TourId TourId, DateTimeOffset UpdatedAt, string BatchId, IReadOnlyList<CheckAnswer> Answers);

/// <summary>The output of <c>fetch_answers</c>.</summary>
/// <param name="TourId">The batch's tour.</param>
/// <param name="BatchId">The batch's ID.</param>
/// <param name="SentAt">When the reviewer sent the batch.</param>
/// <param name="ReceivedAt">When the session that the batch went to first fetched it while bound to the tour, or null before it did. JSON leaves it out when null.</param>
/// <param name="Answers">The batch's answers, in the tour's order of answers.</param>
internal sealed record FetchedAnswers(
   TourId TourId,
   string BatchId,
   DateTimeOffset SentAt,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ReceivedAt,
   IReadOnlyList<FetchedAnswer> Answers);

/// <summary>One answer of a fetched batch, with the question of the check that it answers.</summary>
/// <param name="StopId">The ID of the stop whose content holds the check.</param>
/// <param name="CheckId">The check's ID.</param>
/// <param name="Question">
/// The check's question as prose, from the stop's current content or, for an outdated answer, from the version that the answer stays on, or null when neither holds the check. JSON leaves it out
/// when null.
/// </param>
/// <param name="Text">The reviewer's answer as plain text.</param>
/// <param name="Type">The answer's type, which the reviewer set.</param>
/// <param name="OutdatedOn">The <see cref="CheckAnswer.OutdatedOn"/> of the answer. JSON leaves it out when null.</param>
internal sealed record FetchedAnswer(
   string StopId,
   string CheckId,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Question,
   string Text,
   AnswerType Type,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? OutdatedOn = null);

/// <summary>The input of <c>set_stop_skipped</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SetStopSkippedInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId,
   [property: Description("The ID of a stop of the tour's plan, pending or with content.")]
   string StopId,
   [property: Description("true to skip the stop, false to take the skip back.")]
   bool Skipped);

/// <summary>A tour's skipped stops as one write stored them, with the tour's update time after that write, so the UI orders them against the events and loads in flight.</summary>
/// <param name="TourId">The tour.</param>
/// <param name="UpdatedAt">The tour's update time after the write, or the tour's current update time when the write changed nothing.</param>
/// <param name="StopIds">The IDs of every skipped stop of the tour, in plan order, as <see cref="Plan.SkippedIds"/> lists them.</param>
internal sealed record UpdatedSkips(TourId TourId, DateTimeOffset UpdatedAt, IReadOnlyList<string> StopIds);

/// <summary>The input of <c>set_stop_done</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SetStopDoneInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId,
   [property: Description("The ID of a stop of the tour's plan that has content.")]
   string StopId,
   [property: Description("The number of the stop's version that the reviewer read, from 1 up to the stop's current version, or null to take the done mark back.")]
   int? Version);

/// <summary>A stop's done mark as one write stored it, with the tour's update time after that write, so the UI orders it against the events and loads in flight.</summary>
/// <param name="TourId">The tour.</param>
/// <param name="UpdatedAt">The tour's update time after the write, or the tour's current update time when the write changed nothing.</param>
/// <param name="StopId">The stop.</param>
/// <param name="DoneVersion">The stop's <see cref="Stop.Written.DoneVersion"/> as stored, or null when the stop has no done mark. JSON leaves it out when null.</param>
internal sealed record UpdatedDoneVersion(TourId TourId, DateTimeOffset UpdatedAt, string StopId, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? DoneVersion);

/// <summary>
/// The reviewer's answers to checks, skips of stops and done marks: <c>answer_check</c>, <c>set_answer_type</c> and <c>clear_answer</c>, the batches of <c>send_answers</c> and
/// <c>fetch_answers</c>, <c>set_stop_skipped</c> and <c>set_stop_done</c>.
/// </summary>
/// <remarks>
/// Only the UI writes an answer, because it holds the reviewer's own words, and each write needs a check that the stop's current content holds on the latest tour, so a <c>put_stop</c> that
/// drops the check first refuses the write. A batch goes to the session that was bound when the writer stored it, which the batch names. A binding that ends or moves after the store therefore
/// finds the batch stored, and the next send holds its answers, as <see cref="CheckAnswer.AwaitsSending"/> describes.
/// </remarks>
internal sealed class AnswerOperations
{
   private readonly TourWriter _writer;
   private readonly IterationScheduler _scheduler;

   public AnswerOperations(TourWriter writer, IterationScheduler scheduler)
   {
      _writer = writer;
      _scheduler = scheduler;
   }

   /// <summary>Stores the UI's answer to a check as a draft, in the place of the check's earlier answer, and returns it with the tour's update time.</summary>
   /// <remarks>
   /// An answer from a suggestion takes the suggestion's type. The reviewer's own words keep the type of the check's earlier answer, or start as a decision when the check has no answer. A new
   /// text or type makes the answer a draft, as <see cref="CheckAnswer.Revised"/> describes. The same text and type change nothing, and the server publishes no event for them.
   /// </remarks>
   public OperationResult<UpdatedAnswer> AnswerCheck(Access.Reviewer.Pass pass, AnswerCheckInput input) =>
      ChangeAnswer(pass, input.TourId, input.StopId, input.CheckId, (check, current, now) =>
      {
         var type = current?.Type ?? AnswerType.Decision;
         if (input.FromSuggestion)
         {
            if (check.Suggestions?.FirstOrDefault(suggestion => suggestion.Text == input.Text) is not { } picked)
            {
               return OperationError.Invalid("text", $"check '{check.Id}' has no suggested answer with this text. Read the tour again, or answer in the reviewer's own words.");
            }

            type = picked.Type;
         }

         return current?.Revised(input.Text, type, now) ?? new CheckAnswer(input.StopId, input.CheckId, input.Text, type, AnswerState.Draft, now);
      }, ReviewerWords.Check(input.Text, "answer"));

   /// <summary>Sets the type of a check's answer and returns the answer with the tour's update time.</summary>
   /// <remarks>A new type makes the answer a draft, as <see cref="CheckAnswer.Revised"/> describes. The same type changes nothing, and the server publishes no event for it.</remarks>
   public OperationResult<UpdatedAnswer> SetAnswerType(Access.Reviewer.Pass pass, SetAnswerTypeInput input) =>
      ChangeAnswer(pass, input.TourId, input.StopId, input.CheckId, (_, current, now) =>
         current is null ? OperationError.AnswerNotFound(input.StopId, input.CheckId) : current.Revised(current.Text, input.Type, now), invalidInput: null);

   /// <summary>Removes a check's answer and returns the removal with the tour's update time.</summary>
   /// <remarks>A sent or received answer is removed too, so it leaves its batch. Nothing reaches the session that the batch went to.</remarks>
   public OperationResult<ClearedAnswer> ClearAnswer(Access.Reviewer.Pass pass, ClearAnswerInput input) =>
      _writer.Write(pass, input.TourId, edit =>
      {
         var latest = edit.Latest;
         if (FindCheck(latest, input.StopId, input.CheckId).Error is { } refused)
         {
            return refused;
         }

         if (latest.Answers.FirstOrDefault(answer => answer.Answers(input.StopId, input.CheckId)) is not { } current)
         {
            return OperationError.AnswerNotFound(input.StopId, input.CheckId);
         }

         return edit.Store(latest with { Answers = [.. latest.Answers.Where(answer => !ReferenceEquals(answer, current))] }, stored =>
         {
            var cleared = new ClearedAnswer(stored.Tour.Id, stored.Tour.UpdatedAt, input.StopId, input.CheckId);
            stored.Publish(ServerEvents.ForAnswerCleared(cleared));
            return cleared;
         });
      });

   /// <summary>Sends every answer to send of the tour to its bound session as one batch, sends the session one event with the batch's ID, and returns the batch's answers.</summary>
   /// <remarks>
   /// The batch holds the answers that <see cref="CheckAnswer.ToSend"/> gives. One store's update sends the whole batch or nothing: a tour with no bound session is refused, and so is a tour
   /// with no answer to send. The session need not be confirmed, and the event carries the session's nonce until it is.
   /// </remarks>
   public OperationResult<UpdatedBatch> SendAnswers(Access.ReviewerToSession.Pass pass, SendAnswersInput input)
   {
      _scheduler.Nudge(input.TourId, Nudge.ReviewerAction);
      return _writer.Write(pass, input.TourId, edit =>
      {
         var latest = edit.Latest;
         var session = edit.Recipient;
         var toSend = CheckAnswer.ToSend(latest, session.Id);
         if (toSend.Value is not { } held)
         {
            return toSend.Error!;
         }

         var sending = held.ToHashSet(ReferenceEqualityComparer.Instance);
         var batch = new AnswerBatch(TourIds.Random.Next(TourIds.NamedBy(latest)), session.Id, edit.Now);
         var answers = latest.Answers.Select(answer => sending.Contains(answer) ? answer.SentIn(batch) : answer).ToList();
         return edit.Store(latest with { Answers = answers }, stored =>
         {
            var sent = new UpdatedBatch(stored.Tour.Id, stored.Tour.UpdatedAt, batch.Id, [.. stored.Tour.Answers.Where(answer => answer.Batch?.Id == batch.Id)]);
            stored.Publish(ServerEvents.ForAnswerBatchUpdated(sent));

            // A session that ends before the delivery gets nothing, and its batch's answers await the next send.
            stored.Send(session.Answers(stored.Tour.Id, batch.Id));
            return sent;
         });
      });
   }

   /// <summary>Returns a batch of the reviewer's answers, each with the question of its check.</summary>
   /// <remarks>
   /// A fetch by the session that the batch went to, while it is bound to the tour, marks the batch's answers received, once, inside the store's update, so two fetches that race mark them once and a
   /// later fetch changes nothing. A fetch by any other session only reads. That includes the batch's session once another session took the tour over, because only the bound session acts on the
   /// answers, so they stay sent and the next send holds them.
   /// </remarks>
   public OperationResult<FetchedAnswers> FetchAnswers(Access.AnySession.Pass pass, FetchAnswersInput input) => _writer.Exclusive<OperationResult<FetchedAnswers>>(pass, locked =>
   {
      var read = locked.Read(input.TourId);
      if (read.Value is not { } tour)
      {
         return read.Error!;
      }

      var session = pass.Recipient;
      if (locked.Holder(tour.Id) == session && tour.Answers.Any(answer => IsUnreceived(answer, input.BatchId, session)))
      {
         var marked = locked.Write(tour.Id, edit => MarkBatchReceived(edit, input.BatchId, session));
         if (marked.Value is not { } stored)
         {
            return marked.Error!;
         }

         tour = stored;
      }

      var answers = AgentView.Of(tour).Answers.Where(answer => answer.Batch?.Id == input.BatchId).ToList();
      if (answers.Count == 0)
      {
         return OperationError.BatchNotFound(tour.Id, input.BatchId);
      }

      var batch = answers[0].Batch!;
      return new FetchedAnswers(tour.Id, batch.Id, batch.SentAt, batch.ReceivedAt, [.. answers.Select(answer => new FetchedAnswer(
         answer.StopId,
         answer.CheckId,
         QuestionOf(tour, answer),
         answer.Text,
         answer.Type,
         answer.OutdatedOn))]);
   });

   /// <summary>Skips a stop of the plan or takes its skip back, and returns every skip of the tour with the tour's update time.</summary>
   /// <remarks>
   /// The tour needs no bound session, and nothing reaches any session. The write finds the stop in the latest plan, so a <c>set_plan</c> that drops the stop first refuses the write. A stop already
   /// in the state stays as it is, and the server publishes no event for it.
   /// </remarks>
   public OperationResult<UpdatedSkips> SetStopSkipped(Access.Reviewer.Pass pass, SetStopSkippedInput input) =>
      _writer.Write(pass, input.TourId, edit =>
      {
         var latest = edit.Latest;
         if (latest.Plan is not { } plan || latest.FindStop(input.StopId) is not { } stop)
         {
            return OperationError.StopNotShown(input.StopId);
         }

         var changed = stop.Skipped != input.Skipped;
         var next = changed ? latest with { Plan = plan with { Stops = [.. plan.Stops.Select(other => other.Id == stop.Id ? other with { Skipped = input.Skipped } : other)] } } : latest;
         return edit.Store(next, stored =>
         {
            var result = new UpdatedSkips(stored.Tour.Id, stored.Tour.UpdatedAt, stored.Tour.Plan!.SkippedIds);
            if (changed)
            {
               stored.Publish(ServerEvents.ForSkipsUpdated(result));
            }

            return result;
         });
      });

   /// <summary>Marks a version of a stop with content done or takes the done mark back, and returns the stop's done mark with the tour's update time.</summary>
   /// <remarks>
   /// The tour needs no bound session, and nothing reaches any session. The write finds the stop in the latest plan, so a <c>set_plan</c> that drops the stop first refuses the write. It accepts an
   /// earlier version that the stop holds, so a mark from a page that read the stop before a revision covers the version that the reviewer read. A stop that has the mark already stays as it is, and
   /// the server publishes no event for it.
   /// </remarks>
   public OperationResult<UpdatedDoneVersion> SetStopDone(Access.Reviewer.Pass pass, SetStopDoneInput input) =>
      _writer.Write(pass, input.TourId, edit =>
      {
         var latest = edit.Latest;
         if (latest.Plan is not { } plan || latest.FindStop(input.StopId) is not { } found)
         {
            return OperationError.StopNotShown(input.StopId);
         }

         if (found is not Stop.Written stop)
         {
            return OperationError.Invalid("stopId", $"stop '{input.StopId}' has no content yet, so the reviewer has nothing to mark done. Wait until Claude writes the stop.");
         }

         if (input.Version is { } version && (version < 1 || version > stop.Version))
         {
            return OperationError.Invalid("version", $"stop '{input.StopId}' has versions 1 to {stop.Version}, not {version}. Read the tour again.");
         }

         var changed = stop.DoneVersion != input.Version;
         var next = changed ? latest with { Plan = plan with { Stops = [.. plan.Stops.Select(other => other.Id == stop.Id ? stop with { DoneVersion = input.Version } : other)] } } : latest;
         return edit.Store(next, stored =>
         {
            var result = new UpdatedDoneVersion(stored.Tour.Id, stored.Tour.UpdatedAt, stop.Id, input.Version);
            if (changed)
            {
               stored.Publish(ServerEvents.ForDoneVersionUpdated(result));
            }

            return result;
         });
      });

   // Marks the batch's sent answers received and publishes them, and returns the stored tour.
   private static Edited<Tour> MarkBatchReceived(TourEdit edit, string batchId, Recipient session)
   {
      var latest = edit.Latest;
      if (!latest.Answers.Any(answer => IsUnreceived(answer, batchId, session)))
      {
         return edit.Store(latest, stored => stored.Tour);
      }

      var answers = latest.Answers.Select(answer => IsUnreceived(answer, batchId, session) ? answer.AsReceived(edit.Now) : answer).ToList();
      return edit.Store(latest with { Answers = answers }, stored =>
      {
         stored.Publish(ServerEvents.ForAnswerBatchUpdated(new UpdatedBatch(stored.Tour.Id, stored.Tour.UpdatedAt, batchId, [.. stored.Tour.Answers.Where(answer => answer.Batch?.Id == batchId)])));
         return stored.Tour;
      });
   }

   // Claude wrote the question in an earlier version, so reading it there shows Claude none of the reviewer's words.
   private static string? QuestionOf(Tour tour, CheckAnswer answer)
   {
      var stop = tour.FindStop(answer.StopId) as Stop.Written;
      var content = answer.OutdatedOn is { } number ? stop?.ContentOf(number) : stop?.Content;
      return content?.Checks?.FirstOrDefault(check => check.Id == answer.CheckId)?.Question;
   }

   private static bool IsUnreceived(CheckAnswer answer, string batchId, Recipient session) =>
      answer is { State: AnswerState.Sent, Batch: { } batch } && batch.Id == batchId && batch.SessionId == session.Id;

   // Returns the check, or the refusal for a stop that the plan lacks, or a check that the stop's current content lacks.
   private static OperationResult<StopCheck> FindCheck(Tour tour, string stopId, string checkId)
   {
      if (tour.Plan?.Stops.FirstOrDefault(stop => stop.Id == stopId) is not { } stop)
      {
         return OperationError.StopNotShown(stopId);
      }

      return (stop as Stop.Written)?.Content.Checks?.FirstOrDefault(check => check.Id == checkId) is { } check ? check : OperationError.CheckNotFound(stopId, checkId);
   }

   // Replaces the check's answer with what the change returns from the check, the current answer or null, and the time. The change returns the current answer when nothing changes.
   private OperationResult<UpdatedAnswer> ChangeAnswer(
      Access.Reviewer.Pass pass,
      TourId tourId,
      string stopId,
      string checkId,
      Func<StopCheck, CheckAnswer?, DateTimeOffset, OperationResult<CheckAnswer>> change,
      OperationError? invalidInput) =>
      _writer.Write(pass, tourId, edit =>
      {
         var latest = edit.Latest;
         var found = FindCheck(latest, stopId, checkId);
         if (found.Value is not { } check)
         {
            return found.Error!;
         }

         var current = latest.Answers.FirstOrDefault(candidate => candidate.Answers(stopId, checkId));
         var changed = change(check, current, edit.Now);
         if (changed.Value is not { } answer)
         {
            return changed.Error!;
         }

         var next = ReferenceEquals(answer, current) ? latest
            : latest with { Answers = current is null ? [.. latest.Answers, answer] : [.. latest.Answers.Select(candidate => ReferenceEquals(candidate, current) ? answer : candidate)] };
         return edit.Store(next, stored =>
         {
            var result = new UpdatedAnswer(stored.Tour.Id, stored.Tour.UpdatedAt, answer);
            if (!ReferenceEquals(answer, current))
            {
               stored.Publish(ServerEvents.ForAnswerUpdated(result));
            }

            return result;
         });
      }, invalidInput);
}
