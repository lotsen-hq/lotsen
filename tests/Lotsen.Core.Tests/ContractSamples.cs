using System.Text.Json;
using System.Text.Json.Nodes;
using Lotsen.Git;
using Lotsen.Json;
using Lotsen.Operations;
using Lotsen.Platforms;
using Lotsen.Prose;
using Lotsen.Server;
using Lotsen.Sessions;
using Lotsen.Tours;

namespace Lotsen.Tests;

/// <summary>
/// Builds one sample of each event payload and of each output of an operation that the UI calls, from one tour that holds every kind of stop, thread, message and answer, and serializes each as
/// the server sends it to the UI. The UI's golden fixtures are these samples.
/// </summary>
internal static class ContractSamples
{
   private static readonly DateTimeOffset At = TourValues.Now;
   private static readonly DateTimeOffset Later = At.AddMinutes(5);
   private static readonly TourId Id = TourValues.Id;
   private static readonly TourPin.Pinned Pin = TourValues.Pinned;
   private static readonly CommitId Head = TourValues.Head;
   private static readonly TourPin.Pinned NextPin = new(Pin.MergeBase, CommitId.Parse(new string('c', 40)), 2, Pin.RemoteUrl);

   private static readonly StopContent FirstWriterContent = new("The writer **stores** the tour.", [TourValues.Passage()], How: "It calls `Store`.");

   private static readonly Stop.Written Overview = new("s1", "The change", StopKind.Overview, "a", new StopContent("What the change does.", [], Why: "Tours need a [writer](ref:head/src/a.cs#L1-L2)."), 1, []);

   private static readonly Stop.Written Writer = new(
      "s2",
      "The writer",
      StopKind.Inspect,
      "a",
      new StopContent(
         "The writer **stores** the tour in one step.",
         [TourValues.Passage(), TourValues.Passage(PassageRole.Related, 4, 6) with { Label = "The caller" }],
         How: "It calls `Store` once.",
         DesignChoices: "One write per edit.",
         PinnedBy: "`TourWriterTests`",
         Tags: ["storage"],
         Placement: StopPlacement.Side),
      2,
      [new StopVersion("The writer", StopKind.Inspect, FirstWriterContent, At)],
      SeenVersion: 1);

   private static readonly Stop.Written Question = new(
      "s3",
      "The lock",
      StopKind.Question,
      "a",
      new StopContent(
         "Does the lock hold?",
         [TourValues.Passage()],
         Checks:
         [
            new StopCheck("c1", "Keep the **lock**?", "Keep it, because two writers race.", [new SuggestedAnswer("Keep it.", AnswerType.Decision), new SuggestedAnswer("Unsure.", AnswerType.OpenQuestion)]),
            new StopCheck("c2", "Is the timeout right?"),
         ]),
      1,
      []);

   private static readonly Stop.Written Risk = new("s4", "The race", StopKind.Risk, "b", new StopContent("Two writers can race.", [TourValues.Passage()], Scenario: "Two pages **save** at once."), 1, [], StaleOn: 2);

   private static readonly Stop.Pending Tests = new("s5", "The tests", StopKind.Inspect, "b", new AnchorHint("tests/a.cs", 3));

   private static readonly Stop.Pending Docs = new("s6", "The docs", StopKind.Context, "b", Skipped: true);

   private static readonly Plan Plan = new(
      [new Chapter("a", "The writer"), new Chapter("b", "Its risks")],
      [Overview, Writer, Question, Risk, Tests, Docs],
      [new FileMark("src/a.g.cs", FileMarkKind.Generated, "The build writes it.")]);

   private static readonly AnswerBatch Batch = new("b1", "session1", At);

   private static readonly CheckAnswer Draft = new("s3", "c1", "Keep it.", AnswerType.Decision, AnswerState.Draft, At);

   private static readonly CheckAnswer SentAnswer = new("s3", "c2", "It is too short.", AnswerType.Concern, AnswerState.Sent, At, Batch);

   private static readonly CheckAnswer Outdated = new("s2", "c0", "Fine.", AnswerType.Decision, AnswerState.Received, At, Batch with { ReceivedAt = Later }, outdatedOn: 1);

   private static readonly QuestionThread NoteThread = ThreadFixtures.Note("t1", new ThreadAnchor.CodeRange(FileSide.Head, "src/a.cs", 1, 2, Head, "line 1\nline 2"), "Check the loop.", stopId: "s2");

   private static readonly QuestionThread AskedThread = ThreadFixtures.Parse(Asked());

   private static readonly QuestionThread ActionThread = ThreadFixtures.Asked(
      "t3", new ThreadAnchor.WholeStop("s3"), stopId: "s3", action: new TourAction.Explain(1), request: RequestState.Pending, text: "");

   private static readonly QuestionThread FamiliarityThread = ThreadFixtures.Decided(
      "t4", ThreadFixtures.Decision("""{"kind":"familiarityChanged","from":"some","to":"new","stopIds":["s2","s3"]}"""), request: RequestState.Done);

   private static readonly QuestionThread StartedThread = ThreadFixtures.Started("t5", new ThreadAnchor.WholeFile(FileSide.Base, "src/old.cs", Pin.MergeBase), "The old file had **no** lock.");

   private static readonly QuestionThread ApprovalThread = ThreadFixtures.Decided(
      "t6", ThreadFixtures.Decision("""{"kind":"planApproved","stopIds":["s1","s2","s3","s4","s5","s6"],"skippedStops":["s6"],"familiarity":"some"}"""), text: "Go ahead.");

   private static readonly QuestionThread IterationThread = ThreadFixtures.Decided("t7", ThreadFixtures.Decision("""{"kind":"iterationUpdated","iteration":2,"stopIds":["s4"]}"""));

   private static readonly QuestionThread OutdatedThread = ThreadFixtures.Started("t8", new ThreadAnchor.WholeTour(), "Is the plan **complete**?", state: QuestionThreadState.Outdated);

   private static readonly TourBinding Binding = new("session1", AttachmentLevel.Confirmed);

   private static readonly UpdatedPreference Preference = new("lotsen.diffLayout", "inline");

   private static readonly Tour Tour = Sample();

   private static readonly TourSummary Summary = new(Id, Tour.Target, Tour.Familiarity, At, Later, Pin, Plan.Stops.Count, "contoso/shop/web #42", TourState.InProgress, PullRequestState.Active, Tour.Revision, Binding, PlanApproval.Given);

   /// <summary>Returns one event of each kind, as the server publishes it, by its name.</summary>
   public static IReadOnlyDictionary<string, JsonNode> Events()
   {
      ServerEvent[] events =
      [
         ServerEvent.Of(ServerEvents.Hello, StreamLiveness.Default.Hello),
         ServerEvents.ForTourCreated(Summary),
         ServerEvents.ForPlanSet(Tour, MentionLinks.None),
         ServerEvents.ForStopPut(Tour, Overview, MentionLinks.None),
         ServerEvent.Of(ServerEvents.StopRevised, new StopRevisedEvent(Id, Later, Writer, [Outdated], [AskedThread]), MentionLinks.None),
         ServerEvents.ForTourPinned(Tour),
         ServerEvents.ForAvailableIterationChanged(Tour),
         ServerEvents.ForTourBindingChanged(Id, Binding),
         ServerEvents.ForThreadCreated(Tour, StartedThread, MentionLinks.None),
         ServerEvents.ForThreadUpdated(Tour, AskedThread, MentionLinks.None),
         ServerEvents.ForThreadDeleted(new DeletedThread(Id, Later, "t1")),
         ServerEvents.ForAnswerUpdated(new UpdatedAnswer(Id, Later, Draft)),
         ServerEvents.ForAnswerCleared(new ClearedAnswer(Id, Later, "s3", "c1")),
         ServerEvents.ForAnswerBatchUpdated(new UpdatedBatch(Id, Later, "b1", [SentAnswer])),
         ServerEvents.ForSkipsUpdated(new UpdatedSkips(Id, Later, ["s6"])),
         ServerEvents.ForSeenVersionUpdated(new UpdatedSeenVersion(Id, Later, "s2", 2)),
         ServerEvents.ForFamiliarityChanged(Tour),
         ServerEvents.ForPlanApproved(Tour),
         ServerEvents.ForTourStateChanged(new UpdatedTourState(Id, Later, TourState.InProgress, PullRequestState.Active)),
         ServerEvents.ForSynthesisWritten(new WrittenSynthesis(Id, Later, Later)),
         ServerEvents.ForControlsChanged(Controls()),
         ServerEvents.ForTourDeleted(new DeletedTour(Id)),
         ServerEvents.ForPreferenceChanged(Preference),
      ];
      var stamped = events.Select(serverEvent => serverEvent.IsTourScoped && serverEvent.Revision is null ? serverEvent.At(Tour.Revision) : serverEvent);
      return stamped.ToDictionary(serverEvent => serverEvent.Type, serverEvent => JsonNode.Parse(serverEvent.Data)!);
   }

   /// <summary>Returns the output of each operation that the UI calls, by the operation's name, as the operation's prose view renders it for the UI.</summary>
   /// <exception cref="KeyNotFoundException">No sample has the output type of an operation.</exception>
   public static IReadOnlyDictionary<string, JsonNode> Outputs()
   {
      var samples = OutputSamples();
      return UiContract.Operations.ToDictionary(operation => operation.Name, operation =>
      {
         var output = samples.SingleOrDefault(operation.OutputType.Type.IsInstanceOfType)
            ?? throw new KeyNotFoundException($"No sample has the output type {operation.OutputType.Type.Name} of {operation.Name}.");
         return operation.Prose.Render(output, MentionLinks.None) ?? JsonSerializer.SerializeToNode(output, operation.OutputType)!;
      });
   }

   private static object[] OutputSamples() =>
      [
         Tour,
         ReviewSummaryOf(Tour),
         AskedThread,
         new UpdatedThread(Id, Later, NoteThread),
         new DeletedThread(Id, Later, "t1"),
         new SentNotes(Id, Later, [ThreadFixtures.Asked("t1", new ThreadAnchor.WholeStop("s2"), stopId: "s2", text: "Check the loop.")]),
         new UpdatedAnswer(Id, Later, Draft),
         new ClearedAnswer(Id, Later, "s3", "c1"),
         new UpdatedBatch(Id, Later, "b1", [SentAnswer]),
         new TourList([Summary]),
         new ChangedFileList(
            Pin.MergeBase,
            Head,
            [
               new ChangedFile("src/a.cs", FileChangeKind.Modified, HeadObjectId: new string('d', 40)),
               new ChangedFile("src/b.cs", FileChangeKind.Renamed, OldPath: "src/old.cs", HeadObjectId: new string('e', 40)),
               new ChangedFile("src/c.cs", FileChangeKind.Deleted),
               new ChangedFile("img/logo.png", FileChangeKind.Added, Binary: true, HeadObjectId: new string('f', 40)),
               new ChangedFile("data/big.json", FileChangeKind.Modified, Large: new LargeFile(2_000_000), HeadObjectId: new string('1', 40)),
               new ChangedFile("docs/a.md", FileChangeKind.Modified, LineEndingsOnly: true, HeadObjectId: new string('2', 40)),
            ],
            ["src/a.cs"]),
         new FileText.Decoded("src/a.cs", FileSide.Head, Head, "line 1\nline 2\n"),
         new FileDiff.Text("line 1\n", "line 1\nline 2\n", [new TextChange(7, 7, 7, 14)]),
         new DirectoryListing(null, FileSide.Head, Head, [new RepositoryEntry("src", RepositoryEntryType.Directory), new RepositoryEntry("README.md", RepositoryEntryType.File)]),
         new ReviewMarkList([new ReviewMark("src/a.cs", new string('d', 40))]),
         new UpdatedSkips(Id, Later, ["s6"]),
         new RecordedProgress(Id, Later, TourState.InProgress, PullRequestState.Active, new ReadingProgress(false, "s2"), SeenVersion: 2),
         new UpdatedTourState(Id, Later, TourState.Archived, PullRequestState.Completed),
         new DeletedTour(Id),
         new PrunedClones(2),
         new ChangedFamiliarity(Id, Later, Familiarity.New, FamiliarityThread),
         new ApprovedPlan(Id, Later, PlanApproval.Given, Familiarity.Some, ["s6"], ApprovalThread),
         new CheckedIteration(Id, Later, Tour.AvailableIteration, "The platform could not be reached. Check again."),
         new UpdatedIteration(Id, Later, NextPin, ["s4"], IterationThread),
         new PreferenceList(new Dictionary<string, string> { ["lotsen.theme"] = "dark", [Preference.Key] = Preference.Value! }),
         Preference,
      ];

   /// <summary>Returns the controls of the sample tour while a session holds it, as a response and a <c>controlsChanged</c> event carry them.</summary>
   public static TourControls Controls() => TourControls.Of(Tour, new AgentSessions().TryOpen()!.Recipient);

   // A tour whose summary holds every group, and whose synthesis is based on that summary.
   private static Tour Sample()
   {
      var tour = new Tour(
         Id,
         new PullRequestTarget("azureDevOps", new Uri("https://dev.azure.com/contoso/shop/_git/web/pullrequest/42")),
         Familiarity.Some,
         At,
         Later,
         Pin,
         Plan,
         ThreadFixtures.Threads(NoteThread, AskedThread, ActionThread, FamiliarityThread, StartedThread, ApprovalThread, OutdatedThread),
         [Draft, SentAnswer, Outdated],
         PlanApproval.Given,
         new AvailableIteration(NextPin, ["s4"], Later),
         Progress: new ReadingProgress(false, "s2"))
      { Revision = 7 };
      var basedOn = AgentView.Summary(tour).Fingerprint(tour.Pin);
      return tour with { Synthesis = new TourSynthesis("The writer holds, but **the race** needs a test.", Later, Pin, basedOn) };
   }

   // The summary as read_summary returns it to the reviewer.
   private static ReviewSummary ReviewSummaryOf(Tour tour)
   {
      var agentSummary = AgentView.Summary(tour);
      var fingerprint = agentSummary.Fingerprint(tour.Pin);
      var synthesis = tour.Synthesis!;
      return ReviewSummary.Of(tour, tour) with
      {
         SummaryFingerprint = fingerprint,
         FollowUps = SummaryFollowUps.Of(agentSummary),
         Synthesis = new SummarySynthesis(synthesis.Text, synthesis.WrittenAt, synthesis.Pin, synthesis.BasedOn != fingerprint),
      };
   }

   // An asked thread with a reply, an agent message and a follow-up that waits.
   private static JsonObject Asked() => ThreadFixtures.Thread(
      "asked",
      "t2",
      new ThreadAnchor.StopText("s2", "how", "calls `Store`", 2),
      QuestionThreadState.Open,
      [
         ThreadFixtures.Question("q-t2", "Why one call?", "s2", At, ("r-t2", RequestState.Done, "session1")),
         ThreadFixtures.Reply("a-t2", "r-t2", "session1", "Because a **second** call races.", At),
         ThreadFixtures.AgentMessage("m-t2", "session1", "The test pins it.", At),
         ThreadFixtures.Question("q2-t2", "Which test?", "s2", Later, ("r2-t2", RequestState.Pending, "session1")),
      ]);
}
