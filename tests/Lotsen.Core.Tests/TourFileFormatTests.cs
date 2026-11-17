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

   [Fact]
   public void Repository_tour_with_a_snapshot_pin_round_trips_through_save_and_load()
   {
      var store = NewStore();
      var tour = store.Create(PullRequest, Familiarity.Some);
      var parent = store.Create(PullRequest, Familiarity.Some);

      var saved = store.Change(tour.Id, current => AsRepositoryTour(current) with { Plan = SamplePlan() with { FileMarks = [] }, Words = "Is the cache safe?", DiveOrigin = new DiveOrigin(parent.Id, parent.Id, "s3"), Archived = true, ArchivedWith = parent.Id });

      Assert.NotNull(saved);
      Assert.Equivalent(saved, NewStore().Get(tour.Id), strict: true);
      var file = JsonNode.Parse(File.ReadAllText(PathOf(tour.Id)))!["tour"]!;
      Assert.Equal(
         """{"kind":"repository","url":"https://platform.test/shop","ref":"release/2.0","platform":"test"}""",
         file["target"]!.ToJsonString());
      Assert.Equal($$"""{"state":"snapshot","head":"{{new string('c', 40)}}","remoteUrl":"https://platform.test/shop.git"}""", file["pin"]!.ToJsonString());
      Assert.Equal(("evaluate", "The cache under load", "Is the cache safe?"), ((string)file["purpose"]!, (string)file["title"]!, (string)file["words"]!));
      Assert.Equal($$"""{"parentId":"{{parent.Id}}","rootId":"{{parent.Id}}","stopId":"s3"}""", file["diveOrigin"]!.ToJsonString());
      Assert.Equal(parent.Id.ToString(), (string)file["archivedWith"]!);
   }

   [Fact]
   public void Repository_target_of_the_default_branch_without_a_platform_leaves_out_its_ref_and_platform()
   {
      var store = NewStore();
      var tour = store.Create(PullRequest, Familiarity.Some);

      store.Change(tour.Id, current => AsRepositoryTour(current) with { Target = new RepositoryTarget(new Uri("https://git.test/shop.git"), null, null) });

      Assert.Equal(new RepositoryTarget(new Uri("https://git.test/shop.git"), null, null), NewStore().Get(tour.Id)?.Target);
      Assert.Equal("""{"kind":"repository","url":"https://git.test/shop.git"}""", JsonNode.Parse(File.ReadAllText(PathOf(tour.Id)))!["tour"]!["target"]!.ToJsonString());
   }

   /// <summary>Each row is the raw JSON of a file that does not decode, with what its dive origin says of the parent.</summary>
   [Theory]
   [InlineData("""{"schemaVersion":23,"tour":{"diveOrigin":{"parentId":"abcdefghijklmnop","rootId":"abcdefghijklmnop"}}}""", "abcdefghijklmnop")]
   [InlineData("""{"schemaVersion":23,"tour":{"title":""}}""", "none")]
   [InlineData("""{"schemaVersion":21,"tour":{"diveOrigin":{"parentId":"abcdefghijklmnop"}}}""", "none")]
   [InlineData("""{"SchemaVersion":23,"Tour":{"DiveOrigin":{"ParentId":"abcdefghijklmnop"}}}""", "abcdefghijklmnop")]
   [InlineData("""{"schemaVersion":23,"tour":{"diveOrigin":null,"DiveOrigin":{"parentId":"abcdefghijklmnop"}}}""", "unknown")]
   [InlineData("""{"schemaVersion":24,"tour":{}}""", "newer")]
   [InlineData("""{"schemaVersion":23,"tour":{"diveOrigin":{"parentId":"not an ID"}}}""", "unknown")]
   [InlineData("""{"schemaVersion":"23","tour":{}}""", "unknown")]
   [InlineData("""{"schemaVersion":23,"tour":{"diveOr""", "unknown")]
   public void Raw_file_names_the_parent_of_a_dive_that_does_not_decode(string json, string expected)
   {
      var parent = TourDocument.ParentIn(System.Text.Encoding.UTF8.GetBytes(json));

      Assert.Equal(expected, parent switch { StoredParent.Of of => of.ParentId.Value, StoredParent.None => "none", StoredParent.NewerLayout => "newer", _ => "unknown" });
   }

   [Fact]
   public void Pull_request_tour_file_holds_none_of_the_properties_of_a_repository_tour()
   {
      var store = NewStore();
      var tour = store.Create(PullRequest, Familiarity.Some);
      store.Change(tour.Id, current => current with { Plan = SamplePlan() });

      var file = JsonNode.Parse(File.ReadAllText(PathOf(tour.Id)))!["tour"]!.AsObject();

      Assert.DoesNotContain(file, property => property.Key is "purpose" or "words" or "title" or "diveOrigin" or "archivedWith");
   }

   /// <summary>A repository's URL is absolute and holds no user information, a ref or a platform ID that is given is not empty, and a dive origin names its root.</summary>
   [Theory]
   [InlineData("\"url\":\"https://platform.test/shop\"", "\"url\":\"shop\"")]
   [InlineData("\"url\":\"https://platform.test/shop\"", "\"url\":\"https://user:secret@platform.test/shop\"")]
   [InlineData("\"ref\":\"release/2.0\"", "\"ref\":\"\"")]
   [InlineData("\"ref\":\"release/2.0\"", "\"ref\":\" \"")]
   [InlineData("\"platform\":\"test\"", "\"platform\":\"\"")]
   [InlineData("\"purpose\":\"evaluate\"", "\"purpose\":\"review\"")]
   [InlineData("\"state\":\"snapshot\"", "\"state\":\"commit\"")]
   [InlineData(",\"rootId\"", ",\"root\"")]
   public void Repository_tour_file_with_an_unknown_or_missing_value_is_refused(string original, string replacement)
   {
      var store = NewStore();
      var tour = store.Create(PullRequest, Familiarity.Some);
      store.Change(tour.Id, current => AsRepositoryTour(current) with { DiveOrigin = new DiveOrigin(TourId.New(), TourId.New()) });
      var content = File.ReadAllText(PathOf(tour.Id));
      Assert.Contains(original, content);
      File.WriteAllText(PathOf(tour.Id), content.Replace(original, replacement));

      Assert.Throws<InvalidDataException>(() => NewStore().Get(tour.Id));
   }

   [Fact]
   public void Version_22_file_reads_as_its_pull_request_tour_and_is_written_back_at_the_current_version()
   {
      var store = NewStore();
      var tour = store.Create(PullRequest, Familiarity.Some);
      var saved = store.Change(tour.Id, current => current with { Plan = SamplePlan(), Pin = new TourPin.Pinned(CommitId.Parse(new string('a', 40)), CommitId.Parse(new string('b', 40)), 2, new Uri("https://platform.test/shop.git")) });
      var current = $"\"schemaVersion\":{TourDocument.CurrentSchemaVersion}";
      var content = File.ReadAllText(PathOf(tour.Id));
      Assert.Contains(current, content);
      File.WriteAllText(PathOf(tour.Id), content.Replace(current, "\"schemaVersion\":22"));

      Assert.Equivalent(saved, NewStore().Get(tour.Id), strict: true);

      NewStore().ChangeMarks(tour.Id, _ => [new ReviewMark("src/a.txt", null)]);

      var written = JsonNode.Parse(File.ReadAllText(PathOf(tour.Id)))!;
      Assert.Equal(TourDocument.CurrentSchemaVersion, (int)written["schemaVersion"]!);
      Assert.Equal(JsonNode.Parse(content)!["tour"]!.ToJsonString(), written["tour"]!.ToJsonString());
   }

   /// <summary>Schema version 22 has no repository tours, so a file of that version that holds one was not written by lotsen.</summary>
   [Fact]
   public void Version_22_file_of_a_repository_tour_is_refused()
   {
      var store = NewStore();
      var tour = store.Create(PullRequest, Familiarity.Some);
      store.Change(tour.Id, AsRepositoryTour);
      var content = File.ReadAllText(PathOf(tour.Id));
      File.WriteAllText(PathOf(tour.Id), content.Replace($"\"schemaVersion\":{TourDocument.CurrentSchemaVersion}", "\"schemaVersion\":22"));

      var exception = Assert.Throws<InvalidDataException>(() => NewStore().Get(tour.Id));

      Assert.Contains("schemaVersion", exception.Message);
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
   [InlineData("\"schemaVersion\":23", "\"schemaVersion\":21")]
   [InlineData("\"schemaVersion\":23", "\"schemaVersion\":24")]
   [InlineData("\"schemaVersion\":23", "\"schemaVersion\":\"23\"")]
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
   [InlineData(TourAction.DraftWorkItemsKind, "decisionThreads", false)]
   [InlineData(TourAction.WalkOpenQuestionsKind, "openQuestionThreads", false)]
   [InlineData(TourAction.DraftCommentsKind, "concernThreads", false)]
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

   /// <summary>
   /// Each kind holds a sent thread, so its file holds every list. A list of threads is optional, so a stored action without one reads back with none, and a null in its place reads as none.
   /// </summary>
   [Theory]
   [InlineData(TourAction.DraftWorkItemsKind, "decisionThreads", false)]
   [InlineData(TourAction.DraftWorkItemsKind, "decisionThreads", true)]
   [InlineData(TourAction.WalkOpenQuestionsKind, "openQuestionThreads", false)]
   [InlineData(TourAction.WalkOpenQuestionsKind, "openQuestionThreads", true)]
   [InlineData(TourAction.DraftCommentsKind, "concernThreads", false)]
   [InlineData(TourAction.DraftCommentsKind, "concernThreads", true)]
   public void Stored_follow_up_action_without_its_threads_reads_back_with_none(string kind, string threads, bool nullList)
   {
      var store = NewStore();
      var tour = store.Create(PullRequest, Familiarity.Some);
      var action = FollowUpActions().Single(candidate => candidate.Kind == kind);
      store.Change(tour.Id, current => current with { Plan = SamplePlan(), Threads = ThreadFixtures.Threads(ActionThread("t1", action)) });
      var tourPath = Path.Combine(ToursPath, $"{tour.Id}.json");
      var file = JsonNode.Parse(File.ReadAllText(tourPath))!;
      var stored = file["tour"]!["threads"]![0]!["action"]!.AsObject();
      Assert.True(stored.Remove(threads), $"The stored action has no {threads}: {stored}");
      var otherMembers = stored.Count;
      if (nullList)
      {
         stored[threads] = null;
      }

      File.WriteAllText(tourPath, file.ToJsonString());

      var read = ((QuestionThread.Sent.Asked)NewStore().Get(tour.Id)!.Threads.Single()).Action!;
      var written = JsonNode.Parse(Json(read))!.AsObject();
      Assert.False(written.ContainsKey(threads), $"The action reads back as {written}");
      Assert.Equal(otherMembers, written.Count);
   }

   private static TourAction[] FollowUpActions()
   {
      var decision = new FollowUpAnswer("s1", "c1", "The cap is right.", AnswerType.Decision, "Discounts", "Is the cap right?");
      var thread = new FollowUpThread("t9", new ThreadAnchor.WholeStop("s1"), "Keep the cap.", AnswerType.Decision, ReviewerThreadState.Resolved, "s1", "Discounts");
      return
      [
         new TourAction.FollowUp.DraftWorkItems([decision], [thread]),
         new TourAction.FollowUp.WalkOpenQuestions(
            [decision with { StopTitle = null, Question = null, Text = "Ask the team.", Type = AnswerType.OpenQuestion }],
            [thread with { Anchor = new ThreadAnchor.WholeTour(), StopId = null, StopTitle = null, Type = AnswerType.OpenQuestion, State = ReviewerThreadState.Open }]),
         new TourAction.FollowUp.DraftComments(
            [decision with { Type = AnswerType.Concern }],
            [new FollowUpRisk("s2", "Rounding", "Two discounts round twice."), new FollowUpRisk("s3", "Eviction", "Eviction drops a hot entry.")],
            [thread with { Type = AnswerType.Concern, State = ReviewerThreadState.Open }]),
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

   [Fact]
   [Trait("Category", "Summary")]
   public void Stored_synthesis_of_a_repository_tour_reads_back_with_its_snapshot_as_the_snapshot_object_without_its_state()
   {
      var store = NewStore();
      var tour = store.Create(PullRequest, Familiarity.Some);
      var snapshot = (TourPin.Snapshot)AsRepositoryTour(tour).Pin;
      var synthesis = SampleSynthesis(new SynthesisPin.Repository(snapshot));

      store.Change(tour.Id, current => AsRepositoryTour(current) with { Synthesis = synthesis });

      Assert.Equal(synthesis, NewStore().Get(tour.Id)!.Synthesis);
      Assert.Equal(
         $$"""{"head":"{{snapshot.Head.Value}}","remoteUrl":"https://platform.test/shop.git"}""",
         JsonNode.Parse(File.ReadAllText(PathOf(tour.Id)))!["tour"]!["synthesis"]!["pin"]!.ToJsonString());
   }

   /// <summary>The pin is the object that schema version 22 holds, a pull request's pin without its <c>state</c>, so a synthesis that an earlier release stored reads.</summary>
   [Fact]
   [Trait("Category", "Summary")]
   public void Synthesis_of_a_pull_request_keeps_the_pin_object_of_version_22()
   {
      const string Pin = """{"mergeBase":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","head":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","iteration":1,"remoteUrl":"https://platform.test/shop.git"}""";
      var store = NewStore();
      var tour = store.Create(PullRequest, Familiarity.Some);
      store.Change(tour.Id, current => current with { Synthesis = SampleSynthesis() });
      var content = File.ReadAllText(PathOf(tour.Id));

      Assert.Equal(Pin, JsonNode.Parse(content)!["tour"]!["synthesis"]!["pin"]!.ToJsonString());
      File.WriteAllText(PathOf(tour.Id), content.Replace($"\"schemaVersion\":{TourDocument.CurrentSchemaVersion}", "\"schemaVersion\":22"));
      Assert.Equal(SampleSynthesis(), NewStore().Get(tour.Id)!.Synthesis);
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

   // A repository tour of a release branch on the test platform, at a snapshot, built from a stored pull request's tour.
   private static Tour AsRepositoryTour(Tour tour) => tour with
   {
      Target = new RepositoryTarget(new Uri("https://platform.test/shop"), "release/2.0", "test"),
      Pin = new TourPin.Snapshot(CommitId.Parse(new string('c', 40)), new Uri("https://platform.test/shop.git")),
      Purpose = TourPurpose.Evaluate,
      Title = "The cache under load",
   };

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

   private static TourSynthesis SampleSynthesis(SynthesisPin? pin = null) => new(
      "The review keeps the digit.",
      new DateTimeOffset(2026, 10, 22, 9, 31, 0, TimeSpan.Zero),
      pin ?? new SynthesisPin.PullRequest(new TourPin.Pinned(CommitId.Parse(new string('a', 40)), CommitId.Parse(new string('b', 40)), 1, new Uri("https://platform.test/shop.git"))),
      SummaryFingerprint.TryParse("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", out var fingerprint) ? fingerprint : default);

}
