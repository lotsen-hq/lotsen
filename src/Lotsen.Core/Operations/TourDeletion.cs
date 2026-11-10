using System.ComponentModel;
using System.Text.Json.Serialization;
using Lotsen.Git;
using Lotsen.Hosting;
using Lotsen.Pinning;
using Lotsen.Sessions;
using Lotsen.Tours;

namespace Lotsen.Operations;

/// <summary>The input of <c>delete_tour</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DeleteTourInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId);

/// <summary>A tour that the reviewer deleted: the output of <c>delete_tour</c> and the payload of <see cref="ServerEvents.TourDeleted"/>.</summary>
/// <param name="TourId">The deleted tour's ID.</param>
internal sealed record DeletedTour(TourId TourId);

/// <summary>The output of <c>prune_clones</c>.</summary>
/// <param name="Removed">The number of clones and temporary clones that the prune removed.</param>
internal sealed record PrunedClones(int Removed);

/// <summary>The reviewer's delete of a tour, and the prunes of the clones that no tour refers to.</summary>
/// <remarks>
/// A tour refers to the clone of each remote that <see cref="TourRemote.Of"/> lists for it. A tour stored with another schema version refers to none, because this build can never open it.
/// While any other tour cannot be read, a prune removes nothing, because that tour may refer to any clone. Each prune then starts a maintenance run of the clones that are due.
/// </remarks>
internal sealed class TourDeletion
{
   private readonly TourWriter _writer;
   private readonly CloneCache _clones;
   private readonly CloneMaintenance _maintenance;
   private readonly BackgroundJobs _jobs;
   private readonly IterationScheduler _scheduler;
   private readonly ILogger<TourDeletion> _logger;

   public TourDeletion(TourWriter writer, CloneCache clones, CloneMaintenance maintenance, BackgroundJobs jobs, IterationScheduler scheduler, ILogger<TourDeletion> logger)
   {
      _writer = writer;
      _clones = clones;
      _maintenance = maintenance;
      _jobs = jobs;
      _scheduler = scheduler;
      _logger = logger;
   }

   /// <summary>Removes the tour for good, releases its binding and tells every page, then removes the clones that the tour referred to and no other tour refers to.</summary>
   /// <remarks>
   /// <para>
   /// The tour needs no bound session, and no session event tells of the delete. The bound session's later calls on the tour are refused as not found. Every write runs through <see
   /// cref="ITourStore.Update"/>, which finds no tour after the delete, so a pin, an iteration check or any other write that ends later stores nothing and publishes nothing.
   /// </para>
   /// <para>
   /// The prune of the tour's clones runs as a background job after the answer, as <see cref="CloneCache.PruneAsync"/> describes, and leaves a clone that another tour refers to or that a pin, fetch
   /// or file read uses. A tour deleted while it pins refers to no clone yet, so the clone of that pin stays until <see cref="PruneClonesAsync"/>.
   /// </para>
   /// </remarks>
   public OperationResult<DeletedTour> DeleteTour(Access.Reviewer.Pass pass, DeleteTourInput input)
   {
      var result = _writer.Exclusive(pass, locked =>
      {
         var deleted = locked.Delete(input.TourId);
         if (deleted.Value is { } tour)
         {
            locked.Publish(ServerEvents.ForTourDeleted(new DeletedTour(tour.Id)));
         }

         return deleted;
      });
      if (result.Value is not { } deleted)
      {
         return result.Error!;
      }

      _logger.LogInformation("Tour {TourId} was deleted.", deleted.Id.Value);
      _scheduler.Forget(deleted.Id);
      IReadOnlyCollection<Uri> clones = [.. UrlsOf(deleted)];
      if (clones.Count > 0)
      {
         _jobs.Run(async stopping =>
         {
            await _clones.PruneAsync(() => ReferencedClones(out _), only: clones, stopping);
            _maintenance.Request();
         });
      }

      return new DeletedTour(deleted.Id);
   }

   /// <summary>Removes every clone that no tour refers to and every temporary clone that a clone cut short left, and returns how many it removed.</summary>
   /// <remarks>
   /// The prune leaves a clone that a pin, fetch or file read uses, as <see cref="CloneCache.PruneAsync"/> describes. A prune while a tour cannot be read for a reason other than another schema version
   /// removes nothing and is refused.
   /// </remarks>
   /// <param name="cancellationToken">Stops the prune before its next clone, as <see cref="CloneCache.PruneAsync"/> says.</param>
   public async Task<OperationResult<PrunedClones>> PruneClonesAsync(CancellationToken cancellationToken)
   {
      OperationError? refusal = null;
      var removed = await _clones.PruneAsync(() => ReferencedClones(out refusal), only: null, cancellationToken);
      _maintenance.Request();
      return refusal is { } refused ? refused : new PrunedClones(removed);
   }

   // A prune runs no git against the remotes, so it needs no credential.
   private static IEnumerable<Uri> UrlsOf(Tour tour) => TourRemote.Of(tour, credential: null).Select(remote => remote.Url);

   // Returns the remote URLs of the clones that the stored tours refer to. Returns null, with the refusal for the reviewer, when the tours cannot be listed or a tour could not be read.
   private List<Uri>? ReferencedClones(out OperationError? refusal)
   {
      TourListing listing;
      try
      {
         listing = _writer.List();
      }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
      {
         _logger.LogWarning("The tours could not be listed, so no clone was pruned: {Reason}", exception.Message);
         refusal = OperationError.ToursUnlisted();
         return null;
      }

      if (listing.Unreadable.Count > 0)
      {
         List<string> files = [.. listing.Unreadable.Select(_writer.PathOf)];
         _logger.LogWarning("No clone was pruned, because these tour files could not be read: {Files}. Make them readable, or move or delete them, then prune again.", string.Join(", ", files));

         // The refusal reaches Claude and the UI, so it names the file without the user's home directory.
         refusal = OperationError.ToursUnreadable([.. listing.Unreadable.Zip(files, (id, file) => (id, Path.GetFileName(file)))]);
         return null;
      }

      refusal = null;
      return [.. listing.Tours.SelectMany(UrlsOf).Distinct()];
   }
}
