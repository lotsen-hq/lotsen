using System.Text.Json;
using System.Text.Json.Nodes;
using Lotsen.Hosting;
using Lotsen.Json;
using Lotsen.Tours;

namespace Lotsen.Tests;

/// <summary>Covers the checks that the thread types and <see cref="TourThreads"/> run when they load, so a stored thread that breaks an invariant fails to load.</summary>
[Trait("Category", "Threads")]
public sealed class ThreadLoadTests
{
   private static readonly DateTimeOffset At = ThreadFixtures.At;

   public static TheoryData<string> Broken => [.. Cases.Keys];

   private static IReadOnlyDictionary<string, Func<JsonArray>> Cases { get; } = new Dictionary<string, Func<JsonArray>>
   {
      ["a question with no requests"] = () => [Asked("t1", [ThreadFixtures.Question("q1", "Why?", null, At)])],
      ["a question with two pending requests"] = () => [Asked("t1", [ThreadFixtures.Question("q1", "Why?", null, At, ("r1", RequestState.Pending, "s1"), ("r2", RequestState.Pending, "s1"))])],
      ["a question whose pending request is not its last"] = () => [Asked("t1", [ThreadFixtures.Question("q1", "Why?", null, At, ("r1", RequestState.Pending, "s1"), ("r2", RequestState.Canceled, "s1"))])],
      ["a question with two resolved requests"] = () =>
         [Asked("t1", [ThreadFixtures.Question("q1", "Why?", null, At, ("r1", RequestState.Done, "s1"), ("r2", RequestState.Declined, "s1")), Reply("a1", "r1"), Reply("a2", "r2")])],
      ["a question with a pending request beside a resolved one"] = () =>
         [Asked("t1", [ThreadFixtures.Question("q1", "Why?", null, At, ("r1", RequestState.Done, "s1"), ("r2", RequestState.Pending, "s1")), Reply("a1", "r1")])],
      ["a pending request on an earlier question"] = () =>
         [Asked("t1", [ThreadFixtures.Question("q1", "Why?", null, At, ("r1", RequestState.Pending, "s1")), ThreadFixtures.Question("q2", "And?", null, At, ("r2", RequestState.Canceled, "s1"))])],
      ["a reply to an unknown request"] = () => [Asked("t1", [ThreadFixtures.Question("q1", "Why?", null, At, ("r1", RequestState.Done, "s1")), Reply("a1", "r9")])],
      ["a reply to a request that holds no resolution"] = () => [Asked("t1", [ThreadFixtures.Question("q1", "Why?", null, At, ("r1", RequestState.Canceled, "s1")), Reply("a1", "r1")])],
      ["a reply from another session than its request went to"] = () => [Asked("t1", [ThreadFixtures.Question("q1", "Why?", null, At, ("r1", RequestState.Done, "s1")), Reply("a1", "r1", "s2")])],
      ["a reply before its question"] = () => [Asked("t1", [ThreadFixtures.Question("q0", "First?", null, At, ("r0", RequestState.Canceled, "s1")), Reply("a1", "r1"), ThreadFixtures.Question("q1", "Why?", null, At, ("r1", RequestState.Done, "s1"))])],
      ["a resolved request without a reply"] = () => [Asked("t1", [ThreadFixtures.Question("q1", "Why?", null, At, ("r1", RequestState.Done, "s1"))])],
      ["two replies to one request"] = () => [Asked("t1", [ThreadFixtures.Question("q1", "Why?", null, At, ("r1", RequestState.Done, "s1")), Reply("a1", "r1"), Reply("a2", "r1")])],
      ["a duplicate ID across threads"] = () => [Asked("t1", [Pending("q1", "r1")]), Asked("t2", [Pending("q2", "t1")])],
      ["a duplicate ID within a thread"] = () => [Asked("t1", [Pending("q1", "q1")])],
      ["a tour action with a null item"] = () => [With(Asked("t1", [Pending("q1", "r1")]), "action", JsonNode.Parse("""{ "kind": "reorder", "stopIds": ["s1", null] }"""))],
      ["a tour action without its list"] = () => [With(Asked("t1", [Pending("q1", "r1")]), "action", new JsonObject { ["kind"] = "draftComments", ["concerns"] = new JsonArray() })],
      ["an action on a started thread"] = () => [With(Started("t1"), "action", new JsonObject { ["kind"] = "summarize" })],
      ["a decided thread without its decision"] = () => [ThreadFixtures.Thread("decided", "t1", new ThreadAnchor.WholeTour(), QuestionThreadState.Open, [Pending("q1", "r1")])],
      ["a decided thread on a stop"] = () => [Decided("t1", new ThreadAnchor.WholeStop("s1"))],
      ["a decided thread with an action"] = () => [With(Decided("t1"), "action", new JsonObject { ["kind"] = "summarize" })],
      ["a decided thread that starts with an agent message"] = () =>
         [With(ThreadFixtures.Thread("decided", "t1", new ThreadAnchor.WholeTour(), QuestionThreadState.Open, [ThreadFixtures.AgentMessage("m1", "s1", "Hi.", At)]), "decision", JsonNode.Parse(PlanApproved))],
      ["a decision of an unknown kind"] = () => [With(Decided("t1"), "decision", new JsonObject { ["kind"] = "approvePlan", ["stopIds"] = new JsonArray(), ["skippedStops"] = new JsonArray(), ["familiarity"] = "some" })],
      ["a note with messages"] = () => [With(Note("t1"), "messages", new JsonArray())],
      ["an asked thread that starts with an agent message"] = () => [ThreadFixtures.Thread("asked", "t1", new ThreadAnchor.WholeTour(), QuestionThreadState.Open, [ThreadFixtures.AgentMessage("m1", "s1", "Hi.", At)])],
      ["a started thread that starts with a question"] = () => [ThreadFixtures.Thread("started", "t1", new ThreadAnchor.WholeTour(), QuestionThreadState.Open, [Pending("q1", "r1")])],
      ["a sent thread with no messages"] = () => [Asked("t1", [])],
      ["a null thread"] = () => [null],
      ["a null message"] = () => [Asked("t1", [null])],
      ["a thread with an unknown kind"] = () => [With(Started("t1"), "kind", "draft")],
      ["a message with the author of an earlier format"] = () => [Asked("t1", [With(Pending("q1", "r1"), "author", "user")])],
   };

   [Theory]
   [MemberData(nameof(Broken))]
   public void Thread_json_that_breaks_an_invariant_does_not_load(string name)
   {
      var json = Cases[name]().ToJsonString();

      Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, LotsenJsonContext.Default.TourThreads));
   }

   [Fact]
   public void Thread_json_of_each_kind_loads_and_writes_back_unchanged()
   {
      JsonArray threads =
      [
         Note("t1"),
         Started("t2"),
         Asked("t3", [ThreadFixtures.Question("q1", "Why?", "s1", At, ("r1", RequestState.Canceled, "s1"), ("r2", RequestState.Done, "s2")), Reply("a1", "r2", "s2"), Pending("q2", "r3")]),
         Decided("t4"),
      ];

      var loaded = JsonSerializer.Deserialize(threads.ToJsonString(), LotsenJsonContext.Default.TourThreads)!;

      Assert.True(JsonNode.DeepEquals(threads, JsonSerializer.SerializeToNode(loaded, LotsenJsonContext.Default.TourThreads)), JsonSerializer.Serialize(loaded, LotsenJsonContext.Default.TourThreads));
      var asked = Assert.IsType<QuestionThread.Sent.Asked>(loaded[2]);
      Assert.Equal(("q2", "q2"), (asked.LastQuestion!.Id, asked.Waiting!.Id));
      Assert.Equal("r2", ((ThreadMessage.Question)asked.Messages[0]).Answered!.Id);
      Assert.Equal(["s2", "s1"], Assert.IsType<ReviewerDecision.PlanApproved>(Assert.IsType<QuestionThread.Sent.Decided>(loaded[3]).Decision).StopIds);
   }

   [Fact]
   public void Thread_that_does_not_load_makes_its_tour_unreadable_in_the_store()
   {
      var root = Directory.CreateTempSubdirectory("lotsen-tests-").FullName;
      try
      {
         var store = new FileTourStore(new DataDirectory(Path.Combine(root, "data")));
         var tour = store.Create(new PullRequestTarget("test", new Uri("https://platform.test/shop/pull/42")), Familiarity.Some);
         var path = store.PathOf(tour.Id);
         var file = JsonNode.Parse(File.ReadAllText(path))!;
         file["tour"]!["threads"] = new JsonArray([With(Started("t1"), "action", new JsonObject { ["kind"] = "summarize" })]);
         File.WriteAllText(path, file.ToJsonString());

         Assert.Throws<InvalidDataException>(() => store.Get(tour.Id));
      }
      finally
      {
         Directory.Delete(root, recursive: true);
      }
   }

   private static JsonObject Asked(string id, JsonArray messages) => ThreadFixtures.Thread("asked", id, new ThreadAnchor.WholeTour(), QuestionThreadState.Open, messages);

   private const string PlanApproved = """{ "kind": "planApproved", "stopIds": ["s2", "s1"], "skippedStops": [], "familiarity": "some" }""";

   private static JsonObject Decided(string id, ThreadAnchor? anchor = null) =>
      With(ThreadFixtures.Thread("decided", id, anchor ?? new ThreadAnchor.WholeTour(), QuestionThreadState.Open, [Pending($"q-{id}", $"r-{id}")]), "decision", JsonNode.Parse(PlanApproved));

   private static JsonObject Started(string id) =>
      ThreadFixtures.Thread("started", id, new ThreadAnchor.WholeTour(), QuestionThreadState.Open, [ThreadFixtures.AgentMessage($"m-{id}", "s1", "Look here.", At)]);

   private static JsonObject Note(string id) => JsonSerializer.SerializeToNode(ThreadFixtures.Note(id, new ThreadAnchor.WholeTour()), LotsenJsonContext.Default.QuestionThread)!.AsObject();

   private static JsonObject Pending(string questionId, string requestId) => ThreadFixtures.Question(questionId, "Why?", null, At, (requestId, RequestState.Pending, "s1"));

   private static JsonObject Reply(string id, string requestId, string sessionId = "s1") => ThreadFixtures.Reply(id, requestId, sessionId, "Here.", At);

   private static JsonObject With(JsonObject node, string property, JsonNode? value)
   {
      node[property] = value;
      return node;
   }
}
