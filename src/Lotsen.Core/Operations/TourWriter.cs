using System.Text.Json;
using Lotsen.Hosting;
using Lotsen.Json;
using Lotsen.Platforms;
using Lotsen.Sessions;
using Lotsen.Tours;

namespace Lotsen.Operations;

/// <summary>The point in a write at which <see cref="TourWriter.Hook"/> runs: its git phase has ended, and the write has not taken the lock again.</summary>
/// <param name="Operation">The operation's name, such as <c>put_stop</c>.</param>
/// <param name="TourId">The tour that the write writes.</param>
internal sealed record WritePoint(string Operation, TourId TourId);

/// <summary>The stored tours, as <see cref="TourWriter.List"/> found them.</summary>
/// <param name="Tours">The readable tours, oldest first.</param>
/// <param name="Unreadable">The IDs of the tours that could not be read or break a rule, other than those in <paramref name="OtherSchema"/>.</param>
/// <param name="OtherSchema">The IDs of the tours whose files name another schema version, which this build can never read.</param>
internal sealed record TourListing(IReadOnlyList<Tour> Tours, IReadOnlyList<TourId> Unreadable, IReadOnlyList<TourId> OtherSchema);

/// <summary>A tour as one store read it and as it stored it.</summary>
/// <param name="Before">The latest stored tour that the store's change started from.</param>
/// <param name="After">The tour that the store wrote.</param>
internal sealed record StoredChange(Tour Before, Tour After);

/// <summary>Runs under the writer's lock, with the means to store, bind, publish and send session events.</summary>
internal delegate T ExclusiveBody<T>(TourWriter.Locked locked);

/// <summary>Writes tours: the one holder of the write lock, the owner of the session ledger, the only writer of the tour files, event publishing and session events.</summary>
/// <remarks>
/// <para>
/// One lock serializes each store write with the events it publishes and every change of a binding. So events reach subscribers in the order of the stores, and a binding changes between two
/// writes, never during one. Each write checks the binding inside the store's update, so a write that a take-over or a session's end overtakes stores nothing. A refused write stores nothing and
/// publishes nothing. Every write that stores publishes <see cref="ServerEvents.TourStateChanged"/> when the tour's state or pull request state changed. The store takes no lock of its own, so the
/// writer builds the one store of its data directory.
/// </para>
/// <para>
/// A write of an operation takes the <see cref="Access.Pass"/> that the endpoint issued when it admitted the caller, so the writer checks no role. The pass's type decides whether the write
/// needs the tour's bound session: a <see cref="Access.BoundPass"/> finds it with <see cref="Access.BoundPass.Bind"/> inside the store's update, and its change gets a <see cref="SessionEdit"/>.
/// </para>
/// <para>
/// A binding lasts until another session takes the tour over or its session ends, as <see cref="AgentSessions.Ledger"/> keeps it. A session's end is a fact that the ledger records, and
/// <see cref="SettleEnds"/> settles it under the lock: it publishes the release of each binding that the session still held, and marks the tour's pending requests unanswered. The writer delivers
/// a hold's session events at the end of that hold, before it releases the lock, so each session receives its events in the order of the stores. A full queue ends its session there, which only
/// records the end.
/// </para>
/// <para>
/// Every store goes through <see cref="TourDocument"/>: a write decodes the latest stored tour, and the document checks the next tour against <see cref="TourRules"/> before the store writes
/// it. A change that builds a tour that breaks a rule is refused with <see cref="OperationErrorCode.TourRuleBroken"/>, logged as an error, and stores, publishes and sends nothing. A stored tour
/// that breaks a rule reads as <see cref="OperationErrorCode.TourUnreadable"/>, and the log names the rule.
/// </para>
/// <para>
/// A write with a git phase reads the tour, checks it, and reads git outside the lock. Under the lock it checks the binding, the same checks, the pin that git read at and the gathered basis
/// again on the latest tour, so a change that lands while git runs refuses the write.
/// </para>
/// </remarks>
internal sealed class TourWriter
{
   private readonly Lock _lock = new();
   private readonly FileTourStore _store;
   private readonly EventBroadcaster _events;
   private readonly PullRequestPlatforms _platforms;
   private readonly AgentSessions.Ledger _ledger;
   private readonly TimeProvider _timeProvider;
   private readonly ILogger<TourWriter> _logger;

   // The JSON of the offers that the last controlsChanged event of each tour carried, so a store publishes the event only when they changed.
   private readonly Dictionary<TourId, string> _published = [];

   // The stores of the current hold of the lock, which the hold passes to the store observers once it released the lock.
   private List<StoredChange> _stores = [];
   private Action<StoredChange>? _afterStore;

   /// <exception cref="InvalidOperationException">Another owner claimed the ledger of <paramref name="sessions"/> before.</exception>
   public TourWriter(DataDirectory dataDirectory, EventBroadcaster events, PullRequestPlatforms platforms, AgentSessions sessions, TimeProvider timeProvider, ILogger<TourWriter> logger)
   {
      _store = new FileTourStore(dataDirectory);
      _events = events;
      _platforms = platforms;
      _ledger = sessions.ClaimLedger();
      _timeProvider = timeProvider;
      _logger = logger;
   }

   /// <summary>Registers an observer of every store that changes a tour, which the writer calls with the tour before and after, in the order of the stores, once it released the lock.</summary>
   /// <remarks>The observer runs on the thread of the write that stored, so it must return at once and never throw.</remarks>
   public void ObserveStores(Action<StoredChange> observer) => _afterStore += observer;

   /// <summary>A test hook that each write with a git phase runs at its <see cref="WritePoint"/>, outside the lock, or null for none.</summary>
   internal Func<WritePoint, ValueTask>? Hook { get; set; }

   /// <summary>Returns the stored tour, as <see cref="TourDocument.Decode"/> checks it.</summary>
   /// <returns>The tour, or <see cref="OperationErrorCode.TourNotFound"/> or <see cref="OperationErrorCode.TourUnreadable"/>.</returns>
   public OperationResult<Tour> Read(TourId id)
   {
      var loaded = Load(id);
      return loaded.Value is { } stored ? stored.Tour : loaded.Error!;
   }

   /// <summary>Returns every readable stored tour, oldest first, and the IDs of the tours that could not be read.</summary>
   /// <remarks>An unreadable tour does not throw here, so one damaged file cannot hide the other tours. A tour that breaks a rule is unreadable, and the log names the rule.</remarks>
   /// <exception cref="IOException">The tours directory could not be listed.</exception>
   /// <exception cref="UnauthorizedAccessException">The tours directory could not be listed.</exception>
   public TourListing List()
   {
      var tours = new List<Tour>();
      var unreadable = new List<TourId>();
      var otherSchema = new List<TourId>();
      foreach (var read in _store.ReadAll())
      {
         switch (Loaded(read))
         {
            case { Value: { } stored }:
               tours.Add(stored.Tour);
               break;
            case { Error: { } error } when error.Code == OperationErrorCode.TourUnreadable && read is TourRead.OtherSchema:
               otherSchema.Add(read.Id);
               break;
            case { Error: { } error } when error.Code == OperationErrorCode.TourUnreadable:
               unreadable.Add(read.Id);
               break;
         }
      }

      return new TourListing(
         [.. tours.OrderBy(tour => tour.CreatedAt).ThenBy(tour => tour.Id.Value, StringComparer.Ordinal)],
         [.. unreadable.OrderBy(id => id.Value, StringComparer.Ordinal)],
         [.. otherSchema.OrderBy(id => id.Value, StringComparer.Ordinal)]);
   }

   /// <summary>Returns the review marks of the tour.</summary>
   /// <returns>The marks, or <see cref="OperationErrorCode.TourNotFound"/> or <see cref="OperationErrorCode.TourUnreadable"/>.</returns>
   public OperationResult<IReadOnlyList<ReviewMark>> ReadMarks(TourId id)
   {
      var loaded = Load(id);
      return loaded.Value is { } stored ? OperationResult<IReadOnlyList<ReviewMark>>.Of(stored.ReviewMarks) : loaded.Error!;
   }

   /// <summary>Applies a change to the review marks of the tour and stores the result, without changing the tour or its <see cref="Tour.UpdatedAt"/>.</summary>
   /// <remarks>It runs under the lock, one at a time with every other write, and it publishes nothing, because no output that holds the tour shows the marks.</remarks>
   /// <param name="pass">The pass of the operation that writes, whose name a refusal of broken marks names.</param>
   /// <param name="id">The tour whose marks to change.</param>
   /// <param name="change">Returns the changed marks from the latest stored ones. When it returns the list it was given, nothing is written.</param>
   /// <returns>The stored marks, or the refusal. Nothing was changed then.</returns>
   public OperationResult<IReadOnlyList<ReviewMark>> WriteMarks(Access.UnboundPass pass, TourId id, Func<IReadOnlyList<ReviewMark>, IReadOnlyList<ReviewMark>> change)
   {
      IReadOnlyList<ReviewMark>? stored = null;
      try
      {
         lock (_lock)
         {
            return Finished(id, _store.Update(id, document =>
            {
               var loaded = Decoded(id, document);
               var changed = change(loaded.ReviewMarks);
               if (ReferenceEquals(changed, loaded.ReviewMarks))
               {
                  stored = changed;
                  return document;
               }

               var next = Unbroken(pass.Operation, id, loaded.WithMarks(changed));
               stored = next.ReviewMarks;
               return next.Document;
            })) is { } error ? error : OperationResult<IReadOnlyList<ReviewMark>>.Of(stored!);
         }
      }
      catch (RejectedException rejected)
      {
         return rejected.Error;
      }
   }

   /// <summary>Returns the tour's controls for the UI, at the tour's latest stored revision and with its binding then, or null when the tour cannot be read.</summary>
   public TourControls? Controls(TourId id)
   {
      lock (_lock)
      {
         return Read(id).Value is { } tour ? TourControls.Of(tour, _ledger.Holder(id)) : null;
      }
   }

   /// <inheritdoc cref="ITourStore.PathOf"/>
   public string PathOf(TourId id) => _store.PathOf(id);

   /// <summary>Writes the tour in one hold of the lock: the change on the latest tour.</summary>
   /// <param name="pass">The pass of the operation that writes, whose name a refusal of a broken rule names.</param>
   /// <param name="id">The tour to write.</param>
   /// <param name="change">Builds the next tour, or returns the refusal.</param>
   /// <param name="invalidInput">The refusal of an input that is invalid whatever the tour holds, or null. It comes before the tour lookup, and for a <see cref="Access.BoundPass"/> before the binding check.</param>
   /// <returns>What the change's <see cref="TourEdit.Store{T}(Tour, Func{Stored, T})"/> returned, or the refusal.</returns>
   public OperationResult<T> Write<T>(Access.UnboundPass pass, TourId id, Func<TourEdit, Edited<T>> change, OperationError? invalidInput = null)
      where T : class => Written(pass, null, id, change, invalidInput);

   /// <summary>Writes the tour as <see cref="Write{T}(Access.UnboundPass, TourId, Func{TourEdit, Edited{T}}, OperationError)"/> does, for the session that <see cref="Access.BoundPass.Bind"/> finds.</summary>
   /// <remarks>The binding check runs inside the store's update, so a write that a take-over or a session's end overtakes stores nothing.</remarks>
   public OperationResult<T> Write<T>(Access.BoundPass pass, TourId id, Func<SessionEdit, Edited<T>> change, OperationError? invalidInput = null)
      where T : class => Written(pass, pass, id, edit => change((SessionEdit)edit), invalidInput);

   /// <summary>Writes the tour after a git phase: <paramref name="check"/> on the read tour, the git phase outside the lock, then the checks and the change on the latest tour.</summary>
   /// <param name="pass">The pass of the operation that writes, whose name the hook's point carries.</param>
   /// <param name="id">The tour to write.</param>
   /// <param name="check">Returns the refusal of the tour, or null. It runs on the read tour and again on the latest one.</param>
   /// <param name="gather">Reads git for the read tour, through its <see cref="GitPhase"/>, or returns the refusal.</param>
   /// <param name="change">Builds the next tour from the latest one and what the phase gathered, or returns the refusal.</param>
   /// <param name="cancellationToken">Stops the git phase, but not a download that the phase started, as <see cref="GitPhase.DownloadAsync"/> says.</param>
   public Task<OperationResult<T>> WriteAfterGitAsync<TGathered, T>(
      Access.UnboundPass pass,
      TourId id,
      Func<Tour, OperationError?> check,
      Func<Tour, GitPhase, CancellationToken, Task<OperationResult<Gathered<TGathered>>>> gather,
      Func<TourEdit, TGathered, Edited<T>> change,
      CancellationToken cancellationToken)
      where T : class => WrittenAfterGitAsync(pass, null, id, check, gather, change, cancellationToken);

   /// <summary>
   /// Writes the tour after a git phase, as <see cref="WriteAfterGitAsync{TGathered, T}(Access.UnboundPass, TourId, Func{Tour, OperationError}, Func{Tour, GitPhase, CancellationToken, Task{OperationResult{Gathered{TGathered}}}}, Func{TourEdit, TGathered, Edited{T}}, CancellationToken)"/>
   /// does, for the session that <see cref="Access.BoundPass.Bind"/> finds.
   /// </summary>
   /// <remarks>The binding check runs on the read tour before the git phase, and again inside the store's update.</remarks>
   public Task<OperationResult<T>> WriteAfterGitAsync<TGathered, T>(
      Access.BoundPass pass,
      TourId id,
      Func<Tour, OperationError?> check,
      Func<Tour, GitPhase, CancellationToken, Task<OperationResult<Gathered<TGathered>>>> gather,
      Func<SessionEdit, TGathered, Edited<T>> change,
      CancellationToken cancellationToken)
      where T : class => WrittenAfterGitAsync(pass, pass, id, check, gather, (edit, gathered) => change((SessionEdit)edit, gathered), cancellationToken);

   /// <summary>Runs the body under the lock, for a lifecycle that does not fit one store update, such as a create, an attach or a pin job.</summary>
   /// <remarks>The writer delivers the body's session events once the body returns, before it releases the lock, as it does a write's.</remarks>
   public T Exclusive<T>(ExclusiveBody<T> body) => Exclusive((Access.Pass?)null, body);

   /// <inheritdoc cref="Exclusive{T}(ExclusiveBody{T})"/>
   public void Exclusive(Action<Locked> body) => Exclusive<bool>((Access.Pass?)null, locked =>
   {
      body(locked);
      return true;
   });

   /// <summary>Runs the body of an operation under the lock, as <see cref="Exclusive{T}(ExclusiveBody{T})"/> does, with the pass's operation name for a refusal of a broken rule.</summary>
   /// <remarks>It takes no <see cref="Access.BoundPass"/>, because <see cref="Locked.Write"/> stores with no binding check.</remarks>
   public T Exclusive<T>(Access.UnboundPass pass, ExclusiveBody<T> body) => Exclusive((Access.Pass)pass, body);

   private T Exclusive<T>(Access.Pass? pass, ExclusiveBody<T> body)
   {
      T value;
      List<StoredChange> stores;
      lock (_lock)
      {
         var sends = new List<SessionSend>();
         var locked = new Locked(this, sends, pass?.Operation ?? "a write of the server's own", pass);
         try
         {
            value = body(locked);
         }
         finally
         {
            locked.Close();
            stores = TakeStores();
         }

         Deliver(sends);
      }

      Notify(stores);
      return value;
   }

   /// <summary>Settles every session end that the ledger recorded, each in one hold of the lock, and returns how many it settled.</summary>
   /// <remarks>
   /// For each tour that the ended session still held, the hold publishes the release of the binding and marks the tour's pending requests unanswered. An end that the ledger's <c>TakeEnd</c> returns
   /// is settled in the same hold, so every end recorded before the call is settled when it returns, whether this call or <see cref="RunEndSweepAsync"/> settled it.
   /// </remarks>
   public int SettleEnds()
   {
      var settled = 0;
      while (SettleNextEnd())
      {
         settled++;
      }

      return settled;
   }

   /// <summary>Settles session ends as the ledger records them, until the token is canceled or the registry is disposed.</summary>
   /// <remarks>An end that the server's stop leaves unsettled leaves its requests pending in the file, and <see cref="MarkPendingRequestsUnanswered"/> marks them on the next start.</remarks>
   public async Task RunEndSweepAsync(CancellationToken stopping)
   {
      try
      {
         while (await _ledger.WaitForEndAsync(stopping))
         {
            try
            {
               SettleEnds();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
               _logger.LogError(exception, "A session's end could not be settled.");
            }
         }
      }
      catch (OperationCanceledException) when (stopping.IsCancellationRequested)
      {
      }
   }

   /// <summary>Marks every pending request of every stored tour unanswered, because no session is bound when the server starts, so none of them can be resolved.</summary>
   /// <remarks>Call it once when the server starts, before any client can reach it.</remarks>
   public void MarkPendingRequestsUnanswered()
   {
      TourListing listing;
      try
      {
         listing = List();
      }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
      {
         // A failed listing must not stop the server from starting.
         _logger.LogWarning("The tours could not be listed, so their pending requests stay pending: {Reason}", exception.Message);
         return;
      }

      List<StoredChange> stores;
      lock (_lock)
      {
         foreach (var tour in listing.Tours.Where(tour => TourThreads.Settle(tour, new ThreadFact.Released()).Changed.Count > 0))
         {
            MarkPendingUnanswered(tour.Id);
         }

         stores = TakeStores();
      }

      Notify(stores);
   }

   // Settles the next session end that the ledger recorded, in one hold of the lock, and returns whether the ledger held one.
   private bool SettleNextEnd()
   {
      List<StoredChange> stores;
      lock (_lock)
      {
         if (_ledger.TakeEnd() is not { } end)
         {
            return false;
         }

         foreach (var id in end.Released)
         {
            PublishBinding(id, null);
            MarkPendingUnanswered(id);
         }

         stores = TakeStores();
      }

      Notify(stores);
      return true;
   }

   // Call it under the lock, at the end of a hold. Returns the hold's stores and starts an empty list for the next hold.
   private List<StoredChange> TakeStores()
   {
      var stores = _stores;
      _stores = [];
      return stores;
   }

   // Call it outside the lock, after the hold that made the stores.
   private void Notify(List<StoredChange> stores)
   {
      foreach (var change in stores)
      {
         _afterStore?.Invoke(change);
      }
   }

   private OperationResult<T> Written<T>(Access.Pass pass, Access.BoundPass? binding, TourId id, Func<TourEdit, Edited<T>> change, OperationError? invalidInput)
      where T : class
   {
      if (invalidInput is not null)
      {
         return invalidInput;
      }

      OperationResult<T> result;
      List<StoredChange> stores;
      lock (_lock)
      {
         var sends = new List<SessionSend>();
         result = Commit(sends, pass, pass.Operation, id, binding, recheck: null, change);
         stores = TakeStores();
         Deliver(sends);
      }

      Notify(stores);
      return result;
   }

   private async Task<OperationResult<T>> WrittenAfterGitAsync<TGathered, T>(
      Access.Pass pass,
      Access.BoundPass? binding,
      TourId id,
      Func<Tour, OperationError?> check,
      Func<Tour, GitPhase, CancellationToken, Task<OperationResult<Gathered<TGathered>>>> gather,
      Func<TourEdit, TGathered, Edited<T>> change,
      CancellationToken cancellationToken)
      where T : class
   {
      var read = Read(id);
      if (read.Value is not { } tour)
      {
         return read.Error!;
      }

      if (binding is not null)
      {
         lock (_lock)
         {
            if (binding.Bind(tour.Id, _ledger.Holder(tour.Id)).Error is { } unbound)
            {
               return unbound;
            }
         }
      }

      if (check(tour) is { } invalid)
      {
         return invalid;
      }

      var git = new GitPhase(tour, cancellationToken);
      var gathering = await gather(tour, git, cancellationToken);
      if (gathering.Value is not { } gathered)
      {
         return gathering.Error!;
      }

      using (gathered.Held)
      {
         if (Hook is { } hook)
         {
            await hook(new WritePoint(pass.Operation, id));
         }

         OperationResult<T> result;
         List<StoredChange> stores;
         lock (_lock)
         {
            var sends = new List<SessionSend>();
            result = Commit(sends, pass, pass.Operation, id, binding, latest => check(latest) ?? git.Recheck(latest) ?? gathered.Recheck(latest), edit => change(edit, gathered.Value));
            stores = TakeStores();
            Deliver(sends);
         }

         Notify(stores);
         return result;
      }
   }

   // Call it under the lock. Runs the binding check, the recheck and the change inside the store's update, then the change's continuation on what was stored. The pass, when the write has
   // one, takes the stored tour's revision for the operation's output.
   private OperationResult<T> Commit<T>(
      List<SessionSend> sends, Access.Pass? pass, string operation, TourId id, Access.BoundPass? binding, Func<Tour, OperationError?>? recheck, Func<TourEdit, Edited<T>> change)
      where T : class
   {
      Func<Stored, T>? then = null;
      var written = Change(operation, id, (latest, now) =>
      {
         Recipient? recipient = null;
         if (binding is not null)
         {
            var bound = binding.Bind(latest.Id, _ledger.Holder(latest.Id));
            recipient = bound.Value ?? throw new RejectedException(bound.Error!);
         }

         if (recheck?.Invoke(latest) is { } refused)
         {
            throw new RejectedException(refused);
         }

         var edited = change(recipient is null ? new TourEdit(latest, now) : new SessionEdit(latest, now, recipient));
         if (edited.Refusal is { } refusal)
         {
            throw new RejectedException(refusal);
         }

         then = edited.Then;
         return edited.Next!;
      });
      if (written.Value is not { } stored1)
      {
         return written.Error!;
      }

      var stored = new Stored(stored1.Before, stored1.After, _platforms.MentionsOf(stored1.After.Target), _events, sends);
      T value;
      try
      {
         value = then!(stored);
      }
      finally
      {
         stored.Close();
      }

      PublishStateChange(stored1.Before, stored1.After);
      PublishControls(stored1.After);
      pass?.Showed(stored1.After.Revision);
      return value;
   }

   // Call it under the lock. Stores the tour that the change builds from the latest stored one, through the document's check on the way out, and returns the tours before and after. A change
   // that returns the tour it was given stores nothing. A change that throws a RejectedException stores nothing and returns its error.
   private OperationResult<TourChange> Change(string operation, TourId id, Func<Tour, DateTimeOffset, Tour> change)
   {
      TourChange? changed = null;
      try
      {
         return Finished(id, _store.Update(id, document =>
         {
            var loaded = Decoded(id, document);
            var now = _timeProvider.GetUtcNow();
            var next = change(loaded.Tour, now);
            if (ReferenceEquals(next, loaded.Tour))
            {
               changed = new TourChange(loaded.Tour, loaded.Tour);
               return document;
            }

            var stored = Unbroken(operation, id, loaded.Next(next, now));
            changed = new TourChange(loaded.Tour, stored.Tour);
            return stored.Document;
         })) is { } error ? error : Stored(changed!);
      }
      catch (RejectedException rejected)
      {
         return rejected.Error;
      }
   }

   // Records a store that changed the tour for the hold's observers, and returns it.
   private TourChange Stored(TourChange change)
   {
      if (!ReferenceEquals(change.Before, change.After))
      {
         _stores.Add(new StoredChange(change.Before, change.After));
      }

      return change;
   }

   // Returns the stored tour of a document that the store read, or why it cannot be used. A fault is logged with its path, and the tour counts as unreadable.
   private OperationResult<TourDocument.StoredTour> Loaded(TourRead read) => Finished(read.Id, read) is { } error ? error : Decoded(read.Id, ((TourRead.Found)read).Document, rejects: false);

   private OperationResult<TourDocument.StoredTour> Load(TourId id) => Loaded(_store.Read(id));

   // Returns the error of a read that found no document to use, or null for a found one.
   private OperationError? Finished(TourId id, TourRead read) => read switch
   {
      TourRead.Found => null,
      TourRead.Missing => OperationError.TourNotFound(id.Value),
      TourRead.OtherSchema => Unreadable(id, "its file has the layout of another schema version"),
      TourRead.Unreadable unreadable => Unreadable(id, unreadable.Cause.Message),
   };

   private OperationError Unreadable(TourId id, string reason)
   {
      _logger.LogWarning("Tour {TourId} could not be read: {Reason}", id.Value, reason);
      return OperationError.TourUnreadable(id);
   }

   // Returns the document's stored tour, or throws a RejectedException that holds the TourUnreadable refusal, so a write stores nothing.
   private TourDocument.StoredTour Decoded(TourId id, TourDocument document) => Decoded(id, document, rejects: true).Value!;

   private OperationResult<TourDocument.StoredTour> Decoded(TourId id, TourDocument document, bool rejects)
   {
      var decoded = document.Decode(id);
      if (decoded.Value is { } stored)
      {
         return stored;
      }

      _logger.LogWarning("Tour {TourId} could not be read, because it breaks a rule at {Path}: {Problem}", id.Value, decoded.Fault!.Path.ToString(), decoded.Fault.Problem);
      var error = OperationError.TourUnreadable(id);
      return rejects ? throw new RejectedException(error) : error;
   }

   // Returns the checked tour, or throws a RejectedException that holds the TourRuleBroken refusal: the write built a tour that breaks a rule, which is a fault of the server.
   private TourDocument.StoredTour Unbroken(string operation, TourId id, Checked<TourDocument.StoredTour> next)
   {
      if (next.Value is { } stored)
      {
         return stored;
      }

      _logger.LogError("{Operation} built tour {TourId} breaking a rule at {Path}: {Problem}", operation, id.Value, next.Fault!.Path.ToString(), next.Fault.Problem);
      throw new RejectedException(OperationError.TourRuleBroken(id, operation, next.Fault));
   }

   // Call it under the lock. Publishes threadUpdated for each thread it changes, and returns the stored tour, or null when no request was pending or the tour could not be stored.
   private Tour? MarkPendingUnanswered(TourId id)
   {
      ThreadWrite? released = null;
      OperationResult<TourChange> stored;
      try
      {
         stored = Change("the end of a binding", id, (tour, _) =>
         {
            released = TourThreads.Settle(tour, new ThreadFact.Released());
            return released.Next;
         });
      }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
      {
         _logger.LogWarning("The pending requests of tour {TourId} could not be marked unanswered, so they stay pending: {Reason}", id.Value, exception.Message);
         return null;
      }

      if (stored.Value is not { After: var tour } || released is not { Changed.Count: > 0 })
      {
         if (stored.Error is { } error && error.Code != OperationErrorCode.TourNotFound)
         {
            _logger.LogWarning("The pending requests of tour {TourId} could not be marked unanswered, so they stay pending: {Reason}", id.Value, error.Reason);
         }

         return null;
      }

      foreach (var serverEvent in ServerEvents.ForThreadWrite(released, tour, _platforms.MentionsOf(tour.Target)))
      {
         _events.Publish(serverEvent.At(tour.Revision));
      }

      PublishControls(tour);
      return tour;
   }

   // Call it under the lock, after a change of the tour's binding. Stores the tour's next revision, then publishes the binding and the controls at it. A tour that cannot be stored gets the
   // binding event at revision 0, which no page takes over a revision that it holds.
   private void PublishBinding(TourId id, TourBinding? binding)
   {
      Tour? bumped = null;
      try
      {
         if (Finished(id, _store.Update(id, document =>
         {
            var stored = Decoded(id, document).Bumped();
            bumped = stored.Tour;
            return stored.Document;
         })) is { } error && error.Code != OperationErrorCode.TourNotFound)
         {
            _logger.LogWarning("The binding change of tour {TourId} could not raise its revision: {Reason}", id.Value, error.Reason);
         }
      }
      catch (RejectedException rejected)
      {
         _logger.LogWarning("The binding change of tour {TourId} could not raise its revision: {Reason}", id.Value, rejected.Error.Reason);
      }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
      {
         _logger.LogWarning("The binding change of tour {TourId} could not raise its revision: {Reason}", id.Value, exception.Message);
      }

      _events.Publish(ServerEvents.ForTourBindingChanged(id, binding).At(bumped?.Revision ?? 0));
      if (bumped is not null)
      {
         PublishControls(bumped);
      }
   }

   // Call it under the lock, after a store or a binding change. Publishes the tour's controls when their offers differ from those of the last controlsChanged event of the tour.
   private void PublishControls(Tour stored)
   {
      var controls = TourControls.Of(stored, _ledger.Holder(stored.Id));
      var offers = JsonSerializer.Serialize(controls.Offers, LotsenJsonContext.Default.TourOffers);
      if (_published.TryGetValue(stored.Id, out var last) && last == offers)
      {
         return;
      }

      _published[stored.Id] = offers;
      _events.Publish(ServerEvents.ForControlsChanged(controls));
   }

   // Call it under the lock, after a write that can change what TourStates.Of reads or the tour's pull request state.
   private void PublishStateChange(Tour before, Tour stored)
   {
      var state = TourStates.Of(stored);
      if (state != TourStates.Of(before) || stored.PullRequestState != before.PullRequestState)
      {
         _events.Publish(ServerEvents.ForTourStateChanged(new UpdatedTourState(stored.Id, stored.UpdatedAt, state, stored.PullRequestState)).At(stored.Revision));
      }
   }

   // Call it under the lock, at the end of a hold. Delivers the hold's session events in the order sent, and logs an event that a session which has ended did not get.
   private void Deliver(List<SessionSend> sends)
   {
      foreach (var send in sends)
      {
         var queued = _ledger.Deliver(send);
         switch (send)
         {
            case SessionSend.Question question when !queued:
               _logger.LogWarning("Question {QuestionId} of tour {TourId} did not reach {Session}, which has ended.", question.Event.QuestionId, question.Event.TourId.Value, send.To);
               break;
            case SessionSend.Answers answers when !queued:
               _logger.LogWarning("Answer batch {BatchId} of tour {TourId} did not reach {Session}, which has ended.", answers.Event.BatchId, answers.Event.TourId.Value, send.To);
               break;
            case SessionSend.Probe probe when queued:
               _logger.LogInformation("Sent {Session} a probe on attaching tour {TourId}.", send.To, probe.TourId.Value);
               break;
         }
      }
   }

   /// <summary>The means of an <see cref="Exclusive{T}(ExclusiveBody{T})"/> body, which it may use only while the body runs.</summary>
   /// <remarks>It forwards the ledger's methods, so the ledger never leaves the writer, and every member throws once the body has returned.</remarks>
   internal sealed class Locked
   {
      private readonly TourWriter _writer;
      private readonly List<SessionSend> _sends;
      private readonly string _operation;
      private readonly Access.Pass? _pass;
      private bool _closed;

      internal Locked(TourWriter writer, List<SessionSend> sends, string operation, Access.Pass? pass)
      {
         _writer = writer;
         _sends = sends;
         _operation = operation;
         _pass = pass;
      }

      /// <inheritdoc cref="TourWriter.Read"/>
      public OperationResult<Tour> Read(TourId id) => _writer.Read(ThrowIfClosed(id));

      /// <summary>Stores a new tour with a new ID, no plan and its pin in progress, as <see cref="TourDocument.New"/> builds it, and returns it.</summary>
      public Tour Create(TourTarget target, Familiarity familiarity, PlanApproval planApproval)
      {
         ThrowIfClosed(target);
         var now = _writer._timeProvider.GetUtcNow();
         TourDocument.StoredTour? created = null;
         _writer._store.Create(id =>
         {
            created = _writer.Unbroken(_operation, id, TourDocument.New(id, target, familiarity, planApproval, now));
            return created.Document;
         });
         return created!.Tour;
      }

      /// <summary>Removes the tour for good and forgets its binding without an event, and returns the tour as it was stored.</summary>
      public OperationResult<Tour> Delete(TourId id)
      {
         var loaded = _writer.Load(ThrowIfClosed(id));
         if (loaded.Value is not { } stored)
         {
            return loaded.Error!;
         }

         if (_writer.Finished(id, _writer._store.Delete(id)) is { } gone)
         {
            return gone;
         }

         _writer._ledger.Forget(id);
         _writer._published.Remove(id);
         return stored.Tour;
      }

      /// <summary>Writes the tour as <see cref="TourWriter.Write{T}(Access.UnboundPass, TourId, Func{TourEdit, Edited{T}}, OperationError)"/> does.</summary>
      public OperationResult<T> Write<T>(TourId id, Func<TourEdit, Edited<T>> change)
         where T : class => _writer.Commit(_sends, _pass, _operation, ThrowIfClosed(id), binding: null, recheck: null, change);

      /// <summary>Publishes the event now, under the lock. A tour-scoped event must carry its tour's revision already.</summary>
      public void Publish(ServerEvent serverEvent) => _writer._events.Publish(ThrowIfClosed(serverEvent));

      /// <summary>Raises the tour's revision for a change of its binding, then publishes the binding and the tour's controls at the new revision.</summary>
      /// <param name="id">The tour whose binding changed.</param>
      /// <param name="binding">The tour's binding now, or null when the change released it.</param>
      public void PublishBinding(TourId id, TourBinding? binding) => _writer.PublishBinding(ThrowIfClosed(id), binding);

      /// <summary>Queues the session event, which the writer delivers once the body returns, before it releases the lock, in the order sent.</summary>
      public void Send(SessionSend send) => _sends.Add(ThrowIfClosed(send));

      /// <inheritdoc cref="AgentSessions.Ledger.Holder"/>
      public Recipient? Holder(TourId id) => _writer._ledger.Holder(ThrowIfClosed(id));

      /// <inheritdoc cref="AgentSessions.Ledger.Snapshot"/>
      public IReadOnlyDictionary<TourId, TourBinding> Snapshot() => ThrowIfClosed(_writer._ledger).Snapshot();

      /// <inheritdoc cref="AgentSessions.Ledger.Attach"/>
      public AttachOutcome Attach(TourId id, Recipient caller, bool takeOver) => _writer._ledger.Attach(ThrowIfClosed(id), caller, takeOver);

      /// <inheritdoc cref="AgentSessions.Ledger.BindNew"/>
      public TourBinding? BindNew(TourId id, Recipient creator) => _writer._ledger.BindNew(ThrowIfClosed(id), creator);

      /// <inheritdoc cref="AgentSessions.Ledger.Confirm"/>
      public AckOutcome Confirm(Recipient caller, string nonce) => _writer._ledger.Confirm(ThrowIfClosed(caller), nonce);

      /// <summary>Marks the tour's pending requests unanswered, and returns the stored tour, or null when no request was pending or the tour could not be stored.</summary>
      public Tour? MarkPendingUnanswered(TourId id) => _writer.MarkPendingUnanswered(ThrowIfClosed(id));

      internal void Close() => _closed = true;

      private TValue ThrowIfClosed<TValue>(TValue value)
      {
         ObjectDisposedException.ThrowIf(_closed, this);
         return value;
      }
   }

   // The tour that a write read and the tour that it stored, which is the same tour when the write stored nothing.
   private sealed record TourChange(Tour Before, Tour After);

   // Thrown inside the store's update to refuse the change, so the stored tour stays unchanged.
   private sealed class RejectedException : Exception
   {
      public RejectedException(OperationError error)
         : base(error.Reason)
      {
         Error = error;
      }

      public OperationError Error { get; }
   }
}
