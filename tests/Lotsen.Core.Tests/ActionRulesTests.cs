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

   private static readonly TourAction.Dive StoredDive = new(TourPurpose.Prepare, Familiarity.New);

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
      ["draftWorkItems hands the sent decisions and the sent decision threads"] = (
         Answered(AnswerType.Decision) with { Threads = ThreadFixtures.Threads(SentThread(AnswerType.Decision)) },
         new ActionRequest.FollowUp.DraftWorkItems(),
         "",
         Admitted(
            """{"kind":"tour"}""",
            """{"kind":"draftWorkItems","decisions":[{"stopId":"s1","checkId":"c1","text":"Sent.","type":"decision","stopTitle":"Stop s1"}],"decisionThreads":[{"threadId":"t1","anchor":{"kind":"stop","stopId":"s1"},"text":"Found.","type":"decision","state":"open","stopId":"s1","stopTitle":"Stop s1"}]}""")),
      ["draftWorkItems hands a sent decision thread alone"] = (
         Planned() with { Threads = ThreadFixtures.Threads(SentThread(AnswerType.Decision)) },
         new ActionRequest.FollowUp.DraftWorkItems(),
         "",
         Admitted("""{"kind":"tour"}""", """{"kind":"draftWorkItems","decisions":[],"decisionThreads":[{"threadId":"t1","anchor":{"kind":"stop","stopId":"s1"},"text":"Found.","type":"decision","state":"open","stopId":"s1","stopTitle":"Stop s1"}]}""")),
      ["walkOpenQuestions hands a sent open question thread alone"] = (
         Planned() with { Threads = ThreadFixtures.Threads(SentThread(AnswerType.OpenQuestion)) },
         new ActionRequest.FollowUp.WalkOpenQuestions(),
         "",
         Admitted(
            """{"kind":"tour"}""",
            """{"kind":"walkOpenQuestions","openQuestions":[],"openQuestionThreads":[{"threadId":"t1","anchor":{"kind":"stop","stopId":"s1"},"text":"Found.","type":"openQuestion","state":"open","stopId":"s1","stopTitle":"Stop s1"}]}""")),
      ["draftComments hands a sent concern thread alone"] = (
         Planned() with { Threads = ThreadFixtures.Threads(SentThread(AnswerType.Concern)) },
         new ActionRequest.FollowUp.DraftComments(),
         "",
         Admitted(
            """{"kind":"tour"}""",
            """{"kind":"draftComments","concerns":[],"risks":[],"concernThreads":[{"threadId":"t1","anchor":{"kind":"stop","stopId":"s1"},"text":"Found.","type":"concern","state":"open","stopId":"s1","stopTitle":"Stop s1"}]}""")),
      ["draftWorkItems hands a resolved decision thread with its state"] = (
         Planned() with { Threads = ThreadFixtures.Threads(SentThread(AnswerType.Decision, QuestionThreadState.Resolved)) },
         new ActionRequest.FollowUp.DraftWorkItems(),
         "",
         Admitted(
            """{"kind":"tour"}""",
            """{"kind":"draftWorkItems","decisions":[],"decisionThreads":[{"threadId":"t1","anchor":{"kind":"stop","stopId":"s1"},"text":"Found.","type":"decision","state":"resolved","stopId":"s1","stopTitle":"Stop s1"}]}""")),
      ["draftComments with only a resolved concern thread"] = (
         Planned() with { Threads = ThreadFixtures.Threads(SentThread(AnswerType.Concern, QuestionThreadState.Resolved)) },
         new ActionRequest.FollowUp.DraftComments(),
         "",
         Refused("action", ActionRules.ConcernThreadsSettled)),
      ["draftComments hands the sent concern and not the resolved concern thread"] = (
         Answered(AnswerType.Concern) with { Threads = ThreadFixtures.Threads(SentThread(AnswerType.Concern, QuestionThreadState.Resolved)) },
         new ActionRequest.FollowUp.DraftComments(),
         "",
         Admitted("""{"kind":"tour"}""", """{"kind":"draftComments","concerns":[{"stopId":"s1","checkId":"c1","text":"Sent.","type":"concern","stopTitle":"Stop s1"}],"risks":[]}""")),
      ["walkOpenQuestions hands the open thread and not the resolved or outdated one"] = (
         Planned() with
         {
            Threads = ThreadFixtures.Threads(
               SentThread(AnswerType.OpenQuestion, QuestionThreadState.Resolved, "t1"),
               SentThread(AnswerType.OpenQuestion, QuestionThreadState.Open, "t2"),
               SentThread(AnswerType.OpenQuestion, QuestionThreadState.Outdated, "t3")),
         },
         new ActionRequest.FollowUp.WalkOpenQuestions(),
         "",
         Admitted(
            """{"kind":"tour"}""",
            """{"kind":"walkOpenQuestions","openQuestions":[],"openQuestionThreads":[{"threadId":"t2","anchor":{"kind":"stop","stopId":"s1"},"text":"Found.","type":"openQuestion","state":"open","stopId":"s1","stopTitle":"Stop s1"}]}""")),
      ["draftWorkItems with only an outdated decision thread"] = (
         Planned() with { Threads = ThreadFixtures.Threads(SentThread(AnswerType.Decision, QuestionThreadState.Outdated)) },
         new ActionRequest.FollowUp.DraftWorkItems(),
         "",
         Refused("action", ActionRules.DecisionThreadsOutdated)),
      ["walkOpenQuestions with only a resolved open question thread"] = (
         Planned() with { Threads = ThreadFixtures.Threads(SentThread(AnswerType.OpenQuestion, QuestionThreadState.Resolved)) },
         new ActionRequest.FollowUp.WalkOpenQuestions(),
         "",
         Refused("action", ActionRules.OpenQuestionThreadsSettled)),
      ["walkOpenQuestions with only an outdated open question thread"] = (
         Planned() with { Threads = ThreadFixtures.Threads(SentThread(AnswerType.OpenQuestion, QuestionThreadState.Outdated)) },
         new ActionRequest.FollowUp.WalkOpenQuestions(),
         "",
         Refused("action", ActionRules.OpenQuestionThreadsSettled)),
      ["draftComments with only an outdated concern thread"] = (
         Planned() with { Threads = ThreadFixtures.Threads(SentThread(AnswerType.Concern, QuestionThreadState.Outdated)) },
         new ActionRequest.FollowUp.DraftComments(),
         "",
         Refused("action", ActionRules.ConcernThreadsSettled)),
      ["draftWorkItems hands no sent thread of another type"] = (
         Answered(AnswerType.Decision) with { Threads = ThreadFixtures.Threads(SentThread(AnswerType.Concern)) },
         new ActionRequest.FollowUp.DraftWorkItems(),
         "",
         Admitted("""{"kind":"tour"}""", """{"kind":"draftWorkItems","decisions":[{"stopId":"s1","checkId":"c1","text":"Sent.","type":"decision","stopTitle":"Stop s1"}]}""")),
      ["draftWorkItems with only a draft decision"] = (Answered(AnswerType.Decision, AnswerState.Draft), new ActionRequest.FollowUp.DraftWorkItems(), "", Refused("action", ActionRules.NoDecisionSent)),
      ["draftWorkItems with only an unsent decision note"] = (
         Planned() with { Threads = ThreadFixtures.Threads(UnsentNote(AnswerType.Decision)) },
         new ActionRequest.FollowUp.DraftWorkItems(),
         "",
         Refused("action", ActionRules.NoDecisionSent)),
      ["walkOpenQuestions with only an unsent open question note"] = (
         Planned() with { Threads = ThreadFixtures.Threads(UnsentNote(AnswerType.OpenQuestion)) },
         new ActionRequest.FollowUp.WalkOpenQuestions(),
         "",
         Refused("action", ActionRules.NoOpenQuestionSent)),
      ["draftComments with only an unsent concern note"] = (
         Planned() with { Threads = ThreadFixtures.Threads(UnsentNote(AnswerType.Concern)) },
         new ActionRequest.FollowUp.DraftComments(),
         "",
         Refused("action", ActionRules.NoConcernSent)),
      ["walkOpenQuestions with no sent open question"] = (Answered(AnswerType.Decision), new ActionRequest.FollowUp.WalkOpenQuestions(), "", Refused("action", ActionRules.NoOpenQuestionSent)),
      ["draftComments with only a pending or skipped risk stop"] = (
         Tour(Plan(Written("r1", StopKind.Risk) with { Skipped = true }, Pending("r2", StopKind.Risk))),
         new ActionRequest.FollowUp.DraftComments(),
         "",
         Refused("action", ActionRules.NoConcernSent)),
      ["dive from a written stop takes the stop and stores the purpose and familiarity"] = (
         RepositoryPlanned(),
         Dive("s1"),
         "",
         Admitted("""{"kind":"stop","stopId":"s1"}""", """{"kind":"dive","purpose":"prepare","familiarity":"new"}""")),
      ["dive from a pending stop takes the stop"] = (RepositoryPlanned(), Dive("s2"), "The cache.", Admitted("""{"kind":"stop","stopId":"s2"}""", """{"kind":"dive","purpose":"prepare","familiarity":"new"}""")),
      ["dive from the whole tour with words takes the tour"] = (RepositoryPlanned(), Dive(), "The cache.", Admitted("""{"kind":"tour"}""", """{"kind":"dive","purpose":"prepare","familiarity":"new"}""")),
      ["dive from the whole tour without a plan"] = (RepositoryTour(), Dive(), "The cache.", Admitted("""{"kind":"tour"}""", """{"kind":"dive","purpose":"prepare","familiarity":"new"}""")),
      ["dive from the whole tour without words"] = (RepositoryPlanned(), Dive(), "", Refused("text", "a dive from the whole tour needs the reviewer's words")),
      ["dive from a stop that the plan lacks"] = (RepositoryPlanned(), Dive("gone"), "", Refused("action.stopId", "the plan has no stop 'gone'")),
      ["dive on a pull request's tour"] = (Planned(), Dive("s1"), "The cache.", Refused("action", ActionRules.NoDiveOnPullRequest)),
      ["dive on a repository tour that is still pinning"] = (RepositoryPlanned() with { Pin = new TourPin.Pinning() }, Dive(), "The cache.", Refused("action", "the tour is not pinned to a commit yet")),
      ["dive on a repository tour whose pin failed"] = (RepositoryPlanned() with { Pin = new TourPin.Failed("No sign-in.") }, Dive("s1"), "", Refused("action", "the tour's pin failed")),
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
      ["walkOpenQuestions whose thread stays open goes again"] = (
         Threaded(AnswerType.OpenQuestion, QuestionThreadState.Open),
         new ThreadAnchor.WholeTour(),
         new TourAction.FollowUp.WalkOpenQuestions([], [Item(AnswerType.OpenQuestion, ReviewerThreadState.Open)]),
         null),
      ["walkOpenQuestions whose thread the reviewer resolved since"] = (
         Threaded(AnswerType.OpenQuestion, QuestionThreadState.Resolved),
         new ThreadAnchor.WholeTour(),
         new TourAction.FollowUp.WalkOpenQuestions([], [Item(AnswerType.OpenQuestion, ReviewerThreadState.Open)]),
         Refused("action.openQuestionThreads[0]", "You resolved this open question since you asked")),
      ["walkOpenQuestions whose thread is outdated since"] = (
         Threaded(AnswerType.OpenQuestion, QuestionThreadState.Outdated),
         new ThreadAnchor.WholeTour(),
         new TourAction.FollowUp.WalkOpenQuestions([], [Item(AnswerType.OpenQuestion, ReviewerThreadState.Open)]),
         Refused("action.openQuestionThreads[0]", "A thread that this request hands to Claude is outdated since you asked")),
      ["draftWorkItems whose thread the reviewer resolved since goes again"] = (
         Threaded(AnswerType.Decision, QuestionThreadState.Resolved),
         new ThreadAnchor.WholeTour(),
         new TourAction.FollowUp.DraftWorkItems([], [Item(AnswerType.Decision, ReviewerThreadState.Open)]),
         null),
      ["draftWorkItems whose resolved thread the reviewer reopened since goes again"] = (
         Threaded(AnswerType.Decision, QuestionThreadState.Open),
         new ThreadAnchor.WholeTour(),
         new TourAction.FollowUp.DraftWorkItems([], [Item(AnswerType.Decision, ReviewerThreadState.Resolved)]),
         null),
      ["draftWorkItems whose thread is outdated since"] = (
         Threaded(AnswerType.Decision, QuestionThreadState.Outdated),
         new ThreadAnchor.WholeTour(),
         new TourAction.FollowUp.DraftWorkItems([], [Item(AnswerType.Decision, ReviewerThreadState.Open)]),
         Refused("action.decisionThreads[0]", "is outdated since you asked. Cancel this request if it still waits, then click \"Draft work items\" again on the summary page, which leaves out outdated threads.")),
      ["draftComments whose thread the reviewer resolved since"] = (
         Threaded(AnswerType.Concern, QuestionThreadState.Resolved),
         new ThreadAnchor.WholeTour(),
         new TourAction.FollowUp.DraftComments([], [], [Item(AnswerType.Concern, ReviewerThreadState.Open)]),
         Refused(
            "action.concernThreads[0]",
            "You resolved this concern since you asked, so it is settled. Cancel this request if it still waits, then click \"Draft PR comments\" again on the summary page, which leaves out the concerns you resolved.")),
      ["draftComments whose thread stays open goes again"] = (
         Threaded(AnswerType.Concern, QuestionThreadState.Open),
         new ThreadAnchor.WholeTour(),
         new TourAction.FollowUp.DraftComments([], [], [Item(AnswerType.Concern, ReviewerThreadState.Open)]),
         null),
      ["draftWorkItems whose thread is deleted since"] = (
         Planned(),
         new ThreadAnchor.WholeTour(),
         new TourAction.FollowUp.DraftWorkItems([], [Item(AnswerType.Decision, ReviewerThreadState.Open)]),
         Refused("action.decisionThreads[0]", "A thread that this request hands to Claude is deleted since you asked")),
      ["walkOpenQuestions whose thread is deleted since"] = (
         Planned(),
         new ThreadAnchor.WholeTour(),
         new TourAction.FollowUp.WalkOpenQuestions([], [Item(AnswerType.OpenQuestion, ReviewerThreadState.Open)]),
         Refused("action.openQuestionThreads[0]", "A thread that this request hands to Claude is deleted since you asked")),
      ["draftComments whose thread is deleted since"] = (
         Planned(),
         new ThreadAnchor.WholeTour(),
         new TourAction.FollowUp.DraftComments([], [], [Item(AnswerType.Concern, ReviewerThreadState.Open)]),
         Refused("action.concernThreads[0]", "is deleted since you asked. Cancel this request if it still waits, then click \"Draft PR comments\" again on the summary page.")),
      ["draftWorkItems whose thread changed its type since goes again"] = (
         Threaded(AnswerType.Concern, QuestionThreadState.Open),
         new ThreadAnchor.WholeTour(),
         new TourAction.FollowUp.DraftWorkItems([], [Item(AnswerType.Decision, ReviewerThreadState.Open)]),
         null),
      ["draftComments whose thread is outdated since"] = (
         Threaded(AnswerType.Concern, QuestionThreadState.Outdated),
         new ThreadAnchor.WholeTour(),
         new TourAction.FollowUp.DraftComments([], [], [Item(AnswerType.Concern, ReviewerThreadState.Open)]),
         Refused("action.concernThreads[0]", "A thread that this request hands to Claude is outdated since you asked")),
      ["dive from a stop goes again"] = (RepositoryPlanned(), new ThreadAnchor.WholeStop("s1"), StoredDive, null),
      ["dive from the whole tour goes again"] = (RepositoryPlanned(), new ThreadAnchor.WholeTour(), StoredDive, null),
      ["dive from a stop that the plan dropped"] = (
         RepositoryTour(Plan(Written("s9"))),
         new ThreadAnchor.WholeStop("s1"),
         StoredDive,
         Refused("anchor.stopId", "Claude dropped this stop from the plan since you asked, so there is no stop to dive from")),
      ["dive that a tour file stores on a pull request's tour"] = (Planned(), new ThreadAnchor.WholeTour(), StoredDive, Refused("action", ActionRules.NoDiveOnPullRequest)),
      ["dive that a tour file stores on stop text"] = (
         RepositoryPlanned(),
         new ThreadAnchor.StopText("s1", "headline", "point", 1),
         StoredDive,
         Refused("anchor.kind", "the dive action takes a stop or the whole tour")),
   };

   public static TheoryData<string> AdmissionRows => [.. Admissions.Keys];

   public static TheoryData<string> ResendRows => [.. Resends.Keys];

   public static TheoryData<string> UncheckedRedoRows => [.. Resends.Where(row => row.Value.Action is not TourAction.Reorder).Select(row => row.Key)];

   public static TheoryData<string> ThreadItemRows =>
   [
      .. Resends.Where(row => row.Value.Action is TourAction.FollowUp.DraftWorkItems { DecisionThreads: not null }
         or TourAction.FollowUp.WalkOpenQuestions { OpenQuestionThreads: not null }
         or TourAction.FollowUp.DraftComments { ConcernThreads: not null }).Select(row => row.Key),
   ];

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

   /// <summary>Each re-send row of a follow-up action with a thread item, so the marker that <c>fetch_question</c> shows and the re-send's refusal keep one rule.</summary>
   [Theory]
   [MemberData(nameof(ThreadItemRows))]
   public void Thread_item_applies_now_exactly_when_the_resend_goes(string row)
   {
      var (tour, anchor, action, _) = Resends[row];

      var item = Assert.Single(ActionRules.ThreadItemsNow(tour, action)!);
      var refused = ActionRules.CheckResend(tour, anchor, action);

      Assert.Equal("t1", item.ThreadId);
      Assert.Equal(refused is null, item.Applies);
      Assert.Equal(refused is null, item.Reason is null);
   }

   [Fact]
   public void Redo_of_a_reorder_fits_while_its_order_names_each_stop_of_the_plan_once()
   {
      var reorder = new TourAction.Reorder(["s2", "s1"]);

      Assert.Null(ActionRules.CheckRedo(Tour(Plan(Written("s2"), Written("s1"))), reorder));
      Assert.Equal(
         "Claude changed the plan's stops since you proposed this order, so the order no longer names each stop once. Propose an order of the current stops with Reorder… in the Plan panel's ⋯ menu.",
         ActionRules.CheckRedo(Tour(Plan(Written("s1"), Pending("s3"))), reorder));
   }

   /// <summary>Each re-send row of a kind other than reorder, a refused one included, so <see cref="TourThreads.CheckRedo"/> checks no redo of these kinds.</summary>
   [Theory]
   [MemberData(nameof(UncheckedRedoRows))]
   public void Redo_is_unchecked_for_every_action_kind_but_reorder(string row)
   {
      var (tour, anchor, action, _) = Resends[row];

      Assert.Null(TourThreads.CheckRedo(tour, (QuestionThread.Sent)ThreadFixtures.Asked("t1", anchor, action: action, request: RequestState.Done)));
   }

   /// <summary>The reorder's thread hands the action's rule to <see cref="TourThreads.CheckRedo"/>.</summary>
   [Fact]
   public void Redo_of_a_reorder_thread_takes_the_rule_of_the_reorder()
   {
      var tour = Tour(Plan(Written("s1"), Pending("s3")));
      var reorder = new TourAction.Reorder(["s2", "s1"]);

      var redo = TourThreads.CheckRedo(tour, (QuestionThread.Sent)ThreadFixtures.Asked("t1", new ThreadAnchor.WholeTour(), action: reorder, request: RequestState.Done));

      Assert.Equal(ActionRules.CheckRedo(tour, reorder), redo);
      Assert.NotNull(redo);
   }

   [Fact]
   public void Redo_is_unchecked_for_a_thread_without_an_action_or_decision()
   {
      Assert.Null(TourThreads.CheckRedo(Tour(), (QuestionThread.Sent)ThreadFixtures.Asked("t1", new ThreadAnchor.WholeTour())));
      Assert.Null(TourThreads.CheckRedo(Tour(), (QuestionThread.Sent)ThreadFixtures.Started("t1", new ThreadAnchor.WholeTour(), "The rounding needs a test.")));
   }

   [Fact]
   public void Thread_items_now_are_null_for_an_action_without_thread_items()
   {
      Assert.Null(ActionRules.ThreadItemsNow(Planned(), new TourAction.Explain(1)));
      Assert.Null(ActionRules.ThreadItemsNow(Planned(), new TourAction.Summarize()));
      Assert.Null(ActionRules.ThreadItemsNow(Planned(), new TourAction.FollowUp.DraftWorkItems([])));
      Assert.Null(ActionRules.ThreadItemsNow(Planned(), new TourAction.FollowUp.DraftComments([], [])));
      Assert.Null(ActionRules.ThreadItemsNow(RepositoryPlanned(), StoredDive));
   }

   [Fact]
   public void Waits_alone_puts_summarize_each_follow_up_kind_and_the_plan_actions_in_their_own_groups_and_the_stop_actions_and_dives_in_none()
   {
      Assert.Null(ActionRules.WaitsAlone(new TourAction.Explain(1)));
      Assert.Null(ActionRules.WaitsAlone(new TourAction.Revise()));
      Assert.Null(ActionRules.WaitsAlone(StoredDive));
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
      Assert.False(ActionRules.HandsOverReview(StoredDive));
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

   private static Tour RepositoryPlanned() => RepositoryTour(Plan(Written("s1"), Pending("s2")));

   // The plan's stop s1 at version 2, whose headline the revision kept.
   private static Tour Revised() => Tour(Plan(Written("s1") with { Version = 2, Earlier = [new StopVersion("Stop s1", StopKind.Inspect, Content(), Now)] }, Pending("s2")));

   private static Tour Answered(AnswerType type, AnswerState state = AnswerState.Sent) =>
      Planned() with { Answers = [new CheckAnswer("s1", "c1", "Sent.", type, state, Now, state == AnswerState.Draft ? null : Batch)] };

   // A thread on stop s1 that the reviewer asked, of the type.
   private static QuestionThread SentThread(AnswerType type, QuestionThreadState state = QuestionThreadState.Open, string id = "t1") =>
      ThreadFixtures.Asked(id, new ThreadAnchor.WholeStop("s1"), state, text: "Found.", type: type);

   private static Tour Threaded(AnswerType type, QuestionThreadState state) => Planned() with { Threads = ThreadFixtures.Threads(SentThread(type, state)) };

   // The stored item of the thread that SentThread returns, in the state that the action stored.
   private static FollowUpThread Item(AnswerType type, ReviewerThreadState state) => new("t1", new ThreadAnchor.WholeStop("s1"), "Found.", type, state, "s1", "Stop s1");

   private static QuestionThread UnsentNote(AnswerType type) => ThreadFixtures.Note("t1", new ThreadAnchor.WholeStop("s1"), "Found.", type: type);

   private static ActionRequest Explain(StopTarget target) => new ActionRequest.Explain(target);

   private static ActionRequest Reorder(params string[] stopIds) => new ActionRequest.Reorder(stopIds);

   private static ActionRequest Dive(string? stopId = null) => new ActionRequest.Dive(TourPurpose.Prepare, Familiarity.New, stopId);

   private static Outcome Admitted(string anchor, string action) => new(Anchor: anchor, Action: action);

   private static Outcome Refused(string field, string problem) => new(Field: field, Problem: problem);

   private sealed record Outcome(string? Anchor = null, string? Action = null, string? Field = null, string? Problem = null);
}
