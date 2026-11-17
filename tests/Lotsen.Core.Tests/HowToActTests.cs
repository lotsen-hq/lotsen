using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Lotsen.Json;
using Lotsen.Operations;
using Lotsen.Tours;

namespace Lotsen.Tests;

/// <summary>Covers the <c>howToAct</c> field of <c>fetch_question</c>: which questions hold a rule, and what the rule of each tour action and reviewer decision tells Claude.</summary>
[Trait("Category", "Adapter")]
public sealed partial class HowToActTests : IAsyncLifetime
{
   private const string ThreadId = "t1";
   private const string OpeningId = "q-t1";
   private const string FollowUpId = "q-follow-up";
   private const string OutdatedLeftOut = "lotsen leaves out an outdated thread, as it leaves out an outdated answer";
   private const string ThreadsAreTheirs = "A thread item is the reviewer's own finding, in the reviewer's words, not yours";

   private static readonly string BasedOn = SkillTests.PropertyName(LotsenJsonContext.Default.WriteSynthesisInput, nameof(WriteSynthesisInput.BasedOn));
   private static readonly string Fingerprint = SkillTests.PropertyName(LotsenJsonContext.Default.ReviewSummary, nameof(ReviewSummary.SummaryFingerprint));
   private static readonly string TypedThreads = SkillTests.PropertyName(LotsenJsonContext.Default.ReviewSummary, nameof(ReviewSummary.TypedThreads));
   private static readonly string StaleMarker = SkillTests.PropertyName(LotsenJsonContext.Default.WrittenStop, nameof(Stop.Written.StaleOn));
   private static readonly string DiveOrigin = SkillTests.PropertyName(LotsenJsonContext.Default.CreateTourInput, nameof(CreateTourInput.DiveOrigin));
   private static readonly string ParentTourId = SkillTests.PropertyName(LotsenJsonContext.Default.CreateTourDive, nameof(CreateTourDive.ParentTourId));

   private static readonly IReadOnlyDictionary<string, (string Sample, string Carrier, string[] Says)> Actions = new Dictionary<string, (string, string, string[])>
   {
      [TourAction.ExplainKind] = (
         """{ "kind": "explain" }""",
         "put_stop on the anchor's stop",
         ["more deeply than the stop does now, at the tour's familiarity level", "Carry out explain with put_stop on the anchor's stop", "never only into your reply", "mostly how, and keep the rest as it is", "needsAnswer", "carry the action out then, and reply to the follow-up"]),
      [TourAction.ReviseKind] = (
         """{ "kind": "revise" }""",
         "put_stop on the anchor's stop",
         ["as the reviewer's words say", "Carry out revise with put_stop on the anchor's stop", "never only into your reply", "needsAnswer", "declined"]),
      [TourAction.ReplanKind] = (
         """{ "kind": "replan" }""",
         "set_plan on the whole plan",
         ["such as add, drop, merge or split stops", "Carry out replan with set_plan on the whole plan", "its parts and file marks included, because set_plan replaces them", "give every stop that stays the id it has, so it keeps its content, its versions and the reviewer's answers and threads", "such as an overview that lists the stops", "Write the plan's new stops after the reply", "a skip says that the reviewer does not need that stop", "Keep a skipped stop unless the reviewer's words drop it, because the reviewer can take the skip back"]),
      [TourAction.ReorderKind] = (
         """{ "kind": "reorder", "stopIds": [] }""",
         "set_plan on the whole plan",
         ["the action's stopIds, which name every stop of the plan once", "Carry out reorder with set_plan on the whole plan", "its parts and file marks included", "give the chapters, and the parts if the plan has any, to the new order", "give every stop that stays the id it has", "A proposed order is a request", "say in the reply where your order differs from the proposal and why", "Write the plan's new stops after the reply"]),
      [TourAction.SummarizeKind] = (
         """{ "kind": "summarize" }""",
         "write_synthesis",
         ["say what to focus on", "Carry out summarize with write_synthesis", "with read_summary and the tour with read_state", "what the review decided, what worries the reviewer and what stays open", "Name the open questions and the concerns that no answer settled yet", $"{TypedThreads} are the reviewer's own findings", "as the reviewer's, never in your own voice", "Write the synthesis last, after any other change to the tour.", "read the summary again after such a write", $"Pass as {BasedOn} the {Fingerprint} of the summary that you read", "so write the whole synthesis each time", "never the synthesis itself"]),
      [TourAction.DiveKind] = (
         """{ "kind": "dive", "purpose": "prepare", "familiarity": "some" }""",
         $"create_tour and its {DiveOrigin}",
         [$"Carry out dive with create_tour and its {DiveOrigin}", $"{ParentTourId} is this tour's tourId", "stopId is the anchor's stop when the anchor is a stop", "purpose is the action's purpose", "Pass the action's familiarity as familiarity", "lotsen pins the dive to this tour's commit", "a reply of one sentence that names the dive's title", "write the dive after the reply as a tour of its purpose"]),
      [TourAction.DraftWorkItemsKind] = (
         """{ "kind": "draftWorkItems", "decisions": [] }""",
         "only after the user approves it in the terminal",
         ["the decisions that the reviewer had sent when they asked", "never their draft answers", "lotsen itself creates no work item, posts no comment and changes nothing in the repository", "Say which decisions need none", "Create a work item with your own tools, such as the hosting platform's CLI, only after the user approves it in the terminal", "names each work item you created, or says that the user approved none", "never their draft answers or their unsent notes", ThreadsAreTheirs, OutdatedLeftOut, "A resolved item of decisionThreads is a decision that the reviewer settled in its thread, and it can still need a work item"]),
      [TourAction.WalkOpenQuestionsKind] = (
         """{ "kind": "walkOpenQuestions", "openQuestions": [] }""",
         "start_thread",
         ["never their draft answers", "lotsen itself creates no work item, posts no comment and changes nothing in the repository", "Walk the reviewer through them in the UI, not in the terminal: start one thread with start_thread for each item of openQuestions", "field checks[<checkId>].question", "anchor it on the stop with stop", "then your recommendation and why", "how many threads you started", "reply to each as to any question", "add to that thread with add_thread_message and its threadId instead of starting a new one", "answer that question with reply instead", "never their draft answers or their unsent notes", ThreadsAreTheirs, OutdatedLeftOut, "lotsen also leaves out an open question thread that the reviewer resolved, because the reviewer settled it there"]),
      [TourAction.DraftCommentsKind] = (
         """{ "kind": "draftComments", "concerns": [], "risks": [] }""",
         "after the user approves it in the terminal",
         ["never their draft answers", "lotsen itself creates no work item, posts no comment and changes nothing in the repository", "its risks the risk stops that you wrote and the reviewer did not skip, a stale one included", "because you post a comment in the reviewer's name", "read its code again at the tour's pin", "Say which concerns need none", "Return the drafts as text in your reply", "ask the user in the terminal which drafts to post", "Post a comment only with your own tools, such as the hosting platform's CLI, after the user approves it in the terminal", "never their draft answers or their unsent notes", ThreadsAreTheirs, OutdatedLeftOut, "lotsen also leaves out a concern thread that the reviewer resolved, because the discussion settled it"]),
   };

   // What the summarize rule of a repository tour asks for, by the purpose's JSON name, in the words of the rule.
   private static readonly IReadOnlyDictionary<string, string[]> Syntheses = new Dictionary<string, string[]>
   {
      ["orient"] = ["a map of the code for the reviewer to keep", "its parts and how they fit together", "the vocabulary that the code uses", "the main paths through it", "where to look next"],
      ["prepare"] = ["a change plan", "what to change and in what order", "what each change touches", "what the tests pin", "the risks"],
      ["evaluate"] = ["a judgment", "whether to rely on the code", "its strengths and its weak spots", "what stays open", "Name the open questions and the concerns that no answer settled yet"],
   };

   // What the draftWorkItems rule of a repository tour asks about where the work items go, in the words of the rule.
   private static readonly string[] RepositoryWorkItems =
   [
      "ask the user in the terminal where the work items belong: the user's own project, or the tracker of the project that the tour shows",
      "Create a work item there with your own tools only after the user approves its draft in the terminal",
      "create none before that",
   ];

   private static readonly IReadOnlyDictionary<string, (string Sample, string Carrier, string[] Says)> Decisions = new Dictionary<string, (string, string, string[])>
   {
      [ReviewerDecision.FamiliarityChangedKind] = (
         """{ "kind": "familiarityChanged", "from": "some", "to": "new", "stopIds": [] }""",
         "put_stop",
         ["those after the stop the reviewer views, or every stop, that have content and no skip", "That decision keeps the earlier from", "Act on familiarityChanged in this order. Read the new level with read_state", "few or no context stops", "Then revise with put_stop each stop of stopIds that the plan still has", "Apply the reviewer's words too", "say in the reply where you did not follow them and why", "Write the stops that have no content yet at the new level after the reply", "Leave the stops before the one the reviewer views as they are", "A declined reply leaves the decision made"]),
      [ReviewerDecision.PlanApprovedKind] = (
         """{ "kind": "planApproved", "stopIds": [], "skippedStops": [], "familiarity": "some" }""",
         "put_stop",
         ["Act on planApproved in this order", "give every stop the id it has, and keep each skipped stop", "write the stops with put_stop at the tour's familiarity level as read_state shows it now", "in plan order", "say in the reply where your order differs from the proposal and why", "Say in the reply where your plan differs from the reviewer's order or words and why", "A declined reply leaves the decision made"]),
      [ReviewerDecision.IterationUpdatedKind] = (
         """{ "kind": "iterationUpdated", "iteration": 2, "stopIds": [] }""",
         "put_stop",
         [StaleMarker, "Act on iterationUpdated with put_stop on each stop of stopIds against the tour's new pin", "still points at the iteration before the one", "read the code again at its commits", "point such a passage at the file's base side, or leave it out", "Leave the other stops as they are", "A declined reply leaves the decision made"]),
      [ReviewerDecision.TourMovedKind] = (
         $$"""{ "kind": "tourMoved", "move": 1, "commit": "{{new string('c', 40)}}", "stopIds": [] }""",
         "put_stop",
         [StaleMarker, "Act on tourMoved with put_stop on each stop of stopIds against the tour's new pin", "still points at the commit before that move", "read the code again at that commit", "so leave such a passage out", "Leave the other stops as they are", "A declined reply leaves the decision made"]),
   };

   private ServerHarness _harness = null!;
   private ThreadCalls _calls = null!;

   public static TheoryData<string> ActionKinds => [.. LotsenJsonContext.Default.TourAction.PolymorphismOptions!.DerivedTypes.Select(derived => (string)derived.TypeDiscriminator!)];

   public static TheoryData<string> Purposes => [.. Enum.GetValues<TourPurpose>().Select(purpose => JsonSerializer.Serialize(purpose, LotsenJsonContext.Default.TourPurpose).Trim('"'))];

   public static TheoryData<string> DecisionKinds => [.. LotsenJsonContext.Default.ReviewerDecision.PolymorphismOptions!.DerivedTypes.Select(derived => (string)derived.TypeDiscriminator!)];

   public async ValueTask InitializeAsync()
   {
      _harness = await ServerHarness.StartAsync();
      _calls = new ThreadCalls(_harness);
   }

   public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

   /// <summary>The kinds come from the JSON contract of <see cref="TourAction"/>, so a kind that the code adds fails here until <see cref="Actions"/> holds it.</summary>
   [Theory]
   [MemberData(nameof(ActionKinds))]
   public async Task Tour_action_returns_the_rule_of_its_kind(string kind)
   {
      var (_, _, says) = Actions[kind];

      var howToAct = await RuleAsync(kind);

      Assert.Contains($"The action \"{kind}\" asks you to", howToAct, StringComparison.Ordinal);
      Assert.All(says, phrase => Assert.Contains(phrase, howToAct, StringComparison.Ordinal));
   }

   /// <summary>The kinds come from the JSON contract of <see cref="ReviewerDecision"/>, so a kind that the code adds fails here until <see cref="Decisions"/> holds it.</summary>
   [Theory]
   [MemberData(nameof(DecisionKinds))]
   public async Task Reviewer_decision_returns_the_rule_of_its_kind(string kind)
   {
      var (_, _, says) = Decisions[kind];

      var howToAct = await RuleAsync(kind);

      Assert.Contains($"The decision \"{kind}\" says that the reviewer", howToAct, StringComparison.Ordinal);
      Assert.DoesNotContain("asks you", howToAct, StringComparison.Ordinal);
      Assert.All(says, phrase => Assert.Contains(phrase, howToAct, StringComparison.Ordinal));
   }

   [Theory]
   [MemberData(nameof(ActionKinds))]
   [MemberData(nameof(DecisionKinds))]
   public async Task Rule_names_the_tool_or_the_approval_step_that_carries_out_its_kind(string kind)
   {
      var carrier = Actions.TryGetValue(kind, out var action) ? action.Carrier : Decisions[kind].Carrier;

      Assert.Contains(carrier, await RuleAsync(kind), StringComparison.Ordinal);
   }

   /// <summary>A session without the skills reads the rule, so a citation of the skills' sections would lead nowhere. The headings come from the skills, so a section that either adds counts.</summary>
   [Theory]
   [MemberData(nameof(ActionKinds))]
   [MemberData(nameof(DecisionKinds))]
   public async Task Rule_cites_no_section_of_the_skill(string kind)
   {
      var howToAct = await RuleAsync(kind);

      Assert.DoesNotMatch("(?i)section [0-9]", howToAct);
      Assert.All(SkillHeadings(), heading => Assert.DoesNotContain($"\"{heading}\"", howToAct, StringComparison.Ordinal));
   }

   /// <summary>The purposes come from the JSON contract of <see cref="TourPurpose"/>, so a purpose that the code adds fails here until <see cref="Syntheses"/> holds it.</summary>
   [Theory]
   [MemberData(nameof(Purposes))]
   public async Task Summarize_rule_of_a_repository_tour_asks_for_the_synthesis_of_its_purpose_with_the_reviewers_findings_as_theirs(string purpose)
   {
      var howToAct = await RuleAsync(TourAction.SummarizeKind, purpose);
      var others = Syntheses.Where(other => other.Key != purpose).SelectMany(other => other.Value).Except(Syntheses[purpose]);

      Assert.All(Syntheses[purpose], phrase => Assert.Contains(phrase, howToAct, StringComparison.Ordinal));
      Assert.All(others, phrase => Assert.DoesNotContain(phrase, howToAct, StringComparison.Ordinal));
      Assert.DoesNotContain("what worries the reviewer", howToAct, StringComparison.Ordinal);
      Assert.All(
         ["Carry out summarize with write_synthesis", "with ref: links to the code each point is about", $"{TypedThreads} are the reviewer's own findings", "as the reviewer's, never in your own voice", $"Pass as {BasedOn} the {Fingerprint} of the summary that you read", "never the synthesis itself"],
         phrase => Assert.Contains(phrase, howToAct, StringComparison.Ordinal));
   }

   [Fact]
   public async Task Summarize_rule_of_a_pull_requests_tour_asks_for_no_synthesis_of_a_purpose()
   {
      var howToAct = await RuleAsync(TourAction.SummarizeKind);
      var purposeOnly = Syntheses.Values.SelectMany(phrases => phrases).Where(phrase => !Actions[TourAction.SummarizeKind].Says.Any(says => says.Contains(phrase, StringComparison.Ordinal)));

      Assert.DoesNotContain("purpose", howToAct, StringComparison.Ordinal);
      Assert.All(purposeOnly, phrase => Assert.DoesNotContain(phrase, howToAct, StringComparison.Ordinal));
   }

   [Theory]
   [MemberData(nameof(Purposes))]
   public async Task Draft_work_items_rule_of_a_repository_tour_asks_where_the_work_items_belong_and_files_none_before_the_user_approves(string purpose)
   {
      var howToAct = await RuleAsync(TourAction.DraftWorkItemsKind, purpose);

      Assert.All(RepositoryWorkItems, phrase => Assert.Contains(phrase, howToAct, StringComparison.Ordinal));
      Assert.DoesNotContain("pull request", howToAct, StringComparison.Ordinal);
      Assert.All(
         ["the decisions that the reviewer had sent when they asked", "Say which decisions need none", "lotsen itself creates no work item", "names each work item you created, or says that the user approved none", ThreadsAreTheirs],
         phrase => Assert.Contains(phrase, howToAct, StringComparison.Ordinal));
   }

   [Fact]
   public async Task Draft_work_items_rule_of_a_pull_requests_tour_asks_nothing_about_where_the_work_items_belong()
   {
      var howToAct = await RuleAsync(TourAction.DraftWorkItemsKind);

      Assert.All(RepositoryWorkItems, phrase => Assert.DoesNotContain(phrase, howToAct, StringComparison.Ordinal));
      Assert.Contains("such as a change that the reviewer decided on and the pull request does not make", howToAct, StringComparison.Ordinal);
   }

   [Theory]
   [InlineData(TourAction.ExplainKind)]
   [InlineData(TourAction.ReviseKind)]
   [InlineData(TourAction.ReplanKind)]
   [InlineData(TourAction.ReorderKind)]
   [InlineData(ReviewerDecision.FamiliarityChangedKind)]
   [InlineData(ReviewerDecision.PlanApprovedKind)]
   [InlineData(ReviewerDecision.IterationUpdatedKind)]
   [InlineData(ReviewerDecision.TourMovedKind)]
   public async Task Rule_of_a_kind_that_writes_stops_says_what_each_familiarity_level_asks(string kind)
   {
      var howToAct = await RuleAsync(kind);
      var levels = Enum.GetValues<Familiarity>().Select(level => JsonSerializer.Serialize(level, LotsenJsonContext.Default.Familiarity).Trim('"'));

      Assert.All(levels, level => Assert.Contains($"{level}: ", howToAct, StringComparison.Ordinal));
      Assert.Contains("go deeper on the why and the design choices", howToAct, StringComparison.Ordinal);
   }

   /// <summary>The names of the item lists and of the items' fields come from the stored actions, so a renamed one must reach the rule.</summary>
   [Theory]
   [InlineData(TourAction.DraftWorkItemsKind)]
   [InlineData(TourAction.WalkOpenQuestionsKind)]
   [InlineData(TourAction.DraftCommentsKind)]
   public async Task Rule_of_a_follow_up_action_names_its_item_lists_and_the_fields_of_their_items(string kind)
   {
      var howToAct = await RuleAsync(kind);
      string[] lists = kind switch
      {
         TourAction.DraftWorkItemsKind =>
         [
            SkillTests.PropertyName(LotsenJsonContext.Default.DraftWorkItemsAction, nameof(TourAction.FollowUp.DraftWorkItems.Decisions)),
            SkillTests.PropertyName(LotsenJsonContext.Default.DraftWorkItemsAction, nameof(TourAction.FollowUp.DraftWorkItems.DecisionThreads)),
         ],
         TourAction.WalkOpenQuestionsKind =>
         [
            SkillTests.PropertyName(LotsenJsonContext.Default.WalkOpenQuestionsAction, nameof(TourAction.FollowUp.WalkOpenQuestions.OpenQuestions)),
            SkillTests.PropertyName(LotsenJsonContext.Default.WalkOpenQuestionsAction, nameof(TourAction.FollowUp.WalkOpenQuestions.OpenQuestionThreads)),
         ],
         _ =>
         [
            SkillTests.PropertyName(LotsenJsonContext.Default.DraftCommentsAction, nameof(TourAction.FollowUp.DraftComments.Concerns)),
            SkillTests.PropertyName(LotsenJsonContext.Default.DraftCommentsAction, nameof(TourAction.FollowUp.DraftComments.Risks)),
            SkillTests.PropertyName(LotsenJsonContext.Default.DraftCommentsAction, nameof(TourAction.FollowUp.DraftComments.ConcernThreads)),
         ],
      };
      var fields = LotsenJsonContext.Default.FollowUpAnswer.Properties
         .Concat(kind == TourAction.DraftCommentsKind ? LotsenJsonContext.Default.FollowUpRisk.Properties : [])
         .Select(property => property.Name);

      Assert.All(lists, list => Assert.Contains($"{list} ", howToAct, StringComparison.Ordinal));
      Assert.All(fields, field => Assert.Contains(field, howToAct, StringComparison.Ordinal));
      Assert.Contains(ThreadItemFields(), howToAct, StringComparison.Ordinal);
      Assert.All(ThreadItemsNowRule(), phrase => Assert.Contains(phrase, howToAct, StringComparison.Ordinal));
   }

   /// <summary>The kinds whose <c>redo</c> <see cref="TourThreads.CheckRedo"/> checks, and the others, whose rule leaves the field out.</summary>
   [Theory]
   [InlineData(TourAction.ExplainKind, false)]
   [InlineData(TourAction.ReviseKind, false)]
   [InlineData(TourAction.ReplanKind, false)]
   [InlineData(TourAction.ReorderKind, true)]
   [InlineData(TourAction.SummarizeKind, false)]
   [InlineData(TourAction.DiveKind, false)]
   [InlineData(TourAction.DraftWorkItemsKind, false)]
   [InlineData(TourAction.WalkOpenQuestionsKind, false)]
   [InlineData(TourAction.DraftCommentsKind, false)]
   [InlineData(ReviewerDecision.FamiliarityChangedKind, true)]
   [InlineData(ReviewerDecision.PlanApprovedKind, true)]
   [InlineData(ReviewerDecision.IterationUpdatedKind, true)]
   [InlineData(ReviewerDecision.TourMovedKind, true)]
   public async Task Rule_names_redo_and_how_to_decline_a_redo_that_no_longer_fits_only_for_a_checked_kind(string kind, bool checks)
   {
      var howToAct = await RuleAsync(kind);
      var redo = SkillTests.PropertyName(LotsenJsonContext.Default.FetchedQuestion, nameof(FetchedQuestion.Redo));
      string[] phrases =
      [
         $"fetch_question's {redo} says whether you can still redo this work on the tour as it is now",
         "it is absent while you can, and otherwise holds the reason with what the reviewer can do instead",
         $"When a follow-up in this thread asks for the work again and {redo} holds a reason, do not redo it: quote the reason in your reply",
         "still answer any question that the follow-up asks",
         "A follow-up that answers your needsAnswer asks for work that you have not done yet",
      ];

      if (checks)
      {
         Assert.All(phrases, phrase => Assert.Contains(phrase, howToAct, StringComparison.Ordinal));
      }
      else
      {
         Assert.DoesNotContain($"fetch_question's {redo} ", howToAct, StringComparison.Ordinal);
      }
   }

   // The phrases of the rule that name fetch_question's threadItems, every field of their items and every reason, and that tell Claude to skip an item that no longer applies on a redo.
   private static string[] ThreadItemsNowRule()
   {
      static string Name(string property) => SkillTests.PropertyName(LotsenJsonContext.Default.ThreadItemNow, property);
      var fields = LotsenJsonContext.Default.ThreadItemNow.Properties.Select(property => property.Name).Order(StringComparer.Ordinal);
      Assert.Equal(fields, new[] { Name(nameof(ThreadItemNow.ThreadId)), Name(nameof(ThreadItemNow.Applies)), Name(nameof(ThreadItemNow.Reason)) }.Order(StringComparer.Ordinal));
      var reasons = Enum.GetValues<ThreadItemLapse>().Select(reason => JsonSerializer.Serialize(reason, LotsenJsonContext.Default.ThreadItemLapse).Trim('"')).ToArray();
      Assert.Equal(3, reasons.Length);
      var threadItems = SkillTests.PropertyName(LotsenJsonContext.Default.FetchedQuestion, nameof(FetchedQuestion.ThreadItems));
      return
      [
         $"fetch_question's {threadItems} say for each thread item, by its {Name(nameof(ThreadItemNow.ThreadId))}, whether it still applies now",
         $"its {Name(nameof(ThreadItemNow.Reason))} then says which: {reasons[0]}, {reasons[1]} or {reasons[2]}",
         $"When a follow-up in this thread asks you to carry out or redo the work, skip each thread item whose {Name(nameof(ThreadItemNow.Applies))} is false",
         "say in your reply which items you skipped and why",
      ];
   }

   // The phrase of the rule that names a thread item's fields. It names every field of FollowUpThread.
   private static string ThreadItemFields()
   {
      static string Name(string property) => SkillTests.PropertyName(LotsenJsonContext.Default.FollowUpThread, property);
      var fields = LotsenJsonContext.Default.FollowUpThread.Properties.Select(property => property.Name).Order(StringComparer.Ordinal);
      string[] named =
      [
         Name(nameof(FollowUpThread.Anchor)), Name(nameof(FollowUpThread.State)), Name(nameof(FollowUpThread.StopId)), Name(nameof(FollowUpThread.StopTitle)),
         Name(nameof(FollowUpThread.Text)), Name(nameof(FollowUpThread.ThreadId)), Name(nameof(FollowUpThread.Type)),
      ];
      Assert.Equal(fields, named.Order(StringComparer.Ordinal));
      return $"each with its {Name(nameof(FollowUpThread.ThreadId))}, its {Name(nameof(FollowUpThread.Anchor))}, its {Name(nameof(FollowUpThread.StopId))} when the thread is about a stop and its "
         + $"{Name(nameof(FollowUpThread.StopTitle))} when the plan still has that stop, the reviewer's {Name(nameof(FollowUpThread.Text))}, which is the thread's first question, its "
         + $"{Name(nameof(FollowUpThread.Type))} and its {Name(nameof(FollowUpThread.State))} when the reviewer asked, open or resolved";
   }

   [Fact]
   public async Task Plain_question_has_no_rule()
   {
      var fetched = await FetchAsync(ThreadFixtures.Asked(ThreadId, new ThreadAnchor.WholeTour()));

      Assert.False(fetched.AsObject().ContainsKey("howToAct"), $"fetched: {fetched}");
   }

   [Fact]
   public async Task Follow_up_in_the_thread_of_a_tour_action_has_the_rule_of_the_action()
   {
      var thread = ThreadWithFollowUp("asked");
      thread["action"] = JsonNode.Parse(Actions[TourAction.ReviseKind].Sample);

      var fetched = await FetchAsync(ThreadFixtures.Parse(thread), FollowUpId);

      Assert.Equal(await RuleAsync(TourAction.ReviseKind), HowToActOf(fetched));
   }

   [Fact]
   public async Task Follow_up_in_a_thread_without_an_action_has_no_rule()
   {
      var fetched = await FetchAsync(ThreadFixtures.Parse(ThreadWithFollowUp("asked")), FollowUpId);

      Assert.False(fetched.AsObject().ContainsKey("howToAct"), $"fetched: {fetched}");
   }

   [Fact]
   public async Task Follow_up_in_the_thread_of_a_reviewer_decision_has_the_rule_of_the_decision()
   {
      var thread = ThreadWithFollowUp("decided");
      thread["decision"] = JsonNode.Parse(Decisions[ReviewerDecision.IterationUpdatedKind].Sample);

      var fetched = await FetchAsync(ThreadFixtures.Parse(thread), FollowUpId);

      Assert.Equal(await RuleAsync(ReviewerDecision.IterationUpdatedKind), HowToActOf(fetched));
   }

   /// <summary>A move is only for a repository tour, which has no pull request, no iteration and no base side.</summary>
   [Fact]
   public async Task Move_rule_speaks_of_the_commit_and_never_of_a_pull_request()
   {
      var howToAct = await RuleAsync(ReviewerDecision.TourMovedKind, purpose: "orient");

      Assert.All(["pull request", "iteration", "mergeBase", "base side"], word => Assert.DoesNotContain(word, howToAct, StringComparison.OrdinalIgnoreCase));
      Assert.Contains("Read pin.head with read_state", howToAct, StringComparison.Ordinal);
   }

   // The headings of the tour-pr skill and its rules skill, without a section's number.
   private static IEnumerable<string> SkillHeadings() =>
      new[] { SkillTests.TourPr, SkillTests.Rules }.SelectMany(skill => HeadingLine().Matches(skill)).Select(match => match.Groups[1].Value.TrimEnd());

   private static TourAction Action(string json) => JsonSerializer.Deserialize(json, LotsenJsonContext.Default.TourAction)!;

   private static string HowToActOf(JsonNode fetched)
   {
      var howToAct = (string?)fetched["howToAct"];
      Assert.False(string.IsNullOrWhiteSpace(howToAct), $"fetched: {fetched}");
      return howToAct;
   }

   // A first question that the session answered, then the follow-up that waits for its reply.
   private static JsonObject ThreadWithFollowUp(string kind)
   {
      var at = ThreadFixtures.At;
      return ThreadFixtures.Thread(kind, ThreadId, new ThreadAnchor.WholeTour(), QuestionThreadState.Open,
      [
         ThreadFixtures.Question(OpeningId, "", null, at, ("r-t1", RequestState.Done, "session1")),
         ThreadFixtures.Reply("a-t1", "r-t1", "session1", "Done.", at),
         ThreadFixtures.Question(FollowUpId, "Why this way?", null, at, ("r-follow-up", RequestState.Pending, "session1")),
      ]);
   }

   // The rule in a pull request's tour, or in a repository tour of the purpose when one is given.
   private async Task<string> RuleAsync(string kind, string? purpose = null) => HowToActOf(Actions.TryGetValue(kind, out var action)
      ? await FetchAsync(ThreadFixtures.Asked(ThreadId, new ThreadAnchor.WholeTour(), action: Action(action.Sample), text: ""), purpose: purpose)
      : await FetchAsync(ThreadFixtures.Decided(ThreadId, ThreadFixtures.Decision(Decisions[kind].Sample)), purpose: purpose));

   private async Task<JsonNode> FetchAsync(QuestionThread thread, string questionId = OpeningId, string? purpose = null)
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var tourId = purpose is null
         ? await _calls.CreateAsync(session)
         : (string)(await _calls.CallAsync(_calls.Agent(session, "create_tour"), RepositoryPinningTests.CreateBody(@ref: null, purpose: purpose)))["id"]!;
      Assert.True(TourId.TryParse(tourId, out var id), $"The tour ID {tourId} does not parse.");
      _harness.ChangeStored(id, tour => tour with { Threads = ThreadFixtures.Threads(thread) });
      return await _calls.FetchAsync(session, tourId, questionId);
   }

   [GeneratedRegex(@"^#+ (?:[0-9]+\. )?(.+)$", RegexOptions.Multiline)]
   private static partial Regex HeadingLine();
}

