using System.Diagnostics;
using Lotsen.Pinning;
using Lotsen.Platforms;
using Lotsen.Sessions;
using Lotsen.Tours;

namespace Lotsen.Operations;

/// <summary>One attempt of a retried pass: done with its value, or to run again because the tour changed under it.</summary>
internal closed record Attempt<T>
{
   private Attempt()
   {
   }

   /// <summary>An attempt that finished.</summary>
   internal sealed record Done(T Value) : Attempt<T>;

   /// <summary>An attempt whose tour needed facts that the attempt read, so the next attempt runs on the latest tour again.</summary>
   internal sealed record Again : Attempt<T>;
}

/// <summary>The one retry rule of the iteration check and the iteration update: a pass that needs facts reads them outside the lock and runs again, at most <see cref="Attempts"/> times.</summary>
internal static class IterationRetry
{
   /// <summary>The number of attempts in all.</summary>
   public const int Attempts = 3;

   /// <summary>Returns the value of the first attempt that finishes, or <paramref name="exhausted"/>'s value when none of the attempts did.</summary>
   public static async Task<T> BoundedAsync<T>(Func<CancellationToken, Task<Attempt<T>>> attempt, Func<T> exhausted, CancellationToken cancellationToken)
   {
      for (var i = 0; i < Attempts; i++)
      {
         if (await attempt(cancellationToken) is Attempt<T>.Done { Value: var value })
         {
            return value;
         }
      }

      return exhausted();
   }
}

/// <summary>What one iteration check left on the tour.</summary>
/// <param name="Tour">The tour after the check, or null when it is missing or unreadable.</param>
/// <param name="Failure">Why the check could not finish, written for the reviewer, or null when it finished.</param>
/// <param name="Answer">The platform's answer that the check used, or null when the check asked no platform.</param>
internal sealed record CheckRun(Tour? Tour, string? Failure, PlatformAnswer? Answer);

/// <summary>
/// Runs the iteration lifecycle against the platform and git: the first pin of a tour, the iteration check and the iteration update, each decided by <see cref="IterationCore"/>.
/// </summary>
/// <remarks>
/// <para>
/// Platform calls and git reads run outside the writer's lock. Under the lock only the core's pass over the facts read so far, the store, its events and its sends run, always on the latest
/// tour, so nothing computed for an older tour is stored. A pass that needs facts reads them outside the lock and runs again, as <see cref="IterationRetry"/> bounds it.
/// </para>
/// <para>
/// Each run that reads git opens one <see cref="IFactScope"/> and disposes it after its store, so a prune always finds the clone in use or referred to by the stored tour.
/// </para>
/// </remarks>
internal sealed class IterationLifecycle
{
   private const string UnexpectedCheckFailure = "lotsen could not check the pull request for a new update because of an unexpected error, which the server's log describes. Check again.";

   private const string ChangedCheckFailure = "lotsen could not check the pull request for a new update, because the tour changed during every attempt, as when Claude wrote stops. Check again.";

   private readonly TourWriter _writer;
   private readonly PullRequestPlatforms _platforms;
   private readonly IGitFacts _facts;
   private readonly TimeProvider _time;
   private readonly ILogger<IterationLifecycle> _logger;

   public IterationLifecycle(TourWriter writer, PullRequestPlatforms platforms, IGitFacts facts, TimeProvider time, ILogger<IterationLifecycle> logger)
   {
      _writer = writer;
      _platforms = platforms;
      _facts = facts;
      _time = time;
      _logger = logger;
   }

   /// <summary>A test hook that each iteration check awaits once it has the facts of its first pass, before it stores, with its fact scope open, or null for none.</summary>
   internal Func<TourId, Task>? BeforeCheckStored { get; set; }

   /// <summary>A test hook that each iteration update awaits before each of its passes under the lock, or null for none.</summary>
   internal Func<TourId, Task>? BeforeUpdatePass { get; set; }

   /// <summary>Finds the pin of a tour of this target, and passes it to <paramref name="storeAsync"/> while the pin's clone is in use.</summary>
   /// <remarks>
   /// The target's platform resolves the latest iteration. The scope syncs the pull request's repository into the clone cache with both commits, with the platform's token, so every later query
   /// of the tour finds them there. A pin then downloads the contents of the changed files, so listing them and their diffs read the clone alone. A failed download is logged and leaves the pin.
   /// </remarks>
   /// <param name="id">The tour, which the log names.</param>
   /// <param name="target">What the tour reviews.</param>
   /// <param name="storeAsync">
   /// Stores the pin, with the pull request's state when the platform answered, or null. A <see cref="TourPin.Failed"/> names the fix where there is one, as for a missing sign-in, a pull request that
   /// is not found or a git failure.
   /// </param>
   /// <param name="cancellationToken">Stops the platform's request and git.</param>
   /// <exception cref="OperationCanceledException">The token was canceled.</exception>
   public async Task PinAsync(TourId id, TourTarget target, Func<TourPin, PullRequestState?, Task> storeAsync, CancellationToken cancellationToken)
   {
      IFactScope? scope = null;
      try
      {
         TourPin pin;
         PullRequestState? state = null;
         try
         {
            switch (target)
            {
               case PullRequestTarget pullRequest:
                  var platform = _platforms.Of(pullRequest);
                  var latest = await platform.ResolveAsync(pullRequest.Url, cancellationToken);
                  state = latest.State;
                  scope = _facts.Open(TourRemote.OfIteration(latest, platform.GitCredential), [latest.SourceCommit, latest.TargetCommit], FetchPolicy.Sync);

                  // The memo can answer every fact without this remote's clone, which every later query of the tour reads, so the pin syncs the clone first.
                  await scope.OpenCloneAsync(cancellationToken);
                  pin = await FactRounds.SettleAsync(scope, known => IterationCore.FirstPin(pullRequest.Url, latest, known), cancellationToken);
                  if (pin is TourPin.Pinned pinned)
                  {
                     await WarmAsync(id, scope, pinned, cancellationToken);
                  }

                  break;
               default:
                  pin = new TourPin.Failed("lotsen cannot pin this kind of tour target.");
                  break;
            }
         }
         catch (ExternalFailureException failure)
         {
            pin = new TourPin.Failed(failure.Reason);
         }

         if (pin is TourPin.Pinned stored)
         {
            _logger.LogInformation("Tour {TourId} is pinned to merge base {MergeBase} and head {Head} of iteration {Iteration}.", id.Value, stored.MergeBase.Value, stored.Head.Value, stored.Iteration);
         }
         else if (pin is TourPin.Failed failed)
         {
            _logger.LogWarning("Tour {TourId} could not be pinned. {Reason}", id.Value, failed.Reason);
         }

         await storeAsync(pin, state);
      }
      finally
      {
         if (scope is not null)
         {
            await scope.DisposeAsync();
         }
      }
   }

   /// <summary>Checks the tour's pull request for a new iteration, stores the result on the latest tour, and returns what the check left.</summary>
   /// <remarks>
   /// The check decides on the latest tour under the lock, whatever changed since it read the tour, so a check that ends after an iteration update compares the answer with the new pin. A failure
   /// of the platform, its token or git is logged and returned as the run's failure, and keeps the available iteration.
   /// </remarks>
   /// <param name="id">The tour.</param>
   /// <param name="reuse">The platform's answer to use instead of a new request, or null to ask the platform.</param>
   /// <param name="cancellationToken">Stops the platform's request and git.</param>
   /// <exception cref="OperationCanceledException">The token was canceled.</exception>
   public async Task<CheckRun> CheckAsync(TourId id, PlatformAnswer? reuse, CancellationToken cancellationToken)
   {
      try
      {
         return await CheckOnceAsync(id, reuse, cancellationToken);
      }
      catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
      {
         throw;
      }
      catch (Exception exception)
      {
         _logger.LogError(exception, "The pull request of tour {TourId} could not be checked for a new iteration.", id.Value);
         return new CheckRun(_writer.Read(id).Value, UnexpectedCheckFailure, null);
      }
   }

   /// <summary>
   /// Moves the tour onto the pin of its available iteration in the reviewer's name, as <see cref="Repin"/> describes, together with a decided thread that reports the iteration update to the
   /// tour's bound session when a stop is stale. Sends the session the question, and returns the new pin and the stale stops with the thread.
   /// </summary>
   /// <remarks>
   /// <para>
   /// The update moves the tour onto the input's pin, the available iteration that the reviewer's notice showed, and asks the platform nothing, so a newer head becomes the next available
   /// iteration. It reads git only for the facts that the memo lacks. Before its first pass it downloads the new pin's changed files when no scope did, and a failed download is logged and leaves
   /// the update to run. A pass whose tour needs facts reads them outside the lock and runs again on the latest tour, up to <see cref="IterationRetry.Attempts"/> times in all.
   /// </para>
   /// <para>
   /// One write stores the pin and the decided thread, so neither lands without the other. A tour with no bound session is refused, and so is a tour with no available iteration and an update
   /// while the thread of an earlier update waits. Each pass that finds another available iteration than the input's pin is refused too, whether a check stored it before the update or during
   /// it. The plan approval stays as it is.
   /// </para>
   /// </remarks>
   public async Task<OperationResult<UpdatedIteration>> UpdateAsync(Access.ReviewerToSession.Pass pass, UpdateIterationInput input, CancellationToken cancellationToken)
   {
      IFactScope? scope = null;
      var shown = input.Pin.ToPin();
      try
      {
         // The store sends the session its question, which leads to a listing of the changed files, so their download runs before the store whenever the tour can update.
         if (_writer.Read(input.TourId).Value is { AvailableIteration.Pin: var pin } read && pin == shown && DecisionRules.CheckUpdatable(read) is null)
         {
            if (!_facts.Downloaded(pin.RemoteUrl, pin.MergeBase, pin.Head))
            {
               try
               {
                  scope = OpenScope(read, null);
                  await WarmAsync(input.TourId, scope, pin, cancellationToken);
               }
               catch (ExternalFailureException failure)
               {
                  _logger.LogWarning("The changed files of tour {TourId} could not be downloaded, so listing them downloads them. {Reason}", input.TourId.Value, failure.Reason);
               }
            }
         }

         return await IterationRetry.BoundedAsync<OperationResult<UpdatedIteration>>(async attemptToken =>
         {
            if (BeforeUpdatePass is { } beforeUpdatePass)
            {
               await beforeUpdatePass(input.TourId);
            }

            Tour? needsFacts = null;
            var known = scope?.Known ?? _facts.Known;
            var result = _writer.Write<Attempt<UpdatedIteration>>(pass, input.TourId, edit =>
            {
               // Every pass moves the tour onto the notice's iteration alone, so the scope's remote and commits stay those of the iteration that it reads.
               if (edit.Latest.AvailableIteration?.Pin is { } offered && offered != shown)
               {
                  return OperationError.IterationChangedSinceNotice(shown.Iteration, offered.Iteration);
               }

               switch (IterationCore.Update(edit.Latest, known))
               {
                  case Decision<UpdateOutcome>.Decided { Value: UpdateOutcome.Repinned { Repin: var repin } }:
                     return Store(edit, repin);
                  case Decision<UpdateOutcome>.Decided { Value: UpdateOutcome.Refused { Refusal: var refusal } }:
                     return refusal;
                  default:
                     return Unstored(edit, needsFacts = edit.Latest);
               }
            });
            if (result.Value is not Attempt<UpdatedIteration>.Again)
            {
               return new Attempt<OperationResult<UpdatedIteration>>.Done(result.Value is Attempt<UpdatedIteration>.Done { Value: var updated } ? updated : result.Error!);
            }

            try
            {
               scope ??= OpenScope(needsFacts!, null);
               await FactRounds.SettleAsync(scope, facts => IterationCore.Update(needsFacts!, facts), attemptToken);
               return new Attempt<OperationResult<UpdatedIteration>>.Again();
            }
            catch (ExternalFailureException failure)
            {
               return new Attempt<OperationResult<UpdatedIteration>>.Done(OperationError.IterationUnreadable(failure.Reason));
            }
         }, () => OperationError.TourChangedDuringUpdate(), cancellationToken);
      }
      finally
      {
         if (scope is not null)
         {
            await scope.DisposeAsync();
         }
      }
   }

   // A pass that stores nothing and asks for another attempt.
   private static Edited<Attempt<UpdatedIteration>> Unstored(TourEdit edit, Tour latest) => edit.Store(latest, _ => (Attempt<UpdatedIteration>)new Attempt<UpdatedIteration>.Again());

   // Stores the update with its decided thread and its events.
   private static Edited<Attempt<UpdatedIteration>> Store(SessionEdit edit, Repin repin)
   {
      var settled = TourThreads.Settle(repin.Tour, repin.Anchors);
      var write = repin.Decision is { } decision ? settled.Then(new ThreadCommand.Report(decision, "", null, edit.Recipient, "tourId"), edit.Now) : settled;
      return edit.Store(write, (stored, written) =>
      {
         var tour = stored.Tour;
         stored.Publish(ServerEvents.ForTourPinned(tour));
         stored.Publish(ServerEvents.ForAvailableIterationChanged(tour));
         if (tour.Plan is not null)
         {
            stored.Publish(ServerEvents.ForPlanSet(tour, stored.Mentions));
         }

         return (Attempt<UpdatedIteration>)new Attempt<UpdatedIteration>.Done(new UpdatedIteration(tour.Id, tour.UpdatedAt, tour.Pin, repin.StaleStops, written.Thread));
      });
   }

   // Opens the scope of a check over the latest iteration, or of an update over the tour's available iteration when latest is null.
   // The remote is the iteration's, which can differ from the pin's, so the scope's first read fetches the pin's commits when the clone lacks them.
   // Throws PlatformException for a tour whose platform lotsen does not know.
   private IFactScope OpenScope(Tour tour, ResolvedPullRequest? latest)
   {
      var credential = _platforms.Of(tour.Target).GitCredential;
      var remote = latest is not null ? TourRemote.OfIteration(latest, credential) : TourRemote.OfPin(tour.AvailableIteration?.Pin ?? (TourPin.Pinned)tour.Pin, credential);
      return _facts.Open(remote, IterationCore.Commits(tour, latest), FetchPolicy.WhenMissing);
   }

   // Settles the facts for the read tour outside the lock before the first pass, so that pass decides unless the tour changed. A pass whose latest tour needs facts reads them for that tour.
   private async Task<CheckRun> CheckOnceAsync(TourId id, PlatformAnswer? reuse, CancellationToken cancellationToken)
   {
      var read = _writer.Read(id).Value;
      if (read is not { Pin: TourPin.Pinned pin })
      {
         return new CheckRun(read, null, null);
      }

      var answer = reuse ?? await AskAsync(read, cancellationToken);
      IFactScope? scope = null;
      try
      {
         // A latest iteration with the pinned head needs no git, so the scope opens only for one with another head, or for a latest tour that a pass finds on another pin.
         if (answer is PlatformAnswer.Resolved { Latest: var latest } && latest.SourceCommit != pin.Head)
         {
            scope = OpenScope(read, latest);
            await FactRounds.SettleAsync(scope, facts => IterationCore.Check(read, answer, facts, _time.GetUtcNow()), cancellationToken);
         }

         if (BeforeCheckStored is { } beforeCheckStored)
         {
            await beforeCheckStored(id);
         }

         return await IterationRetry.BoundedAsync(async attemptToken =>
         {
            var attempt = StorePass(read, answer, scope?.Known ?? FactSet.Empty);
            if (attempt is Attempt<CheckRun>.Again && answer is PlatformAnswer.Resolved { Latest: var resolved } && _writer.Read(id).Value is { } latestTour)
            {
               scope ??= OpenScope(latestTour, resolved);
               await FactRounds.SettleAsync(scope, facts => IterationCore.Check(latestTour, answer, facts, _time.GetUtcNow()), attemptToken);
            }

            return attempt;
         }, () => Changed(id, answer), cancellationToken);
      }
      catch (ExternalFailureException failure)
      {
         return StoreFailure(read, answer, failure.Reason);
      }
      finally
      {
         if (scope is not null)
         {
            await scope.DisposeAsync();
         }
      }
   }

   // Stores what the core decides for the latest tour, or asks for another attempt when the latest tour needs facts that the set lacks.
   private Attempt<CheckRun> StorePass(Tour read, PlatformAnswer answer, FactSet facts)
   {
      string? failure = (answer as PlatformAnswer.Unreachable)?.Reason;
      var again = false;
      var stored = _writer.Exclusive(locked => locked.Write(read.Id, edit =>
      {
         var latest = edit.Latest;
         var learned = Learned(read, latest, answer);

         AvailableIteration? available;
         switch (IterationCore.Check(latest, answer, facts, edit.Now))
         {
            case Decision<CheckOutcome>.NeedsFacts:
               again = true;
               return edit.Store(latest, written => written.Tour);
            case Decision<CheckOutcome>.Decided { Value: CheckOutcome.Failed { Reason: var reason } }:
               failure = reason;
               return edit.Store(learned, written => written.Tour);
            case Decision<CheckOutcome>.Decided { Value: CheckOutcome.Found { Available: var found } }:
               available = found;
               break;
            case Decision<CheckOutcome>.Decided { Value: CheckOutcome.Unchanged }:
            default:
               available = null;
               break;
         }

         if (available is null ? latest.AvailableIteration is null : latest.AvailableIteration?.HasSameResult(available) == true)
         {
            return edit.Store(learned, written => written.Tour);
         }

         return edit.Store(learned with { AvailableIteration = available }, written =>
         {
            written.Publish(ServerEvents.ForAvailableIterationChanged(written.Tour));
            return written.Tour;
         });
      }));
      if (again)
      {
         return new Attempt<CheckRun>.Again();
      }

      if (stored.Error is { } error && error.Code != OperationErrorCode.TourNotFound && error.Code != OperationErrorCode.TourUnreadable)
      {
         _logger.LogWarning("The iteration check of tour {TourId} could not be stored. {Reason}", read.Id.Value, error.Reason);
      }

      Log(read.Id, stored.Value, failure);
      return new Attempt<CheckRun>.Done(new CheckRun(stored.Value, failure, answer));
   }

   // Returns the latest tour with the pull request state that the answer reports, unless the stored state changed since `read`, so the stored state wins until the next check.
   private static Tour Learned(Tour read, Tour latest, PlatformAnswer answer) =>
      latest.PullRequestState == read.PullRequestState && answer is PlatformAnswer.Resolved { Latest.State: var state } && state != latest.PullRequestState
         ? latest with { PullRequestState = state }
         : latest;

   // Stores the pull request state that the answer reports, and keeps the available iteration, for a check that failed after the platform answered.
   private CheckRun StoreFailure(Tour read, PlatformAnswer answer, string reason)
   {
      var stored = _writer.Exclusive(locked => locked.Write(read.Id, edit => edit.Store(Learned(read, edit.Latest, answer), written => written.Tour)));
      Log(read.Id, stored.Value, reason);
      return new CheckRun(stored.Value, reason, answer);
   }

   private CheckRun Changed(TourId id, PlatformAnswer answer)
   {
      _logger.LogWarning("The pull request of tour {TourId} could not be checked for a new iteration, because the tour changed during every attempt.", id.Value);
      return new CheckRun(_writer.Read(id).Value, ChangedCheckFailure, answer);
   }

   private void Log(TourId id, Tour? tour, string? failure)
   {
      if (failure is not null)
      {
         _logger.LogWarning("The pull request of tour {TourId} could not be checked for a new iteration. {Reason}", id.Value, failure);
      }
      else if (tour?.AvailableIteration is { } available)
      {
         _logger.LogInformation(
            "The pull request of tour {TourId} has iteration {Iteration} with head {Head}, which makes {Count} stops stale.", id.Value, available.Pin.Iteration, available.Pin.Head.Value, available.StaleStops.Count);
      }
   }

   private async Task<PlatformAnswer> AskAsync(Tour tour, CancellationToken cancellationToken)
   {
      try
      {
         // Of throws for every target that is not a pull request, so the cast holds.
         var platform = _platforms.Of(tour.Target);
         var url = ((PullRequestTarget)tour.Target).Url;
         return new PlatformAnswer.Resolved(await platform.ResolveAsync(url, cancellationToken), _time.GetUtcNow());
      }
      catch (ExternalFailureException failure)
      {
         return new PlatformAnswer.Unreachable(failure.Reason, _time.GetUtcNow());
      }
   }

   // A failed download leaves the pin intact: listing the changed files then downloads them itself, within its query timeout.
   private async Task WarmAsync(TourId id, IFactScope scope, TourPin.Pinned pin, CancellationToken cancellationToken)
   {
      var started = Stopwatch.GetTimestamp();
      try
      {
         await scope.WarmAsync(pin.MergeBase, pin.Head, cancellationToken);
         _logger.LogInformation("Downloaded the changed files of tour {TourId} in {Milliseconds} ms.", id.Value, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
      }
      catch (ExternalFailureException failure)
      {
         _logger.LogWarning("The changed files of tour {TourId} could not be downloaded, so listing them downloads them. {Reason}", id.Value, failure.Reason);
      }
   }
}
