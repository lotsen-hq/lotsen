using System.Text.Json;
using System.Text.Json.Nodes;
using Lotsen.Json;
using Lotsen.Tours;
using static Lotsen.Tests.TourValues;

namespace Lotsen.Tests;

/// <summary>Covers the columns of <see cref="ActionRules"/>, one row per rule, on tour values.</summary>
/// <remarks>
/// The tour's plan holds the written stop <c>s1</c> at version 1, whose headline is "The point.", and the pending stop <c>s2</c>. An admitted row names the anchor and the stored action as JSON, and
/// a refused row names the field and a part of the problem.
/// </remarks>
[Trait("Category", "TourActions")]
public sealed class ActionRulesTests
{
   private static readonly AnswerBatch Batch = new("b1", "session1", Now);

   private static readonly Dictionary<string, (Tour Tour, ActionRequest Request, string Text, Outcome Expect)> Admissions = new()
   {
      ["explain on a written stop stamps the stop's version"] = (Planned(), Explain(new StopTarget.WholeStop("s1")), "", Admitted("""{"kind":"stop","stopId":"s1"}""", """{"kind":"explain","stopVersion":1}""")),
      ["explain on a written stop at its current version"] = (Planned(), Explain(new StopTarget.WholeStop("s1", 1)), "", Admitted("""{"kind":"stop","stopId":"s1"}""", """{"kind":"explain","stopVersion":1}""")),
      ["revise on stop text keeps the anchor's version and stamps none"] = (
         Planned(),
         new ActionRequest.Revise(new StopTarget.StopText("s1", "headline", "point")),
         "Shorter.",
         Admitted("""{"kind":"stopText","stopId":"s1","field":"headline","text":"point","version":1}""", """{"kind":"revise"}""")),
      ["explain on a pending stop"] = (Planned(), Explain(new StopTarget.WholeStop("s2")), "", Refused("action.target.stopId", "stop 's2' has no content yet, so Claude has nothing to explain")),
      ["revise on text of a pending stop"] = (Planned(), new ActionRequest.Revise(new StopTarget.StopText("s2", "headline", "x")), "", Refused("action.target.stopId", "has no content yet, so it has no text to select")),
      ["revise without words"] = (Planned(), new ActionRequest.Revise(new StopTarget.WholeStop("s1")), "", Refused("text", "a revise needs the reviewer's words")),
      ["explain on a stop that the plan lacks"] = (Planned(), Explain(new StopTarget.WholeStop("gone")), "", Refused("action.target.stopId", "the plan has no stop 'gone'")),
      ["explain on a stale version of a stop"] = (Planned(), Explain(new StopTarget.WholeStop("s1", 2)), "", Refused("action.target.version", "stop 's1' is at version 1, not 2")),
      ["explain on text of a stale version"] = (Planned(), Explain(new StopTarget.StopText("s1", "headline", "point", 2)), "", Refused("action.target.version", "is at version 1, not 2")),
      ["explain on a prose field that the stop lacks"] = (Planned(), Explain(new StopTarget.StopText("s1", "why", "point")), "", Refused("action.target.field", "has no prose field 'why'")),
      ["explain on blank selected text"] = (Planned(), Explain(new StopTarget.StopText("s1", "headline", " ")), "", Refused("action.target.text", "needs the text that the reviewer selected")),
      ["replan with words takes the tour"] = (Planned(), new ActionRequest.Replan(), "Fewer stops.", Admitted("""{"kind":"tour"}""", """{"kind":"replan"}""")),
      ["replan without words"] = (Planned(), new ActionRequest.Replan(), "", Refused("text", "a replan needs the reviewer's words")),
      ["replan without a plan"] = (Tour(), new ActionRequest.Replan(), "Fewer stops.", Refused("action", "no plan to replan")),
      ["reorder of each stop once takes the tour"] = (Planned(), Reorder("s2", "s1"), "", Admitted("""{"kind":"tour"}""", """{"kind":"reorder","stopIds":["s2","s1"]}""")),
      ["reorder of a stop that the plan lacks"] = (Planned(), Reorder("s3", "s1"), "", Refused("action.stopIds[0]", "the plan has no stop 's3'")),
      ["reorder of a stop twice"] = (Planned(), Reorder("s1", "s1", "s2"), "", Refused("action.stopIds[1]", "stop 's1' appears more than once")),
      ["reorder that leaves a stop out"] = (Planned(), Reorder("s1"), "", Refused("action.stopIds", "the proposed order leaves out stop 's2'")),
      ["reorder with a null stop ID"] = (Planned(), Reorder("s1", null!), "", Refused("action.stopIds[1]", "a stop ID cannot be null")),
      ["reorder without a plan"] = (Tour(), Reorder(), "", Refused("action", "no plan to reorder")),
      ["summarize takes the tour"] = (Planned(), new ActionRequest.Summarize(), "", Admitted("""{"kind":"tour"}""", """{"kind":"summarize"}""")),
      ["summarize without a plan"] = (Tour(), new ActionRequest.Summarize(), "", Refused("action", "no review to summarize")),
      ["draftWorkItems hands the sent decisions"] = (
         Answered(AnswerType.Decision),
         new ActionRequest.FollowUp.DraftWorkItems(),
         "",
         Admitted("""{"kind":"tour"}""", """{"kind":"draftWorkItems","decisions":[{"stopId":"s1","checkId":"c1","text":"Sent.","type":"decision","stopTitle":"Stop s1"}]}""")),
      ["walkOpenQuestions hands the sent open questions"] = (
         Answered(AnswerType.OpenQuestion),
         new ActionRequest.FollowUp.WalkOpenQuestions(),
         "",
         Admitted("""{"kind":"tour"}""", """{"kind":"walkOpenQuestions","openQuestions":[{"stopId":"s1","checkId":"c1","text":"Sent.","type":"openQuestion","stopTitle":"Stop s1"}]}""")),
      ["draftComments hands the sent concerns and the written risk stops that the reviewer did not skip"] = (
         Answered(AnswerType.Concern) with { Plan = Plan(Written("s1"), Written("r1", StopKind.Risk), Written("r2", StopKind.Risk) with { Skipped = true }, Pending("r3", StopKind.Risk)) },
         new ActionRequest.FollowUp.DraftComments(),
         "",
         Admitted(
            """{"kind":"tour"}""",
            """{"kind":"draftComments","concerns":[{"stopId":"s1","checkId":"c1","text":"Sent.","type":"concern","stopTitle":"Stop s1"}],"risks":[{"stopId":"r1","title":"Stop r1","scenario":"It breaks."}]}""")),
      ["draftComments hands a written risk stop without a sent concern"] = (
         Tour(Plan(Written("r1", StopKind.Risk))),
         new ActionRequest.FollowUp.DraftComments(),
         "",
         Admitted("""{"kind":"tour"}""", """{"kind":"draftComments","concerns":[],"risks":[{"stopId":"r1","title":"Stop r1","scenario":"It breaks."}]}""")),
      ["draftWorkItems with only a draft decision"] = (Answered(AnswerType.Decision, AnswerState.Draft), new ActionRequest.FollowUp.DraftWorkItems(), "", Refused("action", ActionRules.NoDecisionSent)),
      ["walkOpenQuestions with no sent open question"] = (Answered(AnswerType.Decision), new ActionRequest.FollowUp.WalkOpenQuestions(), "", Refused("action", ActionRules.NoOpenQuestionSent)),
      ["draftComments with only a pending or skipped risk stop"] = (
         Tour(Plan(Written("r1", StopKind.Risk) with { Skipped = true }, Pending("r2", StopKind.Risk))),
         new ActionRequest.FollowUp.DraftComments(),
         "",
         Refused("action", ActionRules.NoConcernSent)),
   };

   private static readonly Dictionary<string, (Tour Tour, ThreadAnchor Anchor, TourAction Action, Outcome? Expect)> Resends = new()
   {
      ["explain on a whole stop at the stored version goes again"] = (Planned(), new ThreadAnchor.WholeStop("s1"), new TourAction.Explain(1), null),
      ["explain on a whole stop that a revision revised since"] = (Revised(), new ThreadAnchor.WholeStop("s1"), new TourAction.Explain(1), Refused("action.stopVersion", "Claude revised this stop since you asked")),
      ["revise on a whole stop that a revision revised since"] = (Revised(), new ThreadAnchor.WholeStop("s1"), new TourAction.Revise(1), Refused("action.stopVersion", "Claude revised this stop since you asked")),
      ["revise on a stop that the plan dropped"] = (Tour(Plan(Written("s9"))), new ThreadAnchor.WholeStop("s1"), new TourAction.Revise(1), Refused("anchor.stopId", "Claude dropped this stop from the plan since you asked")),
      ["explain on stop text that a revision left on an earlier version"] = (
         Revised(),
         new ThreadAnchor.StopText("s1", "headline", "point", 1),
         new TourAction.Explain(),
         Refused("anchor.version", "the text you selected is in an earlier version")),
      ["explain on stop text that a revision moved to its version goes again"] = (Revised(), new ThreadAnchor.StopText("s1", "headline", "point", 2), new TourAction.Explain(), null),
      ["explain that a tour file stores on the tour anchor"] = (Planned(), new ThreadAnchor.WholeTour(), new TourAction.Explain(), Refused("anchor.kind", "the explain action takes a stop or text in a stop")),
      ["revise that a tour file stores on a file anchor"] = (Planned(), new ThreadAnchor.WholeFile(FileSide.Head, "src/a.cs", Head), new TourAction.Revise(), Refused("anchor.kind", "the revise action takes a stop or text in a stop")),
      ["explain that a tour file stores on a code anchor"] = (
         Planned(),
         new ThreadAnchor.CodeRange(FileSide.Head, "src/a.cs", 1, 2, Head, "a\nb"),
         new TourAction.Explain(1),
         Refused("anchor.kind", "the explain action takes a stop or text in a stop")),
      ["replan with a plan goes again"] = (Planned(), new ThreadAnchor.WholeTour(), new TourAction.Replan(), null),
      ["reorder of the plan's stops goes again"] = (Planned(), new ThreadAnchor.WholeTour(), new TourAction.Reorder(["s2", "s1"]), null),
      ["reorder after the plan's stops changed"] = (
         Tour(Plan(Written("s1"), Pending("s3"))),
         new ThreadAnchor.WholeTour(),
         new TourAction.Reorder(["s2", "s1"]),
         Refused("action.stopIds", "Claude changed the plan's stops since you proposed this order")),
      ["summarize goes again"] = (Planned(), new ThreadAnchor.WholeTour(), new TourAction.Summarize(), null),
      ["follow-up action goes again with its stored items"] = (Tour(), new ThreadAnchor.WholeTour(), new TourAction.FollowUp.DraftWorkItems([]), null),
   };

   public static TheoryData<string> AdmissionRows => [.. Admissions.Keys];

   public static TheoryData<string> ResendRows => [.. Resends.Keys];

   [Theory]
   [MemberData(nameof(AdmissionRows))]
   public void Admit_row(string row)
   {
      var (tour, request, text, expect) = Admissions[row];

      var admitted = ActionRules.Admit(tour, request, text);

      Check(expect, admitted.Error, () =>
      {
         Assert.Equal(expect.Anchor, JsonSerializer.Serialize(admitted.Value!.Anchor, LotsenJsonContext.Default.ThreadAnchor));
         Assert.Equal(expect.Action, JsonSerializer.Serialize(admitted.Value.Action, LotsenJsonContext.Default.TourAction));
      });
   }

   [Theory]
   [MemberData(nameof(ResendRows))]
   public void Resend_row(string row)
   {
      var (tour, anchor, action, expect) = Resends[row];

      var refused = ActionRules.CheckResend(tour, anchor, action);

      if (expect is null)
      {
         Assert.Null(refused);
      }
      else
      {
         Check(expect, refused, () => Assert.Fail("The re-send went."));
      }
   }

   [Fact]
   public void Waits_alone_puts_summarize_each_follow_up_kind_and_the_plan_actions_in_their_own_groups_and_the_stop_actions_in_none()
   {
      Assert.Null(ActionRules.WaitsAlone(new TourAction.Explain(1)));
      Assert.Null(ActionRules.WaitsAlone(new TourAction.Revise()));
      Assert.Equal(WaitGroup.Plan, ActionRules.WaitsAlone(new TourAction.Replan()));
      Assert.Equal(WaitGroup.Plan, ActionRules.WaitsAlone(new TourAction.Reorder([])));
      Assert.Equal(WaitGroup.Summary, ActionRules.WaitsAlone(new TourAction.Summarize()));
      Assert.Equal(WaitGroup.WorkItems, ActionRules.WaitsAlone(new TourAction.FollowUp.DraftWorkItems([])));
      Assert.Equal(WaitGroup.OpenQuestions, ActionRules.WaitsAlone(new TourAction.FollowUp.WalkOpenQuestions([])));
      Assert.Equal(WaitGroup.Comments, ActionRules.WaitsAlone(new TourAction.FollowUp.DraftComments([], [])));
   }

   [Fact]
   public void Hands_over_review_holds_for_summarize_and_the_follow_up_actions_alone()
   {
      Assert.False(ActionRules.HandsOverReview(new TourAction.Explain(1)));
      Assert.False(ActionRules.HandsOverReview(new TourAction.Revise()));
      Assert.False(ActionRules.HandsOverReview(new TourAction.Replan()));
      Assert.False(ActionRules.HandsOverReview(new TourAction.Reorder([])));
      Assert.True(ActionRules.HandsOverReview(new TourAction.Summarize()));
      Assert.True(ActionRules.HandsOverReview(new TourAction.FollowUp.DraftWorkItems([])));
      Assert.True(ActionRules.HandsOverReview(new TourAction.FollowUp.WalkOpenQuestions([])));
      Assert.True(ActionRules.HandsOverReview(new TourAction.FollowUp.DraftComments([], [])));
   }

   /// <summary>Each JSON request reads to the request kind of the same name, so the kind names of the request and of the stored action stay one list.</summary>
   [Fact]
   public void Request_kinds_are_the_stored_action_kinds()
   {
      static IEnumerable<string> Kinds(System.Text.Json.Serialization.Metadata.JsonTypeInfo info) => info.PolymorphismOptions!.DerivedTypes.Select(derived => (string)derived.TypeDiscriminator!);

      Assert.Equal(Kinds(LotsenJsonContext.Default.TourAction), Kinds(LotsenJsonContext.Default.ActionRequest));
      Assert.True(JsonNode.DeepEquals(
         JsonNode.Parse("""{"kind":"explain","target":{"kind":"stopText","stopId":"s1","field":"how","text":"x","version":2}}"""),
         JsonSerializer.SerializeToNode(new ActionRequest.Explain(new StopTarget.StopText("s1", "how", "x", 2)), LotsenJsonContext.Default.ActionRequest)));
   }

   private static void Check(Outcome expect, OperationError? error, Action admitted)
   {
      if (expect.Field is { } field)
      {
         Assert.NotNull(error);
         Assert.Equal(OperationErrorCode.InvalidInput, error.Code);
         Assert.Equal(field, error.Field?.ToString());
         Assert.Contains(expect.Problem!, error.Problem, StringComparison.Ordinal);
         return;
      }

      Assert.Null(error);
      admitted();
   }

   private static Tour Planned() => Tour(Plan(Written("s1"), Pending("s2")));

   // The plan's stop s1 at version 2, whose headline the revision kept.
   private static Tour Revised() => Tour(Plan(Written("s1") with { Version = 2, Earlier = [new StopVersion("Stop s1", StopKind.Inspect, Content(), Now)] }, Pending("s2")));

   private static Tour Answered(AnswerType type, AnswerState state = AnswerState.Sent) =>
      Planned() with { Answers = [new CheckAnswer("s1", "c1", "Sent.", type, state, Now, state == AnswerState.Draft ? null : Batch)] };

   private static ActionRequest Explain(StopTarget target) => new ActionRequest.Explain(target);

   private static ActionRequest Reorder(params string[] stopIds) => new ActionRequest.Reorder(stopIds);

   private static Outcome Admitted(string anchor, string action) => new(Anchor: anchor, Action: action);

   private static Outcome Refused(string field, string problem) => new(Field: field, Problem: problem);

   private sealed record Outcome(string? Anchor = null, string? Action = null, string? Field = null, string? Problem = null);
}
