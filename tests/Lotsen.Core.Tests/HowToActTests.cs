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

   private static readonly string BasedOn = SkillTests.PropertyName(LotsenJsonContext.Default.WriteSynthesisInput, nameof(WriteSynthesisInput.BasedOn));
   private static readonly string Fingerprint = SkillTests.PropertyName(LotsenJsonContext.Default.ReviewSummary, nameof(ReviewSummary.SummaryFingerprint));
   private static readonly string StaleMarker = SkillTests.PropertyName(LotsenJsonContext.Default.WrittenStop, nameof(Stop.Written.StaleOn));

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
         ["say what to focus on", "Carry out summarize with write_synthesis", "with read_summary and the tour with read_state", "what the review decided, what worries the reviewer and what stays open", "Name the open questions and the concerns that no answer settled yet", "Write the synthesis last, after any other change to the tour.", "read the summary again after such a write", $"Pass as {BasedOn} the {Fingerprint} of the summary that you read", "so write the whole synthesis each time", "never the synthesis itself"]),
      [TourAction.DraftWorkItemsKind] = (
         """{ "kind": "draftWorkItems", "decisions": [] }""",
         "only after the user approves it in the terminal",
         ["the decisions that the reviewer had sent when they asked", "never their draft answers", "lotsen itself creates no work item, posts no comment and changes nothing in the repository", "Say which decisions need none", "Create a work item with your own tools, such as the hosting platform's CLI, only after the user approves it in the terminal", "names each work item you created, or says that the user approved none"]),
      [TourAction.WalkOpenQuestionsKind] = (
         """{ "kind": "walkOpenQuestions", "openQuestions": [] }""",
         "start_thread",
         ["never their draft answers", "lotsen itself creates no work item, posts no comment and changes nothing in the repository", "Walk the reviewer through them in the UI, not in the terminal: start one thread with start_thread for each open question", "field checks[<checkId>].question", "anchor it on the stop with stop", "then your recommendation and why", "how many threads you started", "reply to each as to any question"]),
      [TourAction.DraftCommentsKind] = (
         """{ "kind": "draftComments", "concerns": [], "risks": [] }""",
         "after the user approves it in the terminal",
         ["never their draft answers", "lotsen itself creates no work item, posts no comment and changes nothing in the repository", "its risks the risk stops that you wrote and the reviewer did not skip, a stale one included", "because you post a comment in the reviewer's name", "read its code again at the tour's pin", "Say which concerns need none", "Return the drafts as text in your reply", "ask the user in the terminal which drafts to post", "Post a comment only with your own tools, such as the hosting platform's CLI, after the user approves it in the terminal"]),
   };

   private static readonly IReadOnlyDictionary<string, (string Sample, string Carrier, string[] Says)> Decisions = new Dictionary<string, (string, string, string[])>
   {
      [ReviewerDecision.FamiliarityChangedKind] = (
         """{ "kind": "familiarityChanged", "from": "some", "to": "new", "stopIds": [] }""",
         "put_stop",
         ["those after the stop the reviewer views, or every stop, that have content and no skip", "That decision keeps the earlier from", "Act on familiarityChanged in this order. Read the new level with read_state", "few or no context stops", "Then revise with put_stop each stop of stopIds that the plan still has", "Apply the reviewer's words too", "say in the reply where you did not follow them and why", "Write the stops that have no content yet at the new level after the reply", "Leave the stops before the one the reviewer views as they are", "A declined reply leaves the decision made"]),
      [ReviewerDecision.PlanApprovedKind] = (
         """{ "kind": "planApproved", "stopIds": [], "skippedStops": [], "familiarity": "some" }""",
         "put_stop",
         ["Act on planApproved in this order", "give every stop the id it has, and keep each skipped stop", "write the stops with put_stop at the decision's familiarity", "in plan order", "say in the reply where your order differs from the proposal and why", "Say in the reply where your plan differs from the reviewer's order or words and why", "A declined reply leaves the decision made"]),
      [ReviewerDecision.IterationUpdatedKind] = (
         """{ "kind": "iterationUpdated", "iteration": 2, "stopIds": [] }""",
         "put_stop",
         [StaleMarker, "Act on iterationUpdated with put_stop on each stop of stopIds against the tour's new pin", "still points at the iteration before the one", "read the code again at its commits", "point such a passage at the file's base side, or leave it out", "Leave the other stops as they are", "A declined reply leaves the decision made"]),
   };

   private ServerHarness _harness = null!;
   private ThreadCalls _calls = null!;

   public static TheoryData<string> ActionKinds => [.. LotsenJsonContext.Default.TourAction.PolymorphismOptions!.DerivedTypes.Select(derived => (string)derived.TypeDiscriminator!)];

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

   /// <summary>A session without the skill reads the rule, so a citation of the skill's sections would lead nowhere. The headings come from the skill, so a section that it adds counts.</summary>
   [Theory]
   [MemberData(nameof(ActionKinds))]
   [MemberData(nameof(DecisionKinds))]
   public async Task Rule_cites_no_section_of_the_skill(string kind)
   {
      var howToAct = await RuleAsync(kind);

      Assert.DoesNotMatch("(?i)section [0-9]", howToAct);
      Assert.All(SkillHeadings(), heading => Assert.DoesNotContain($"\"{heading}\"", howToAct, StringComparison.Ordinal));
   }

   [Theory]
   [InlineData(TourAction.ExplainKind)]
   [InlineData(TourAction.ReviseKind)]
   [InlineData(TourAction.ReplanKind)]
   [InlineData(TourAction.ReorderKind)]
   [InlineData(ReviewerDecision.FamiliarityChangedKind)]
   [InlineData(ReviewerDecision.PlanApprovedKind)]
   [InlineData(ReviewerDecision.IterationUpdatedKind)]
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
         TourAction.DraftWorkItemsKind => [SkillTests.PropertyName(LotsenJsonContext.Default.DraftWorkItemsAction, nameof(TourAction.FollowUp.DraftWorkItems.Decisions))],
         TourAction.WalkOpenQuestionsKind => [SkillTests.PropertyName(LotsenJsonContext.Default.WalkOpenQuestionsAction, nameof(TourAction.FollowUp.WalkOpenQuestions.OpenQuestions))],
         _ =>
         [
            SkillTests.PropertyName(LotsenJsonContext.Default.DraftCommentsAction, nameof(TourAction.FollowUp.DraftComments.Concerns)),
            SkillTests.PropertyName(LotsenJsonContext.Default.DraftCommentsAction, nameof(TourAction.FollowUp.DraftComments.Risks)),
         ],
      };
      var fields = LotsenJsonContext.Default.FollowUpAnswer.Properties.Concat(kind == TourAction.DraftCommentsKind ? LotsenJsonContext.Default.FollowUpRisk.Properties : []).Select(property => property.Name);

      Assert.All(lists, list => Assert.Contains($"{list} ", howToAct, StringComparison.Ordinal));
      Assert.All(fields, field => Assert.Contains(field, howToAct, StringComparison.Ordinal));
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

   // The skill's headings, without a section's number.
   private static IEnumerable<string> SkillHeadings() =>
      HeadingLine().Matches(File.ReadAllText(RepositoryFile.PathOf("plugin/skills/tour/SKILL.md"))).Select(match => match.Groups[1].Value.TrimEnd());

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

   private async Task<string> RuleAsync(string kind) => HowToActOf(Actions.TryGetValue(kind, out var action)
      ? await FetchAsync(ThreadFixtures.Asked(ThreadId, new ThreadAnchor.WholeTour(), action: Action(action.Sample), text: ""))
      : await FetchAsync(ThreadFixtures.Decided(ThreadId, ThreadFixtures.Decision(Decisions[kind].Sample))));

   private async Task<JsonNode> FetchAsync(QuestionThread thread, string questionId = OpeningId)
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var tourId = await _calls.CreateAsync(session);
      Assert.True(TourId.TryParse(tourId, out var id), $"The tour ID {tourId} does not parse.");
      _harness.ChangeStored(id, tour => tour with { Threads = ThreadFixtures.Threads(thread) });
      return await _calls.FetchAsync(session, tourId, questionId);
   }

   [GeneratedRegex(@"^#+ (?:[0-9]+\. )?(.+)$", RegexOptions.Multiline)]
   private static partial Regex HeadingLine();
}

