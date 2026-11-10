using Lotsen.Prose;
using Lotsen.Tours;

namespace Lotsen.Pinning;

/// <summary>A tour moved onto a new pin, as an iteration update stores it, computed in one pass from the tour that it moves.</summary>
/// <remarks>
/// <para>
/// A stop whose passages and code references all move takes them at their new places, without a new stop version. Every other stop with content gets a stale marker that names the new
/// iteration, and keeps its content as it was. A stop that is stale already keeps its marker and its content, because its code references read against a pin before the tour's.
/// </para>
/// <para>
/// A file mark takes its new path as <see cref="FileMarkRemap"/> says. The threads follow <see cref="ThreadFact.Repinned"/> in <see cref="TourThreads"/>: a code or file anchor that moves
/// takes its new place, and a thread whose anchor is stale keeps it on its commit and becomes outdated, notes included. A stop text thread keeps its anchor and its state, because a moved link
/// points at the same code.
/// </para>
/// <para>
/// Only <see cref="Map"/> builds one, from the remap of the tour that it receives, so a repin always holds the stops, versions, anchors and file marks of the tour that it moves.
/// </para>
/// </remarks>
internal sealed class Repin
{
   private Repin(Tour tour, IReadOnlyList<string> staleStops, ThreadFact.Repinned anchors, ReviewerDecision.IterationUpdated? decision)
   {
      Tour = tour;
      StaleStops = staleStops;
      Anchors = anchors;
      Decision = decision;
   }

   /// <summary>The tour on the new pin, with no available iteration and the stale markers set, and with the threads of the tour that it moved, which <see cref="Anchors"/> then settles.</summary>
   public Tour Tour { get; }

   /// <summary>The IDs of the stops with a stale marker, in plan order, those that were stale already included.</summary>
   public IReadOnlyList<string> StaleStops { get; }

   /// <summary>The fact that moves or outdates the threads' code and file anchors.</summary>
   public ThreadFact.Repinned Anchors { get; }

   /// <summary>The iteration update that the decided thread reports, or null when no stop is stale, so no decision goes.</summary>
   public ReviewerDecision.IterationUpdated? Decision { get; }

   /// <summary>Returns the tour moved from its pin onto a new pin, or the facts that the remap needs first.</summary>
   /// <param name="tour">The tour, pinned to <paramref name="from"/>.</param>
   /// <param name="from">The tour's pin.</param>
   /// <param name="to">The new pin.</param>
   /// <param name="facts">The git facts known so far.</param>
   public static Decision<Repin> Map(Tour tour, TourPin.Pinned from, TourPin.Pinned to, FactSet facts) => TourRemap.Map(tour, from, to, facts) switch
   {
      Decision<RemapResult>.Decided { Value: var remap } => new Decision<Repin>.Decided(Moved(tour, remap)),
      Decision<RemapResult>.NeedsFacts { Missing: var missing } => new Decision<Repin>.NeedsFacts(missing),
   };

   private static Repin Moved(Tour tour, RemapResult remap)
   {
      var mapped = remap.Stops.ToDictionary(stop => stop.StopId, StringComparer.Ordinal);
      var stale = new List<string>();
      var plan = tour.Plan is null ? null : tour.Plan with
      {
         FileMarks = [.. remap.FileMarks.Select(mark => mark.Mark with { Path = mark.Path })],
         Stops = [.. tour.Plan.Stops.Select(Stop (stop) =>
         {
            if (stop is not Stop.Written written)
            {
               return stop;
            }

            if (written.StaleOn is not null)
            {
               stale.Add(written.Id);
               return written;
            }

            if (Moved(written.Content, mapped[written.Id]) is not { } moved)
            {
               stale.Add(written.Id);
               return written with { StaleOn = remap.To.Iteration };
            }

            return written with { Content = moved };
         })],
      };

      var decision = stale.Count > 0 ? ReviewerDecision.IterationUpdated.For(remap.To.Iteration, stale) : null;
      return new Repin(tour with { Pin = remap.To, AvailableIteration = null, Plan = plan }, stale, new ThreadFact.Repinned(remap.Anchors), decision);
   }

   // Returns the content with its passages and code references at their new places, or null when one of them is stale. A link that the prose does not hold in the form that the remap read,
   // as with a backslash escape, makes the stop stale too, because its place cannot be written.
   private static StopContent? Moved(StopContent content, StopRemap remap)
   {
      if (remap.Stale)
      {
         return null;
      }

      var passages = remap.Passages.OrderBy(passage => passage.Index).Select(passage => ((Remapped<StopPassage>.Moved)passage.Outcome).To).ToList();
      var links = remap.References.ToLookup(reference => reference.Reference.Field);
      var moved = StopProse.Map(content, (field, prose) => Rewrite(prose, links[field.Path])) with { Passages = passages };

      var expected = remap.References.Select(reference =>
      {
         var place = ((Remapped<ReferencePlace>.Moved)reference.Outcome).To;
         return (reference.Reference.Field, reference.Reference.Reference.Side, place.Path, place.StartLine, place.EndLine);
      });
      var written = ProseReferences.Of(moved).Value?
         .Select(reference => (reference.Field, reference.Reference.Side, reference.Reference.Path, reference.Reference.StartLine, reference.Reference.EndLine));
      return written is not null && written.SequenceEqual(expected) ? moved : null;
   }

   // Writes each moved link of the field at its new place, as ProseRenderer.RewriteReferences writes destinations.
   private static string Rewrite(string prose, IEnumerable<ReferenceRemap> references)
   {
      var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
      foreach (var reference in references)
      {
         var (link, place) = (reference.Reference.Reference, ((Remapped<ReferencePlace>.Moved)reference.Outcome).To);
         if ((place.Path, place.StartLine, place.EndLine) != (link.Path, link.StartLine, link.EndLine))
         {
            replacements[link.Link] = CodeReference.Format(link.Side, place.Path, place.StartLine, place.EndLine);
         }
      }

      return replacements.Count == 0 ? prose : ProseRenderer.RewriteReferences(prose, link => replacements.GetValueOrDefault(link.Link));
   }
}
