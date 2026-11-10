using System.Text.Json;
using System.Text.Json.Nodes;
using Lotsen.Json;
using Lotsen.Sessions;

namespace Lotsen.Adapter;

/// <summary>Turns the events of the adapter's agent session into Claude Code channel notifications, which reach Claude as a <c>&lt;channel&gt;</c> block.</summary>
/// <remarks>
/// A notification's params are <c>content</c>, the text that Claude reads, and <c>meta</c>, whose entries become attributes of the block. Claude Code drops a meta key that holds anything but letters,
/// digits and underscores: https://code.claude.com/docs/en/channels-reference#notification-format. Every notification names its event in meta, and each event about a tour names the tour.
/// </remarks>
internal static class ChannelNotifications
{
   /// <summary>The method of a channel notification.</summary>
   public const string Method = "notifications/claude/channel";

   /// <summary>Returns the params of the notification for a session event, or null for an event that Claude does not receive.</summary>
   /// <exception cref="JsonException">The event's payload does not fit its type.</exception>
   public static JsonObject? ParamsFor(SessionEvent sessionEvent) => sessionEvent.Type switch
   {
      SessionEvents.TourTakenOver => TourTakenOver(Read(sessionEvent, LotsenJsonContext.Default.TourTakenOverEvent)),
      SessionEvents.Question => Question(Read(sessionEvent, LotsenJsonContext.Default.QuestionEvent)),
      SessionEvents.Probe => Probe(Read(sessionEvent, LotsenJsonContext.Default.ProbeEvent)),
      SessionEvents.Answers => Answers(Read(sessionEvent, LotsenJsonContext.Default.AnswersEvent)),
      SessionEvents.ThreadDeleted => ThreadDeleted(Read(sessionEvent, LotsenJsonContext.Default.ThreadDeletedEvent)),
      _ => null,
   };

   // The request to ack lives in the server's instructions, so the content only says what the event is.
   private static JsonObject Probe(ProbeEvent payload) => new()
   {
      ["content"] = "lotsen checks that its questions reach this session.",
      ["meta"] = new JsonObject { ["event"] = SessionEvents.Probe, ["nonce"] = payload.Nonce },
   };

   private static JsonObject TourTakenOver(TourTakenOverEvent payload) => Notification(
      SessionEvents.TourTakenOver,
      payload.TourId.Value,
      $"The user took over tour {payload.TourId} in another Claude Code session. This session is no longer attached to the tour, so lotsen refuses its set_plan and put_stop calls for the "
      + "tour. Stop writing the tour. To write it again, ask the user, and only after the user confirms, call attach_tour with takeOver true.");

   // The content is the reviewer's words alone, which Claude Code shows as untrusted channel content, or without words a sentence of the server's for a tour action or a decision. Everything else
   // Claude fetches with fetch_question, so no code or stop text reaches the block, and meta holds only IDs, the kind of the action or decision and the nonce, which the server generated.
   private static JsonObject Question(QuestionEvent payload)
   {
      var content = payload switch
      {
         { Text.Length: > 0 } => payload.Text,
         { Action: { } kind } => $"The reviewer asks for the tour action {kind}, without words. Call fetch_question with the tourId and questionId for its anchor and what it asks.",
         { Decision: { } kind } => $"The reviewer made a decision for you to act on: {kind}, without words. Call fetch_question with the tourId and questionId for what the decision holds.",
         _ => payload.Text,
      };
      var notification = Notification(SessionEvents.Question, payload.TourId.Value, content);
      var meta = notification["meta"]!.AsObject();
      if (payload.Action is { } action)
      {
         meta["action"] = action;
      }

      if (payload.Decision is { } decision)
      {
         meta["decision"] = decision;
      }

      meta["threadId"] = payload.ThreadId;
      meta["questionId"] = payload.QuestionId;
      meta["requestId"] = payload.RequestId;
      if (payload.StopId is { } stopId)
      {
         meta["stopId"] = stopId;
      }

      if (payload.Nonce is { } nonce)
      {
         meta["nonce"] = nonce;
      }

      return notification;
   }

   // The reviewer's answers stay out of the block, so Claude reads them with fetch_answers, which also shows the reviewer that Claude has them.
   private static JsonObject Answers(AnswersEvent payload)
   {
      var notification = Notification(
         SessionEvents.Answers,
         payload.TourId.Value,
         $"The reviewer sent answers to checks of tour {payload.TourId}. Call fetch_answers with the tourId and batchId to read them before you act on them.");
      var meta = notification["meta"]!.AsObject();
      meta["batchId"] = payload.BatchId;
      if (payload.Nonce is { } nonce)
      {
         meta["nonce"] = nonce;
      }

      return notification;
   }

   private static JsonObject ThreadDeleted(ThreadDeletedEvent payload)
   {
      var canceled = payload.QuestionId is { } questionId ? $" Its question {questionId} waited for your reply, and lotsen canceled it, so don't reply to it." : "";
      var notification = Notification(
         SessionEvents.ThreadDeleted,
         payload.TourId.Value,
         $"The reviewer deleted thread {payload.ThreadId} of tour {payload.TourId}, with every message in it.{canceled} Stop any work on the thread, because lotsen refuses every call on it.");
      var meta = notification["meta"]!.AsObject();
      meta["threadId"] = payload.ThreadId;
      if (payload.QuestionId is { } question)
      {
         meta["questionId"] = question;
      }

      return notification;
   }

   private static JsonObject Notification(string eventType, string tourId, string content) => new()
   {
      ["content"] = content,
      ["meta"] = new JsonObject { ["event"] = eventType, ["tourId"] = tourId },
   };

   private static T Read<T>(SessionEvent sessionEvent, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
      JsonSerializer.Deserialize(sessionEvent.Data, typeInfo) ?? throw new JsonException($"The {sessionEvent.Type} event has no payload.");
}
