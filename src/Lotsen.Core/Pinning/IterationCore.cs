using Lotsen.Git;
using Lotsen.Platforms;
using Lotsen.Tours;

namespace Lotsen.Pinning;

/// <summary>The platform's answer to an iteration check: the pull request's latest iteration, or why the platform gave none.</summary>
internal closed record PlatformAnswer
{
   private PlatformAnswer()
   {
   }

   /// <summary>The time at which the platform answered.</summary>
   public DateTimeOffset At => this switch
   {
      Resolved resolved => resolved.AnsweredAt,
      Unreachable unreachable => unreachable.AnsweredAt,
   };

   /// <summary>An answer with the pull request's latest iteration.</summary>
   /// <param name="Latest">The latest iteration, with the pull request's state.</param>
   /// <param name="AnsweredAt">When the platform answered.</param>
   internal sealed record Resolved(ResolvedPullRequest Latest, DateTimeOffset AnsweredAt) : PlatformAnswer;

   /// <summary>A failure of the platform or of its token.</summary>
   /// <param name="Reason">What went wrong, written for the reviewer. It names the fix where there is one.</param>
   /// <param name="AnsweredAt">When the request failed.</param>
   internal sealed record Unreachable(string Reason, DateTimeOffset AnsweredAt) : PlatformAnswer;
}

/// <summary>What an iteration check finds, which the shell stores on the latest tour.</summary>
internal closed record CheckOutcome
{
   private CheckOutcome()
   {
   }

   /// <summary>A latest iteration whose source commit is the pinned head, which clears the available iteration.</summary>
   /// <param name="PullRequestState">The pull request's state that the platform reported.</param>
   internal sealed record Unchanged(PullRequestState PullRequestState) : CheckOutcome;

   /// <summary>A latest iteration with another head, which becomes the tour's available iteration.</summary>
   /// <param name="Available">The available iteration, with the stops that an update to it marks stale.</param>
   /// <param name="PullRequestState">The pull request's state that the platform reported.</param>
   internal sealed record Found(AvailableIteration Available, PullRequestState PullRequestState) : CheckOutcome;

   /// <summary>A check that could not finish, which keeps the available iteration.</summary>
   /// <param name="Reason">What went wrong, written for the reviewer. It names the fix where there is one.</param>
   /// <param name="PullRequestState">The pull request's state, when the platform answered before the failure, or null when it gave no answer.</param>
   internal sealed record Failed(string Reason, PullRequestState? PullRequestState) : CheckOutcome;
}

/// <summary>What an iteration update decides for the latest tour.</summary>
internal closed record UpdateOutcome
{
   private UpdateOutcome()
   {
   }

   /// <summary>The tour moved onto its available iteration.</summary>
   internal sealed record Repinned(Repin Repin) : UpdateOutcome;

   /// <summary>A tour that takes no iteration update now.</summary>
   internal sealed record Refused(OperationError Refusal) : UpdateOutcome;
}

/// <summary>Decides the outcomes of the first pin, the iteration check and the iteration update from a tour, the platform's answer and git facts. It does no I/O and reads no clock.</summary>
/// <remarks>
/// A new iteration is one whose source commit differs from the pinned head. The iteration number does not count, so a target that moves alone never gives a new iteration. An iteration update
/// moves the tour onto the available iteration that the reviewer's notice showed, as <see cref="Operations.IterationLifecycle.UpdateAsync"/> checks, without the platform, so a newer head
/// becomes the next available iteration.
/// </remarks>
internal static class IterationCore
{
   /// <summary>Returns the pin of the latest iteration of a pull request: its merge base and head, or a failure when the two share no history.</summary>
   /// <param name="pullRequest">The pull request's URL, which the failure names.</param>
   /// <param name="latest">The latest iteration, as the platform resolved it.</param>
   /// <param name="facts">The git facts known so far.</param>
   public static Decision<TourPin> FirstPin(Uri pullRequest, ResolvedPullRequest latest, FactSet facts)
   {
      if (!facts.TryGet(new FactKey.MergeBaseOf(latest.SourceCommit, latest.TargetCommit), out var mergeBase))
      {
         return new Decision<TourPin>.NeedsFacts(new HashSet<FactKey> { new FactKey.MergeBaseOf(latest.SourceCommit, latest.TargetCommit) });
      }

      return new Decision<TourPin>.Decided(mergeBase is { } found
         ? new TourPin.Pinned(found, latest.SourceCommit, latest.Iteration, latest.RemoteUrl)
         : new TourPin.Failed($"The source branch of the pull request {pullRequest.AbsoluteUri} shares no history with its target branch, so the pull request has no merge base to show its changes from."));
   }

   /// <summary>Returns what the platform's answer is to the pinned tour: no new iteration, an available iteration with the stops that an update to it makes stale, or a failure.</summary>
   /// <param name="latest">The tour, pinned.</param>
   /// <param name="answer">The platform's answer.</param>
   /// <param name="facts">The git facts known so far.</param>
   /// <param name="now">The time that a new available iteration stores as found.</param>
   public static Decision<CheckOutcome> Check(Tour latest, PlatformAnswer answer, FactSet facts, DateTimeOffset now)
   {
      if (latest.Pin is not TourPin.Pinned pin)
      {
         return new Decision<CheckOutcome>.Decided(new CheckOutcome.Failed("lotsen checks only a pinned tour for a new update.", (answer as PlatformAnswer.Resolved)?.Latest.State));
      }

      if (answer is not PlatformAnswer.Resolved { Latest: var resolved })
      {
         return new Decision<CheckOutcome>.Decided(new CheckOutcome.Failed(((PlatformAnswer.Unreachable)answer).Reason, null));
      }

      if (resolved.SourceCommit == pin.Head)
      {
         return new Decision<CheckOutcome>.Decided(new CheckOutcome.Unchanged(resolved.State));
      }

      var url = (latest.Target as PullRequestTarget)?.Url ?? resolved.RemoteUrl;
      return FirstPin(url, resolved, facts) switch
      {
         Decision<TourPin>.NeedsFacts { Missing: var missing } => new Decision<CheckOutcome>.NeedsFacts(missing),
         Decision<TourPin>.Decided { Value: TourPin.Pinned to } => Repin.Map(latest, pin, to, facts) switch
         {
            Decision<Repin>.Decided { Value: var repin } => new Decision<CheckOutcome>.Decided(new CheckOutcome.Found(new AvailableIteration(to, repin.StaleStops, now), resolved.State)),
            Decision<Repin>.NeedsFacts { Missing: var missing } => new Decision<CheckOutcome>.NeedsFacts(missing),
         },
         Decision<TourPin>.Decided { Value: var failed } => new Decision<CheckOutcome>.Decided(new CheckOutcome.Failed(((TourPin.Failed)failed).Reason, resolved.State)),
      };
   }

   /// <summary>Returns the latest tour moved onto its available iteration, or the refusal of a tour that takes no update now, as <see cref="DecisionRules.CheckUpdatable"/> says.</summary>
   /// <param name="latest">The latest stored tour.</param>
   /// <param name="facts">The git facts known so far.</param>
   public static Decision<UpdateOutcome> Update(Tour latest, FactSet facts)
   {
      if (DecisionRules.CheckUpdatable(latest) is { } refused)
      {
         return new Decision<UpdateOutcome>.Decided(new UpdateOutcome.Refused(refused));
      }

      // TourRules keeps an available iteration only on a pinned tour.
      var (from, to) = ((TourPin.Pinned)latest.Pin, latest.AvailableIteration!.Pin);
      return Repin.Map(latest, from, to, facts) switch
      {
         Decision<Repin>.Decided { Value: var repin } => new Decision<UpdateOutcome>.Decided(new UpdateOutcome.Repinned(repin)),
         Decision<Repin>.NeedsFacts { Missing: var missing } => new Decision<UpdateOutcome>.NeedsFacts(missing),
      };
   }

   /// <summary>Returns the commits that a scope over the tour names up front: the tour's pin and the latest iteration's source and target, or the available iteration's pin.</summary>
   /// <param name="tour">The tour, pinned.</param>
   /// <param name="latest">The latest iteration that the platform resolved, or null for the tour's available iteration.</param>
   public static IReadOnlyCollection<CommitId> Commits(Tour tour, ResolvedPullRequest? latest)
   {
      var commits = new List<CommitId>();
      if (tour.Pin is TourPin.Pinned pin)
      {
         commits.AddRange([pin.MergeBase, pin.Head]);
      }

      if (latest is not null)
      {
         commits.AddRange([latest.SourceCommit, latest.TargetCommit]);
      }
      else if (tour.AvailableIteration is { Pin: var available })
      {
         commits.AddRange([available.MergeBase, available.Head]);
      }

      return [.. commits.Distinct()];
   }
}
