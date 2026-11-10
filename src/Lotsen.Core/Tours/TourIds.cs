using System.Security.Cryptography;

namespace Lotsen.Tours;

/// <summary>Where the server draws the IDs that it gives stops, checks, answer batches, threads, messages and requests: random in production, a fixed sequence in tests.</summary>
internal sealed class TourIds
{
   /// <summary>The characters of a random ID.</summary>
   public const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

   private readonly Func<ISet<string>, string> _draw;

   private TourIds(Func<ISet<string>, string> draw)
   {
      _draw = draw;
   }

   /// <summary>Random IDs of 8 characters from <see cref="Alphabet"/>.</summary>
   public static TourIds Random { get; } = new(used =>
   {
      string id;
      do
      {
         id = RandomNumberGenerator.GetString(Alphabet, 8);
      }
      while (!used.Add(id));

      return id;
   });

   /// <summary>Returns IDs that come from <paramref name="ids"/> in order, one per draw.</summary>
   /// <remarks>A draw past the last ID, or of an ID in use, throws <see cref="InvalidOperationException"/>, so a test that miscounts fails.</remarks>
   public static TourIds Sequence(params string[] ids)
   {
      var next = 0;
      return new(used =>
      {
         if (next >= ids.Length)
         {
            throw new InvalidOperationException($"The sequence of {ids.Length} IDs is exhausted.");
         }

         var id = ids[next++];
         return used.Add(id) ? id : throw new InvalidOperationException($"The ID '{id}' is in use.");
      });
   }

   /// <summary>
   /// Returns the IDs that the tour names for its stops, its checks in every version, its answer batches and its threads with their messages and requests, and the stop and check IDs that its
   /// answers name, the stop that its reading progress names and the stale stops of its available iteration.
   /// </summary>
   /// <remarks>
   /// The server draws every ID that it stores outside this set, so no two stops, checks, batches, threads, messages or requests share an ID, whatever their kinds. A new stop also takes no ID
   /// that an answer, the reading progress or an available iteration still names after a <c>set_plan</c> dropped its stop.
   /// </remarks>
   public static HashSet<string> NamedBy(Tour tour)
   {
      var ids = new HashSet<string>(StringComparer.Ordinal);
      foreach (var stop in tour.Plan?.Stops ?? [])
      {
         ids.Add(stop.Id);
         if (stop is Stop.Written written)
         {
            ids.UnionWith(written.Earlier.Select(version => version.Content).Append(written.Content).SelectMany(content => content.Checks ?? []).Select(check => check.Id));
         }
      }
      ids.UnionWith(tour.Answers.Select(answer => answer.StopId));
      ids.UnionWith(tour.Answers.Select(answer => answer.CheckId));
      ids.UnionWith(tour.AvailableIteration?.StaleStops ?? []);
      if (tour.Progress?.LastStop is { } lastStop)
      {
         ids.Add(lastStop);
      }

      ids.UnionWith(tour.Answers.Select(answer => answer.Batch?.Id).OfType<string>());
      ids.UnionWith(tour.Threads.Ids);
      return ids;
   }

   /// <summary>Returns a new ID that is not in <paramref name="used"/>, and adds it to it.</summary>
   public string Next(ISet<string> used) => _draw(used);
}
