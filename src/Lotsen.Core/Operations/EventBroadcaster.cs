using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Lotsen.Json;
using Lotsen.Prose;
using Lotsen.Server;
using Lotsen.Sessions;
using Lotsen.Tours;

namespace Lotsen.Operations;

/// <summary>One event for the event stream: its kind's name and its payload as JSON text, with the tour's revision for a tour-scoped kind.</summary>
/// <remarks>
/// Only <see cref="Of"/> builds an event, from a kind and a payload of the kind's type, so each payload goes under the name that declares its type. The broadcaster publishes an event of a
/// tour-scoped kind only once <see cref="At"/> gave it the revision of the tour that it shows, as <see cref="EventBroadcaster.Publish"/> checks. <see cref="Comment"/> builds a comment line,
/// which has no kind.
/// </remarks>
internal sealed class ServerEvent : IStreamEvent
{
   /// <summary>The property of a tour-scoped event's payload that holds the revision.</summary>
   public const string RevisionProperty = "revision";

   private ServerEvent(string type, string data, bool tourScoped, long? revision, bool comment = false)
   {
      Type = type;
      Data = data;
      IsTourScoped = tourScoped;
      Revision = revision;
      IsComment = comment;
   }

   public string Type { get; }

   public string Data { get; }

   public bool IsComment { get; }

   /// <summary>Whether the event's kind is about one tour, so its payload carries the tour's revision.</summary>
   public bool IsTourScoped { get; }

   /// <summary>The revision of the tour that the event shows, or null before <see cref="At"/> or for a kind that is not tour-scoped.</summary>
   public long? Revision { get; }

   /// <summary>Returns the event of the kind with the payload, as the kind's prose view renders it.</summary>
   public static ServerEvent Of<TPayload>(ServerEventKind<TPayload> kind, TPayload payload)
      where TPayload : class => new(kind.Name, kind.Write(payload), kind.IsTourScoped, null);

   /// <summary>Returns a comment line of the text, which every reader of the stream skips.</summary>
   /// <exception cref="ArgumentException">The text holds a line break, which would end the comment line.</exception>
   public static ServerEvent Comment(string text) =>
      text.AsSpan().IndexOfAny('\r', '\n') >= 0 ? throw new ArgumentException("A comment holds one line.", nameof(text)) : new(string.Empty, text, tourScoped: false, revision: null, comment: true);

   /// <summary>Returns the event with the revision of the tour that it shows in its payload.</summary>
   /// <exception cref="InvalidOperationException">The event's kind is not tour-scoped, or the event has a revision already.</exception>
   public ServerEvent At(long revision)
   {
      if (!IsTourScoped || Revision is not null)
      {
         throw new InvalidOperationException($"The event {Type} takes no revision{(Revision is null ? "" : " again")}.");
      }

      var payload = JsonNode.Parse(Data)!.AsObject();
      payload[RevisionProperty] = revision;
      return new ServerEvent(Type, payload.ToJsonString(), IsTourScoped, revision);
   }
}

/// <summary>The payload of a <see cref="ServerEvents.PlanSet"/> event.</summary>
/// <param name="TourId">The tour whose plan was set.</param>
/// <param name="UpdatedAt">The tour's update time after the change.</param>
/// <param name="Plan">The whole new plan.</param>
internal sealed record PlanSetEvent(TourId TourId, DateTimeOffset UpdatedAt, Plan Plan);

/// <summary>The payload of a <see cref="ServerEvents.StopPut"/> event.</summary>
/// <param name="TourId">The tour whose stop was written.</param>
/// <param name="UpdatedAt">The tour's update time after the change.</param>
/// <param name="Stop">The whole stop as stored, with its content, status, title, kind and chapter.</param>
internal sealed record StopPutEvent(TourId TourId, DateTimeOffset UpdatedAt, Stop Stop);

/// <summary>The payload of a <see cref="ServerEvents.StopRevised"/> event, which carries everything that the revision changed, so the UI applies the revision from the one event.</summary>
/// <param name="TourId">The tour whose stop was revised.</param>
/// <param name="UpdatedAt">The tour's update time after the change.</param>
/// <param name="Stop">
/// The whole stop as stored, always a <see cref="Tours.Stop.Written"/>, whose <see cref="Tours.Stop.Written.Version"/> is the new version's number and whose earlier versions end with the one that
/// the revision replaced. Its declared type is <see cref="Tours.Stop"/>, so its JSON names its stage.
/// </param>
/// <param name="Answers">The answers that the revision made outdated, as stored.</param>
/// <param name="Threads">The threads that the revision made outdated or moved to the new version.</param>
internal sealed record StopRevisedEvent(TourId TourId, DateTimeOffset UpdatedAt, Stop Stop, IReadOnlyList<CheckAnswer> Answers, IReadOnlyList<QuestionThread> Threads);

/// <summary>The payload of a <see cref="ServerEvents.TourPinned"/> event.</summary>
/// <param name="TourId">The tour whose pin state changed.</param>
/// <param name="UpdatedAt">The tour's update time after the change.</param>
/// <param name="Pin">The new pin state, with the pinned commits or the reason of the failure.</param>
internal sealed record TourPinnedEvent(TourId TourId, DateTimeOffset UpdatedAt, TourPin Pin);

/// <summary>The payload of a <see cref="ServerEvents.AvailableIterationChanged"/> event.</summary>
/// <param name="TourId">The tour whose available iteration changed.</param>
/// <param name="UpdatedAt">The tour's update time after the change.</param>
/// <param name="AvailableIteration">The new available iteration, or null when the check found the pinned head again.</param>
internal sealed record AvailableIterationChangedEvent(TourId TourId, DateTimeOffset UpdatedAt, AvailableIteration? AvailableIteration);

/// <summary>The payload of a <see cref="ServerEvents.TourBindingChanged"/> event.</summary>
/// <param name="TourId">The tour whose binding changed.</param>
/// <param name="Binding">The agent session now bound to the tour, or null when the binding was released.</param>
internal sealed record TourBindingChangedEvent(TourId TourId, TourBinding? Binding);

/// <summary>The payload of a <see cref="ServerEvents.ThreadCreated"/> event.</summary>
/// <param name="TourId">The tour that the thread belongs to.</param>
/// <param name="UpdatedAt">The tour's update time after the change.</param>
/// <param name="Thread">The new thread as stored.</param>
internal sealed record ThreadCreatedEvent(TourId TourId, DateTimeOffset UpdatedAt, QuestionThread Thread);

/// <summary>The payload of a <see cref="ServerEvents.ThreadUpdated"/> event.</summary>
/// <param name="TourId">The tour that the thread belongs to.</param>
/// <param name="UpdatedAt">The tour's update time after the change.</param>
/// <param name="Thread">The whole thread as stored.</param>
internal sealed record ThreadUpdatedEvent(TourId TourId, DateTimeOffset UpdatedAt, QuestionThread Thread);

/// <summary>The event kinds of the event stream, each declared once with its payload type and prose view, and how each payload is built.</summary>
/// <remarks>
/// Every stop with content and every agent message or reply with text in a payload carries the HTML of its prose, because each kind whose payload can hold prose declares the payload's HTML view.
/// </remarks>
internal static class ServerEvents
{
   // Each Declare adds its kind here, so the list comes before the kinds, whose initializers run in the order of their declarations.
   private static readonly List<ServerEventKind> Declared = [];

   private static readonly IReadOnlyList<ServerEventKind> DeclaredView = Declared.AsReadOnly();

   /// <summary>Every event kind, in the order of the declarations below.</summary>
   public static IReadOnlyList<ServerEventKind> All => DeclaredView;

   /// <summary>The first event of every event stream, which announces the stream's liveness. The payload is a <see cref="HelloEvent"/>.</summary>
   public static readonly ServerEventKind<HelloEvent> Hello = Declare("hello", LotsenJsonContext.Default.HelloEvent, ProseView.None, tourScoped: false);

   /// <summary>A tour was created. The payload is its <see cref="Operations.TourSummary"/>.</summary>
   public static readonly ServerEventKind<TourSummary> TourCreated = Declare("tourCreated", LotsenJsonContext.Default.TourSummary, ProseView.None);

   /// <summary>A tour's plan was set or replaced. The payload is a <see cref="PlanSetEvent"/>.</summary>
   public static readonly ServerEventKind<PlanSetEvent> PlanSet = Declare("planSet", LotsenJsonContext.Default.PlanSetEvent, ProseView.Html<PlanSetEvent>(ProseHtml.Of));

   /// <summary>A stop got its first content, which may also change the stop's title, kind and chapter. The payload is a <see cref="StopPutEvent"/>.</summary>
   public static readonly ServerEventKind<StopPutEvent> StopPut = Declare("stopPut", LotsenJsonContext.Default.StopPutEvent, ProseView.Html<StopPutEvent>(ProseHtml.Of));

   /// <summary>
   /// A stop's content was written again, as its next version, which may also change the stop's title, kind and chapter, the answers to its checks and the threads on its text. The payload is a
   /// <see cref="StopRevisedEvent"/>. The bound session wrote the revision, so no session event follows it.
   /// </summary>
   public static readonly ServerEventKind<StopRevisedEvent> StopRevised = Declare("stopRevised", LotsenJsonContext.Default.StopRevisedEvent, ProseView.Html<StopRevisedEvent>(ProseHtml.Of));

   /// <summary>A tour's pin state changed: a pin ended as pinned or failed, or a failed pin started again. The payload is a <see cref="TourPinnedEvent"/>.</summary>
   public static readonly ServerEventKind<TourPinnedEvent> TourPinned = Declare("tourPinned", LotsenJsonContext.Default.TourPinnedEvent, ProseView.None);

   /// <summary>
   /// An iteration check stored another available iteration for a tour, or cleared it, or an iteration update cleared it. The payload is an <see cref="AvailableIterationChangedEvent"/>. A check that finds what the tour holds
   /// publishes nothing.
   /// </summary>
   public static readonly ServerEventKind<AvailableIterationChangedEvent> AvailableIterationChanged =
      Declare("availableIterationChanged", LotsenJsonContext.Default.AvailableIterationChangedEvent, ProseView.None);

   /// <summary>
   /// The agent session bound to a tour changed: a session attached the tour or took it over, its first <c>ack</c> confirmed it, or the settlement of its end released it. The payload is a
   /// <see cref="TourBindingChangedEvent"/>. A tour that a session creates publishes none, because its <see cref="TourCreated"/> event carries the binding.
   /// </summary>
   public static readonly ServerEventKind<TourBindingChangedEvent> TourBindingChanged = Declare("tourBindingChanged", LotsenJsonContext.Default.TourBindingChangedEvent, ProseView.None);

   /// <summary>The reviewer asked a question or saved a note that starts a new thread, or the bound session started one. The payload is a <see cref="ThreadCreatedEvent"/>.</summary>
   public static readonly ServerEventKind<ThreadCreatedEvent> ThreadCreated = Declare("threadCreated", LotsenJsonContext.Default.ThreadCreatedEvent, ProseView.Html<ThreadCreatedEvent>(ProseHtml.Of));

   /// <summary>
   /// A thread changed: a reply, a follow-up, an agent message, a request that became unanswered, canceled or received, a question sent again, a new state of the thread, a note edited or sent,
   /// or an anchor that an iteration update moved or left stale. The payload is a <see cref="ThreadUpdatedEvent"/>. A revision's changes to threads go in <see cref="StopRevised"/> instead.
   /// </summary>
   public static readonly ServerEventKind<ThreadUpdatedEvent> ThreadUpdated = Declare("threadUpdated", LotsenJsonContext.Default.ThreadUpdatedEvent, ProseView.Html<ThreadUpdatedEvent>(ProseHtml.Of));

   /// <summary>A thread was removed, as when the reviewer discards a note or the bound session deletes a thread. The payload is a <see cref="Operations.DeletedThread"/>.</summary>
   public static readonly ServerEventKind<DeletedThread> ThreadDeleted = Declare("threadDeleted", LotsenJsonContext.Default.DeletedThread, ProseView.None);

   /// <summary>The reviewer answered a check, answered it again or changed its answer's type. The payload is an <see cref="UpdatedAnswer"/>.</summary>
   public static readonly ServerEventKind<UpdatedAnswer> AnswerUpdated = Declare("answerUpdated", LotsenJsonContext.Default.UpdatedAnswer, ProseView.None);

   /// <summary>The reviewer cleared a check's answer. The payload is a <see cref="ClearedAnswer"/>.</summary>
   public static readonly ServerEventKind<ClearedAnswer> AnswerCleared = Declare("answerCleared", LotsenJsonContext.Default.ClearedAnswer, ProseView.None);

   /// <summary>The reviewer sent a batch of answers, or the session that the batch went to fetched it, so its answers became sent or received. The payload is an <see cref="UpdatedBatch"/>.</summary>
   public static readonly ServerEventKind<UpdatedBatch> AnswerBatchUpdated = Declare("answerBatchUpdated", LotsenJsonContext.Default.UpdatedBatch, ProseView.None);

   /// <summary>The reviewer skipped a stop or took its skip back. The payload is an <see cref="UpdatedSkips"/>. A <c>set_plan</c> that drops a skip publishes <see cref="PlanSet"/> alone.</summary>
   public static readonly ServerEventKind<UpdatedSkips> SkipsUpdated = Declare("skipsUpdated", LotsenJsonContext.Default.UpdatedSkips, ProseView.None);

   /// <summary>The reviewer saw a newer version of a stop than the stop's seen version. The payload is an <see cref="UpdatedSeenVersion"/>.</summary>
   public static readonly ServerEventKind<UpdatedSeenVersion> SeenVersionUpdated = Declare("seenVersionUpdated", LotsenJsonContext.Default.UpdatedSeenVersion, ProseView.None);

   /// <summary>The reviewer changed the tour's familiarity level. The payload is an <see cref="UpdatedFamiliarity"/>. The same write's <see cref="ThreadCreated"/> event carries its tour action.</summary>
   public static readonly ServerEventKind<UpdatedFamiliarity> FamiliarityChanged = Declare("familiarityChanged", LotsenJsonContext.Default.UpdatedFamiliarity, ProseView.None);

   /// <summary>The reviewer approved the plan of a plan-first tour. The payload is an <see cref="UpdatedPlanApproval"/>. The same write's <see cref="ThreadCreated"/> event carries its tour action.</summary>
   public static readonly ServerEventKind<UpdatedPlanApproval> PlanApproved = Declare("planApproved", LotsenJsonContext.Default.UpdatedPlanApproval, ProseView.None);

   /// <summary>
   /// A tour's state changed: the reviewer opened its first stop or its summary page, or archived or unarchived it, or a plan or a stop's content changed whether a stop is pending. The payload is
   /// an <see cref="UpdatedTourState"/>. A write that leaves the state as it was publishes none.
   /// </summary>
   public static readonly ServerEventKind<UpdatedTourState> TourStateChanged = Declare("tourStateChanged", LotsenJsonContext.Default.UpdatedTourState, ProseView.None);

   /// <summary>The bound session wrote its synthesis of the review. The payload is a <see cref="WrittenSynthesis"/>, and the review summary holds the synthesis.</summary>
   public static readonly ServerEventKind<WrittenSynthesis> SynthesisWritten = Declare("synthesisWritten", LotsenJsonContext.Default.WrittenSynthesis, ProseView.None);

   /// <summary>
   /// What the UI's controls can do on a tour changed: a store or a change of the tour's binding opened or closed a control, or moved a waiting thread. The payload is the
   /// <see cref="TourControls"/> of the tour at its revision.
   /// </summary>
   public static readonly ServerEventKind<TourControls> ControlsChanged = Declare("controlsChanged", LotsenJsonContext.Default.TourControls, ProseView.None);

   /// <summary>
   /// The reviewer deleted a tour, whose file is gone for good. The payload is a <see cref="DeletedTour"/>. A page drops the tour and leaves it when it shows it. The delete releases the tour's
   /// binding without a <see cref="TourBindingChanged"/> event.
   /// </summary>
   public static readonly ServerEventKind<DeletedTour> TourDeleted = Declare("tourDeleted", LotsenJsonContext.Default.DeletedTour, ProseView.None, tourScoped: false);

   public static ServerEvent ForControlsChanged(TourControls controls) => ServerEvent.Of(ControlsChanged, controls).At(controls.Revision);

   public static ServerEvent ForTourCreated(TourSummary summary) => ServerEvent.Of(TourCreated, summary);

   public static ServerEvent ForPlanSet(Tour tour) => ServerEvent.Of(PlanSet, new PlanSetEvent(tour.Id, tour.UpdatedAt, tour.Plan!));

   public static ServerEvent ForStopPut(Tour tour, Stop stop) => ServerEvent.Of(StopPut, new StopPutEvent(tour.Id, tour.UpdatedAt, stop));

   public static ServerEvent ForStopRevised(Tour tour, StopWrite write) => ServerEvent.Of(StopRevised, new StopRevisedEvent(tour.Id, tour.UpdatedAt, write.Stop, write.Outdated, write.Threads.Changed));

   public static ServerEvent ForTourBindingChanged(TourId tourId, TourBinding? binding) => ServerEvent.Of(TourBindingChanged, new TourBindingChangedEvent(tourId, binding));

   public static ServerEvent ForThreadCreated(Tour tour, QuestionThread thread) => ServerEvent.Of(ThreadCreated, new ThreadCreatedEvent(tour.Id, tour.UpdatedAt, thread));

   public static ServerEvent ForThreadUpdated(Tour tour, QuestionThread thread) => ServerEvent.Of(ThreadUpdated, new ThreadUpdatedEvent(tour.Id, tour.UpdatedAt, thread));

   public static ServerEvent ForThreadDeleted(DeletedThread deleted) => ServerEvent.Of(ThreadDeleted, deleted);

   /// <summary>Returns the events that the thread write publishes once the store holds <paramref name="stored"/>: one per change that no caller's event carries.</summary>
   public static IEnumerable<ServerEvent> ForThreadWrite(ThreadWrite write, Tour stored) => write.Changes.Where(change => !change.Carried).Select(change => change.Kind switch
   {
      ThreadChangeKind.Created => ForThreadCreated(stored, change.Thread),
      ThreadChangeKind.Updated => ForThreadUpdated(stored, change.Thread),
      _ => ForThreadDeleted(new DeletedThread(stored.Id, stored.UpdatedAt, change.Thread.Id)),
   });

   public static ServerEvent ForAnswerUpdated(UpdatedAnswer updated) => ServerEvent.Of(AnswerUpdated, updated);

   public static ServerEvent ForAnswerCleared(ClearedAnswer cleared) => ServerEvent.Of(AnswerCleared, cleared);

   public static ServerEvent ForAnswerBatchUpdated(UpdatedBatch updated) => ServerEvent.Of(AnswerBatchUpdated, updated);

   public static ServerEvent ForSkipsUpdated(UpdatedSkips updated) => ServerEvent.Of(SkipsUpdated, updated);

   public static ServerEvent ForSeenVersionUpdated(UpdatedSeenVersion updated) => ServerEvent.Of(SeenVersionUpdated, updated);

   public static ServerEvent ForTourStateChanged(UpdatedTourState updated) => ServerEvent.Of(TourStateChanged, updated);

   public static ServerEvent ForTourDeleted(DeletedTour deleted) => ServerEvent.Of(TourDeleted, deleted);

   public static ServerEvent ForSynthesisWritten(WrittenSynthesis written) => ServerEvent.Of(SynthesisWritten, written);

   public static ServerEvent ForFamiliarityChanged(Tour tour) => ServerEvent.Of(FamiliarityChanged, new UpdatedFamiliarity(tour.Id, tour.UpdatedAt, tour.Familiarity));

   public static ServerEvent ForPlanApproved(Tour tour) => ServerEvent.Of(PlanApproved, new UpdatedPlanApproval(tour.Id, tour.UpdatedAt, tour.PlanApproval));

   public static ServerEvent ForAvailableIterationChanged(Tour tour) => ServerEvent.Of(AvailableIterationChanged, new AvailableIterationChangedEvent(tour.Id, tour.UpdatedAt, tour.AvailableIteration));

   public static ServerEvent ForTourPinned(Tour tour) => ServerEvent.Of(TourPinned, new TourPinnedEvent(tour.Id, tour.UpdatedAt, tour.Pin));

   // Every kind but the hello and the deletion of a tour is about a tour that still exists.
   private static ServerEventKind<TPayload> Declare<TPayload>(string name, JsonTypeInfo<TPayload> payload, ProseView prose, bool tourScoped = true)
      where TPayload : class
   {
      var kind = new Kind<TPayload>(name, payload, prose, tourScoped);
      Declared.Add(kind);
      return kind;
   }

   private sealed class Kind<TPayload> : ServerEventKind<TPayload>
      where TPayload : class
   {
      public Kind(string name, JsonTypeInfo<TPayload> payload, ProseView prose, bool tourScoped)
         : base(name, payload, prose, tourScoped)
      {
      }
   }
}

/// <summary>
/// Hands every published event to every subscriber, such as each open event stream.
/// </summary>
/// <remarks>
/// Publishing never waits. Each subscriber has a queue of <see cref="SubscriberCapacity"/> events. The broadcaster disconnects a subscriber whose queue is full, so its stream ends and its client
/// reconnects and reloads, instead of silently missing events.
/// </remarks>
internal sealed class EventBroadcaster
{
   /// <summary>How many events a subscriber may fall behind before it is disconnected.</summary>
   public const int SubscriberCapacity = 256;

   private readonly Lock _lock = new();
   private readonly HashSet<Subscription> _subscribers = [];

   /// <summary>Starts receiving events. Dispose the subscription to stop.</summary>
   public Subscription Subscribe()
   {
      var subscription = new Subscription(this);
      lock (_lock)
      {
         _subscribers.Add(subscription);
      }

      return subscription;
   }

   /// <summary>Queues the event for every subscriber, and disconnects each one whose queue is full.</summary>
   /// <exception cref="InvalidOperationException">The event's kind is tour-scoped, and the event has no revision.</exception>
   public void Publish(ServerEvent serverEvent)
   {
      if (serverEvent.IsTourScoped && serverEvent.Revision is null)
      {
         throw new InvalidOperationException($"The event {serverEvent.Type} is about a tour, so it needs the tour's revision.");
      }

      Subscription[] subscribers;
      lock (_lock)
      {
         subscribers = [.. _subscribers];
      }

      foreach (var subscriber in subscribers)
      {
         if (!subscriber.Writer.TryWrite(serverEvent))
         {
            subscriber.Dispose();
         }
      }
   }

   private void Remove(Subscription subscription)
   {
      lock (_lock)
      {
         _subscribers.Remove(subscription);
      }
   }

   /// <summary>One subscriber's queue of events. Its reader completes when the subscriber is disconnected.</summary>
   public sealed class Subscription : IDisposable
   {
      private readonly EventBroadcaster _owner;
      private readonly Channel<ServerEvent> _channel = Channel.CreateBounded<ServerEvent>(
         new BoundedChannelOptions(SubscriberCapacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

      internal Subscription(EventBroadcaster owner) => _owner = owner;

      public ChannelReader<ServerEvent> Reader => _channel.Reader;

      internal ChannelWriter<ServerEvent> Writer => _channel.Writer;

      public void Dispose()
      {
         _channel.Writer.TryComplete();
         _owner.Remove(this);
      }
   }
}
