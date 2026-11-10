using System.Collections;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Lotsen.Json;
using Lotsen.Sessions;

namespace Lotsen.Tours;

/// <summary>A tour's threads in the order they were started, which only the table of this class changes.</summary>
/// <remarks>
/// The IDs of the threads, their messages and their requests are unique among them all. The constructor checks it, so a stored file that repeats an ID fails to load. A removed thread's IDs
/// leave the set, so a later write may draw one of them again. In JSON the threads are a plain array.
/// </remarks>
[JsonConverter(typeof(Json))]
internal sealed partial class TourThreads : IReadOnlyList<QuestionThread>
{
   private readonly ImmutableArray<QuestionThread> _threads;
   private readonly HashSet<string> _ids;

   private TourThreads(ImmutableArray<QuestionThread> threads)
   {
      _ids = new HashSet<string>(StringComparer.Ordinal);
      foreach (var thread in threads)
      {
         if (thread is null)
         {
            throw StoredData.Invalid("A tour's threads cannot hold null.");
         }

         foreach (var id in IdsOf(thread))
         {
            if (!_ids.Add(id))
            {
               throw StoredData.Invalid($"The ID '{id}' appears more than once among the tour's threads, messages and requests.");
            }
         }
      }

      _threads = threads;
   }

   /// <summary>The IDs of the threads, their messages and their requests.</summary>
   public IReadOnlyCollection<string> Ids => _ids;

   /// <summary>The threads of a tour that has none.</summary>
   public static TourThreads Empty { get; } = new([]);

   public int Count => _threads.Length;

   public QuestionThread this[int index] => _threads[index];

   /// <summary>Returns the thread, or null when the tour has none with the ID.</summary>
   public QuestionThread? Find(string threadId) => _threads.FirstOrDefault(thread => thread.Id == threadId);

   /// <summary>Returns the question with its thread, or null when no thread of the tour holds it.</summary>
   public (QuestionThread.Sent Thread, ThreadMessage.Question Question)? FindQuestion(string questionId)
   {
      foreach (var thread in _threads.OfType<QuestionThread.Sent>())
      {
         if (thread.Messages.OfType<ThreadMessage.Question>().FirstOrDefault(question => question.Id == questionId) is { } found)
         {
            return (thread, found);
         }
      }

      return null;
   }

   /// <summary>Returns the first thread whose last question waits and that holds back a thread of the wait group, other than <paramref name="except"/>, or null when none waits.</summary>
   /// <remarks>
   /// A follow-up in the thread of an action or a decision is a question about it, so the thread's last question decides. <see cref="WaitGroups.HoldsBack"/> says which groups count.
   /// </remarks>
   public QuestionThread.Sent? Waiting(WaitGroup group, QuestionThread? except = null) =>
      _threads.OfType<QuestionThread.Sent>().FirstOrDefault(thread => thread.Id != except?.Id && WaitGroupOf(thread) is { } of && of.HoldsBack(group) && thread.Waiting is not null);

   /// <summary>Returns the wait group of the thread's tour action or reviewer decision, or null for a thread outside every group.</summary>
   public static WaitGroup? WaitGroupOf(QuestionThread thread) => thread switch
   {
      QuestionThread.Sent.Asked asked => asked.Action is { } action ? ActionRules.WaitsAlone(action) : null,
      QuestionThread.Sent.Decided decided => DecisionRules.WaitsAlone(decided.Decision),
      QuestionThread.Sent.Started or QuestionThread.Note => null,
   };

   /// <summary>Returns the wait group whose waiting threads hold back a follow-up in the thread and a re-send of that follow-up, or null when nothing holds them back.</summary>
   /// <remarks>
   /// It is the thread's own group, except for a replan or a reorder. Only the thread's first question asks Claude to change the plan, and a follow-up is a question about that change, so
   /// nothing holds it back. A re-send of the first question waits for <see cref="WaitGroupOf"/>, as a new request does.
   /// </remarks>
   public static WaitGroup? FollowUpGroupOf(QuestionThread thread) => WaitGroupOf(thread) is { } group && group != WaitGroup.Plan ? group : null;

   /// <summary>Returns the threads without the notes, as every agent session sees them.</summary>
   public TourThreads WithoutNotes() => _threads.Any(thread => thread is QuestionThread.Note) ? new([.. _threads.Where(thread => thread is not QuestionThread.Note)]) : this;

   public IEnumerator<QuestionThread> GetEnumerator() => ((IEnumerable<QuestionThread>)_threads).GetEnumerator();

   IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

   private static IEnumerable<string> IdsOf(QuestionThread thread)
   {
      yield return thread.Id;
      foreach (var message in (thread as QuestionThread.Sent)?.Messages ?? [])
      {
         yield return message.Id;
         foreach (var request in (message as ThreadMessage.Question)?.Requests ?? [])
         {
            yield return request.Id;
         }
      }
   }

   /// <summary>The commands of one write that queue no question, on the latest stored tour and the write's time.</summary>
   internal class Handle
   {
      internal Handle(Tour latest, DateTimeOffset now)
      {
         Latest = latest;
         Now = now;
      }

      private protected Tour Latest { get; }

      private protected DateTimeOffset Now { get; }

      /// <summary>Returns the command applied to the latest tour, or its refusal.</summary>
      public OperationResult<ThreadWrite> Apply<TCommand>(TCommand command)
         where TCommand : ThreadCommand, ThreadCommand.ISendsNothing => TourThreads.Apply(Latest, command, Now, TourIds.Random);
   }

   /// <summary>The commands of one write that names the tour's bound session, the commands that queue a question to that session included.</summary>
   internal sealed class SessionHandle : Handle
   {
      private readonly Recipient _recipient;

      internal SessionHandle(Tour latest, DateTimeOffset now, Recipient recipient)
         : base(latest, now)
      {
         _recipient = recipient;
      }

      /// <summary>Returns the reviewer's question as a new thread with a pending request to the bound session.</summary>
      /// <param name="anchor">Where the reviewer asked, as the feature read it.</param>
      /// <param name="text">The reviewer's words.</param>
      /// <param name="stopId">The stop that the reviewer views, or null for none.</param>
      public OperationResult<ThreadWrite> Ask(ThreadAnchor anchor, string text, string? stopId) =>
         TourThreads.Apply(Latest, new ThreadCommand.Ask(anchor, text, stopId, _recipient), Now, TourIds.Random);

      /// <summary>Returns the reviewer's tour action as a new thread with a pending request to the bound session, or the refusal.</summary>
      /// <param name="admitted">The action and its anchor, as <see cref="ActionRules.Admit"/> built them from the latest tour.</param>
      /// <param name="text">The reviewer's words, or empty without words.</param>
      /// <param name="stopId">The stop that the reviewer views, or null for none.</param>
      public OperationResult<ThreadWrite> Request(ActionRules.Admitted admitted, string text, string? stopId) =>
         TourThreads.Apply(Latest, new ThreadCommand.Request(admitted, text, stopId, _recipient), Now, TourIds.Random);

      /// <summary>Returns the report of the reviewer's decision as a new thread with a pending request to the bound session, or the refusal.</summary>
      /// <param name="decision">The decision, built from <paramref name="tour"/>.</param>
      /// <param name="text">The reviewer's words, or empty without words.</param>
      /// <param name="stopId">The stop that the reviewer views, or null for none.</param>
      /// <param name="waitField">The input's field that a refusal names, while another thread of the decision's wait group waits.</param>
      /// <param name="tour">The latest tour with the write's other fields changed, such as the new level, or null for the latest tour itself.</param>
      public OperationResult<ThreadWrite> Report(ReviewerDecision decision, string text, string? stopId, FieldPath waitField, Tour? tour = null) =>
         TourThreads.Apply(tour ?? Latest, new ThreadCommand.Report(decision, text, stopId, _recipient, waitField), Now, TourIds.Random);

      /// <summary>Returns the reviewer's follow-up in the thread, with a pending request to the bound session, or the refusal.</summary>
      public OperationResult<ThreadWrite> FollowUp(string threadId, string text, string? stopId) =>
         TourThreads.Apply(Latest, new ThreadCommand.FollowUp(threadId, text, stopId, _recipient), Now, TourIds.Random);

      /// <summary>Returns the question sent again to the bound session, or the refusal.</summary>
      public OperationResult<ThreadWrite> Resend(string questionId) => TourThreads.Apply(Latest, new ThreadCommand.Resend(questionId, _recipient), Now, TourIds.Random);

      /// <summary>Returns the notes sent to the bound session, or the refusal of the whole batch.</summary>
      public OperationResult<ThreadWrite> SendNotes(IReadOnlyList<string> threadIds) => TourThreads.Apply(Latest, new ThreadCommand.SendNotes(threadIds, _recipient), Now, TourIds.Random);
   }

   /// <summary>Reads and writes the threads as a JSON array of <see cref="QuestionThread"/>, with the checks of the constructor on load.</summary>
   internal sealed class Json : JsonConverter<TourThreads>, IJsonSchemaConverter
   {
      public override TourThreads Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
      {
         if (reader.TokenType != JsonTokenType.StartArray)
         {
            throw new JsonException("A tour's threads must be an array.");
         }

         var info = ThreadInfo(options);
         var threads = ImmutableArray.CreateBuilder<QuestionThread>();
         while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
         {
            threads.Add(reader.TokenType == JsonTokenType.Null ? throw StoredData.Invalid("A tour's threads cannot hold null.") : JsonSerializer.Deserialize(ref reader, info)!);
         }

         return new TourThreads(threads.ToImmutable());
      }

      public override void Write(Utf8JsonWriter writer, TourThreads value, JsonSerializerOptions options)
      {
         var info = ThreadInfo(options);
         writer.WriteStartArray();
         foreach (var thread in value)
         {
            JsonSerializer.Serialize(writer, thread, info);
         }

         writer.WriteEndArray();
      }

      public JsonObject GetSchema() => GetSchema(OperationSchemas.Export);

      public JsonObject GetSchema(Func<JsonTypeInfo, JsonNode> export) => new() { ["type"] = "array", ["items"] = export(LotsenJsonContext.Default.QuestionThread) };

      private static JsonTypeInfo<QuestionThread> ThreadInfo(JsonSerializerOptions options) => options.GetTypeInfo<QuestionThread>();
   }
}
