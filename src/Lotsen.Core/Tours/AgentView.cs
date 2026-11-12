using Lotsen.Sessions;

namespace Lotsen.Tours;

/// <summary>Who reads a tour, which decides what the read shows, and never what the reader may do.</summary>
/// <remarks>An <c>Access.Open</c> pass carries one, from the caller that the endpoint admitted.</remarks>
internal closed record Viewer
{
   private Viewer()
   {
   }

   /// <summary>The reviewer, through the UI.</summary>
   internal sealed record Reviewer : Viewer;

   /// <summary>One agent session.</summary>
   /// <param name="Recipient">The session.</param>
   internal sealed record Session(Recipient Recipient) : Viewer;

   /// <summary>The CLI, with the server token alone.</summary>
   internal sealed record Cli : Viewer;
}

/// <summary>Returns what every agent session sees of a tour, whoever the caller is.</summary>
internal static class AgentView
{
   /// <summary>Returns the tour as the viewer reads it: for the reviewer as stored, and for every other viewer as <see cref="Of(Tour)"/> shows it.</summary>
   /// <remarks>No tour holds its binding, which only the tour list, the binding events and <c>ack</c> serve.</remarks>
   public static Tour For(Viewer viewer, Tour tour) => viewer is Viewer.Reviewer ? tour : Of(tour);

   /// <summary>Returns the tour without its notes, without the answers that <see cref="CheckAnswer.ReachesAgents"/> hides from agents, and with each stop as <see cref="Of(Stop)"/> shows it.</summary>
   /// <remarks>
   /// A note and a draft answer are the reviewer's own until the reviewer sends them. Claude wrote the earlier versions, and each stop's version number says that they exist. It flags the tour as an
   /// agent view, which no write stores, as <see cref="TourDocument.StoredTour.Next"/> says.
   /// </remarks>
   public static Tour Of(Tour tour) => tour.AsAgentView() with
   {
      Threads = tour.Threads.WithoutNotes(),
      Answers = [.. tour.Answers.Where(answer => CheckAnswer.ReachesAgents(answer.State))],
      Plan = tour.Plan is { } plan ? plan with { Stops = [.. plan.Stops.Select(Of)] } : null,
   };

   /// <summary>Returns the stop without its earlier versions, the version that the reviewer has seen and the done mark, as every agent session sees it. It keeps its version number.</summary>
   public static Stop Of(Stop stop) => stop is Stop.Written written ? written with { Earlier = [], SeenVersion = null, DoneVersion = null } : stop;

   /// <summary>
   /// Returns the review summary that an agent session reads, so a draft answer or a note never reaches its fingerprint or a follow-up action's items. It holds no fingerprint, follow-up reasons
   /// or synthesis.
   /// </summary>
   public static ReviewSummary Summary(Tour tour) => ReviewSummary.Of(Of(tour), tour);
}
