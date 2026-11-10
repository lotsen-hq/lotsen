using System.ComponentModel;
using System.Text.Json.Serialization;
using Lotsen.Pinning;
using Lotsen.Tours;

namespace Lotsen.Operations;

/// <summary>The input of <c>check_iteration</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CheckIterationInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId);

/// <summary>The output of <c>check_iteration</c>: the tour's available iteration after the check.</summary>
/// <param name="TourId">The tour's ID.</param>
/// <param name="UpdatedAt">The tour's update time after the check.</param>
/// <param name="AvailableIteration">The tour's available iteration, or null when the pull request has no iteration with another head as far as lotsen knows.</param>
/// <param name="Failure">Why the check could not finish, written for the reviewer, or null when it finished. The available iteration is then the one an earlier check found. JSON leaves it out when null.</param>
internal sealed record CheckedIteration(
   TourId TourId,
   DateTimeOffset UpdatedAt,
   AvailableIteration? AvailableIteration,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Failure = null);

/// <summary><c>check_iteration</c>: the reviewer's check of whether a pinned tour's pull request has a new iteration, which runs now, as <see cref="IterationScheduler.CheckNowAsync"/> runs it.</summary>
internal sealed class IterationChecks
{
   private readonly TourWriter _writer;
   private readonly IterationScheduler _scheduler;

   public IterationChecks(TourWriter writer, IterationScheduler scheduler)
   {
      _writer = writer;
      _scheduler = scheduler;
   }

   /// <summary>Checks the tour's pull request for a new iteration and returns the tour's available iteration after the check.</summary>
   /// <remarks>A tour that is not pinned is refused.</remarks>
   public async Task<OperationResult<CheckedIteration>> CheckIterationAsync(CheckIterationInput input, CancellationToken cancellationToken)
   {
      var read = _writer.Read(input.TourId);
      if (read.Value is not { } tour)
      {
         return read.Error!;
      }

      if (tour.Pin is not TourPin.Pinned)
      {
         return OperationError.NoIterationToCheck(tour);
      }

      var run = await _scheduler.CheckNowAsync(tour.Id, cancellationToken);
      return run.Tour is { } latest ? new CheckedIteration(latest.Id, latest.UpdatedAt, latest.AvailableIteration, run.Failure) : OperationError.TourNotFound(input.TourId.Value);
   }
}
