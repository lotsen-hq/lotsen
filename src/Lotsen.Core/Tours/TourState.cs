using System.Text.Json.Serialization;
using Lotsen.Json;

namespace Lotsen.Tours;

/// <summary>Where a tour is in its life, as the tour list shows it: a <see cref="TourStates.Of"/> of the tour.</summary>
[JsonConverter(typeof(StrictEnumConverter<TourState>))]
internal enum TourState
{
   /// <summary>A tour without a plan, or with a pending stop, whose reviewer has opened no stop and not the summary page.</summary>
   Generating,

   /// <summary>A tour whose every stop has content and whose reviewer has opened no stop and not the summary page.</summary>
   Ready,

   /// <summary>A tour whose reviewer has opened a stop but not the summary page. Pending stops that Claude adds later leave it in progress.</summary>
   InProgress,

   /// <summary>A tour whose reviewer has opened the summary page. A stop that the reviewer opens later, or that Claude adds, leaves it finished.</summary>
   Finished,

   /// <summary>A tour that the reviewer archived. Unarchiving it returns it to the state that its plan and reading progress give.</summary>
   Archived,
}

/// <summary>Derives a tour's <see cref="TourState"/>.</summary>
internal static class TourStates
{
   /// <summary>Returns the tour's state.</summary>
   /// <remarks>
   /// The state follows from <see cref="Tour.Archived"/>, <see cref="Tour.Progress"/> and whether the plan has a pending stop. The <c>TourWriter</c> publishes a change after each write.
   /// </remarks>
   public static TourState Of(Tour tour)
   {
      if (tour.Archived)
      {
         return TourState.Archived;
      }

      if (tour.Progress is { } progress)
      {
         return progress.Finished ? TourState.Finished : TourState.InProgress;
      }

      return tour.Plan is null || tour.Plan.Stops.Any(stop => stop is Stop.Pending) ? TourState.Generating : TourState.Ready;
   }
}

/// <summary>The reviewer's reading progress on a tour: the place that the reviewer opened last, and whether the reviewer has opened the summary page.</summary>
/// <remarks>Only the UI records it, with <c>record_reading_progress</c>. A tour without it has had no stop and no summary page opened.</remarks>
internal sealed record ReadingProgress
{
   /// <param name="finished">Whether the reviewer has opened the summary page.</param>
   /// <param name="lastStop">The ID of the stop that the reviewer opened last, or null when the summary page was the last place opened.</param>
   /// <exception cref="ArgumentException">The stop ID is empty, or the summary page was the last place opened and the tour is not finished.</exception>
   public ReadingProgress(bool finished, string? lastStop = null)
   {
      if (lastStop is null ? !finished : lastStop.Length == 0)
      {
         throw new ArgumentException(lastStop is null ? "The summary page was opened last, so the tour is finished." : "The last stop needs an ID.", nameof(lastStop));
      }

      Finished = finished;
      LastStop = lastStop;
   }

   /// <summary>Whether the reviewer has opened the summary page, which stays true once set. It is true whenever <see cref="LastStop"/> is null.</summary>
   public bool Finished { get; }

   /// <summary>
   /// The ID of the stop that the reviewer opened last, or null when the summary page was the last place opened. A <c>set_plan</c> may drop the stop from the plan, and the ID stays. JSON leaves it
   /// out when null.
   /// </summary>
   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
   public string? LastStop { get; }
}
