using System.Text.Json.Serialization;

namespace Lotsen.Tours;

/// <summary>
/// The content of a tour's file, <c>tours/&lt;id&gt;.json</c>: the tour, the reviewer's review marks and the version of the file's layout.
/// </summary>
/// <remarks>
/// <para>
/// A document is the only thing that <see cref="ITourStore"/> writes, and only JSON, <see cref="New"/>, <see cref="StoredTour.Next"/> and <see cref="StoredTour.WithMarks"/> build one. The last
/// three run <see cref="TourRules"/> and <see cref="ReviewMarkRules"/>, so no path stores a tour that breaks a rule. A document that JSON built holds the file as read, unchecked, until
/// <see cref="Decode"/> checks it.
/// </para>
/// <para>
/// The review marks sit beside the tour, not in it, so no output that holds the tour shows them to Claude.
/// </para>
/// </remarks>
internal sealed class TourDocument
{
   /// <summary>The layout version of every file written. Change it when the layout changes incompatibly.</summary>
   public const int CurrentSchemaVersion = 22;

   /// <param name="schemaVersion">The version of the file's layout.</param>
   /// <param name="tour">The tour.</param>
   /// <param name="reviewMarks">The review marks.</param>
   [JsonConstructor]
   private TourDocument(int schemaVersion, Tour tour, IReadOnlyList<ReviewMark> reviewMarks)
   {
      SchemaVersion = schemaVersion;
      Tour = tour;
      ReviewMarks = reviewMarks;
   }

   /// <summary>The version of the file's layout, <see cref="CurrentSchemaVersion"/> for every file written.</summary>
   public int SchemaVersion { get; }

   /// <summary>The tour as the file holds it, which <see cref="Decode"/> checks.</summary>
   public Tour Tour { get; }

   /// <summary>The review marks as the file holds them, which <see cref="Decode"/> checks.</summary>
   public IReadOnlyList<ReviewMark> ReviewMarks { get; }

   /// <summary>Returns the document of a new tour with this ID, no plan and its pin in progress.</summary>
   /// <param name="id">The new tour's ID.</param>
   /// <param name="target">What the tour reviews.</param>
   /// <param name="familiarity">How well the reviewer knows the code.</param>
   /// <param name="planApproval">Whether the plan waits for the reviewer's approval: <see cref="PlanApproval.Awaited"/> for a plan-first tour, or <see cref="PlanApproval.NotNeeded"/>.</param>
   /// <param name="now">The tour's creation time, which is also its update time.</param>
   public static Checked<StoredTour> New(TourId id, TourTarget target, Familiarity familiarity, PlanApproval planApproval, DateTimeOffset now)
   {
      var tour = new Tour(id, target, familiarity, now, now, new TourPin.Pinning(), Plan: null, Threads: TourThreads.Empty, Answers: [], planApproval);
      return StoredTour.Checked(tour, []);
   }

   /// <summary>Returns the tour and the review marks that the document holds, after the one check on the way in: the schema version, the tour's ID, and the rules.</summary>
   /// <param name="expected">The ID that the file's name gives.</param>
   /// <returns>The stored tour, or the first fault, with its path in the file.</returns>
   public Checked<StoredTour> Decode(TourId expected)
   {
      if (SchemaVersion != CurrentSchemaVersion)
      {
         return new RuleFault("schemaVersion", $"the file has the layout of schema version {SchemaVersion}, and this build reads version {CurrentSchemaVersion}.");
      }

      if (Tour.Id != expected)
      {
         return new RuleFault(((FieldPath)"tour").Then("id"), $"the file of tour {expected} holds tour {Tour.Id}.");
      }

      if ((TourRules.Check(Tour)?.Under("tour") ?? ReviewMarkRules.Check(ReviewMarks)) is { } fault)
      {
         return fault;
      }

      return new StoredTour(Tour, ReviewMarks, this);
   }

   /// <summary>A tour and its review marks that follow every rule, with the document that holds them.</summary>
   /// <remarks>It is nested in the document, so it alone reaches the document's constructor.</remarks>
   internal sealed class StoredTour
   {
      internal StoredTour(Tour tour, IReadOnlyList<ReviewMark> reviewMarks, TourDocument document)
      {
         Tour = tour;
         ReviewMarks = reviewMarks;
         Document = document;
      }

      /// <summary>The tour.</summary>
      public Tour Tour { get; }

      /// <summary>The review marks, each on a different path.</summary>
      public IReadOnlyList<ReviewMark> ReviewMarks { get; }

      /// <summary>The document that holds the tour and the marks.</summary>
      public TourDocument Document { get; }

      /// <summary>
      /// Returns the next stored tour, after the one check on the way out: the next tour keeps the ID and the creation time, takes the update time and the next revision, and follows the rules.
      /// </summary>
      /// <param name="next">The tour that a write built from <see cref="Tour"/>.</param>
      /// <param name="now">The time of the write, which becomes the tour's update time.</param>
      /// <returns>The stored tour with the same review marks, or the first fault, with its path in the tour.</returns>
      /// <exception cref="InvalidOperationException">
      /// The next tour is an agent view, which <see cref="AgentView.Of(Tour)"/> built. A view lacks the reviewer's draft answers and notes, so storing it would delete them.
      /// </exception>
      public Checked<StoredTour> Next(Tour next, DateTimeOffset now)
      {
         if (next.IsAgentView)
         {
            throw new InvalidOperationException($"A write of tour {Tour.Id} stores an agent view, which lacks the reviewer's draft answers and notes.");
         }

         if (next.Id != Tour.Id)
         {
            return new RuleFault("id", $"a write of tour {Tour.Id} keeps its ID, but the next tour has the ID {next.Id}.");
         }

         if (next.CreatedAt != Tour.CreatedAt)
         {
            return new RuleFault("createdAt", "a write keeps the tour's creation time.");
         }

         return Checked(next with { UpdatedAt = now, Revision = Tour.Revision + 1 }, ReviewMarks);
      }

      /// <summary>Returns the stored tour with the next revision and nothing else changed, for a change of the tour's binding, which the tour does not hold.</summary>
      public StoredTour Bumped()
      {
         var bumped = Tour with { Revision = Tour.Revision + 1 };
         return new StoredTour(bumped, ReviewMarks, new TourDocument(CurrentSchemaVersion, bumped, ReviewMarks));
      }

      /// <summary>Returns the stored tour with these review marks, after <see cref="ReviewMarkRules"/>. The tour and its update time stay as they are.</summary>
      /// <returns>The stored tour, or the first fault, with its path in the file.</returns>
      public Checked<StoredTour> WithMarks(IReadOnlyList<ReviewMark> marks) =>
         ReviewMarkRules.Check(marks) is { } fault ? fault : new StoredTour(Tour, marks, new TourDocument(CurrentSchemaVersion, Tour, marks));

      internal static Checked<StoredTour> Checked(Tour tour, IReadOnlyList<ReviewMark> marks) =>
         TourRules.Check(tour) is { } fault ? fault : new StoredTour(tour, marks, new TourDocument(CurrentSchemaVersion, tour, marks));
   }
}

/// <summary>A value that passed its checks, or the first fault that it broke.</summary>
internal sealed class Checked<T>
   where T : class
{
   private Checked(T? value, RuleFault? fault)
   {
      Value = value;
      Fault = fault;
   }

   /// <summary>The value, or null when it broke a rule.</summary>
   public T? Value { get; }

   /// <summary>The broken rule, or null when the value passed.</summary>
   public RuleFault? Fault { get; }

   public static implicit operator Checked<T>(T value) => new(value, null);

   public static implicit operator Checked<T>(RuleFault fault) => new(null, fault);
}
