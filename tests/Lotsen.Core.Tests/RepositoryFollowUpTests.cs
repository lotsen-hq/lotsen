using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Git;
using Lotsen.Tours;
using static Lotsen.Tests.OperationCall;
using static Lotsen.Tests.ThreadCalls;
using static Lotsen.Tests.TourActionTests;

namespace Lotsen.Tests;

/// <summary>
/// Covers the follow-up actions on a repository tour, which has no pull request: it offers <c>draftWorkItems</c> and <c>walkOpenQuestions</c> as a pull request's tour does, and refuses
/// <c>draftComments</c>, as <see cref="ActionRules.NotOffered"/> says.
/// </summary>
/// <remarks>Each test creates its own repository tour with its own session, pinned to a snapshot whose commit holds <c>src/app.txt</c>, and asks a sent typed thread of each type on it.</remarks>
[Trait("Category", "TourActions")]
public sealed class RepositoryFollowUpTests : IAsyncLifetime
{
   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

   private readonly GitFixture _git = new();
   private ServerHarness _harness = null!;
   private ThreadCalls _calls = null!;
   private CommitId _head;

   public async ValueTask InitializeAsync()
   {
      _head = _git.Commit("Base", ("src/app.txt", "one\ntwo\nthree\n"));
      var platform = new TestPlatform();
      platform.Repository(_git.Url, _head);
      _harness = await ServerHarness.StartAsync(platform: platform);
      _calls = new ThreadCalls(_harness);
   }

   public async ValueTask DisposeAsync()
   {
      // A start that failed may leave no harness.
      if (_harness is not null)
      {
         await _harness.DisposeAsync();
      }

      _git.Dispose();
   }

   [Fact]
   public async Task Offers_of_a_repository_tour_hold_draft_work_items_and_walk_open_questions_and_no_draft_comments()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await TourWithFindingsAsync(session, "orient");

      using var response = await _calls.SendAsync(await _calls.UiAsync(On("read_state", id)));
      var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;

      var followUps = body["controls"]!["offers"]!["followUps"]!.AsObject();
      Assert.Equal([TourAction.DraftWorkItemsKind, TourAction.WalkOpenQuestionsKind], followUps.Select(entry => entry.Key).Order(StringComparer.Ordinal));
      Assert.All(followUps, entry => Assert.Equal("open", (string)entry.Value!["state"]!));
   }

   /// <summary>The tour holds a sent concern, so the refusal comes from the tour's target and not from an empty group.</summary>
   [Fact]
   public async Task Draft_comments_on_a_repository_tour_is_refused_as_the_summary_says_and_stores_nothing()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await TourWithFindingsAsync(session, "evaluate");
      var threadsBefore = (await _calls.ThreadsAsync(id)).Count;

      using var response = await _calls.SendAsync(await _calls.UiAsync(On("request_tour_action", id)), ActionBody(TourAction.DraftCommentsKind));
      var summary = await _calls.CallAsync(await _calls.UiAsync(On("read_summary", id)));

      var error = await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("action", (string)error["field"]!);
      Assert.Equal($"action: {ActionRules.NoPullRequestAuthor}", (string)error["reason"]!);
      Assert.Equal(ActionRules.NoPullRequestAuthor, (string)summary["followUps"]![TourAction.DraftCommentsKind]!);
      Assert.Single(summary["typedThreads"]!["concerns"]!.AsArray());
      Assert.Equal(threadsBefore, (await _calls.ThreadsAsync(id)).Count);
   }

   /// <summary>No request stores a draftComments action on a repository tour, so the test writes its thread into the stored tour, canceled so that it can go again.</summary>
   [Fact]
   public async Task Resend_of_a_stored_draft_comments_action_on_a_repository_tour_is_refused_as_a_new_request_is()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await TourWithFindingsAsync(session, "evaluate");
      Assert.True(TourId.TryParse(id, out var tourId), $"The tour ID {id} does not parse.");
      var stored = ThreadFixtures.Asked("t9", new ThreadAnchor.WholeTour(), action: new TourAction.FollowUp.DraftComments([], []), request: RequestState.Canceled, text: "", sessionId: session.Id);
      _harness.ChangeStored(tourId, tour => tour with { Threads = ThreadFixtures.Threads([.. tour.Threads, stored]) });

      using var response = await _calls.SendAsync(await _calls.UiAsync(On("resend_question", id, questionId: "q-t9")));

      var error = await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal(("action", $"action: {ActionRules.NoPullRequestAuthor}"), ((string)error["field"]!, (string)error["reason"]!));
   }

   [Fact]
   public async Task Draft_work_items_on_a_repository_tour_hands_the_sent_decision_and_asks_where_the_work_items_belong()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await TourWithFindingsAsync(session, "orient");

      var thread = await _calls.CallAsync(await _calls.UiAsync(On("request_tour_action", id)), ActionBody(TourAction.DraftWorkItemsKind));
      var fetched = await _calls.FetchAsync(session, id, QuestionIdOf(thread));
      var replied = await _calls.CallAsync(_calls.Agent(session, On("reply", id, questionId: QuestionIdOf(thread))), Done);

      var handed = Assert.Single(fetched["thread"]!["action"]!["decisionThreads"]!.AsArray())!;
      Assert.Equal(("Keep the counter.", "decision"), ((string)handed["text"]!, (string)handed["type"]!));
      Assert.Contains("ask the user in the terminal where the work items belong", (string)fetched["howToAct"]!, StringComparison.Ordinal);
      Assert.Equal("done", (string)RequestOf(replied)["state"]!);
   }

   /// <summary>Claude starts a thread on code at the snapshot's head and adds to the open question thread, as on a pull request's tour.</summary>
   [Fact]
   public async Task Walk_open_questions_on_a_repository_tour_starts_a_thread_at_the_snapshot_and_adds_to_the_open_question_thread()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await TourWithFindingsAsync(session, "evaluate");

      var thread = await _calls.CallAsync(await _calls.UiAsync(On("request_tour_action", id)), ActionBody(TourAction.WalkOpenQuestionsKind));
      var fetched = await _calls.FetchAsync(session, id, QuestionIdOf(thread));
      var openQuestion = (string)Assert.Single(fetched["thread"]!["action"]!["openQuestionThreads"]!.AsArray())!["threadId"]!;
      var started = await _calls.CallAsync(
         _calls.Agent(session, On("start_thread", id)),
         StartBody("The middle line decides it.", """{ "kind": "code", "side": "head", "path": "src/app.txt", "startLine": 2, "endLine": 2 }"""));
      var added = await _calls.AddMessageAsync(session, id, openQuestion, "Keep it, because the tests pin it.");
      var replied = await _calls.CallAsync(_calls.Agent(session, On("reply", id, questionId: QuestionIdOf(thread))), Done);

      var anchor = started["thread"]!["anchor"]!;
      Assert.Equal(("head", _head.Value, "two"), ((string)anchor["side"]!, (string)anchor["commit"]!, (string)anchor["text"]!));
      Assert.Contains(added["thread"]!["messages"]!.AsArray(), message => (string?)message!["text"] == "Keep it, because the tests pin it.");
      Assert.Equal("done", (string)RequestOf(replied)["state"]!);
   }

   // A repository tour of the purpose, created by the session and pinned to the snapshot, with a sent typed thread of each type that the session already answered.
   private async Task<string> TourWithFindingsAsync(AgentSessionConnection session, string purpose)
   {
      var id = (string)(await _calls.CallAsync(_calls.Agent(session, "create_tour"), RepositoryPinningTests.CreateBody(@ref: null, purpose: purpose)))["id"]!;
      await _harness.PinsIdleAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);
      foreach (var (text, type) in ((string, string)[])[("Keep the counter.", "decision"), ("Is three enough?", "openQuestion"), ("The counter can overflow.", "concern")])
      {
         var asked = await _calls.AskAsync(id, text, type);
         await _calls.CallAsync(_calls.Agent(session, On("reply", id, questionId: QuestionIdOf(asked))), Done);
      }

      return id;
   }
}
