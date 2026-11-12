using Lotsen.Tours;

namespace Lotsen.Operations;

/// <summary>
/// A stop written by <c>put_stop</c>, with what the write settled: the tour after the write, the stored stop, and for a revision the stop as it was, the answers it made outdated and the stop
/// text threads it changed.
/// </summary>
/// <param name="Threads">
/// The write of the tour after the stop's write, before the write sets its update time. For a revision, its changed threads are those whose state or anchor version the revision changed, in the
/// tour's order of threads, and the <c>stopRevised</c> event carries them.
/// </param>
/// <param name="Stop">The stop as written.</param>
/// <param name="Replaced">The stop as it was before the write, at the version that the write replaced, or null for the stop's first content.</param>
/// <param name="Outdated">The answers that the write made outdated, in the tour's order of answers.</param>
internal sealed record StopWrite(ThreadWrite Threads, Stop.Written Stop, Stop.Written? Replaced, IReadOnlyList<CheckAnswer> Outdated);

/// <summary>The rules by which a write of a stop's content replaces its current version and settles the answers and the threads on that version.</summary>
internal static class StopVersions
{
   /// <summary>Returns the tour with the stop's new content, as its next version, and with the answers and threads that the replaced version held settled.</summary>
   /// <remarks>
   /// <para>
   /// The replaced content joins the stop's earlier versions. An answer to a check that the new content lacks, because Claude dropped it or wrote it without its ID, becomes outdated on the
   /// replaced version, in whatever state it has. An answer to a kept check stays as it is.
   /// </para>
   /// <para>
   /// The stop text threads on the replaced version follow <see cref="ThreadFact.StopRevised"/> in <see cref="TourThreads"/>: a thread moves to the new version when its field's Markdown is the
   /// same in both, compared exactly, and otherwise its anchor stays on the replaced version and the thread becomes outdated. The write clears the stop's stale marker and keeps its skip, its seen
   /// version and its done mark, which then covers the replaced version.
   /// </para>
   /// </remarks>
   /// <param name="tour">The latest stored tour, whose plan holds the stop at <paramref name="change"/>'s index.</param>
   /// <param name="change">The plan with the input's title, kind and chapter applied to the stop.</param>
   /// <param name="content">The checked content to store.</param>
   /// <param name="now">The time of the write.</param>
   public static StopWrite Write(Tour tour, PlanChange change, StopContent content, DateTimeOffset now)
   {
      var planned = change.Stop;
      var replaced = tour.Plan!.Stops[change.Index] as Stop.Written;
      var written = replaced is null
         ? new Stop.Written(planned.Id, planned.Title, planned.Kind, planned.Chapter, content, 1, [], Skipped: planned.Skipped)
         : new Stop.Written(
            planned.Id,
            planned.Title,
            planned.Kind,
            planned.Chapter,
            content,
            replaced.Version + 1,
            [.. replaced.Earlier, new StopVersion(replaced.Title, replaced.Kind, replaced.Content, now)],
            Skipped: planned.Skipped,
            SeenVersion: replaced.SeenVersion,
            DoneVersion: replaced.DoneVersion);
      var plan = change.Plan with { Stops = [.. change.Plan.Stops.Select((stop, i) => i == change.Index ? written : stop)] };
      if (replaced is null)
      {
         return new StopWrite(TourThreads.Unchanged(tour with { Plan = plan }), written, null, []);
      }

      var kept = (content.Checks ?? []).Select(check => check.Id).ToHashSet(StringComparer.Ordinal);
      var dropped = (replaced.Content.Checks ?? []).Select(check => check.Id).Where(id => !kept.Contains(id)).ToHashSet(StringComparer.Ordinal);
      var outdated = new List<CheckAnswer>();
      var answers = tour.Answers.Select(answer =>
      {
         if (answer.StopId != replaced.Id || answer.OutdatedOn is not null || !dropped.Contains(answer.CheckId))
         {
            return answer;
         }

         var marked = answer.OutdatedAt(replaced.Version);
         outdated.Add(marked);
         return marked;
      }).ToList();
      var revised = tour with { Plan = plan, Answers = answers };
      return new StopWrite(TourThreads.Settle(revised, new ThreadFact.StopRevised(replaced, written)), written, replaced, outdated);
   }
}
