using System.ComponentModel;
using System.Text.Json.Serialization;
using Lotsen.Json;
using Lotsen.Prose;
using Lotsen.Sessions;
using Lotsen.Tours;

namespace Lotsen.Operations;

/// <summary>The input of <c>start_thread</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record StartThreadInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId,
   [property: Description("The message that starts the thread, in Markdown, which may link to code as [label](ref:<side>/<path>#L<start>-<end>).")]
   string Text,
   [property: Description("Where the thread belongs: code for whole lines at one side, stopText for text in a prose field of a stop, file, stop, or tour.")]
   AnchorInput Anchor);

/// <summary>The input of <c>add_thread_message</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AddThreadMessageInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId,
   [property: Description("The ID of the thread, from read_state, fetch_question or start_thread.")]
   string ThreadId,
   [property: Description("The message in Markdown, which may link to code as [label](ref:<side>/<path>#L<start>-<end>).")]
   string Text);

/// <summary>The input of <c>outdate_thread</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record OutdateThreadInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId,
   [property: Description("The ID of the thread, from read_state, fetch_question or start_thread.")]
   string ThreadId);

/// <summary>The input of <c>delete_thread</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DeleteThreadInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId,
   [property: Description("The ID of the thread, from read_state, fetch_question or start_thread.")]
   string ThreadId);

/// <summary>The input of <c>remove_thread</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RemoveThreadInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId,
   [property: Description("The ID of the thread.")]
   string ThreadId);

/// <summary>The input of <c>set_thread_state</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SetThreadStateInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId,
   [property: Description("The ID of the thread.")]
   string ThreadId,
   [property: Description("The thread's new state: resolved when the reviewer considers it settled, open to reopen it.")]
   ReviewerThreadState State);

/// <summary>A thread as one write stored it, with the tour's update time after that write, so the UI orders it against the events and loads in flight.</summary>
/// <param name="TourId">The thread's tour.</param>
/// <param name="UpdatedAt">The tour's update time after the write, or the tour's current update time when the write changed nothing.</param>
/// <param name="Thread">The thread as stored.</param>
internal sealed record UpdatedThread(TourId TourId, DateTimeOffset UpdatedAt, QuestionThread Thread);

/// <summary>A thread that one write removed, with the tour's update time after that write, so the UI orders the removal against the events and loads in flight.</summary>
/// <param name="TourId">The thread's tour.</param>
/// <param name="UpdatedAt">The tour's update time after the write.</param>
/// <param name="ThreadId">The removed thread's ID.</param>
internal sealed record DeletedThread(TourId TourId, DateTimeOffset UpdatedAt, string ThreadId);

/// <summary>
/// The writes of a thread that send no question to an agent session: the bound session's <c>start_thread</c>, <c>add_thread_message</c>, <c>outdate_thread</c> and <c>delete_thread</c>, and
/// the reviewer's <c>set_thread_state</c> and <c>remove_thread</c>.
/// </summary>
/// <remarks>
/// The input types hold no author, so each agent message's author is the calling session. The refusals are those of <see cref="TourThreads"/>: the session writes only the threads that its reads
/// show it, so a note is not found, and it writes no thread whose last question waits, because the reviewer waits for a reply there.
/// </remarks>
internal sealed class ThreadOperations
{
   private readonly TourWriter _writer;
   private readonly FileOperations _files;

   public ThreadOperations(TourWriter writer, FileOperations files)
   {
      _writer = writer;
      _files = files;
   }

   /// <summary>Stores a new thread whose first message is the calling session's agent message, and returns the thread with the tour's update time.</summary>
   /// <remarks>Only the bound session starts a thread. The anchor passes the checks of a question's anchor, and the text's links those of a reply, as <see cref="ThreadOpener.StoreAgentMessageAsync"/> describes.</remarks>
   public async Task<OperationResult<UpdatedThread>> StartThreadAsync(Access.BoundSession.Pass pass, StartThreadInput input, CancellationToken cancellationToken)
   {
      var created = await ThreadOpener.StoreAgentMessageAsync(_writer, _files, pass, new NewThreadInput(input.TourId, input.Text, input.Anchor, null), cancellationToken);
      return created.Value is { } stored ? new UpdatedThread(stored.Tour.Id, stored.Tour.UpdatedAt, stored.Thread) : created.Error!;
   }

   /// <summary>Adds the calling session's agent message to the end of a thread, and returns the thread with the tour's update time.</summary>
   /// <remarks>
   /// Only the bound session adds a message, and the earlier messages stay as they are. The message resolves no request, and its state rule is that of <see cref="TourThreads"/>. The text's
   /// <c>ref:</c> links pass the checks of a reply.
   /// </remarks>
   public Task<OperationResult<UpdatedThread>> AddThreadMessageAsync(Access.BoundSession.Pass pass, AddThreadMessageInput input, CancellationToken cancellationToken) =>
      _writer.WriteAfterGitAsync<IReadOnlyList<ProseReference>, UpdatedThread>(
         pass,
         input.TourId,
         tour => ThreadOpener.CheckAgentText(input.Text) ?? TourThreads.Check(tour, new ThreadCommand.AgentWrote(input.ThreadId, input.Text, pass.Recipient)),
         async (_, git, token) =>
         {
            var links = await ProseLinks.CheckAsync(git, _files, input.Text, token);
            return links.Value is { } references ? Gathered.Of(references) : links.Error!;
         },
         (edit, _) => StoreThread(edit, edit.Threads.Apply(new ThreadCommand.AgentWrote(input.ThreadId, input.Text, edit.Recipient))),
         cancellationToken);

   /// <summary>Marks an open thread outdated for the calling session, and returns the thread with the tour's update time.</summary>
   /// <remarks>
   /// Only the bound session marks it. A resolved or outdated thread stays as it is, and the server publishes no event for it. So only an open thread is ever outdated, and the agent message that
   /// opens it again never undoes the reviewer's resolve.
   /// </remarks>
   public OperationResult<UpdatedThread> OutdateThread(Access.BoundSession.Pass pass, OutdateThreadInput input) =>
      _writer.Write(pass, input.TourId, edit => StoreThread(edit, edit.Threads.Apply(new ThreadCommand.Outdate(input.ThreadId, edit.Recipient))));

   /// <summary>Removes a thread with every message in it for the calling session, and returns the removal with the tour's update time.</summary>
   /// <remarks>Only the bound session removes a thread. The removal is permanent.</remarks>
   public OperationResult<DeletedThread> DeleteThread(Access.BoundSession.Pass pass, DeleteThreadInput input) =>
      _writer.Write(pass, input.TourId, edit =>
         edit.Store(edit.Threads.Apply(new ThreadCommand.Delete(input.ThreadId, edit.Recipient)), (stored, _) => new DeletedThread(stored.Tour.Id, stored.Tour.UpdatedAt, input.ThreadId)));

   /// <summary>Removes any thread of the tour for the reviewer, a note included, and returns the removal with the tour's update time.</summary>
   /// <remarks>
   /// The tour needs no bound session. A question of the thread that waits is canceled first, in the same store update, as <c>cancel_question</c> cancels it. When a session holds the tour, it
   /// gets one <see cref="SessionEvents.ThreadDeleted"/> for a sent thread, which names the canceled question. A note gets none, because no session saw it.
   /// </remarks>
   public OperationResult<DeletedThread> RemoveThread(Access.Reviewer.Pass pass, RemoveThreadInput input) =>
      _writer.Exclusive(pass, locked =>
      {
         var holder = locked.Holder(input.TourId);
         return locked.Write(input.TourId, edit =>
         {
            var waiting = (edit.Latest.Threads.Find(input.ThreadId) as QuestionThread.Sent)?.Waiting;
            var removal = waiting is null ? edit.Threads.Apply(new ThreadCommand.Remove(input.ThreadId)) : CanceledThenRemoved(edit, waiting.Id, input.ThreadId);
            return edit.Store(removal, (stored, _) =>
            {
               if (holder is not null && stored.Before.Threads.Find(input.ThreadId) is QuestionThread.Sent)
               {
                  stored.Send(holder.ThreadDeleted(stored.Tour.Id, input.ThreadId, waiting?.Id));
               }

               return new DeletedThread(stored.Tour.Id, stored.Tour.UpdatedAt, input.ThreadId);
            });
         });
      });

   /// <summary>Sets a thread's state, and returns the thread with the tour's update time.</summary>
   /// <remarks>
   /// The tour needs no bound session, and nothing reaches any session. The reviewer may open or resolve an outdated thread or a note. A thread already in the state stays as it is, and the server
   /// publishes no event for it. The update changes only the state, so a question of the thread may still wait, and its reply leaves the state as it is.
   /// </remarks>
   public OperationResult<UpdatedThread> SetThreadState(Access.Reviewer.Pass pass, SetThreadStateInput input) =>
      _writer.Write(pass, input.TourId, edit =>
         StoreThread(edit, edit.Threads.Apply(new ThreadCommand.SetState(input.ThreadId, input.State))));

   private static OperationResult<ThreadWrite> CanceledThenRemoved(TourEdit edit, string questionId, string threadId)
   {
      var canceled = edit.Threads.Apply(new ThreadCommand.Cancel(questionId));
      return canceled.Value is { } write ? write.Then(new ThreadCommand.Remove(threadId), edit.Now) : canceled.Error!;
   }

   // Stores the write, which publishes the thread when it changed, and returns the thread with the tour's update time.
   private static Edited<UpdatedThread> StoreThread(TourEdit edit, OperationResult<ThreadWrite> write) =>
      edit.Store(write, (stored, written) => new UpdatedThread(stored.Tour.Id, stored.Tour.UpdatedAt, written.Thread!));
}
