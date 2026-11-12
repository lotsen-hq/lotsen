using System.Text.Json.Nodes;
using Lotsen.Git;
using Lotsen.Hosting;
using Lotsen.Operations;
using Lotsen.Platforms;
using Lotsen.Tours;

namespace Lotsen.Tests;

/// <summary>Covers the JSON of a tour's file: what a file holds after a write, what reads back, and which values a read refuses because the serializer or a type of the tour refuses them.</summary>
/// <remarks>The rules that span a tour are <see cref="TourRules"/>', whose table tests cover them on values.</remarks>
[Trait("Category", "Store")]
public sealed class TourFileFormatTests : IDisposable
{
   private static readonly PullRequestTarget PullRequest = new("test", new Uri("https://platform.test/shop/pull/42"));

   private readonly DataDirectory _dataDirectory = new(Path.Combine(Directory.CreateTempSubdirectory("lotsen-tests-").FullName, "data"));

   private string ToursPath => Path.Combine(_dataDirectory.Path, "tours");

   [Fact]
   public void Tour_round_trips_through_save_and_load_unchanged()
   {
      var store = NewStore();
      var created = store.Create(PullRequest, Familiarity.New);
      var saved = store.Change(created.Id, tour => tour with { Plan = SamplePlan() });

      var loaded = NewStore().Get(created.Id);

      Assert.NotNull(saved);
      Assert.NotNull(loaded);
      Assert.Equivalent(saved, loaded, strict: true);
      Assert.Equal(created.CreatedAt, loaded.CreatedAt);
   }

   [Fact]
   public void Review_marks_round_trip_beside_the_tour()
   {
      var store = NewStore();
      var tour = store.Create(PullRequest, Familiarity.Some);
      ReviewMark[] marks = [new("src/a.txt", new string('c', 40)), new("src/deleted.txt", null)];

      store.ChangeMarks(tour.Id, _ => marks);
      store.Change(tour.Id, current => current with { Plan = SamplePlan() });

      Assert.Equal(marks, NewStore().GetReviewMarks(tour.Id));
      Assert.Equal("src/a.txt", (string)JsonNode.Parse(File.ReadAllText(PathOf(tour.Id)))!["reviewMarks"]![0]!["path"]!);
   }

   [Fact]
   public void Pins_round_trip_through_save_and_load()
   {
      var store = NewStore();
      var tour = store.Create(PullRequest, Familiarity.Some);
      TourPin[] pins =
      [
         new TourPin.Pinned(
            CommitId.Parse(new string('a', 40)),
            CommitId.Parse(new string('b', 64)),
            3,
            new Uri("https://platform.test/shop.git")),
         new TourPin.Failed("Sign in again, then pin the tour again."),
         new TourPin.Pinning(),
      ];

      Assert.All(pins, pin =>
      {
         store.Change(tour.Id, current => current with { Pin = pin });
         Assert.Equal(pin, NewStore().Get(tour.Id)?.Pin);
      });
   }

   /// <summary>The serializer reads each of these as a relative URL, which has no absolute form for a label or a pin.</summary>
   [Theory]
   [InlineData("shop/pull/42")]
   [InlineData("")]
   [InlineData("//platform.test/shop/pull/42")]
   [InlineData("/shop/pull/42")]
   public void Tour_file_whose_target_url_is_relative_is_refused(string url)
   {
      var store = NewStore();
      var tour = store.Create(PullRequest, Familiarity.Some);
      var tourPath = Path.Combine(ToursPath, $"{tour.Id}.json");
      var content = File.ReadAllText(tourPath);
      Assert.Contains($"\"url\":\"{PullRequest.Url}\"", content);
      File.WriteAllText(tourPath, content.Replace($"\"url\":\"{PullRequest.Url}\"", $"\"url\":\"{url}\""));

      var exception = Assert.Throws<InvalidDataException>(() => store.Get(tour.Id));

      Assert.Contains(tour.Id.ToString(), exception.Message);
   }

   [Theory]
   [InlineData("https://platform.test/shop/pull/42")]
   [InlineData("file:///shop/pull/42")]
   public void Tour_file_whose_target_url_is_absolute_loads(string url)
   {
      var store = NewStore();
      var tour = store.Create(new PullRequestTarget(PullRequest.Platform, new Uri(url)), Familiarity.Some);

      Assert.Equal(url, NewStore().Get(tour.Id)?.Target is PullRequestTarget target ? target.Url.AbsoluteUri : null);
   }

   [Theory]
   [InlineData("\"familiarity\":\"some\"", "\"familiarity\":\"expert\"")]
   [InlineData("\"familiarity\":\"some\"", "\"familiarity\":1")]
   [InlineData("\"familiarity\":\"some\"", "\"familiarity\":\"1\"")]
   [InlineData("\"familiarity\":\"some\"", "\"familiarity\":\"some, new\"")]
   [InlineData("\"familiarity\":\"some\"", "\"familiarity\":\"familiar,new\"")]
   [InlineData("\"familiarity\":\"some\"", "\"familiarity\":\"some,\"")]
   [InlineData("\"familiarity\":\"some\"", "\"familiarity\":\"Some\"")]
   [InlineData("\"familiarity\":\"some\"", "\"familiarity\":\"SOME\"")]
   [InlineData("\"familiarity\":\"some\"", "\"familiarity\":\" some\"")]
   [InlineData("\"familiarity\":\"some\"", "\"familiarity\":\"some \"")]
   [InlineData("\"familiarity\":\"some\"", "\"familiarity\":\"\"")]
   [InlineData("\"familiarity\":\"some\"", "\"familiarity\":null")]
   [InlineData("\"kind\":\"risk\"", "\"kind\":\"risk, question\"")]
   [InlineData("\"kind\":\"risk\"", "\"kind\":\"Risk\"")]
   [InlineData("\"chapters\":[", "\"chapters\":[null,")]
   [InlineData("\"stops\":[", "\"stops\":[null,")]
   [InlineData("\"fileMarks\":[", "\"fileMarks\":[null,")]
   [InlineData("\"mark\":\"generated\"", "\"mark\":\"noise\"")]
   [InlineData(",\"reason\":\"The build writes it.\"", "")]
   [InlineData("\"kind\":\"risk\"", "\"kind\":\"defect\"")]
   [InlineData(",\"earlier\":[]", "")]
   [InlineData("\"stage\":\"written\"", "\"stage\":\"done\"")]
   [InlineData("\"stage\":\"pending\"", "\"stage\":\"Pending\"")]
   [InlineData("\"version\":1", "\"version\":\"1\"")]
   [InlineData(",\"version\":1", "")]
   [InlineData("\"kind\":\"pullRequest\"", "\"kind\":\"branch\"")]
   [InlineData(",\"platform\":\"test\"", "")]
   [InlineData("\"skipped\":true", "\"skipped\":\"yes\"")]
   [InlineData("\"schemaVersion\":22", "\"schemaVersion\":21")]
   [InlineData("\"schemaVersion\":22", "\"schemaVersion\":23")]
   [InlineData("\"schemaVersion\":22", "\"schemaVersion\":\"22\"")]
   [InlineData(",\"threads\":[]", "")]
   [InlineData(",\"answers\":[]", "")]
   [InlineData("\"answers\":[", "\"answers\":[null,")]
   [InlineData("\"reviewMarks\":[", "\"reviewMarks\":[null,")]
   [InlineData("\"reviewMarks\":[", "\"reviewMarks\":null,\"unread\":[")]
   [InlineData("\"path\":\"src/reviewed.txt\"", "\"path\":null")]
   [InlineData(",\"headObjectId\":null", "")]
   [InlineData("{\"state\":\"pinning\"}", "{\"state\":\"unpinned\"}")]
   [InlineData("{\"state\":\"pinning\"}", "{}")]
   [InlineData("{\"state\":\"pinning\"}", "null")]
   [InlineData(",\"title\":\"Pricing\"", "")]
   [InlineData("\"title\":\"Pricing\"", "\"title\":null")]
   public void Tour_file_with_an_unknown_or_missing_value_is_refused(string original, string replacement)
   {
      var store = NewStore();
      var tour = store.Create(PullRequest, Familiarity.Some);
      store.Change(tour.Id, current => current with { Plan = SamplePlan() });
      store.ChangeMarks(tour.Id, _ => [new ReviewMark("src/reviewed.txt", null)]);
      var tourPath = Path.Combine(ToursPath, $"{tour.Id}.json");
      var content = File.ReadAllText(tourPath);
      Assert.Contains(original, content);
      File.WriteAllText(tourPath, content.Replace(original, replacement));

      var exception = Assert.Throws<InvalidDataException>(() => store.Get(tour.Id));

      Assert.Contains(tour.Id.ToString(), exception.Message);
      Assert.DoesNotContain(_dataDirectory.Path, exception.Message);
   }

   [Fact]
   public void Stale_marker_on_a_stop_with_content_is_stored_and_read_back()
   {
      var store = NewStore();
      var tour = store.Create(PullRequest, Familiarity.Some);
      var passage = new StopPassage(FileSide.Head, "a.txt", 1, 1, PassageRole.Primary, CommitId.Parse(new string('a', 40)), "a");
      var stale = new Stop.Written("stale", "Stale", StopKind.Inspect, "pricing", new StopContent("A headline.", [passage]), 1, [], StaleOn: 2);
      store.Change(tour.Id, current => current with { Plan = new Plan([new Chapter("pricing", "Pricing")], [stale], []) });

      Assert.Equal(2, Assert.IsType<Stop.Written>(NewStore().Get(tour.Id)!.Plan!.Stops[0]).StaleOn);
   }

   [Fact]
   public void Seen_version_of_a_stop_is_stored_and_read_back_and_left_out_while_unset()
   {
      var store = NewStore();
      var tour = store.Create(PullRequest, Familiarity.Some);
      var passage = new StopPassage(FileSide.Head, "a.txt", 1, 1, PassageRole.Primary, CommitId.Parse(new string('a', 40)), "a");
      var seen = new Stop.Written("seen", "Seen", StopKind.Inspect, "pricing", new StopContent("A headline.", [passage]), 1, [], SeenVersion: 1);
      var unseen = seen with { Id = "unseen", SeenVersion = null };
      store.Change(tour.Id, current => current with { Plan = new Plan([new Chapter("pricing", "Pricing")], [seen, unseen], []) });

      var stops = NewStore().Get(tour.Id)!.Plan!.Stops;

      Assert.Equal(1, Assert.IsType<Stop.Written>(stops[0]).SeenVersion);
      Assert.Null(Assert.IsType<Stop.Written>(stops[1]).SeenVersion);
      var file = JsonNode.Parse(File.ReadAllText(PathOf(tour.Id)))!;
      Assert.Equal(1, (int)file["tour"]!["plan"]!["stops"]![0]!["seenVersion"]!);
      Assert.False(file["tour"]!["plan"]!["stops"]![1]!.AsObject().ContainsKey("seenVersion"));
   }

   [Fact]
   public void Parts_of_a_plan_are_stored_and_read_back_and_left_out_without_parts()
   {
      var store = NewStore();
      var parted = store.Create(PullRequest, Familiarity.Some);
      var plain = store.Create(PullRequest, Familiarity.Some);
      var plan = SamplePlan();
      store.Change(parted.Id, current => current with { Plan = plan with { Chapters = [.. plan.Chapters.Select(chapter => chapter with { Part = "why" })], Parts = [new Part("why", "Why")] } });
      store.Change(plain.Id, current => current with { Plan = plan });

      var read = NewStore().Get(parted.Id)!.Plan!;

      Assert.Equal([new Part("why", "Why")], read.Parts);
      Assert.All(read.Chapters, chapter => Assert.Equal("why", chapter.Part));
      Assert.Null(NewStore().Get(plain.Id)!.Plan!.Parts);
      var file = JsonNode.Parse(File.ReadAllText(PathOf(plain.Id)))!["tour"]!["plan"]!;
      Assert.False(file.AsObject().ContainsKey("parts"));
      Assert.All(file["chapters"]!.AsArray(), chapter => Assert.False(chapter!.AsObject().ContainsKey("part")));
   }

   /// <summary>The serializer reads a null element of a collection, so the store refuses one in a stop's content as it does in the plan.</summary>
   [Theory]
   [InlineData("\"checks\":[", "\"checks\":[null,")]
   [InlineData("\"suggestions\":[", "\"suggestions\":[null,")]
   [InlineData("\"tags\":[", "\"tags\":[null,")]
   [InlineData("\"passages\":[", "\"passages\":[null,")]
   public void Tour_file_with_a_null_element_in_a_stops_content_is_refused(string original, string replacement)
   {
      var store = NewStore();
      var tour = store.Create(PullRequest, Familiarity.Some);
      var passage = new StopPassage(FileSide.Head, "a.txt", 1, 1, PassageRole.Primary, CommitId.Parse(new string('a', 40)), "a");
      var ready = new Stop.Written("s1", "Ready", StopKind.Inspect, "pricing", new StopContent("A headline.", [passage], Checks: [new StopCheck("c1", "A check?", Suggestions: [new SuggestedAnswer("Yes.", AnswerType.Decision)])], Tags: ["naming"]), 1, []);
      store.Change(tour.Id, current => current with { Plan = new Plan([new Chapter("pricing", "Pricing")], [ready], []) });
      var tourPath = Path.Combine(ToursPath, $"{tour.Id}.json");
      var content = File.ReadAllText(tourPath);
      Assert.Contains(original, content);
      File.WriteAllText(tourPath, content.Replace(original, replacement));

      Assert.Throws<InvalidDataException>(() => store.Get(tour.Id));
   }

   /// <summary>The serializer reads a null element of a collection, so the store refuses one in the available iteration's stale stops as it does in the plan.</summary>
   [Theory]
   [InlineData("\"staleStops\":[", "\"staleStops\":[null,")]
   [InlineData("\"staleStops\":[\"s1\"]", "\"staleStops\":[\"s1\",null]")]
   [InlineData("\"staleStops\":[\"s1\"]", "\"staleStops\":null")]
   public void Tour_file_with_a_null_in_the_available_iterations_stale_stops_is_refused(string original, string replacement)
   {
      var store = NewStore();
      var tour = store.Create(PullRequest, Familiarity.Some);
      var pin = new TourPin.Pinned(CommitId.Parse(new string('a', 40)), CommitId.Parse(new string('b', 40)), 1, new Uri("https://platform.test/shop.git"));
      var newer = pin with { Head = CommitId.Parse(new string('c', 40)), Iteration = 2 };
      store.Change(tour.Id, current => current with { Pin = pin, AvailableIteration = new AvailableIteration(newer, ["s1"], DateTimeOffset.UnixEpoch) });
      Assert.NotNull(NewStore().Get(tour.Id)?.AvailableIteration);
      var tourPath = Path.Combine(ToursPath, $"{tour.Id}.json");
      var content = File.ReadAllText(tourPath);
      Assert.Contains(original, content);
      File.WriteAllText(tourPath, content.Replace(original, replacement));

      Assert.Throws<InvalidDataException>(() => NewStore().Get(tour.Id));
   }

   /// <summary>The serializer reads a null element of a collection, so the store refuses one in the stop IDs of a stored tour action or decision as it does in the plan. Neither takes a null list.</summary>
   [Theory]
   [InlineData(TourAction.ReorderKind, "\"stopIds\":[\"s1\"]", "\"stopIds\":null")]
   [InlineData(TourAction.ReorderKind, "\"stopIds\":[\"s1\"]", "\"stopIds\":[\"s1\",null]")]
   [InlineData(ReviewerDecision.FamiliarityChangedKind, "\"stopIds\":[\"s1\"]", "\"stopIds\":null")]
   [InlineData(ReviewerDecision.FamiliarityChangedKind, "\"stopIds\":[\"s1\"]", "\"stopIds\":[null]")]
   [InlineData(ReviewerDecision.PlanApprovedKind, "\"stopIds\":[\"s1\"]", "\"stopIds\":null")]
   [InlineData(ReviewerDecision.PlanApprovedKind, "\"stopIds\":[\"s1\"]", "\"stopIds\":[null,\"s1\"]")]
   [InlineData(ReviewerDecision.PlanApprovedKind, "\"skippedStops\":[\"s2\"]", "\"skippedStops\":null")]
   [InlineData(ReviewerDecision.PlanApprovedKind, "\"skippedStops\":[\"s2\"]", "\"skippedStops\":[null]")]
   [InlineData(ReviewerDecision.IterationUpdatedKind, "\"stopIds\":[\"s1\"]", "\"stopIds\":null")]
   [InlineData(ReviewerDecision.IterationUpdatedKind, "\"stopIds\":[\"s1\"]", "\"stopIds\":[\"s1\",null]")]
   public void Tour_file_with_a_null_in_the_stop_IDs_of_a_tour_action_or_decision_is_refused(string kind, string original, string replacement)
   {
      var store = NewStore();
      var tour = store.Create(PullRequest, Familiarity.Some);
      var asked = new DateTimeOffset(2026, 10, 22, 9, 31, 0, TimeSpan.Zero);
      var thread = kind switch
      {
         TourAction.ReorderKind => ThreadFixtures.Asked("t1", new ThreadAnchor.WholeTour(), action: new TourAction.Reorder(["s1"]), text: "", at: asked),
         ReviewerDecision.FamiliarityChangedKind => ThreadFixtures.Decided("t1", ThreadFixtures.Decision("""{"kind":"familiarityChanged","from":"some","to":"new","stopIds":["s1"]}"""), at: asked),
         ReviewerDecision.PlanApprovedKind => ThreadFixtures.Decided("t1", ThreadFixtures.Decision("""{"kind":"planApproved","stopIds":["s1"],"skippedStops":["s2"],"familiarity":"some"}"""), at: asked),
         _ => ThreadFixtures.Decided("t1", ReviewerDecision.IterationUpdated.For(2, ["s1"]), at: asked),
      };
      store.Change(tour.Id, current => current with { Plan = SamplePlan(), Threads = ThreadFixtures.Threads(thread) });
      Assert.NotNull(NewStore().Get(tour.Id));
      var tourPath = Path.Combine(ToursPath, $"{tour.Id}.json");
      var content = File.ReadAllText(tourPath);
      Assert.Contains(original, content);
      File.WriteAllText(tourPath, content.Replace(original, replacement));

      Assert.Throws<InvalidDataException>(() => NewStore().Get(tour.Id));
   }

   [Fact]
   public void Stored_follow_up_actions_read_back_with_their_items()
   {
      var store = NewStore();
      var tour = store.Create(PullRequest, Familiarity.Some);
      var threads = ThreadFixtures.Threads([.. FollowUpActions().Select((action, index) => ActionThread($"t{index}", action))]);

      store.Change(tour.Id, current => current with { Plan = SamplePlan(), Threads = threads });

      var read = NewStore().Get(tour.Id)!.Threads.Select(thread => ((QuestionThread.Sent.Asked)thread).Action);
      Assert.Equal(FollowUpActions().Select(Json), read.Select(action => Json(action!)));
   }

   /// <summary>The serializer reads a null element of a collection and a null in a list that a request omits, so the store refuses both in a stored follow-up action's items.</summary>
   [Theory]
   [InlineData(TourAction.DraftWorkItemsKind, "decisions", false)]
   [InlineData(TourAction.DraftWorkItemsKind, "decisions", true)]
   [InlineData(TourAction.WalkOpenQuestionsKind, "openQuestions", false)]
   [InlineData(TourAction.WalkOpenQuestionsKind, "openQuestions", true)]
   [InlineData(TourAction.DraftCommentsKind, "concerns", false)]
   [InlineData(TourAction.DraftCommentsKind, "concerns", true)]
   [InlineData(TourAction.DraftCommentsKind, "risks", false)]
   [InlineData(TourAction.DraftCommentsKind, "risks", true)]
   public void Tour_file_with_a_null_item_in_a_follow_up_action_is_refused(string kind, string items, bool nullList)
   {
      var store = NewStore();
      var tour = store.Create(PullRequest, Familiarity.Some);
      var action = FollowUpActions().Single(candidate => candidate.Kind == kind);
      store.Change(tour.Id, current => current with { Plan = SamplePlan(), Threads = ThreadFixtures.Threads(ActionThread("t1", action)) });
      Assert.NotNull(NewStore().Get(tour.Id));
      var tourPath = Path.Combine(ToursPath, $"{tour.Id}.json");
      var file = JsonNode.Parse(File.ReadAllText(tourPath))!;
      var stored = file["tour"]!["threads"]![0]!["action"]!.AsObject();
      if (nullList)
      {
         stored[items] = null;
      }
      else
      {
         stored[items]!.AsArray().Insert(0, null);
      }

      File.WriteAllText(tourPath, file.ToJsonString());

      Assert.Throws<InvalidDataException>(() => NewStore().Get(tour.Id));
   }

   private static string Json(TourAction action) => System.Text.Json.JsonSerializer.Serialize(action, Lotsen.Json.LotsenJsonContext.Default.TourAction);

   private static TourAction[] FollowUpActions()
   {
      var decision = new FollowUpAnswer("s1", "c1", "The cap is right.", AnswerType.Decision, "Discounts", "Is the cap right?");
      return
      [
         new TourAction.FollowUp.DraftWorkItems([decision]),
         new TourAction.FollowUp.WalkOpenQuestions([decision with { StopTitle = null, Question = null, Text = "Ask the team.", Type = AnswerType.OpenQuestion }]),
         new TourAction.FollowUp.DraftComments([decision with { Type = AnswerType.Concern }], [new FollowUpRisk("s2", "Rounding", "Two discounts round twice."), new FollowUpRisk("s3", "Eviction", "Eviction drops a hot entry.")]),
      ];
   }

   private static QuestionThread ActionThread(string id, TourAction action)
   {
      return ThreadFixtures.Asked(id, new ThreadAnchor.WholeTour(), action: action, text: "");
   }

   [Fact]
   [Trait("Category", "Summary")]
   public void Stored_synthesis_reads_back_with_its_write_time_pin_and_fingerprint()
   {
      var store = NewStore();
      var tour = store.Create(PullRequest, Familiarity.Some);
      var synthesis = SampleSynthesis();

      store.Change(tour.Id, current => current with { Synthesis = synthesis });

      Assert.Equal(synthesis, NewStore().Get(tour.Id)!.Synthesis);
   }

   [Theory]
   [Trait("Category", "Summary")]
   [InlineData("\"text\":\"The review keeps the digit.\"", "\"text\":\"\"")]
   [InlineData("\"text\":\"The review keeps the digit.\"", "\"text\":\" \\n\"")]
   [InlineData("\"text\":\"The review keeps the digit.\"", "\"text\":null")]
   [InlineData("\"writtenAt\":\"2026-10-22T09:31:00+00:00\"", "\"writtenAt\":\"0001-01-01T00:00:00+00:00\"")]
   [InlineData("\"basedOn\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\"", "\"basedOn\":\"abc\"")]
   [InlineData("\"basedOn\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\"", "\"basedOn\":null")]
   [InlineData("\"basedOn\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\"", "\"basedOn\":\"0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef\"")]
   public void Tour_file_with_a_malformed_synthesis_is_refused(string original, string replacement)
   {
      var store = NewStore();
      var tour = store.Create(PullRequest, Familiarity.Some);
      store.Change(tour.Id, current => current with { Synthesis = SampleSynthesis() });
      Assert.NotNull(NewStore().Get(tour.Id));
      var tourPath = Path.Combine(ToursPath, $"{tour.Id}.json");
      var content = File.ReadAllText(tourPath);
      Assert.Contains(original, content);
      File.WriteAllText(tourPath, content.Replace(original, replacement));

      Assert.Throws<InvalidDataException>(() => NewStore().Get(tour.Id));
   }

   [Theory]
   [Trait("Category", "TourActions")]
   [InlineData("awaited")]
   [InlineData("given")]
   public void Plan_approval_round_trips_through_save_and_load_as_its_camel_case_name(string name)
   {
      var approval = name == "awaited" ? PlanApproval.Awaited : PlanApproval.Given;
      var store = NewStore();
      var tour = store.Create(PullRequest, Familiarity.Some, PlanApproval.Awaited);
      store.Change(tour.Id, current => current with { Plan = SamplePlan(), PlanApproval = approval });

      Assert.Contains($"\"planApproval\":\"{name}\"", File.ReadAllText(Path.Combine(ToursPath, $"{tour.Id}.json")));
      Assert.Equal(approval, NewStore().Get(tour.Id)!.PlanApproval);
   }

   [Fact]
   [Trait("Category", "TourActions")]
   public void Tour_without_plan_first_leaves_the_plan_approval_out_of_its_file_and_loads_as_not_needed()
   {
      var store = NewStore();
      var tour = store.Create(PullRequest, Familiarity.Some);

      Assert.DoesNotContain("planApproval", File.ReadAllText(Path.Combine(ToursPath, $"{tour.Id}.json")), StringComparison.Ordinal);
      Assert.Equal(PlanApproval.NotNeeded, NewStore().Get(tour.Id)!.PlanApproval);
   }

   [Theory]
   [Trait("Category", "TourActions")]
   [InlineData("\"planApproval\":\"bogus\"")]
   [InlineData("\"planApproval\":null")]
   [InlineData("\"planApproval\":\"Awaited\"")]
   [InlineData("\"planApproval\":1")]
   public void Tour_file_with_an_unknown_plan_approval_is_refused(string replacement)
   {
      var store = NewStore();
      var tour = store.Create(PullRequest, Familiarity.Some, PlanApproval.Awaited);
      var tourPath = Path.Combine(ToursPath, $"{tour.Id}.json");
      var content = File.ReadAllText(tourPath);
      Assert.Contains("\"planApproval\":\"awaited\"", content);
      File.WriteAllText(tourPath, content.Replace("\"planApproval\":\"awaited\"", replacement));

      Assert.Throws<InvalidDataException>(() => NewStore().Get(tour.Id));
   }

   [Theory]
   [Trait("Category", "TourStates")]
   [InlineData("stop", false)]
   [InlineData("stop", true)]
   [InlineData(null, true)]
   public void Reading_progress_the_archived_flag_and_the_pull_request_state_survive_a_store_reload(string? lastStop, bool finished)
   {
      var store = NewStore();
      var tour = store.Create(PullRequest, Familiarity.Some);
      var progress = new ReadingProgress(finished, lastStop);

      store.Change(tour.Id, current => current with { Plan = SamplePlan(), Progress = progress, Archived = true, PullRequestState = PullRequestState.Abandoned });
      var loaded = NewStore().Get(tour.Id)!;

      Assert.Equal(progress, loaded.Progress);
      Assert.True(loaded.Archived);
      Assert.Equal(PullRequestState.Abandoned, loaded.PullRequestState);
      Assert.Equal(TourState.Archived, TourStates.Of(loaded));
      Assert.Equal(finished ? TourState.Finished : TourState.InProgress, TourStates.Of(loaded with { Archived = false }));
   }

   [Fact]
   [Trait("Category", "TourStates")]
   public void Tour_never_opened_archived_or_flagged_leaves_progress_archived_and_the_pull_request_state_out_of_its_file()
   {
      var store = NewStore();
      var tour = store.Create(PullRequest, Familiarity.Some);
      store.Change(tour.Id, current => current with { Plan = SamplePlan() });

      var content = File.ReadAllText(Path.Combine(ToursPath, $"{tour.Id}.json"));
      var loaded = NewStore().Get(tour.Id)!;

      Assert.DoesNotContain("\"progress\"", content, StringComparison.Ordinal);
      Assert.DoesNotContain("\"archived\"", content, StringComparison.Ordinal);
      Assert.DoesNotContain("\"pullRequestState\"", content, StringComparison.Ordinal);
      Assert.Null(loaded.Progress);
      Assert.False(loaded.Archived);
      Assert.Equal(PullRequestState.Active, loaded.PullRequestState);
   }

   /// <summary><c>record_reading_progress</c> stores the summary page as the last place only on a finished tour, and never an empty stop ID.</summary>
   [Theory]
   [Trait("Category", "TourStates")]
   [InlineData("\"progress\":{\"finished\":true,\"lastStop\":\"stop\"}", "\"progress\":{\"finished\":false}")]
   [InlineData("\"progress\":{\"finished\":true,\"lastStop\":\"stop\"}", "\"progress\":{\"finished\":true,\"lastStop\":\"\"}")]
   [InlineData("\"progress\":{\"finished\":true,\"lastStop\":\"stop\"}", "\"progress\":{\"finished\":\"yes\",\"lastStop\":\"stop\"}")]
   [InlineData("\"archived\":true", "\"archived\":\"yes\"")]
   [InlineData("\"pullRequestState\":\"completed\"", "\"pullRequestState\":\"merged\"")]
   [InlineData("\"pullRequestState\":\"completed\"", "\"pullRequestState\":\"Completed\"")]
   public void Tour_file_with_malformed_progress_archive_or_pull_request_state_is_refused(string original, string replacement)
   {
      var store = NewStore();
      var tour = store.Create(PullRequest, Familiarity.Some);
      store.Change(tour.Id, current => current with { Progress = new ReadingProgress(true, "stop"), Archived = true, PullRequestState = PullRequestState.Completed });
      var tourPath = Path.Combine(ToursPath, $"{tour.Id}.json");
      var content = File.ReadAllText(tourPath);
      Assert.Contains(original, content);
      File.WriteAllText(tourPath, content.Replace(original, replacement));

      Assert.Throws<InvalidDataException>(() => NewStore().Get(tour.Id));
   }

   /// <summary>A stop's status follows from its stage and version, so a read ignores the stored status.</summary>
   [Fact]
   [Trait("Category", "Versions")]
   public void Stored_status_that_disagrees_with_the_version_is_ignored()
   {
      var store = NewStore();
      var tour = store.Create(PullRequest, Familiarity.Some);
      store.Change(tour.Id, current => current with { Plan = SamplePlan() });
      var tourPath = Path.Combine(ToursPath, $"{tour.Id}.json");
      File.WriteAllText(tourPath, File.ReadAllText(tourPath).Replace("\"status\":\"pending\"", "\"status\":\"revised\"", StringComparison.Ordinal));

      Assert.All(NewStore().Get(tour.Id)!.Plan!.Stops.OfType<Stop.Pending>(), stop => Assert.Equal(StopStatus.Pending, stop.Status));
   }

   [Fact]
   public void Tour_with_question_threads_round_trips_through_save_and_load_unchanged()
   {
      var store = NewStore();
      var created = store.Create(PullRequest, Familiarity.Some);
      var saved = store.Change(created.Id, tour => tour with { Plan = SamplePlan(), Threads = ThreadFixtures.Threads([.. SampleThreads()]) });

      var loaded = NewStore().Get(created.Id);

      Assert.NotNull(saved);
      Assert.NotNull(loaded);
      Assert.Equal(5, loaded.Threads.Count);
      Assert.Equivalent(saved, loaded, strict: true);
      Assert.Equal(
         ["code", "stopText", "file", "stop", "tour"],
         JsonNode.Parse(File.ReadAllText(Path.Combine(ToursPath, $"{created.Id}.json")))!["tour"]!["threads"]!.AsArray().Select(thread => (string)thread!["anchor"]!["kind"]!));
   }

   /// <summary>The serializer reads a null element of a collection and an unknown name only as far as the model allows, so the store refuses each in a thread.</summary>
   [Theory]
   [InlineData("\"threads\":[", "\"threads\":[null,")]
   [InlineData("\"messages\":[", "\"messages\":[null,")]
   [InlineData("\"requests\":[", "\"requests\":[null,")]
   [InlineData("\"kind\":\"tour\"", "\"kind\":\"everywhere\"")]
   [InlineData("\"kind\":\"question\"", "\"kind\":\"stranger\"")]
   [InlineData("\"kind\":\"asked\"", "\"kind\":\"stranger\"")]
   [InlineData("\"state\":\"pending\"", "\"state\":\"lost\"")]
   [InlineData("\"state\":\"open\"", "\"state\":\"settled\"")]
   [InlineData(",\"state\":\"open\"", "")]
   [InlineData(",\"text\":\"Why here?\"", "")]
   public void Tour_file_with_a_null_element_or_an_unknown_value_in_a_thread_is_refused(string original, string replacement)
   {
      var store = NewStore();
      var tour = store.Create(PullRequest, Familiarity.Some);
      store.Change(tour.Id, current => current with { Plan = SamplePlan(), Threads = ThreadFixtures.Threads(SampleThreads()[^1]) });
      var tourPath = Path.Combine(ToursPath, $"{tour.Id}.json");
      var content = File.ReadAllText(tourPath);
      Assert.Contains(original, content);
      File.WriteAllText(tourPath, content.Replace(original, replacement));

      Assert.Throws<InvalidDataException>(() => store.Get(tour.Id));
   }

   /// <summary>A question's constructor refuses an empty array of requests, which the serializer reads, so the store refuses the tour as unreadable.</summary>
   [Fact]
   public void Tour_file_with_a_question_that_has_no_request_is_refused()
   {
      var store = NewStore();
      var tour = store.Create(PullRequest, Familiarity.Some);
      store.Change(tour.Id, current => current with { Threads = ThreadFixtures.Threads(SampleThreads()[^1]) });
      var tourPath = Path.Combine(ToursPath, $"{tour.Id}.json");
      var file = JsonNode.Parse(File.ReadAllText(tourPath))!;
      file["tour"]!["threads"]![0]!["messages"]![0]!["requests"] = new JsonArray();
      File.WriteAllText(tourPath, file.ToJsonString());

      Assert.Throws<InvalidDataException>(() => NewStore().Get(tour.Id));
   }

   [Fact]
   public void Familiarity_is_stored_as_its_camel_case_name()
   {
      var store = NewStore();
      (Familiarity Value, string Name)[] cases = [(Familiarity.Familiar, "familiar"), (Familiarity.Some, "some"), (Familiarity.New, "new")];

      Assert.All(cases, familiarity =>
      {
         var tour = store.Create(PullRequest, familiarity.Value);
         var content = File.ReadAllText(Path.Combine(ToursPath, $"{tour.Id}.json"));
         Assert.Contains($"\"familiarity\":\"{familiarity.Name}\"", content);
         Assert.Equal(familiarity.Value, NewStore().Get(tour.Id)?.Familiarity);
      });
   }

   [Fact]
   public void Stop_kinds_and_statuses_are_stored_as_their_camel_case_names()
   {
      var store = NewStore();
      var tour = store.Create(PullRequest, Familiarity.Some);
      var earlier = new StopVersion("Revised", StopKind.Inspect, Content, DateTimeOffset.UnixEpoch);
      var plan = SamplePlan() with { Stops = [.. SamplePlan().Stops, new Stop.Written("s7", "Revised", StopKind.Inspect, "pricing", Content, 2, [earlier])] };
      store.Change(tour.Id, current => current with { Plan = plan });

      var content = File.ReadAllText(Path.Combine(ToursPath, $"{tour.Id}.json"));

      Assert.All(["overview", "context", "inspect", "risk", "question"], kind => Assert.Contains($"\"kind\":\"{kind}\"", content));
      Assert.All(["pending", "ready", "revised"], status => Assert.Contains($"\"status\":\"{status}\"", content));
      Assert.All(["pending", "written"], stage => Assert.Contains($"\"stage\":\"{stage}\"", content));
      Assert.Equivalent(plan, NewStore().Get(tour.Id)?.Plan, strict: true);
   }

   [Fact]
   public void Undefined_enum_value_is_not_written()
   {
      var store = NewStore();
      var tour = store.Create(PullRequest, Familiarity.Some);
      var before = File.ReadAllBytes(Path.Combine(ToursPath, $"{tour.Id}.json"));

      Assert.ThrowsAny<Exception>(() => store.Change(tour.Id, current => current with { Familiarity = (Familiarity)3 }));

      Assert.Equal(before, File.ReadAllBytes(Path.Combine(ToursPath, $"{tour.Id}.json")));
   }

   public void Dispose() => Directory.Delete(Path.GetDirectoryName(_dataDirectory.Path)!, recursive: true);

   private FileTourStore NewStore() => new(_dataDirectory);

   private string PathOf(TourId id) => Path.Combine(ToursPath, $"{id}.json");

   private static readonly StopContent Content = new("A headline.", [new StopPassage(FileSide.Head, "a.txt", 1, 1, PassageRole.Primary, CommitId.Parse(new string('a', 40)), "a")]);

   // A plan with a stop of each kind, the inspect stop written and the risk stop skipped.
   private static Plan SamplePlan() => new(
      [new Chapter("orientation", "Orientation"), new Chapter("pricing", "Pricing")],
      [
         new Stop.Pending("s1", "What this PR changes", StopKind.Overview, "orientation"),
         new Stop.Pending("s2", "How prices were computed before", StopKind.Context, "orientation", new AnchorHint("src/Pricing/PriceCalculator.cs", null)),
         new Stop.Written("s3", "The new discount rule", StopKind.Inspect, "pricing", Content, 1, []),
         new Stop.Pending("s4", "Rounding on stacked discounts", StopKind.Risk, "pricing", Skipped: true),
         new Stop.Pending("s5", "Should the cap be configurable?", StopKind.Question, "pricing"),
      ],
      [new FileMark("src/Pricing/Rates.g.cs", FileMarkKind.Generated, "The build writes it.")]);

   private static IReadOnlyList<QuestionThread> SampleThreads()
   {
      var mergeBase = CommitId.Parse(new string('a', 40));
      var head = CommitId.Parse(new string('b', 40));
      var asked = new DateTimeOffset(2026, 10, 22, 9, 31, 0, TimeSpan.Zero);
      QuestionThread Thread(string id, ThreadAnchor anchor, string? stopId) => ThreadFixtures.Asked(id, anchor, stopId: stopId, at: asked);
      return
      [
         Thread("t1", new ThreadAnchor.CodeRange(FileSide.Base, "src/Pricing/Discounts.cs", 3, 5, mergeBase, "a\r\nb\nc"), "s3"),
         Thread("t2", new ThreadAnchor.StopText("s3", "checks[c1].question", "the cap", 1), "s3"),
         Thread("t3", new ThreadAnchor.WholeFile(FileSide.Head, "logo.png", head), null),
         Thread("t4", new ThreadAnchor.WholeStop("s4"), "s4"),
         Thread("t5", new ThreadAnchor.WholeTour(), null),
      ];
   }

   private sealed record UnregisteredTarget : TourTarget;

   private static TourSynthesis SampleSynthesis() => new(
      "The review keeps the digit.",
      new DateTimeOffset(2026, 10, 22, 9, 31, 0, TimeSpan.Zero),
      new TourPin.Pinned(CommitId.Parse(new string('a', 40)), CommitId.Parse(new string('b', 40)), 1, new Uri("https://platform.test/shop.git")),
      SummaryFingerprint.TryParse("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", out var fingerprint) ? fingerprint : default);

}
