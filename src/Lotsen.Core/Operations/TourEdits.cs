using Lotsen.Prose;
using Lotsen.Sessions;
using Lotsen.Tours;

namespace Lotsen.Operations;

/// <summary>The latest stored tour, inside the store's update of one write, from which the write's change builds the next tour.</summary>
internal class TourEdit
{
   internal TourEdit(Tour latest, DateTimeOffset now)
   {
      Latest = latest;
      Now = now;
      Threads = new TourThreads.Handle(latest, now);
   }

   /// <summary>The tour as the store holds it now.</summary>
   public Tour Latest { get; }

   /// <summary>The time of the write, for the timestamps that the change stores.</summary>
   public DateTimeOffset Now { get; }

   /// <summary>The thread commands of the write that queue no question.</summary>
   public virtual TourThreads.Handle Threads { get; }

   /// <summary>Returns the change that stores <paramref name="next"/> and then runs <paramref name="then"/> on what was stored, still under the lock.</summary>
   /// <param name="next">The tour to store. The tour <see cref="Latest"/> itself stores nothing.</param>
   /// <param name="then">Publishes the write's events, queues its sends and returns the operation's output.</param>
   /// <exception cref="InvalidOperationException">The next tour's threads are not those of <see cref="Latest"/>, because only a <see cref="ThreadWrite"/> changes them.</exception>
   public Edited<T> Store<T>(Tour next, Func<Stored, T> then)
      where T : class =>
      ReferenceEquals(next.Threads, Latest.Threads) ? new(next, then) : throw new InvalidOperationException("A write that changes a tour's threads stores a ThreadWrite.");

   /// <summary>Returns the change that stores the write's tour, runs <paramref name="then"/>, then publishes the write's thread events in change order and queues its questions.</summary>
   /// <remarks>A write that changes nothing stores nothing and publishes nothing, and a carried write publishes no thread event.</remarks>
   /// <param name="write">The thread write, from the latest tour or from that tour with the write's other fields changed.</param>
   /// <param name="then">Publishes the feature's own events and returns the operation's output.</param>
   /// <exception cref="InvalidOperationException">The write did not start from the threads of <see cref="Latest"/>.</exception>
   public Edited<T> Store<T>(ThreadWrite write, Func<Stored, ThreadWrite, T> then)
      where T : class
   {
      if (!ReferenceEquals(write.From.Threads, Latest.Threads))
      {
         throw new InvalidOperationException("A thread write must start from the threads of the latest tour.");
      }

      return new(write.Next, stored =>
      {
         var output = then(stored, write);
         foreach (var serverEvent in ServerEvents.ForThreadWrite(write, stored.Tour, stored.Mentions))
         {
            stored.Publish(serverEvent);
         }

         foreach (var send in write.SendsFor(stored.Tour))
         {
            stored.Send(send);
         }

         return output;
      });
   }

   /// <summary>Returns the change that stores the write, as <see cref="Store{T}(ThreadWrite, Func{Stored, ThreadWrite, T})"/> does, or the refusal, which stores nothing.</summary>
   public Edited<T> Store<T>(OperationResult<ThreadWrite> write, Func<Stored, ThreadWrite, T> then)
      where T : class => write.Value is { } written ? Store(written, then) : write.Error!;
}

/// <summary>A <see cref="TourEdit"/> of a write with an <see cref="Access.BoundPass"/>, which names the tour's bound session.</summary>
internal sealed class SessionEdit : TourEdit
{
   internal SessionEdit(Tour latest, DateTimeOffset now, Recipient recipient)
      : base(latest, now)
   {
      Recipient = recipient;
      Threads = new TourThreads.SessionHandle(latest, now, recipient);
   }

   /// <summary>The bound session, which <see cref="Access.BoundPass.Bind"/> found under the same lock.</summary>
   public Recipient Recipient { get; }

   /// <summary>The thread commands of the write, with those that queue a question to <see cref="Recipient"/>.</summary>
   public override TourThreads.SessionHandle Threads { get; }
}

/// <summary>What a write's change decided: the tour to store with what follows the store, or the refusal, which stores nothing.</summary>
internal readonly record struct Edited<T>
   where T : class
{
   internal Edited(Tour next, Func<Stored, T> then)
   {
      Next = next;
      Then = then;
   }

   private Edited(OperationError refusal)
   {
      Refusal = refusal;
   }

   internal Tour? Next { get; }

   internal Func<Stored, T>? Then { get; }

   internal OperationError? Refusal { get; }

   public static implicit operator Edited<T>(OperationError refused) => new(refused);
}

/// <summary>A tour that one write stored, with the means to publish the write's events and to queue its session events.</summary>
/// <remarks>It exists while the write holds the lock, and throws once its write's change has returned.</remarks>
internal sealed class Stored
{
   private readonly EventBroadcaster _events;
   private readonly List<SessionSend> _sends;
   private bool _closed;

   internal Stored(Tour before, Tour tour, MentionLinks mentions, EventBroadcaster events, List<SessionSend> sends)
   {
      Before = before;
      Tour = tour;
      Mentions = mentions;
      _events = events;
      _sends = sends;
   }

   /// <summary>The tour as stored, with its new update time, or <see cref="Before"/> itself when the write stored nothing.</summary>
   public Tour Tour { get; }

   /// <summary>The tour that the write changed.</summary>
   public Tour Before { get; }

   /// <summary>The links of the mentions in the tour's prose, which the write's events with prose render with.</summary>
   public MentionLinks Mentions { get; }

   /// <summary>Publishes the event now, under the lock, so events reach subscribers in the order of the stores. A tour-scoped event carries the stored tour's revision.</summary>
   public void Publish(ServerEvent serverEvent)
   {
      ThrowIfClosed();
      _events.Publish(serverEvent.IsTourScoped ? serverEvent.At(Tour.Revision) : serverEvent);
   }

   /// <summary>Queues the session event, which the writer delivers after it releases the lock, in the order queued.</summary>
   public void Send(SessionSend send)
   {
      ThrowIfClosed();
      _sends.Add(send);
   }

   internal void Close() => _closed = true;

   private void ThrowIfClosed() => ObjectDisposedException.ThrowIf(_closed, this);
}
