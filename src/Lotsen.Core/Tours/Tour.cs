using System.Text.Json.Serialization;
using Lotsen.Git;
using Lotsen.Json;
using Lotsen.Platforms;

namespace Lotsen.Tours;

/// <summary>
/// A guided tour of one review target.
/// </summary>
/// <remarks>
/// A tour can be changed with <c>with</c>, so a value may break a rule. <see cref="TourDocument"/> runs <see cref="TourRules"/> on every tour that it reads or stores, so no stored tour does.
/// </remarks>
/// <param name="Id">The tour's ID, which the server generates.</param>
/// <param name="Target">What the tour reviews.</param>
/// <param name="Familiarity">How well the reviewer knows the code, which guides how much each stop explains.</param>
/// <param name="CreatedAt">When the tour was created.</param>
/// <param name="UpdatedAt">When the tour was last stored.</param>
/// <param name="Pin">How far the tour has come in pinning the commits it shows.</param>
/// <param name="Plan">The tour's chapters and stops, or null until a plan is set.</param>
/// <param name="Threads">The tour's threads, in the order they were started, which only <see cref="TourThreads"/> changes.</param>
/// <param name="Answers">The reviewer's answers to checks, at most one for each stop ID and check ID, in the order the checks were first answered. An answer stays when its check leaves the stop's content.</param>
/// <param name="PlanApproval">Whether the plan waits for the reviewer's approval before any stop is written, which <c>create_tour</c>'s plan-first option asks for. JSON leaves it out when not needed.</param>
/// <param name="AvailableIteration">The pull request's iteration with another head than the tour's pin, as the last iteration check found it, or null when none is known. Only a pinned tour holds one. JSON leaves it out when null.</param>
/// <param name="Synthesis">The bound session's last synthesis of the review, or null when none has been written. JSON leaves it out when null.</param>
/// <param name="Progress">The reviewer's reading progress, or null while the reviewer has opened no stop and not the summary page. JSON leaves it out when null.</param>
/// <param name="Archived">Whether the reviewer archived the tour, which the tour list then hides by default. JSON leaves it out when false.</param>
/// <param name="PullRequestState">
/// The pull request's state as the first pin or the last iteration check learned it, active until one learns another. A completed or abandoned pull request flags the tour, which the tour list then hides by
/// default. JSON leaves it out when active.
/// </param>
internal sealed record Tour(
   TourId Id,
   TourTarget Target,
   Familiarity Familiarity,
   DateTimeOffset CreatedAt,
   DateTimeOffset UpdatedAt,
   TourPin Pin,
   Plan? Plan,
   TourThreads Threads,
   IReadOnlyList<CheckAnswer> Answers,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] PlanApproval PlanApproval = PlanApproval.NotNeeded,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AvailableIteration? AvailableIteration = null,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TourSynthesis? Synthesis = null,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReadingProgress? Progress = null,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Archived = false,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] PullRequestState PullRequestState = PullRequestState.Active)
{
   /// <summary>The number of the tour's stores, from 1 for the new tour, which every store and every change of the tour's binding raises by one.</summary>
   /// <remarks>Every tour-scoped event and every operation's output carries the revision that it shows, so the UI keeps the value with the higher one.</remarks>
   public long Revision { get; init; } = 1;

   /// <summary>Whether the tour has a plan that waits for the reviewer's approval, so <c>put_stop</c> is refused and <c>approve_plan</c> accepted.</summary>
   [JsonIgnore]
   public bool PlanWaits => PlanApproval == PlanApproval.Awaited && Plan is not null;

   /// <summary>Whether the tour is an agent view, which <see cref="TourDocument.StoredTour.Next"/> refuses to store. JSON leaves it out, and a <c>with</c> copy keeps it.</summary>
   [JsonIgnore]
   public bool IsAgentView { get; private init; }

   /// <summary>Returns the stop of the tour's plan, or null when the plan has none with the ID.</summary>
   public Stop? FindStop(string stopId) => Plan?.Stops.FirstOrDefault(stop => stop.Id == stopId);

   /// <summary>Returns the tour flagged as an agent view.</summary>
   /// <remarks>The flag has a private <c>init</c>, so no <c>with</c> outside the tour clears it.</remarks>
   public Tour AsAgentView() => this with { IsAgentView = true };
}

/// <summary>
/// What a tour reviews. The <c>kind</c> property in JSON names the derived type.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(PullRequestTarget), "pullRequest")]
internal abstract record TourTarget;

/// <summary>A pull request on a platform as a tour's target.</summary>
internal sealed record PullRequestTarget : TourTarget
{
   /// <param name="platform">The ID of the platform that hosts the pull request, as <see cref="Platforms.IPullRequestPlatform.Id"/> names it.</param>
   /// <param name="url">The pull request's web URL, normalized by its platform's <see cref="Platforms.IPullRequestPlatform.Recognize"/>.</param>
   /// <exception cref="ArgumentException">The URL is relative, so no label, pin or platform can use it.</exception>
   public PullRequestTarget(string platform, Uri url)
   {
      Platform = platform;
      Url = url.IsAbsoluteUri ? url : throw new ArgumentException($"The pull request's URL '{url}' is relative.", nameof(url));
   }

   /// <summary>The ID of the platform that hosts the pull request, as <see cref="Platforms.IPullRequestPlatform.Id"/> names it.</summary>
   public string Platform { get; }

   /// <summary>The pull request's web URL, absolute and normalized by its platform's <see cref="Platforms.IPullRequestPlatform.Recognize"/>.</summary>
   public Uri Url { get; }
}

/// <summary>The pin state of a tour: whether the tour is pinned to the commits of its pull request. The <c>state</c> property in JSON names the derived type.</summary>
/// <remarks>
/// A pinned tour shows its pull request as the diff from the merge base to the head, and every later view and check reads those two commits.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "state")]
[JsonDerivedType(typeof(Pinning), "pinning")]
[JsonDerivedType(typeof(Pinned), "pinned")]
[JsonDerivedType(typeof(Failed), "failed")]
internal abstract record TourPin
{
   private TourPin()
   {
   }

   /// <summary>A pin in progress, which reads the pull request and syncs its repository in the background.</summary>
   internal sealed record Pinning : TourPin;

   /// <summary>
   /// The commits that a tour is pinned to.
   /// </summary>
   /// <param name="MergeBase">The best common ancestor of the head and the target branch's head in the iteration.</param>
   /// <param name="Head">The head of the source branch in the iteration.</param>
   /// <param name="Iteration">The number of the pull request's iteration that the commits come from, from 1.</param>
   /// <param name="RemoteUrl">The URL of the repository that the commits were fetched from, without user information. The clone cache keeps the tour's clone under this URL.</param>
   internal sealed record Pinned(CommitId MergeBase, CommitId Head, int Iteration, Uri RemoteUrl) : TourPin
   {
      /// <summary>Returns the commit that a side names: the merge base for the base, the head for the head.</summary>
      public CommitId CommitOf(FileSide side) => side == FileSide.Base ? MergeBase : Head;
   }

   /// <summary>
   /// A pin that failed.
   /// </summary>
   /// <param name="Reason">What went wrong, written for the reviewer and Claude. It names the fix where there is one.</param>
   internal sealed record Failed(string Reason) : TourPin;
}

/// <summary>
/// The pull request's iteration with another head than a pinned tour's pin, which the reviewer can update the tour to. The tour stays on its pin until then.
/// </summary>
/// <remarks>It names no pin to compare with: it holds only while the tour is pinned, and the iteration update that changes the pin clears it in the same store.</remarks>
/// <param name="Pin">The pin that the tour would have on the iteration: its merge base, head, number and remote URL.</param>
/// <param name="StaleStops">The IDs of the stops that an update to <see cref="Pin"/> marks stale, in plan order, as the plan stood when the check ran. Stops that are stale already count too.</param>
/// <param name="FoundAt">When a check found this pin with these stale stops. A later check that finds the same keeps it.</param>
internal sealed record AvailableIteration(TourPin.Pinned Pin, IReadOnlyList<string> StaleStops, DateTimeOffset FoundAt)
{
   /// <summary>Returns whether the other holds the same pin and stale stops, whenever it was found.</summary>
   public bool HasSameResult(AvailableIteration other) => Pin == other.Pin && StaleStops.SequenceEqual(other.StaleStops, StringComparer.Ordinal);
}

/// <summary>The bound session's synthesis of the review: what the review decided, what worries the reviewer and what stays open, which <c>write_synthesis</c> stores.</summary>
internal sealed record TourSynthesis
{
   /// <param name="text">The synthesis as prose, whose <c>ref:</c> links read at <paramref name="pin"/>.</param>
   /// <param name="writtenAt">When the session wrote it.</param>
   /// <param name="pin">The tour's pin when the session wrote it, which its <c>ref:</c> links were checked against.</param>
   /// <param name="basedOn">The fingerprint of the review summary that the session read before it wrote the synthesis, as the session passed it.</param>
   /// <exception cref="ArgumentException">The text is empty or whitespace, the write time is missing, or the fingerprint is not one.</exception>
   public TourSynthesis(string text, DateTimeOffset writtenAt, TourPin.Pinned pin, SummaryFingerprint basedOn)
   {
      if (string.IsNullOrWhiteSpace(text) || writtenAt == default || !basedOn.IsValid)
      {
         throw new ArgumentException("A synthesis needs text, its write time and the fingerprint of the summary that it is based on.", nameof(text));
      }

      Text = text;
      WrittenAt = writtenAt;
      Pin = pin;
      BasedOn = basedOn;
   }

   /// <summary>The synthesis as prose, whose <c>ref:</c> links read at <see cref="Pin"/>.</summary>
   public string Text { get; }

   /// <summary>When the session wrote it.</summary>
   public DateTimeOffset WrittenAt { get; }

   /// <summary>The tour's pin when the session wrote it, which its <c>ref:</c> links were checked against.</summary>
   public TourPin.Pinned Pin { get; }

   /// <summary>The <see cref="ReviewSummary.Fingerprint"/> of the review summary that the session read before it wrote the synthesis, as the session passed it.</summary>
   public SummaryFingerprint BasedOn { get; }
}

/// <summary>Whether a tour's plan waits for the reviewer's approval before any stop is written.</summary>
[JsonConverter(typeof(StrictEnumConverter<PlanApproval>))]
internal enum PlanApproval
{
   /// <summary>A tour started without plan first, whose plan never waits.</summary>
   NotNeeded,

   /// <summary>A plan-first tour whose plan the reviewer has not approved yet, so <c>put_stop</c> is refused.</summary>
   Awaited,

   /// <summary>A plan-first tour whose plan the reviewer approved, so stops can be written.</summary>
   Given,
}

/// <summary>How well the reviewer knows the code under review.</summary>
[JsonConverter(typeof(StrictEnumConverter<Familiarity>))]
internal enum Familiarity
{
   /// <summary>A reviewer who knows the code well and wants the why of the change.</summary>
   Familiar,

   /// <summary>A reviewer who knows parts of the code.</summary>
   Some,

   /// <summary>A reviewer new to the code, who needs its context and conventions.</summary>
   New,
}

/// <summary>
/// The outline of a tour: its chapters and stops, each in tour order, and its file marks.
/// </summary>
/// <param name="Chapters">The chapters in tour order.</param>
/// <param name="Stops">The stops in tour order.</param>
/// <param name="FileMarks">The file marks in the order Claude gave them, each on a different path, or an empty list for none.</param>
/// <remarks><see cref="PlanRules"/> holds the rules that every stored plan follows.</remarks>
internal sealed record Plan(IReadOnlyList<Chapter> Chapters, IReadOnlyList<Stop> Stops, IReadOnlyList<FileMark> FileMarks)
{
   /// <summary>The IDs of the stops that the reviewer skipped, in plan order.</summary>
   [JsonIgnore]
   public IReadOnlyList<string> SkippedIds => [.. Stops.Where(stop => stop.Skipped).Select(stop => stop.Id)];
}

/// <summary>
/// Claude's mark on a file whose change needs little of the reviewer's attention, with the reason. The viewer collapses a changed file that has a mark.
/// </summary>
/// <remarks>
/// The server stores the mark as Claude wrote it. It does not check that the pull request changes the file, because a plan may be set before the pin ends.
/// </remarks>
/// <param name="Path">The file's repository path, as the changed-file list names the file: a renamed file by its path at the head.</param>
/// <param name="Mark">What kind of change the file has.</param>
/// <param name="Reason">One line of plain text that says why the file has the mark.</param>
internal sealed record FileMark(string Path, FileMarkKind Mark, string Reason);

/// <summary>What kind of change a marked file has.</summary>
[JsonConverter(typeof(StrictEnumConverter<FileMarkKind>))]
internal enum FileMarkKind
{
   /// <summary>A change that follows from another change and adds no logic of its own, such as a renamed namespace or an updated import.</summary>
   Mechanical,

   /// <summary>A file that a tool writes, such as a lock file or generated client code.</summary>
   Generated,
}

/// <summary>
/// The reviewer's mark that a changed file is reviewed, with the version of the file that the reviewer saw. Claude neither reads nor writes review marks.
/// </summary>
/// <remarks>
/// A mark counts only while the file at the pinned head still has that version, as <c>FileOperations.ListChangedFilesAsync</c> checks.
/// </remarks>
/// <param name="Path">The file's repository path, as the changed-file list names the file: a renamed file by its path at the head.</param>
/// <param name="HeadObjectId">The object ID of the version of the file that the reviewer saw, as <see cref="Git.FileChange.HeadObjectId"/> in the changed-file list gave it, or null for a deleted file.</param>
internal sealed record ReviewMark(string Path, string? HeadObjectId);

/// <summary>
/// A named group of consecutive stops.
/// </summary>
/// <param name="Id">The chapter's ID, unique within the plan, which stops refer to.</param>
/// <param name="Title">The chapter's title.</param>
internal sealed record Chapter(string Id, string Title);

/// <summary>
/// One step of a tour, as the plan announces it: a pending stop without content, or a written stop with its content and its earlier versions. The <c>stage</c> property in JSON names the
/// derived type.
/// </summary>
/// <remarks>
/// A stop owns what belongs to it, so a <c>set_plan</c> that drops a stop drops its content, its earlier versions, its skip and its done mark with it. Only <c>put_stop</c> turns a pending stop into a
/// written one, and it changes the kind of a written stop, because it checks the content against the kind. A stop can be changed with <c>with</c>, and <see cref="TourRules"/> checks every stored one:
/// a written stop's content and each earlier version follow <see cref="StopContentRules"/> under their kinds, and its version counts its earlier versions.
/// </remarks>
/// <param name="Id">The stop's ID, unique within the plan and stable across revisions.</param>
/// <param name="Title">The stop's title.</param>
/// <param name="Kind">What the stop does for the reviewer.</param>
/// <param name="Chapter">The <see cref="Lotsen.Tours.Chapter.Id"/> of the chapter the stop belongs to.</param>
/// <param name="Skipped">
/// Whether the reviewer skipped the stop: the reviewer's reading state, which only the UI sets. The reviewer passes over the stop, can open it all the same, and can take the skip back. A skip
/// is not private, so an agent session's read of the tour shows it. JSON leaves it out when false.
/// </param>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "stage")]
[JsonDerivedType(typeof(Pending), "pending")]
[JsonDerivedType(typeof(Written), "written")]
internal closed record Stop(string Id, string Title, StopKind Kind, string Chapter, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Skipped = false)
{
   /// <summary>How far the stop's content has come: pending, ready at version 1, revised from version 2. A stale marker does not change it.</summary>
   /// <remarks>JSON writes it and ignores it on a read, so no stored tour holds a status that disagrees with its stage and version.</remarks>
   public StopStatus Status => this switch
   {
      Pending => StopStatus.Pending,
      Written { Version: 1 } => StopStatus.Ready,
      Written => StopStatus.Revised,
   };

   /// <summary>A stop that the plan announces and that has no content yet.</summary>
   /// <param name="AnchorHint">Where the stop is expected to point, or null when the plan does not say.</param>
   internal sealed record Pending(string Id, string Title, StopKind Kind, string Chapter, AnchorHint? AnchorHint = null, bool Skipped = false) : Stop(Id, Title, Kind, Chapter, Skipped);

   /// <summary>A stop with content, which points where its passages point, so it has no anchor hint.</summary>
   /// <param name="Content">What the stop says and the passages it points at.</param>
   /// <param name="Version">The number of the content's version, from 1. The stop holds the versions before it, so <see cref="Earlier"/> holds <c>Version - 1</c> of them.</param>
   /// <param name="Earlier">
   /// The content's earlier versions, oldest first, so a version's number is its index plus 1. An agent session's read of the tour leaves them out and keeps <see cref="Version"/>.
   /// </param>
   /// <param name="StaleOn">
   /// The number of the iteration whose update made the content stale, or null when the content is not stale. The content's passages and code references then stay on the pin before that update
   /// until <c>put_stop</c> writes the stop again, which clears the marker. JSON leaves it out when null.
   /// </param>
   /// <param name="SeenVersion">
   /// The newest version of the content that the reviewer has seen, as <see cref="Seen"/> records it, or null before the reviewer opened one: the reviewer's own reading state, so an agent session's
   /// read of the tour leaves it out. JSON leaves it out when null.
   /// </param>
   /// <param name="DoneVersion">
   /// The version that the reviewer marked done, or null when the stop has no done mark: the reviewer's own reading state, which only the UI sets, so an agent session's read of the tour leaves it
   /// out. The stop is done when the mark equals <see cref="Version"/>, and done on an earlier version when the mark is lower, so a revision leaves the mark on the replaced version. JSON leaves
   /// it out when null.
   /// </param>
   internal sealed record Written(
      string Id,
      string Title,
      StopKind Kind,
      string Chapter,
      StopContent Content,
      int Version,
      IReadOnlyList<StopVersion> Earlier,
      [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? StaleOn = null,
      bool Skipped = false,
      [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? SeenVersion = null,
      [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? DoneVersion = null) : Stop(Id, Title, Kind, Chapter, Skipped)
   {
      /// <summary>Returns the stop with this version recorded as seen, or this same stop when the version is not one of the stop's or the reviewer has seen a version as new.</summary>
      public Written Seen(int version) => version <= Version && version > (SeenVersion ?? 0) ? this with { SeenVersion = version } : this;

      /// <summary>Returns the content of the version with this number, the current one or an earlier one, or null when the stop has no such version.</summary>
      public StopContent? ContentOf(int version) => version == Version ? Content : version >= 1 && version <= Earlier.Count ? Earlier[version - 1].Content : null;
   }
}

/// <summary>A stop's content as a later <c>put_stop</c> replaced it, with the stop's title and kind at that time, which the UI shows read-only. Its number is its index in the stop's earlier versions plus 1.</summary>
/// <param name="Title">The stop's title while the version was current.</param>
/// <param name="Kind">The stop's kind while the version was current, whose rules the content follows.</param>
/// <param name="Content">The content as <c>put_stop</c> stored it.</param>
/// <param name="ReplacedAt">When the next version replaced it.</param>
internal sealed record StopVersion(string Title, StopKind Kind, StopContent Content, DateTimeOffset ReplacedAt);

/// <summary>
/// What a stop says and the code it points at, as <c>put_stop</c> stores it. Each text field is prose. JSON leaves out each optional field that is null.
/// </summary>
/// <param name="Headline">The stop's main point, which the reviewer reads first.</param>
/// <param name="Passages">The lines the stop points at, in the order Claude gave them. Exactly one is the primary passage when there are any. Only an overview may have none.</param>
/// <param name="How">How the code works, or null.</param>
/// <param name="Why">Why the change looks the way it does, or null.</param>
/// <param name="DesignChoices">The design choices behind the change and their trade-offs, or null.</param>
/// <param name="PinnedBy">The tests that pin the behavior, or null.</param>
/// <param name="Checks">The checks for the reviewer to answer, in Claude's order, or null when there are none. A question stop has at least one.</param>
/// <param name="Scenario">How the risk could happen, or null. A risk stop has one.</param>
/// <param name="Tags">Free descriptive tags with no behavior, such as <c>security</c>, or null when there are none.</param>
/// <param name="Placement">Where Claude suggests that the UI shows the stop's text, or null when Claude does not say.</param>
internal sealed record StopContent(
   string Headline,
   IReadOnlyList<StopPassage> Passages,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? How = null,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Why = null,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DesignChoices = null,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PinnedBy = null,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<StopCheck>? Checks = null,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Scenario = null,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Tags = null,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StopPlacement? Placement = null);

/// <summary>
/// The HTML of each prose field of a stop's content, safe to insert into the UI as it is. Each property is null when its field is, and JSON then leaves it out.
/// </summary>
/// <remarks>
/// Only <see cref="Prose.ProseHtml"/> builds one, into the JSON of a stop's content as its <c>html</c> property, for the UI. No stored type holds it, so the store never reads HTML from a tour file.
/// </remarks>
/// <param name="Headline">The HTML of the headline.</param>
/// <param name="How">The HTML of <see cref="StopContent.How"/>, or null.</param>
/// <param name="Why">The HTML of <see cref="StopContent.Why"/>, or null.</param>
/// <param name="DesignChoices">The HTML of <see cref="StopContent.DesignChoices"/>, or null.</param>
/// <param name="PinnedBy">The HTML of <see cref="StopContent.PinnedBy"/>, or null.</param>
/// <param name="Checks">The HTML of each check's prose, in the order of <see cref="StopContent.Checks"/>, or null.</param>
/// <param name="Scenario">The HTML of <see cref="StopContent.Scenario"/>, or null.</param>
internal sealed record StopHtml(
   string Headline,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? How = null,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Why = null,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DesignChoices = null,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PinnedBy = null,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<CheckHtml>? Checks = null,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Scenario = null);

/// <summary>
/// Lines of one file at one side of a pinned tour that a stop points at, with their text as the server read it at write time.
/// </summary>
/// <param name="Side">The side the lines are on.</param>
/// <param name="Path">The file's repository path at that side.</param>
/// <param name="StartLine">The first line, 1-based.</param>
/// <param name="EndLine">The last line, 1-based and inclusive, never before <see cref="StartLine"/>.</param>
/// <param name="Role">Whether the passage is the stop's primary passage.</param>
/// <param name="Commit">The commit that the side named when the stop was written.</param>
/// <param name="Text">The text of the lines, as <c>FileLines.Text</c> returns it.</param>
/// <param name="Label">A short name for the passage, such as <c>caller</c>, or null. JSON leaves it out when null.</param>
internal sealed record StopPassage(
   FileSide Side,
   string Path,
   int StartLine,
   int EndLine,
   PassageRole Role,
   CommitId Commit,
   string Text,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Label = null);

/// <summary>A passage's part in its stop.</summary>
[JsonConverter(typeof(StrictEnumConverter<PassageRole>))]
internal enum PassageRole
{
   /// <summary>The passage the stop is about, which the UI opens first. A change stop's primary passage touches the diff.</summary>
   Primary,

   /// <summary>A passage that supports the primary one, such as a caller, an interface or a test. It may point at unchanged code on either side.</summary>
   Related,
}

/// <summary>Where Claude suggests that the UI shows a stop's text.</summary>
[JsonConverter(typeof(StrictEnumConverter<StopPlacement>))]
internal enum StopPlacement
{
   /// <summary>At the stop's primary passage, inside the code.</summary>
   Inline,

   /// <summary>In the side panel.</summary>
   Side,
}

/// <summary>What a stop does for the reviewer.</summary>
[JsonConverter(typeof(StrictEnumConverter<StopKind>))]
internal enum StopKind
{
   /// <summary>A map of the whole change.</summary>
   Overview,

   /// <summary>Background the reviewer needs before the change.</summary>
   Context,

   /// <summary>A walk through changed code.</summary>
   Inspect,

   /// <summary>A concern in the change, listed in the summary.</summary>
   Risk,

   /// <summary>A question for the reviewer, whose answers become decisions.</summary>
   Question,
}

/// <summary>How far a stop's content has come.</summary>
[JsonConverter(typeof(StrictEnumConverter<StopStatus>))]
internal enum StopStatus
{
   /// <summary>A stop that the plan announces and that has no content yet.</summary>
   Pending,

   /// <summary>A stop with the first version of its content.</summary>
   Ready,

   /// <summary>A stop whose content replaced an earlier version, which the tour keeps.</summary>
   Revised,
}

/// <summary>
/// The plan's guess at where a stop will point, until the stop's own passages replace it.
/// </summary>
/// <param name="Path">The repository path of the file, with forward slashes.</param>
/// <param name="Line">The 1-based line in that file, or null when the hint names only the file.</param>
internal sealed record AnchorHint(string Path, int? Line);
