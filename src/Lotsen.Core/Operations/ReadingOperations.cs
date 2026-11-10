using System.ComponentModel;
using System.Text.Json.Serialization;
using Lotsen.Platforms;
using Lotsen.Prose;
using Lotsen.Sessions;
using Lotsen.Tours;

namespace Lotsen.Operations;

/// <summary>The input of <c>record_reading_progress</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RecordReadingProgressInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId,
   [property: Description("The ID of the stop of the tour's plan that the reviewer opened. Omit it when the reviewer opened the summary page.")]
   string? StopId = null,
   [property: Description("true when the reviewer opened the summary page. Omit it when the reviewer opened a stop.")]
   bool Summary = false,
   [property: Description("The number of the stop's current version when the reviewer sees it. Omit it while the page shows an earlier version.")]
   int? Version = null);

/// <summary>The input of <c>set_tour_archived</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SetTourArchivedInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId,
   [property: Description("true to archive the tour, false to unarchive it.")]
   bool Archived);

/// <summary>A tour's state and pull request state after a write, with the tour's update time after that write, so the UI orders them against the events and loads in flight.</summary>
/// <param name="TourId">The tour.</param>
/// <param name="UpdatedAt">The tour's update time after the write, or the tour's current update time when the write changed nothing.</param>
/// <param name="State">The tour's state, as <see cref="TourStates.Of"/> derives it.</param>
/// <param name="PullRequestState">The pull request's state, as <see cref="Tour.PullRequestState"/> holds it.</param>
internal sealed record UpdatedTourState(TourId TourId, DateTimeOffset UpdatedAt, TourState State, PullRequestState PullRequestState);

/// <summary>The reviewer's reading progress on a tour as one write stored it, with the tour's state, pull request state and update time after that write.</summary>
/// <param name="TourId">The tour.</param>
/// <param name="UpdatedAt">The tour's update time after the write, or the tour's current update time when the write changed nothing.</param>
/// <param name="State">The tour's state, as <see cref="TourStates.Of"/> derives it.</param>
/// <param name="PullRequestState">The pull request's state, as <see cref="Tour.PullRequestState"/> holds it.</param>
/// <param name="Progress">The reading progress as stored.</param>
/// <param name="SeenVersion">The <see cref="Stop.Written.SeenVersion"/> of the stop that the write named, or null when it has none or the write named the summary page. JSON leaves it out when null.</param>
internal sealed record RecordedProgress(
   TourId TourId,
   DateTimeOffset UpdatedAt,
   TourState State,
   PullRequestState PullRequestState,
   ReadingProgress Progress,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? SeenVersion = null);

/// <summary>The payload of the <see cref="ServerEvents.SeenVersionUpdated"/> event.</summary>
/// <param name="TourId">The tour.</param>
/// <param name="UpdatedAt">The tour's update time after the write.</param>
/// <param name="StopId">The stop.</param>
/// <param name="SeenVersion">The stop's <see cref="Stop.Written.SeenVersion"/> as stored.</param>
internal sealed record UpdatedSeenVersion(TourId TourId, DateTimeOffset UpdatedAt, string StopId, int SeenVersion);

/// <summary>The input of <c>write_synthesis</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record WriteSynthesisInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId,
   [property: Description("The synthesis in Markdown, which may link to code as [label](ref:<side>/<path>#L<start>-<end>).")]
   string Text,
   [property: Description("The summaryFingerprint of the review summary that the synthesis is based on, as read_summary returned it before you wrote the synthesis.")]
   string BasedOn);

/// <summary>The output of <c>write_synthesis</c>, and the payload of the <see cref="ServerEvents.SynthesisWritten"/> event.</summary>
/// <param name="TourId">The tour whose synthesis was written.</param>
/// <param name="UpdatedAt">The tour's update time after the write.</param>
/// <param name="WrittenAt">When the synthesis was written, which the review summary returns with it.</param>
internal sealed record WrittenSynthesis(TourId TourId, DateTimeOffset UpdatedAt, DateTimeOffset WrittenAt);

/// <summary>
/// The reviewer's reading state and the review's outcome: <c>record_reading_progress</c> and <c>set_tour_archived</c>, from which <see cref="TourStates"/> derives a tour's state,
/// <c>read_summary</c>, and the bound session's <c>write_synthesis</c>.
/// </summary>
internal sealed class ReadingOperations
{
   private readonly TourWriter _writer;
   private readonly FileOperations _files;

   public ReadingOperations(TourWriter writer, FileOperations files)
   {
      _writer = writer;
      _files = files;
   }

   /// <summary>Records the stop or the summary page that the reviewer opened last, and returns the stored progress with the tour's state.</summary>
   /// <remarks>
   /// <para>
   /// The tour needs no bound session, and nothing reaches any session. Opening the summary page makes the tour finished for good, and a stop opened later only moves the last place. The write finds
   /// the stop in the latest plan, so a <c>set_plan</c> that drops it first refuses the write.
   /// </para>
   /// <para>
   /// The version in the input joins the stop's seen version as <see cref="Stop.Written.Seen"/> says, and a change publishes <see cref="ServerEvents.SeenVersionUpdated"/>. A version on a pending
   /// stop or on the summary page records nothing.
   /// </para>
   /// <para>
   /// A write moves the tour's update time, as every tour write does: a load or an event that carries the later time holds every change stored before it, so nothing that the UI orders by
   /// that time hides an earlier change. A write that changes neither the place nor the seen version stores nothing and keeps the update time, so a reviewer who opens the same stop again causes no
   /// write.
   /// </para>
   /// </remarks>
   public OperationResult<RecordedProgress> RecordReadingProgress(Access.Reviewer.Pass pass, RecordReadingProgressInput input) =>
      _writer.Write(pass, input.TourId, edit =>
      {
         var latest = edit.Latest;
         if (input.StopId is { } stopId && latest.Plan?.Stops.Any(stop => stop.Id == stopId) != true)
         {
            return OperationError.StopNotShown(stopId);
         }

         var opened = input.StopId is { } id ? latest.FindStop(id) as Stop.Written : null;
         var seen = opened is not null && input.Version is { } version ? opened.Seen(version) : opened;
         var progress = new ReadingProgress(input.Summary || latest.Progress?.Finished == true, input.StopId);
         var placed = progress == latest.Progress ? latest : latest with { Progress = progress };
         var seenChanged = !ReferenceEquals(seen, opened);
         var next = seenChanged ? placed with { Plan = placed.Plan! with { Stops = [.. placed.Plan!.Stops.Select(stop => stop.Id == seen!.Id ? seen : stop)] } } : placed;
         return edit.Store(next, stored =>
         {
            if (seenChanged)
            {
               stored.Publish(ServerEvents.ForSeenVersionUpdated(new UpdatedSeenVersion(stored.Tour.Id, stored.Tour.UpdatedAt, seen!.Id, seen.SeenVersion!.Value)));
            }

            return new RecordedProgress(stored.Tour.Id, stored.Tour.UpdatedAt, TourStates.Of(stored.Tour), stored.Tour.PullRequestState, stored.Tour.Progress!, seen?.SeenVersion);
         });
      }, invalidInput: (input.StopId is null) != input.Summary
         ? OperationError.Invalid("stopId", "give the ID of the stop that the reviewer opened, or summary true for the summary page, but not both.")
         : null);

   /// <summary>Archives the tour or unarchives it, and returns the tour's state.</summary>
   /// <remarks>
   /// The tour needs no bound session, and nothing reaches any session. An unarchived tour has the state that its plan and reading progress give. A tour already in the state stays as it is, and the
   /// server publishes no event for it.
   /// </remarks>
   public OperationResult<UpdatedTourState> SetTourArchived(Access.Reviewer.Pass pass, SetTourArchivedInput input) =>
      _writer.Write(pass, input.TourId, edit => edit.Store(
         edit.Latest.Archived == input.Archived ? edit.Latest : edit.Latest with { Archived = input.Archived },
         stored => new UpdatedTourState(stored.Tour.Id, stored.Tour.UpdatedAt, TourStates.Of(stored.Tour), stored.Tour.PullRequestState)));

   /// <summary>
   /// Returns the review summary of the tour as the viewer reads it, built from <see cref="AgentView.For"/>, so an agent session's summary holds no draft answer and no note. Every viewer gets the
   /// fingerprint and the follow-up reasons of <see cref="AgentView.Summary"/> with it. It holds the tour's synthesis, which predates the summary when the agent fingerprint differs from the one that
   /// the synthesis is based on.
   /// </summary>
   public OperationResult<ReviewSummary> ReadSummary(Viewer viewer, ReadSummaryInput input)
   {
      var read = _writer.Read(input.TourId);
      if (read.Value is not { } tour)
      {
         return read.Error!;
      }

      var agentSummary = AgentView.Summary(tour);
      var fingerprint = agentSummary.Fingerprint(tour.Pin);
      var summary = ReviewSummary.Of(AgentView.For(viewer, tour), tour) with { SummaryFingerprint = fingerprint, FollowUps = SummaryFollowUps.Of(agentSummary) };
      return tour.Synthesis is not { } synthesis
         ? summary
         : summary with { Synthesis = new SummarySynthesis(synthesis.Text, synthesis.WrittenAt, synthesis.Pin, synthesis.BasedOn != fingerprint) };
   }

   /// <summary>Stores the calling session's synthesis of the review on the tour, in place of the last one, and returns its write time with the tour's update time.</summary>
   /// <remarks>
   /// Only the bound session writes it, and only on a pinned tour, whose pin the synthesis keeps. The text's <c>ref:</c> links pass the checks of a reply. The synthesis keeps the fingerprint of
   /// the summary that the session read before it wrote the synthesis, from which <see cref="ReadSummary"/> tells whether it predates a later summary. The session passes the fingerprint, because
   /// the reviewer can change the review while the session writes.
   /// </remarks>
   public Task<OperationResult<WrittenSynthesis>> WriteSynthesisAsync(Access.BoundSession.Pass pass, WriteSynthesisInput input, CancellationToken cancellationToken) =>
      _writer.WriteAfterGitAsync<IReadOnlyList<ProseReference>, WrittenSynthesis>(
         pass,
         input.TourId,
         CheckSynthesis(input),
         async (_, git, token) =>
         {
            var links = await ProseLinks.CheckAsync(git, _files, input.Text, token);
            return links.Value is { } references ? Gathered.Of(references) : links.Error!;
         },
         (edit, _) => edit.Store(
            edit.Latest with { Synthesis = new TourSynthesis(input.Text, edit.Now, (TourPin.Pinned)edit.Latest.Pin, Fingerprint(input.BasedOn)) },
            stored =>
            {
               var written = new WrittenSynthesis(stored.Tour.Id, stored.Tour.UpdatedAt, stored.Tour.Synthesis!.WrittenAt);
               stored.Publish(ServerEvents.ForSynthesisWritten(written));
               return written;
            }),
         cancellationToken);

   // Call it on an input that CheckSynthesis accepts.
   private static SummaryFingerprint Fingerprint(string text) => SummaryFingerprint.TryParse(text, out var fingerprint) ? fingerprint : throw new ArgumentException("The text is not a fingerprint.", nameof(text));

   private static Func<Tour, OperationError?> CheckSynthesis(WriteSynthesisInput input) => tour =>
   {
      if (string.IsNullOrWhiteSpace(input.Text))
      {
         return OperationError.Invalid("text", "a synthesis needs text. Write what the review decided, what worries the reviewer and what stays open.");
      }

      if (!SummaryFingerprint.TryParse(input.BasedOn, out _))
      {
         return OperationError.Invalid("basedOn", "this is not a summaryFingerprint. Pass the summaryFingerprint that read_summary returned with the summary that you based the synthesis on.");
      }

      return tour.Pin is TourPin.Pinned ? null : OperationError.TourNotPinned(tour);
   };
}
