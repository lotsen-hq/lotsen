using System.Text.Json;
using System.Text.Json.Nodes;
using Lotsen.Json;
using Lotsen.Tours;

namespace Lotsen.Tests;

/// <summary>Builds stored threads from their JSON, as a tour file holds them, so a fixture can name its own IDs and sessions.</summary>
internal static class ThreadFixtures
{
   /// <summary>The time that the fixtures store, unless a test gives another.</summary>
   public static readonly DateTimeOffset At = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

   /// <summary>Returns the threads as a tour holds them, through the converter's load checks.</summary>
   public static TourThreads Threads(params QuestionThread[] threads) =>
      JsonSerializer.Deserialize(new JsonArray([.. threads.Select(thread => Node(thread))]).ToJsonString(), LotsenJsonContext.Default.TourThreads)!;

   /// <summary>Returns the thread that the JSON holds.</summary>
   public static QuestionThread Parse(JsonNode thread) => JsonSerializer.Deserialize(thread.ToJsonString(), LotsenJsonContext.Default.QuestionThread)!;

   /// <summary>Returns an asked thread with one question <c>q-&lt;id&gt;</c> and its request <c>r-&lt;id&gt;</c>, and for a resolved request the reply <c>a-&lt;id&gt;</c>.</summary>
   public static QuestionThread Asked(
      string id,
      ThreadAnchor anchor,
      QuestionThreadState state = QuestionThreadState.Open,
      string? stopId = null,
      TourAction? action = null,
      RequestState request = RequestState.Pending,
      string text = "Why here?",
      string sessionId = "session1",
      DateTimeOffset? at = null)
   {
      var time = at ?? At;
      var question = Question($"q-{id}", text, stopId, time, (Id: $"r-{id}", State: request, SessionId: sessionId));
      JsonArray messages = [question];
      if (QuestionRequest.IsResolution(request))
      {
         messages.Add(Reply($"a-{id}", $"r-{id}", sessionId, "Here.", time));
      }

      var thread = Thread("asked", id, anchor, state, messages);
      if (action is not null)
      {
         thread["action"] = JsonSerializer.SerializeToNode(action, LotsenJsonContext.Default.TourAction);
      }

      return Parse(thread);
   }

   /// <summary>Returns a decided thread on the tour anchor with one question <c>q-&lt;id&gt;</c> and its request <c>r-&lt;id&gt;</c>, and for a resolved request the reply <c>a-&lt;id&gt;</c>.</summary>
   public static QuestionThread Decided(
      string id,
      ReviewerDecision decision,
      QuestionThreadState state = QuestionThreadState.Open,
      RequestState request = RequestState.Pending,
      string text = "",
      string sessionId = "session1",
      DateTimeOffset? at = null)
   {
      var time = at ?? At;
      JsonArray messages = [Question($"q-{id}", text, null, time, (Id: $"r-{id}", State: request, SessionId: sessionId))];
      if (QuestionRequest.IsResolution(request))
      {
         messages.Add(Reply($"a-{id}", $"r-{id}", sessionId, "Here.", time));
      }

      var thread = Thread("decided", id, new ThreadAnchor.WholeTour(), state, messages);
      thread["decision"] = JsonSerializer.SerializeToNode(decision, LotsenJsonContext.Default.ReviewerDecision);
      return Parse(thread);
   }

   /// <summary>Returns the reviewer decision that the JSON holds, as a stored thread holds it, for a test that needs a decision without the tour that its factory reads.</summary>
   public static ReviewerDecision Decision(string json) => JsonSerializer.Deserialize(json, LotsenJsonContext.Default.ReviewerDecision)!;

   /// <summary>Returns a started thread whose agent message <c>m-&lt;id&gt;</c> the session wrote.</summary>
   public static QuestionThread Started(string id, ThreadAnchor anchor, string text, string sessionId = "session1", QuestionThreadState state = QuestionThreadState.Open, DateTimeOffset? at = null) =>
      Parse(Thread("started", id, anchor, state, [AgentMessage($"m-{id}", sessionId, text, at ?? At)]));

   /// <summary>Returns a note.</summary>
   public static QuestionThread Note(string id, ThreadAnchor anchor, string text = "My note.", string? stopId = null, QuestionThreadState state = QuestionThreadState.Open, DateTimeOffset? at = null)
   {
      var thread = new JsonObject
      {
         ["kind"] = "note",
         ["id"] = id,
         ["anchor"] = JsonSerializer.SerializeToNode(anchor, LotsenJsonContext.Default.ThreadAnchor),
         ["state"] = StateName(state),
         ["text"] = text,
         ["savedAt"] = at ?? At,
      };
      if (stopId is not null)
      {
         thread["stopId"] = stopId;
      }

      return Parse(thread);
   }

   /// <summary>Returns the JSON of a sent thread of the kind with the messages.</summary>
   public static JsonObject Thread(string kind, string id, ThreadAnchor anchor, QuestionThreadState state, JsonArray messages) => new()
   {
      ["kind"] = kind,
      ["id"] = id,
      ["anchor"] = JsonSerializer.SerializeToNode(anchor, LotsenJsonContext.Default.ThreadAnchor),
      ["state"] = StateName(state),
      ["messages"] = messages,
   };

   /// <summary>Returns the JSON of a question with the requests.</summary>
   public static JsonObject Question(string id, string text, string? stopId, DateTimeOffset at, params (string Id, RequestState State, string SessionId)[] requests)
   {
      var question = new JsonObject
      {
         ["kind"] = "question",
         ["id"] = id,
         ["createdAt"] = at,
         ["text"] = text,
         ["requests"] = new JsonArray([.. requests.Select(request => (JsonNode)new JsonObject
         {
            ["id"] = request.Id,
            ["state"] = JsonSerializer.SerializeToNode(request.State, LotsenJsonContext.Default.RequestState),
            ["sessionId"] = request.SessionId,
            ["sentAt"] = at,
         })]),
      };
      if (stopId is not null)
      {
         question["stopId"] = stopId;
      }

      return question;
   }

   /// <summary>Returns the JSON of a reply, or of a decline without words when <paramref name="text"/> is null.</summary>
   public static JsonObject Reply(string id, string requestId, string sessionId, string? text, DateTimeOffset at)
   {
      var reply = new JsonObject { ["kind"] = "reply", ["id"] = id, ["createdAt"] = at, ["sessionId"] = sessionId, ["requestId"] = requestId };
      if (text is not null)
      {
         reply["text"] = text;
      }

      return reply;
   }

   /// <summary>Returns the JSON of an agent message.</summary>
   public static JsonObject AgentMessage(string id, string sessionId, string text, DateTimeOffset at) =>
      new() { ["kind"] = "agentMessage", ["id"] = id, ["createdAt"] = at, ["sessionId"] = sessionId, ["text"] = text };

   private static JsonNode Node(QuestionThread thread) => JsonSerializer.SerializeToNode(thread, LotsenJsonContext.Default.QuestionThread)!;

   private static JsonNode StateName(QuestionThreadState state) => JsonSerializer.SerializeToNode(state, LotsenJsonContext.Default.QuestionThreadState)!;
}
