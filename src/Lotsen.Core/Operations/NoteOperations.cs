using System.ComponentModel;
using System.Text.Json.Serialization;
using Lotsen.Pinning;
using Lotsen.Sessions;
using Lotsen.Tours;

namespace Lotsen.Operations;

/// <summary>The input of <c>edit_note</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record EditNoteInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId,
   [property: Description("The ID of the note's thread.")]
   string ThreadId,
   [property: Description("The note's new words as plain text, with no control characters but tabs and line breaks.")]
   string Text);

/// <summary>The input of <c>discard_note</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DiscardNoteInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId,
   [property: Description("The ID of the note's thread.")]
   string ThreadId);

/// <summary>The input of <c>send_notes</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SendNotesInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId,
   [property: Description("The IDs of the notes' threads, each once, in the order to send them.")]
   IReadOnlyList<string> ThreadIds);

/// <summary>The threads of notes that one <c>send_notes</c> sent, with the tour's update time after the send.</summary>
/// <param name="TourId">The threads' tour.</param>
/// <param name="UpdatedAt">The tour's update time after the send.</param>
/// <param name="Threads">The threads as stored, in the order of the input's thread IDs, each with its question and that question's pending request.</param>
internal sealed record SentNotes(TourId TourId, DateTimeOffset UpdatedAt, IReadOnlyList<QuestionThread> Threads);

/// <summary>
/// The reviewer's notes: <c>save_note</c>, which stores a thread without sending it, <c>edit_note</c> and <c>discard_note</c>, which change or remove a note until it is sent, and
/// <c>send_notes</c>, which turns notes into questions and sends them. Only the UI writes notes, because a note holds the reviewer's own words.
/// </summary>
internal sealed class NoteOperations
{
   /// <summary>The most notes that one <c>send_notes</c> sends. Each note is one event in the session's queue of <see cref="AgentSessions.EventCapacity"/> events, which a full queue ends.</summary>
   public const int MaxNoteBatch = 50;

   private readonly TourWriter _writer;
   private readonly FileOperations _files;
   private readonly IterationScheduler _scheduler;

   public NoteOperations(TourWriter writer, FileOperations files, IterationScheduler scheduler)
   {
      _writer = writer;
      _files = files;
      _scheduler = scheduler;
   }

   /// <summary>Stores the UI's words as a new thread that is a note: no request, and nothing reaches any session. Returns the thread with the tour's update time.</summary>
   /// <remarks>A note needs no bound session. Its words and anchor pass the checks of a question, as <see cref="ThreadOpener.StoreNoteAsync"/> describes.</remarks>
   public async Task<OperationResult<UpdatedThread>> SaveNoteAsync(Access.Reviewer.Pass pass, AskQuestionInput input, CancellationToken cancellationToken)
   {
      var created = await ThreadOpener.StoreNoteAsync(_writer, _files, pass, NewThreadInput.Of(input), cancellationToken);
      return created.Value is { } stored ? new UpdatedThread(stored.Tour.Id, stored.Tour.UpdatedAt, stored.Thread) : created.Error!;
   }

   /// <summary>Replaces the words of a note that is not sent, and returns the thread with the tour's update time.</summary>
   /// <remarks>New words that equal the note's words change nothing, and the server publishes no event for them.</remarks>
   public OperationResult<UpdatedThread> EditNote(Access.Reviewer.Pass pass, EditNoteInput input) =>
      _writer.Write(pass, input.TourId, edit =>
         edit.Store(edit.Threads.Apply(new ThreadCommand.EditNote(input.ThreadId, input.Text)), (stored, written) => new UpdatedThread(stored.Tour.Id, stored.Tour.UpdatedAt, written.Thread!)),
         ReviewerWords.Check(input.Text));

   /// <summary>Removes a note that is not sent, with its thread, and returns the removal with the tour's update time.</summary>
   public OperationResult<DeletedThread> DiscardNote(Access.Reviewer.Pass pass, DiscardNoteInput input) =>
      _writer.Write(pass, input.TourId, edit =>
         edit.Store(edit.Threads.Apply(new ThreadCommand.DiscardNote(input.ThreadId)), (stored, _) => new DeletedThread(stored.Tour.Id, stored.Tour.UpdatedAt, input.ThreadId)));

   /// <summary>
   /// Turns each chosen note into its thread's first question, with a pending request for the tour's bound session, sends the session each question as its own event, and returns the threads.
   /// </summary>
   /// <remarks>
   /// One store's update sends the whole batch or nothing: a chosen thread that is gone or is not a note refuses the batch, and so does a tour with no bound session. A sent note's thread opens
   /// again, as any question that the reviewer sends opens it. Each question goes to the session as <c>ask_question</c> sends one, with the session's nonce while it is unconfirmed. The send does
   /// not check the note's viewed stop again, so a question may name a stop that the plan no longer has.
   /// </remarks>
   public OperationResult<SentNotes> SendNotes(Access.ReviewerToSession.Pass pass, SendNotesInput input)
   {
      _scheduler.Nudge(input.TourId, Nudge.ReviewerAction);
      return _writer.Write(pass, input.TourId, edit =>
         edit.Store(edit.Threads.SendNotes(input.ThreadIds), (stored, written) => new SentNotes(stored.Tour.Id, stored.Tour.UpdatedAt, written.Changed)), CheckBatch(input.ThreadIds));
   }

   private static OperationError? CheckBatch(IReadOnlyList<string>? threadIds)
   {
      if (threadIds is null || threadIds.Count == 0)
      {
         return OperationError.Invalid("threadIds", "give the ID of at least one note's thread.");
      }

      if (threadIds.Count > MaxNoteBatch)
      {
         return OperationError.Invalid("threadIds", $"a batch sends at most {MaxNoteBatch} notes, and this one holds {threadIds.Count}. Send the notes in smaller batches.");
      }

      return threadIds.GroupBy(id => id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1) is { } repeated
         ? OperationError.Invalid("threadIds", $"thread '{repeated.Key}' appears more than once. Give each note's thread once.")
         : null;
   }
}
