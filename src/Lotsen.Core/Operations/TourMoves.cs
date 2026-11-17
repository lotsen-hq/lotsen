using System.ComponentModel;
using System.Text.Json.Serialization;
using Lotsen.Git;
using Lotsen.Json;
using Lotsen.Pinning;
using Lotsen.Platforms;
using Lotsen.Tours;

namespace Lotsen.Operations;

/// <summary>The input of <c>check_move</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CheckMoveInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId);

/// <summary>Whether a repository tour has a move to the latest commit of its ref, or why it has none.</summary>
[JsonConverter(typeof(StrictEnumConverter<MoveAvailability>))]
internal enum MoveAvailability
{
   /// <summary>The ref is a branch whose latest commit is another one than the tour's.</summary>
   Available,

   /// <summary>The ref is a branch whose latest commit is the tour's.</summary>
   AtLatest,

   /// <summary>The ref is a tag, which names one commit for good.</summary>
   Tag,

   /// <summary>The ref is a commit ID.</summary>
   Commit,
}

/// <summary>One dive below the checked tour, as <c>check_move</c> lists it for the reviewer to tick.</summary>
/// <param name="TourId">The dive's ID.</param>
/// <param name="ParentId">The ID of the tour that the dive started from, the checked tour or a dive below it.</param>
/// <param name="Title">The dive's title.</param>
/// <param name="State">The dive's state, as <see cref="TourStates.Of"/> derives it.</param>
/// <param name="Pin">The dive's pin, a snapshot of the commit that it shows.</param>
internal sealed record MoveDive(TourId TourId, TourId ParentId, string Title, TourState State, TourPin Pin);

/// <summary>The output of <c>check_move</c>.</summary>
/// <param name="TourId">The tour.</param>
/// <param name="Move">Whether the tour has a move, or why it has none.</param>
/// <param name="Current">The commit that the tour shows.</param>
/// <param name="Latest">The commit that the tour's ref names now.</param>
/// <param name="Dives">The dives below the tour at another commit than the latest, each tour before its dives, while the tour has a move, or null when it has none. JSON leaves them out when null.</param>
internal sealed record CheckedMove(
   TourId TourId,
   MoveAvailability Move,
   CommitId Current,
   CommitId Latest,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<MoveDive>? Dives = null);

/// <summary>The input of <c>move_tour</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record MoveTourInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId,
   [property: Description("The latest commit of the tour's ref, as check_move returned it. The move is refused when the ref names another commit now.")]
   CommitId Commit,
   [property: Description("The tourIds of the dives that move with the tour, from check_move's dives, or an empty list for none. A dive moves only with the tour that it dives from.")]
   IReadOnlyList<string> DiveIds);

/// <summary>One tour that a move re-pinned, with its stale stops and the decided thread that reports the move.</summary>
/// <param name="TourId">The tour.</param>
/// <param name="UpdatedAt">The tour's update time after the move.</param>
/// <param name="Pin">The tour's pin after the move, a snapshot of the latest commit.</param>
/// <param name="StaleStops">The IDs of the stops with a stale marker after the move, in plan order.</param>
/// <param name="Thread">
/// The new decided thread on the tour, which holds the <see cref="ReviewerDecision.TourMoved"/> decision, or null when no stop is stale, so no decision went. JSON leaves it out when null.
/// </param>
internal sealed record MovedTour(
   TourId TourId,
   DateTimeOffset UpdatedAt,
   TourPin Pin,
   IReadOnlyList<string> StaleStops,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] QuestionThread? Thread = null);

/// <summary>The output of <c>move_tour</c>: the moved tour, with each dive that moved with it.</summary>
/// <param name="TourId">The tour.</param>
/// <param name="UpdatedAt">The tour's update time after the move.</param>
/// <param name="Pin">The tour's pin after the move, a snapshot of the latest commit.</param>
/// <param name="StaleStops">The IDs of the tour's stops with a stale marker after the move, in plan order.</param>
/// <param name="Thread">The tour's new decided thread, as <see cref="MovedTour.Thread"/> says. JSON leaves it out when null.</param>
/// <param name="Dives">The dives that moved, each tour before its dives, or null when none moved. JSON leaves them out when null.</param>
internal sealed record MovedSubtree(
   TourId TourId,
   DateTimeOffset UpdatedAt,
   TourPin Pin,
   IReadOnlyList<string> StaleStops,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] QuestionThread? Thread = null,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<MovedTour>? Dives = null);

/// <summary>
/// <c>check_move</c> and <c>move_tour</c>: the reviewer's check of whether a repository tour has a move to the latest commit of its ref, and the move of the tour with the dives that the reviewer
/// ticks.
/// </summary>
/// <remarks>
/// <para>
/// Only a branch moves. A tag and a commit ID name one commit for good, and the target does not store which kind its ref is, so each check and each move resolves the ref again, as the first pin
/// does. The move asks the platform again too, and goes only to the commit that the check showed.
/// </para>
/// <para>
/// The ref resolves and the git reads run outside the writer's lock. One hold of the lock reads the subtree, remaps and checks every moved tour, and only then stores each of them from the remap
/// that it checked, so a refusal comes before any store and no store reads a fact again. A hold whose tours need facts that the scope lacks stores nothing, and the move reads them and tries
/// again, as <see cref="IterationRetry"/> bounds it.
/// </para>
/// <para>
/// Each tour is its own file, so a store that fails on the disk after others succeeded leaves them moved, as an archive of a subtree does, and the move ends with a refusal that names the tours
/// that moved and those that did not. The tour goes first and each dive after its parent, so every moved dive then has its parent moved. The hold ends normally, so the writer still sends the
/// moved tours' decisions to their sessions. The tour and its dives at the latest commit count as moved for a later check and move, which offer the dives left behind.
/// </para>
/// </remarks>
internal sealed class TourMoves
{
   // What a moved tour's bound session receives, as a refusal for a tour without one names it.
   private const string Sent = "the move to the latest commit";

   private static readonly FieldPath DiveIdsField = "diveIds";

   private readonly TourWriter _writer;
   private readonly PullRequestPlatforms _platforms;
   private readonly PlatformFreeRepositories _platformFree;
   private readonly IGitFacts _facts;
   private readonly ILogger<TourMoves> _logger;

   public TourMoves(TourWriter writer, PullRequestPlatforms platforms, PlatformFreeRepositories platformFree, IGitFacts facts, ILogger<TourMoves> logger)
   {
      _writer = writer;
      _platforms = platforms;
      _platformFree = platformFree;
      _facts = facts;
      _logger = logger;
   }

   /// <summary>
   /// A test hook that a hold of a move runs under the lock before each of its stores, once it has checked every moved tour, with the tour to store, or null for none. A disk failure that it throws
   /// counts as one of that store.
   /// </summary>
   internal Action<TourId>? BeforeStore { get; set; }

   /// <summary>Resolves the tour's ref and returns whether the tour has a move to its latest commit, with the dives below the tour when it has one.</summary>
   /// <remarks>
   /// A tour at the latest commit has a move while a dive below it is at another commit, as after a move that stored the tour and failed on a dive. It changes no tour. A pull request's tour, and
   /// a tour that is not pinned to a snapshot, are refused, and so is a ref that the platform or git cannot resolve.
   /// </remarks>
   public async Task<OperationResult<CheckedMove>> CheckMoveAsync(CheckMoveInput input, CancellationToken cancellationToken)
   {
      var read = _writer.Read(input.TourId);
      if (read.Value is not { } tour)
      {
         return read.Error!;
      }

      if (CheckMovable(tour) is { } notMovable)
      {
         return notMovable;
      }

      var resolved = await ResolveAsync(tour, cancellationToken);
      if (resolved.Value is not { } latest)
      {
         return resolved.Error!;
      }

      var subtree = _writer.Exclusive(locked => locked.Subtree(input.TourId));
      if (subtree.Value is not { } tours)
      {
         return subtree.Error!;
      }

      if (CheckMovable(tours[0]) is { } changed)
      {
         return changed;
      }

      var current = ((TourPin.Snapshot)tours[0].Pin).Head;
      var behind = tours.Skip(1).Where(dive => dive.Pin is TourPin.Snapshot { Head: var head } && head != latest.Commit).ToList();
      var move = AvailabilityOf(latest, current) is MoveAvailability.AtLatest && behind.Count > 0 ? MoveAvailability.Available : AvailabilityOf(latest, current);
      return move == MoveAvailability.Available
         ? new CheckedMove(input.TourId, move, current, latest.Commit, [.. behind.Select(DiveOf)])
         : new CheckedMove(input.TourId, move, current, latest.Commit);
   }

   /// <summary>
   /// Moves the tour, and the dives that the input ticks, to the latest commit of the tour's ref in the reviewer's name, as <see cref="Repin"/> describes for each of them, and reports the move to
   /// the bound session of each moved tour that a stop of it went stale on, in a decided thread. Returns the tour and the moved dives with their new pins, stale stops and threads.
   /// </summary>
   /// <remarks>
   /// <para>
   /// The move fetches the commit into the tour's clone, with the platform's credential, and pins each moved tour to a snapshot of it whose move is one more than the tour's. The tour and each
   /// ticked dive at that commit already stay as they are, and so does every dive that the input does not tick.
   /// </para>
   /// <para>
   /// It is refused, and stores nothing, for a ref that names another commit than the input, a ref that is a tag or a commit ID, and a tour and ticked dives at that commit already. It is refused too
   /// for a ticked ID that is no dive below the tour, a dive whose parent stays behind, a moved tour while the thread of an earlier update or move waits, and a moved tour without a bound session
   /// that the move leaves a stale stop on.
   /// </para>
   /// </remarks>
   public async Task<OperationResult<MovedSubtree>> MoveAsync(Access.Reviewer.Pass pass, MoveTourInput input, CancellationToken cancellationToken)
   {
      for (var i = 0; i < input.DiveIds.Count; i++)
      {
         // The serializer checks nullability on properties but not on collection elements.
         if (input.DiveIds[i] is null)
         {
            return OperationError.Invalid(DiveIdsField.At(i), "a tourId cannot be null.");
         }
      }

      var read = _writer.Read(input.TourId);
      if (read.Value is not { } tour)
      {
         return read.Error!;
      }

      if (CheckMovable(tour) is { } notMovable)
      {
         return notMovable;
      }

      var resolved = await ResolveAsync(tour, cancellationToken);
      if (resolved.Value is not { } latest)
      {
         return resolved.Error!;
      }

      if (CheckLatest(latest, input.Commit, ((TourPin.Snapshot)tour.Pin).Head) is { } noMove)
      {
         return noMove;
      }

      // The first selection names the commits that the moved tours are at, which the scope's first read brings into the clone with the latest one.
      var selected = _writer.Exclusive(locked =>
      {
         var subtree = locked.Subtree(input.TourId);
         return subtree.Value is { } tours ? Select(tours, input.DiveIds, latest.Commit) : subtree.Error!;
      });
      if (selected.Value is not { } moved)
      {
         return selected.Error!;
      }

      IFactScope? scope = null;
      try
      {
         var remote = TourRemote.OfRepository(latest, _platforms.CredentialOf(tour.Target));
         scope = _facts.Open(remote, [latest.Commit, .. moved.Select(movedTour => ((TourPin.Snapshot)movedTour.Pin).Head).Distinct()], FetchPolicy.WhenMissing);
         await scope.OpenCloneAsync(cancellationToken);
         return await IterationRetry.BoundedAsync<OperationResult<MovedSubtree>>(async attemptToken =>
         {
            IReadOnlyList<Tour>? needsFacts = null;
            var known = scope.Known;
            var result = _writer.Exclusive(pass, locked => Move(locked, pass, input, latest, known, out needsFacts));
            if (needsFacts is null)
            {
               return new Attempt<OperationResult<MovedSubtree>>.Done(result!);
            }

            await FactRounds.SettleAsync(scope, facts => Remap(needsFacts, latest, facts), attemptToken);
            return new Attempt<OperationResult<MovedSubtree>>.Again();
         }, () => OperationError.TourChangedDuringMove(), cancellationToken);
      }
      catch (ExternalFailureException failure)
      {
         return OperationError.RepositoryUnavailable(input.TourId, failure.Reason);
      }
      finally
      {
         if (scope is not null)
         {
            await scope.DisposeAsync();
         }
      }
   }

   // Returns why the tour has no move from its pin, whatever its ref names, or null for a repository tour pinned to a snapshot.
   private static OperationError? CheckMovable(Tour tour) => tour switch
   {
      { Target: not RepositoryTarget } => OperationError.NoMoveForPullRequest(tour),
      { Pin: not TourPin.Snapshot } => OperationError.NoSnapshotToMove(tour),
      _ => null,
   };

   private static MoveAvailability AvailabilityOf(ResolvedRepository latest, CommitId current) => latest.Kind switch
   {
      RepositoryRefKind.Tag => MoveAvailability.Tag,
      RepositoryRefKind.Commit => MoveAvailability.Commit,
      _ => latest.Commit == current ? MoveAvailability.AtLatest : MoveAvailability.Available,
   };

   // Returns why the tour at the current commit has no move to the commit that the check showed, or null when the ref names that commit. Whether a tour of the family is behind it is the selection's.
   private static OperationError? CheckLatest(ResolvedRepository latest, CommitId shown, CommitId current) => AvailabilityOf(latest, current) switch
   {
      MoveAvailability.Tag or MoveAvailability.Commit => OperationError.NoMoveOfRef(latest.Kind, latest.Ref),
      _ when latest.Commit != shown => OperationError.RefMovedSinceCheck(shown, latest.Commit),
      _ => null,
   };

   // A dive's own origin names its parent, and Subtree lists only dives below the tour, so each has a title and an origin.
   private static MoveDive DiveOf(Tour dive) => new(dive.Id, dive.DiveOrigin!.ParentId, dive.Title!, TourStates.Of(dive), dive.Pin);

   // Returns the subtree's tours that the move re-pins, each tour before its dives, or the refusal of the first ticked ID that cannot move, or of a move that re-pins no tour. The tour and the ticked dives at the latest commit
   // already stay as they are, and every tour of the subtree at that commit counts as moved for the dives below it.
   private static OperationResult<IReadOnlyList<Tour>> Select(IReadOnlyList<Tour> subtree, IReadOnlyList<string> diveIds, CommitId latest)
   {
      var top = subtree[0];
      var dives = subtree.Skip(1).ToDictionary(dive => dive.Id.Value, StringComparer.Ordinal);
      var ticked = new HashSet<TourId>();
      var atLatest = subtree.Where(tour => tour.Pin is TourPin.Snapshot { Head: var head } && head == latest).Select(tour => tour.Id).ToHashSet();
      for (var i = 0; i < diveIds.Count; i++)
      {
         if (!dives.TryGetValue(diveIds[i], out var dive))
         {
            return OperationError.Invalid(DiveIdsField.At(i), $"tour {diveIds[i]} is not a dive below tour {top.Id}. Give only the tourIds of the dives that check_move listed.");
         }

         ticked.Add(dive.Id);
      }

      for (var i = 0; i < diveIds.Count; i++)
      {
         var dive = dives[diveIds[i]];
         var parent = dive.DiveOrigin!.ParentId;
         if (parent != top.Id && !ticked.Contains(parent) && !atLatest.Contains(parent))
         {
            return OperationError.Invalid(DiveIdsField.At(i), $"dive {dive.Id} dives from tour {parent}, which stays behind at its commit. Tick tour {parent} too, or leave the dive out.");
         }

         if (dive.Pin is not TourPin.Snapshot)
         {
            return OperationError.Invalid(DiveIdsField.At(i), $"dive {dive.Id} is not pinned to a commit, so it has no move. Leave the dive out.");
         }
      }

      IReadOnlyList<Tour> moved = [.. subtree.Where(tour => (tour.Id == top.Id || ticked.Contains(tour.Id)) && !atLatest.Contains(tour.Id))];
      return moved.Count > 0 ? OperationResult<IReadOnlyList<Tour>>.Of(moved) : OperationError.NothingToMove(latest, [.. subtree.Skip(1).Where(dive => !atLatest.Contains(dive.Id)).Select(dive => dive.Id)]);
   }

   // Returns each tour moved onto a snapshot of the latest commit, in the order of the tours, or the facts that the remaps need first.
   private static Decision<IReadOnlyList<Repin>> Remap(IReadOnlyList<Tour> tours, ResolvedRepository latest, FactSet facts)
   {
      var repins = new List<Repin>();
      var missing = new HashSet<FactKey>();
      foreach (var tour in tours)
      {
         switch (RemapOne(tour, latest, facts))
         {
            case Decision<Repin>.Decided { Value: var repin }:
               repins.Add(repin);
               break;
            case Decision<Repin>.NeedsFacts { Missing: var lacked }:
               missing.UnionWith(lacked);
               break;
         }
      }

      return missing.Count > 0 ? new Decision<IReadOnlyList<Repin>>.NeedsFacts(missing) : new Decision<IReadOnlyList<Repin>>.Decided(repins);
   }

   // Select keeps only tours pinned to a snapshot.
   private static Decision<Repin> RemapOne(Tour tour, ResolvedRepository latest, FactSet facts)
   {
      var from = (TourPin.Snapshot)tour.Pin;
      return Repin.Map(tour, from, new TourPin.Snapshot(latest.Commit, latest.RemoteUrl, from.Move + 1), facts);
   }

   // One hold of the lock: checks every moved tour on the latest subtree, then stores each. Sets needsFacts to the moved tours, and stores nothing, when their remaps lack facts.
   private OperationResult<MovedSubtree>? Move(TourWriter.Locked locked, Access.Reviewer.Pass pass, MoveTourInput input, ResolvedRepository latest, FactSet known, out IReadOnlyList<Tour>? needsFacts)
   {
      needsFacts = null;
      var subtree = locked.Subtree(input.TourId);
      if (subtree.Value is not { } tours)
      {
         return subtree.Error!;
      }

      if ((CheckMovable(tours[0]) ?? CheckLatest(latest, input.Commit, ((TourPin.Snapshot)tours[0].Pin).Head)) is { } noMove)
      {
         return noMove;
      }

      var selected = Select(tours, input.DiveIds, latest.Commit);
      if (selected.Value is not { } moved)
      {
         return selected.Error!;
      }

      foreach (var tour in moved)
      {
         if (TourThreads.CheckWaits(tour, WaitGroup.Iteration, null, FieldOf(tour, input)) is { } waits)
         {
            return waits;
         }
      }

      if (Remap(moved, latest, known) is not Decision<IReadOnlyList<Repin>>.Decided { Value: var repins })
      {
         needsFacts = moved;
         return null;
      }

      // The check reads each bound session once, so a session that ends before its tour's store does not fail the store. That session never gets the decision, and the tour keeps the decided thread.
      var reports = new ThreadCommand.Report?[moved.Count];
      for (var i = 0; i < moved.Count; i++)
      {
         if (repins[i].Decision is not { } decision)
         {
            continue;
         }

         if (locked.Holder(moved[i].Id) is not { } holder)
         {
            return OperationError.NoSessionAttached(moved[i].Id, Sent);
         }

         reports[i] = new ThreadCommand.Report(decision, "", null, holder, FieldOf(moved[i], input));
      }

      // The tour goes first, when it moves, and each dive after its parent, as the class remarks say.
      var top = tours[0];
      var (movedTop, topRevision) = ((MovedTour?)null, top.Revision);
      var dives = new List<MovedTour>();
      for (var i = 0; i < moved.Count; i++)
      {
         OperationResult<MovedTour> stored;
         long revision;
         try
         {
            BeforeStore?.Invoke(moved[i].Id);
            stored = Store(locked, moved[i], repins[i], reports[i], out revision);
         }
         catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
         {
            _logger.LogWarning("The move of tour {TourId} could not be stored after {Count} tours of its family moved: {Reason}", moved[i].Id.Value, i, exception.Message);
            return OperationError.MoveStoredInPart([.. moved.Take(i).Select(tour => tour.Id)], [.. moved.Skip(i).Select(tour => tour.Id)], latest.Commit, exception.Message, onDisk: true);
         }

         if (stored.Value is not { } movedTour)
         {
            return i == 0
               ? stored.Error!
               : OperationError.MoveStoredInPart([.. moved.Take(i).Select(tour => tour.Id)], [.. moved.Skip(i).Select(tour => tour.Id)], latest.Commit, stored.Error!.Problem, onDisk: false);
         }

         if (movedTour.TourId == top.Id)
         {
            (movedTop, topRevision) = (movedTour, revision);
         }
         else
         {
            dives.Add(movedTour);
         }
      }

      // Each store shows its own tour's revision to the pass, and the output's revision is the tour's.
      pass.Showed(topRevision);
      var shown = movedTop ?? Unmoved(top);
      return new MovedSubtree(shown.TourId, shown.UpdatedAt, shown.Pin, shown.StaleStops, shown.Thread, dives.Count > 0 ? dives : null);
   }

   // A tour at the latest commit already, as the move leaves it: its stale stops are those that an earlier update or move left.
   private static MovedTour Unmoved(Tour tour) =>
      new(tour.Id, tour.UpdatedAt, tour.Pin, [.. (tour.Plan?.Stops ?? []).OfType<Stop.Written>().Where(stop => stop.StaleOn is not null).Select(stop => stop.Id)]);

   // The field that a refusal of the moved tour names: tourId for the tour, and the ticked ID for a dive.
   private static FieldPath FieldOf(Tour tour, MoveTourInput input)
   {
      var index = input.DiveIds.ToList().FindIndex(id => id == tour.Id.Value);
      return index < 0 ? "tourId" : DiveIdsField.At(index);
   }

   // Stores the move of one tour from the repin that the hold checked, with the report of its decision when the move left a stop stale, and its events. No store ran since the check, so the
   // latest tour is the checked one.
   private static OperationResult<MovedTour> Store(TourWriter.Locked locked, Tour checkedTour, Repin repin, ThreadCommand.Report? report, out long revision)
   {
      var storedRevision = 0L;
      var result = locked.Write(checkedTour.Id, edit =>
      {
         if (edit.Latest.Revision != checkedTour.Revision)
         {
            return OperationError.TourChangedDuringMove();
         }

         var settled = TourThreads.Settle(repin.Applied(edit.Latest), repin.Anchors);
         var write = report is null ? settled : settled.Then(report, edit.Now);
         return edit.Store(write, (stored, written) =>
         {
            var tour = stored.Tour;
            storedRevision = tour.Revision;
            stored.Publish(ServerEvents.ForTourPinned(tour));
            if (tour.Plan is not null)
            {
               stored.Publish(ServerEvents.ForPlanSet(tour, stored.Mentions));
            }

            return new MovedTour(tour.Id, tour.UpdatedAt, tour.Pin, repin.StaleStops, written.Thread);
         });
      });
      revision = storedRevision;
      return result;
   }

   // Resolves the tour's ref, or returns the refusal that names the platform's or git's reason.
   private async Task<OperationResult<ResolvedRepository>> ResolveAsync(Tour tour, CancellationToken cancellationToken)
   {
      try
      {
         return await _platforms.ResolveAsync((RepositoryTarget)tour.Target, _platformFree, cancellationToken);
      }
      catch (ExternalFailureException failure)
      {
         return OperationError.RepositoryUnavailable(tour.Id, failure.Reason);
      }
   }
}
