using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Lotsen.Json;

namespace Lotsen.Tours;

/// <summary>The pin that a synthesis was written at, whose commits its <c>ref:</c> links read at: a pull request's iteration or a repository tour's snapshot.</summary>
[JsonConverter(typeof(Json))]
internal closed record SynthesisPin
{
   private SynthesisPin()
   {
   }

   /// <summary>The iteration of a pull request's tour.</summary>
   /// <param name="Pin">The tour's pin when the session wrote the synthesis.</param>
   internal sealed record PullRequest(TourPin.Pinned Pin) : SynthesisPin;

   /// <summary>The snapshot of a repository tour.</summary>
   /// <param name="Pin">The tour's pin when the session wrote the synthesis.</param>
   internal sealed record Repository(TourPin.Snapshot Pin) : SynthesisPin;

   /// <summary>Returns the pin of a synthesis written at the tour's pin.</summary>
   /// <returns>The synthesis's pin, or null while the tour pins or after its pin failed, so no commit is there to check links against.</returns>
   public static SynthesisPin? Of(TourPin pin) => pin switch
   {
      TourPin.Pinned pinned => new PullRequest(pinned),
      TourPin.Snapshot snapshot => new Repository(snapshot),
      TourPin.Pinning or TourPin.Failed => null,
   };

   /// <summary>Reads and writes the pin as the object of its <see cref="TourPin"/> without the <c>state</c>, so a pull request's synthesis keeps the form of <see cref="TourPin.Pinned"/>.</summary>
   /// <remarks>Only the iteration's object has a <c>mergeBase</c>, which tells the two apart on a read.</remarks>
   internal sealed class Json : JsonConverter<SynthesisPin>, IJsonSchemaConverter
   {
      private static readonly string MergeBase = JsonNamingPolicy.CamelCase.ConvertName(nameof(TourPin.Pinned.MergeBase));

      public override SynthesisPin Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
      {
         using var document = JsonDocument.ParseValue(ref reader);
         var root = document.RootElement;
         if (root.ValueKind != JsonValueKind.Object)
         {
            throw new JsonException("A synthesis's pin must be an object.");
         }

         return root.TryGetProperty(MergeBase, out _)
            ? new PullRequest(root.Deserialize(options.GetTypeInfo<TourPin.Pinned>())!)
            : new Repository(root.Deserialize(options.GetTypeInfo<TourPin.Snapshot>())!);
      }

      public override void Write(Utf8JsonWriter writer, SynthesisPin value, JsonSerializerOptions options)
      {
         switch (value)
         {
            case PullRequest pullRequest:
               JsonSerializer.Serialize(writer, pullRequest.Pin, options.GetTypeInfo<TourPin.Pinned>());
               break;
            case Repository repository:
               JsonSerializer.Serialize(writer, repository.Pin, options.GetTypeInfo<TourPin.Snapshot>());
               break;
         }
      }

      public JsonObject GetSchema() => GetSchema(OperationSchemas.Export);

      public JsonObject GetSchema(Func<JsonTypeInfo, JsonNode> export) => new() { ["anyOf"] = new JsonArray(export(LotsenJsonContext.Default.Pinned), export(LotsenJsonContext.Default.Snapshot)) };
   }
}
