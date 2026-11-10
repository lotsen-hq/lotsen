using Lotsen.Tours;

namespace Lotsen.Tests;

/// <summary>Reads and seeds stored tours past the writer, through the document's checks, for tests that set up a tour state that no operation reaches in one step.</summary>
internal static class StoredTours
{
   /// <summary>Returns the stored tour, or null when no tour has the ID.</summary>
   /// <exception cref="InvalidDataException">The tour's file cannot be read or breaks a rule.</exception>
   public static Tour? Get(this ITourStore store, TourId id) => Stored(id, store.Read(id))?.Tour;

   /// <summary>Returns the stored review marks, or null when no tour has the ID.</summary>
   /// <exception cref="InvalidDataException">The tour's file cannot be read or breaks a rule.</exception>
   public static IReadOnlyList<ReviewMark>? GetReviewMarks(this ITourStore store, TourId id) => Stored(id, store.Read(id))?.ReviewMarks;

   /// <summary>Stores a new tour, as a create does.</summary>
   public static Tour Create(this ITourStore store, TourTarget target, Familiarity familiarity, PlanApproval planApproval = PlanApproval.NotNeeded)
   {
      TourDocument.StoredTour? created = null;
      store.Create(id =>
      {
         created = TourDocument.New(id, target, familiarity, planApproval, DateTimeOffset.UtcNow).Value!;
         return created.Document;
      });
      return created!.Tour;
   }

   /// <summary>Applies the change to the stored tour and stores the result, with the update time now.</summary>
   /// <returns>The stored tour, or null when no tour has the ID.</returns>
   /// <exception cref="InvalidDataException">The tour's file cannot be read or breaks a rule.</exception>
   /// <exception cref="InvalidOperationException">The changed tour breaks a rule, or changes the tour's ID or creation time.</exception>
   public static Tour? Change(this ITourStore store, TourId id, Func<Tour, Tour> change)
   {
      Tour? stored = null;
      var read = store.Update(id, document =>
      {
         var loaded = Stored(id, new TourRead.Found(id, document))!;
         var changed = change(loaded.Tour);
         if (ReferenceEquals(changed, loaded.Tour))
         {
            stored = changed;
            return document;
         }

         var next = loaded.Next(changed, DateTimeOffset.UtcNow);
         stored = next.Value?.Tour ?? throw new InvalidOperationException($"The changed tour breaks a rule at {next.Fault!.Path}: {next.Fault.Problem}");
         return next.Value.Document;
      });
      return read is TourRead.Found ? stored : Stored(id, read)?.Tour;
   }

   /// <summary>Applies the change to the stored review marks and stores the result, without changing the tour.</summary>
   /// <returns>The stored marks, or null when no tour has the ID.</returns>
   /// <exception cref="InvalidDataException">The tour's file cannot be read or breaks a rule.</exception>
   /// <exception cref="InvalidOperationException">The changed marks break a rule.</exception>
   public static IReadOnlyList<ReviewMark>? ChangeMarks(this ITourStore store, TourId id, Func<IReadOnlyList<ReviewMark>, IReadOnlyList<ReviewMark>> change)
   {
      IReadOnlyList<ReviewMark>? stored = null;
      var read = store.Update(id, document =>
      {
         var loaded = Stored(id, new TourRead.Found(id, document))!;
         var changed = change(loaded.ReviewMarks);
         if (ReferenceEquals(changed, loaded.ReviewMarks))
         {
            stored = changed;
            return document;
         }

         var next = loaded.WithMarks(changed);
         stored = next.Value?.ReviewMarks ?? throw new InvalidOperationException($"The changed marks break a rule at {next.Fault!.Path}: {next.Fault.Problem}");
         return next.Value.Document;
      });
      return read is TourRead.Found ? stored : Stored(id, read)?.ReviewMarks;
   }

   private static TourDocument.StoredTour? Stored(TourId id, TourRead read) => read switch
   {
      TourRead.Missing => null,
      TourRead.Found { Document: var document } => document.Decode(id) is { Value: { } stored } ? stored : throw Broken(id, document.Decode(id).Fault!),
      TourRead.OtherSchema => throw new InvalidDataException($"Tour {id} has another schema version."),
      TourRead.Unreadable { Cause: var cause } => throw new InvalidDataException($"Tour {id} could not be read.", cause),
   };

   private static InvalidDataException Broken(TourId id, RuleFault fault) => new($"Tour {id} breaks a rule at {fault.Path}: {fault.Problem}");
}
