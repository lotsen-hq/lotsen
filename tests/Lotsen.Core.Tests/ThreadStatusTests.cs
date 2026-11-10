using System.Text.Json;
using System.Text.Json.Nodes;
using Lotsen.Json;
using Lotsen.Prose;
using Lotsen.Tours;
using static Lotsen.Tests.OperationCall;
using static Lotsen.Tests.ThreadFixtures;

namespace Lotsen.Tests;

/// <summary>Covers <see cref="ThreadStatus.Of"/>, one row per thread shape, and where the served thread carries the status.</summary>
/// <remarks>
/// A row reads the status in short forms. <c>Waiting</c> is <c>question,request,session</c>, or null when no question waits. <c>Questions</c> lists <c>question:outcome</c> in thread order.
/// </remarks>
[Trait("Category", "Threads")]
public sealed class ThreadStatusTests
{
   private static readonly Dictionary<string, (QuestionThread Thread, string? Waiting, string Questions)> Shapes = new()
   {
      ["a note has no question"] = (Note("t1", new ThreadAnchor.WholeTour()), null, ""),
      ["a pending request waits"] = (Asked("t1", new ThreadAnchor.WholeTour()), "q-t1,r-t1,session1", "q-t1:waiting"),
      ["a done reply answers"] = (Asked("t1", new ThreadAnchor.WholeTour(), request: RequestState.Done), null, "q-t1:answered"),
      ["a needsAnswer reply answers"] = (Asked("t1", new ThreadAnchor.WholeTour(), request: RequestState.NeedsAnswer), null, "q-t1:answered"),
      ["a decline answers"] = (Asked("t1", new ThreadAnchor.WholeTour(), request: RequestState.Declined), null, "q-t1:answered"),
      ["a reply to an earlier request answers past a later canceled one"] = (
         Sent(Question("q1", "Why?", null, At, ("r1", RequestState.Done, "session1"), ("r2", RequestState.Canceled, "session2")), Reply("a1", "r1", "session1", "Here.", At)),
         null,
         "q1:answered"),
      ["a canceled request"] = (Asked("t1", new ThreadAnchor.WholeTour(), request: RequestState.Canceled), null, "q-t1:canceled"),
      ["an unanswered request"] = (Asked("t1", new ThreadAnchor.WholeTour(), request: RequestState.Unanswered), null, "q-t1:unanswered"),
      ["a re-sent question waits for its new request"] = (
         Sent(Question("q1", "Why?", null, At, ("r1", RequestState.Canceled, "session1"), ("r2", RequestState.Pending, "session2"))),
         "q1,r2,session2",
         "q1:waiting"),
      ["a follow-up after an answer waits"] = (
         Sent(
            Question("q1", "Why?", null, At, ("r1", RequestState.Done, "session1")),
            Reply("a1", "r1", "session1", "Here.", At),
            Question("q2", "And?", null, At, ("r2", RequestState.Pending, "session1"))),
         "q2,r2,session1",
         "q1:answered q2:waiting"),
      ["a started thread without a follow-up has no question"] = (Started("t1", new ThreadAnchor.WholeTour(), "Look here."), null, ""),
      ["a started thread with a follow-up waits"] = (
         Parse(Thread(
            "started",
            "t1",
            new ThreadAnchor.WholeTour(),
            QuestionThreadState.Open,
            [AgentMessage("m1", "session1", "Look here.", At), Question("q1", "Why?", null, At, ("r1", RequestState.Pending, "session1"))])),
         "q1,r1,session1",
         "q1:waiting"),
   };

   public static TheoryData<string> ShapeRows => [.. Shapes.Keys];

   [Theory]
   [MemberData(nameof(ShapeRows))]
   public void Shape_row(string row)
   {
      var (thread, waiting, questions) = Shapes[row];

      var status = ThreadStatus.Of(thread);

      Assert.Equal(waiting, status.Waiting is { } question ? $"{question.QuestionId},{question.RequestId},{question.SessionId}" : null);
      Assert.Equal(questions, string.Join(" ", status.Questions.Select(each => $"{each.QuestionId}:{JsonNamingPolicy.CamelCase.ConvertName(each.Outcome.ToString())}")));
   }

   [Fact]
   public void Thread_as_the_ui_receives_it_carries_its_status()
   {
      var thread = Asked("t1", new ThreadAnchor.WholeTour());

      var served = ProseHtml.Of(thread);

      Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(ThreadStatus.Of(thread), LotsenJsonContext.Default.ThreadStatus), served["status"]), $"status: {served["status"]}");
   }

   [Fact]
   public async Task Agent_read_serves_threads_without_status_and_the_ui_read_with_it()
   {
      await using var harness = await ServerHarness.StartAsync();
      var tourId = (string)(await harness.AgentCallAsync("create_tour", ThreadCalls.CreateBody))["id"]!;
      await harness.CallAsync(On("ask_question", tourId), ThreadCalls.NoteBody("Why?"));
      using var response = await harness.SendAsync(new PreparedCall(On("read_state", tourId), harness.UiCredential).WithProseHtml());

      var agentThread = (await harness.AgentCallAsync(On("read_state", tourId)))["threads"]![0]!;
      var uiThread = SuccessBody.Output(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))["threads"]![0]!;

      Assert.False(agentThread.AsObject().ContainsKey("status"), $"The agent's thread carries a status: {agentThread}");
      Assert.Equal("waiting", (string?)uiThread["status"]?["questions"]?[0]?["outcome"]);
   }

   private static QuestionThread Sent(params JsonObject[] messages) => Parse(Thread("asked", "t1", new ThreadAnchor.WholeTour(), QuestionThreadState.Open, [.. messages]));
}
