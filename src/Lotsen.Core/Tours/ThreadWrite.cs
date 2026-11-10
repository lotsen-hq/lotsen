using System.Collections.Immutable;
using Lotsen.Sessions;

namespace Lotsen.Tours;

/// <summary>What one write changes in a tour's threads, as <see cref="TourThreads"/> decides it: the next tour, each changed thread with its event, and the questions it sends.</summary>
/// <remarks><see cref="TourThreads"/> builds one, and the tour writer stores it with its events and sends.</remarks>
internal sealed class ThreadWrite
{
   private ThreadWrite(Tour from, Tour next, QuestionThread? thread, ImmutableArray<ThreadChange> changes, ImmutableArray<ThreadSend> sends, TourIds ids)
   {
      From = from;
      Next = next;
      Thread = thread;
      Changes = changes;
      Sends = sends;
      Ids = ids;
      Changed = [.. changes.Where(change => change.Kind != ThreadChangeKind.Removed).Select(change => change.Thread)];
   }

   /// <summary>The thread that the last command named, after the change, or null when the command removed it, named several, or the write settles a fact.</summary>
   public QuestionThread? Thread { get; }

   /// <summary>The threads that the write created or updated, after the change, in the order it changed them: input order for a note batch, tour order for a fact.</summary>
   public IReadOnlyList<QuestionThread> Changed { get; }

   /// <summary>Whether the caller's own event carries the write's changes, so the store publishes no thread event for them.</summary>
   public bool IsCarried => Changes.Length > 0 && Changes.All(change => change.Carried);

   /// <summary>The tour that the write started from.</summary>
   internal Tour From { get; }

   /// <summary>The tour after the write, or <see cref="From"/> itself when it changed no thread.</summary>
   internal Tour Next { get; }

   /// <summary>The changes in the order the write made them, one per thread.</summary>
   internal ImmutableArray<ThreadChange> Changes { get; }

   /// <summary>The questions that the write sends, in the order it made them.</summary>
   internal ImmutableArray<ThreadSend> Sends { get; }

   private TourIds Ids { get; }

   /// <summary>Returns this write followed by the command, applied to <see cref="Next"/> in the same store update, or the command's refusal.</summary>
   /// <remarks>
   /// The command's IDs avoid the IDs that this write drew, because they are in <see cref="Next"/>. A thread that both change keeps one change, at the place of its first, or none when this write
   /// created it and the command removes it. A thread that the command removes keeps none of this write's sends, because the session cannot fetch a question of a thread that is gone.
   /// </remarks>
   public OperationResult<ThreadWrite> Then(ThreadCommand command, DateTimeOffset now)
   {
      var next = TourThreads.Apply(Next, command, now, Ids);
      return next.Value is { } write ? Joined(write) : next.Error!;
   }

   /// <summary>Returns the session events of the write's questions, for the tour as stored.</summary>
   internal IEnumerable<SessionSend> SendsFor(Tour stored) => Sends.Select(send => send.To.Question(stored.Id, send.Thread));

   internal static ThreadWrite Of(Tour from, Tour next, QuestionThread? thread, ImmutableArray<ThreadChange> changes, ImmutableArray<ThreadSend> sends, TourIds ids) =>
      new(from, next, thread, changes, sends, ids);

   private ThreadWrite Joined(ThreadWrite later)
   {
      var changes = Changes.ToList();
      foreach (var change in later.Changes)
      {
         var earlier = changes.FindIndex(candidate => candidate.Thread.Id == change.Thread.Id);
         if (earlier < 0)
         {
            changes.Add(change);
         }
         else if (changes[earlier].Kind == ThreadChangeKind.Created && change.Kind == ThreadChangeKind.Removed)
         {
            changes.RemoveAt(earlier);
         }
         else
         {
            changes[earlier] = change with { Kind = changes[earlier].Kind == ThreadChangeKind.Created ? ThreadChangeKind.Created : change.Kind };
         }
      }

      var removed = later.Changes.Where(change => change.Kind == ThreadChangeKind.Removed).Select(change => change.Thread.Id).ToHashSet(StringComparer.Ordinal);
      return new(From, later.Next, later.Thread, [.. changes], [.. Sends.Where(send => !removed.Contains(send.Thread.Id)), .. later.Sends], Ids);
   }
}

/// <summary>Whether a write created, updated or removed a thread, which decides the thread's event.</summary>
internal enum ThreadChangeKind
{
   Created,
   Updated,
   Removed,
}

/// <summary>One thread that a write changed.</summary>
/// <param name="Kind">What the write did to the thread.</param>
/// <param name="Thread">The thread after the change, or as it was before a removal.</param>
/// <param name="Carried">Whether the caller's own event carries the change, so it gets no thread event.</param>
internal sealed record ThreadChange(ThreadChangeKind Kind, QuestionThread Thread, bool Carried);

/// <summary>A question that a write sends: the thread's last question and that question's last request.</summary>
/// <param name="To">The session that the request names.</param>
/// <param name="Thread">The thread after the write.</param>
internal sealed record ThreadSend(Recipient To, QuestionThread.Sent Thread);
