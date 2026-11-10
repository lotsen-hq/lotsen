using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Lotsen.Git;
using Lotsen.Json;
using Lotsen.Operations;
using Lotsen.Tours;

namespace Lotsen.Tests;

/// <summary>
/// Builds the JSON Schema document of the UI's contract, from which <c>json-schema-to-typescript</c> generates <c>ui/src/lib/contract.generated.ts</c>: every event payload, with the revision of a
/// tour-scoped kind, and the input and output of every operation that the UI calls, each as <see cref="UiContract.Export"/> writes it, the refusal and the body of a successful response.
/// </summary>
/// <remarks>
/// <para>
/// The root is the <c>EventPayloads</c> map from each event's name to its payload. The definitions hold <c>EventName</c>, the <c>Operations</c> map from each operation's name to its input and
/// output, <c>OperationName</c>, <c>OperationError</c>, and each object, union and enum of a lotsen type under the name that <see cref="LotsenJsonContext"/> gives its type info, or the name in
/// <see cref="Renamed"/>.
/// </para>
/// <para>
/// A variant of a union is written without its discriminator where a property declares the variant's own type, so such a use gets the name in <see cref="DirectNames"/>. Two schemas under one
/// name fail the build of the document.
/// </para>
/// </remarks>
internal sealed class ContractDocument
{
   /// <summary>The definition names of union variants where a property declares the variant's own type.</summary>
   private static readonly Dictionary<Type, string> DirectNames = new() { [typeof(TourPin.Pinned)] = "PinnedCommits" };

   /// <summary>The definition names of types whose type info name says too little in TypeScript, where the name of a nested type no longer names its outer type.</summary>
   private static readonly Dictionary<Type, string> Renamed = new()
   {
      [typeof(FileDiff.Text)] = "TextDiff",
      [typeof(QuestionThread.Note)] = "NoteThread",
      [typeof(QuestionThread.Sent.Asked)] = "AskedThread",
      [typeof(QuestionThread.Sent.Decided)] = "DecidedThread",
      [typeof(QuestionThread.Sent.Started)] = "StartedThread",
      [typeof(QuestionThreadState)] = "ThreadState",
      [typeof(ThreadMessage.Question)] = "ThreadQuestion",
      [typeof(ThreadMessage.Question.Reply)] = "ThreadReply",
      [typeof(ThreadMessage.AgentMessage)] = "ThreadAgentMessage",
   };

   private static readonly Dictionary<Type, string> ContextNames = typeof(LotsenJsonContext).GetProperties()
      .Where(property => property.PropertyType.IsGenericType && property.PropertyType.GetGenericTypeDefinition() == typeof(JsonTypeInfo<>))
      .ToDictionary(property => property.PropertyType.GetGenericArguments()[0], property => Renamed.GetValueOrDefault(property.PropertyType.GetGenericArguments()[0]) ?? property.Name);

   private static readonly HashSet<Type> Variants = [.. ContextNames.Keys
      .Select(type => LotsenJsonContext.Default.GetTypeInfo(type)!.PolymorphismOptions)
      .SelectMany(options => options?.DerivedTypes.Select(derived => derived.DerivedType) ?? [])];

   private readonly SortedDictionary<string, (Type Type, JsonObject Schema)> _definitions = new(StringComparer.Ordinal);

   private ContractDocument()
   {
   }

   /// <summary>Returns the document.</summary>
   /// <exception cref="InvalidOperationException">Two schemas, or two types, get one name.</exception>
   public static JsonObject Build()
   {
      var document = new ContractDocument();
      var events = Map(ServerEvents.All.Select(kind => (kind.Name, kind.WithRevision(document.Export(kind.Payload, UiContract.Flow.Received)))));
      var operations = Map(UiContract.Operations.Select(operation => (operation.Name, (JsonNode)Map(
         [("input", document.Export(operation.InputType, UiContract.Flow.Sent)), ("output", document.Export(operation.OutputType, UiContract.Flow.Received))]))));
      document.Export(LotsenJsonContext.Default.OperationError, UiContract.Flow.Received);
      document.Export(LotsenJsonContext.Default.OperationResponseBody, UiContract.Flow.Received);
      document.Add(typeof(ServerEventKind), "EventName", Names(ServerEvents.All.Select(kind => kind.Name)));
      document.Add(typeof(Operation), "Operations", operations);
      document.Add(typeof(OperationCatalog), "OperationName", Names(UiContract.Operations.Select(operation => operation.Name)));
      events["title"] = "EventPayloads";
      events["definitions"] = new JsonObject(document._definitions.Select(entry => KeyValuePair.Create(entry.Key, (JsonNode?)Titled(entry.Key, entry.Value.Schema))));
      return events;
   }

   private JsonNode Export(JsonTypeInfo type, UiContract.Flow flow) => UiContract.Export(type, flow, Define);

   private string Define(Type type, bool variant, JsonObject schema)
   {
      var name = !variant && Variants.Contains(type)
         ? DirectNames.GetValueOrDefault(type) ?? throw new InvalidOperationException($"A property declares the variant {type} of a union, which needs a name in {nameof(DirectNames)}.")
         : ContextNames[type];
      Add(type, name, schema);
      return name;
   }

   private void Add(Type type, string name, JsonObject schema)
   {
      if (!_definitions.TryGetValue(name, out var defined))
      {
         _definitions[name] = (type, (JsonObject)schema.DeepClone());
      }
      else if (defined.Type != type || !JsonNode.DeepEquals(defined.Schema, schema))
      {
         throw new InvalidOperationException($"The contract defines {name} twice: as {defined.Schema.ToJsonString()} of {defined.Type}, and as {schema.ToJsonString()} of {type}.");
      }
   }

   private static JsonObject Map(IEnumerable<(string Name, JsonNode Schema)> properties)
   {
      var list = properties.ToList();
      return new JsonObject
      {
         ["type"] = "object",
         ["properties"] = new JsonObject(list.Select(property => KeyValuePair.Create(property.Name, (JsonNode?)property.Schema))),
         ["required"] = new JsonArray([.. list.Select(property => JsonValue.Create(property.Name))]),
         ["additionalProperties"] = false,
      };
   }

   private static JsonObject Names(IEnumerable<string> names) => new() { ["type"] = "string", ["enum"] = new JsonArray([.. names.Select(name => JsonValue.Create(name))]) };

   private static JsonObject Titled(string name, JsonObject schema)
   {
      var titled = new JsonObject { ["title"] = name };
      foreach (var (key, value) in schema)
      {
         titled[key] = value?.DeepClone();
      }

      return titled;
   }
}
