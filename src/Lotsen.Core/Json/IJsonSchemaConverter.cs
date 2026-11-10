using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Lotsen.Json;

/// <summary>A custom converter that describes the JSON it accepts, which the schema exporter cannot see through a converter.</summary>
internal interface IJsonSchemaConverter
{
   /// <summary>Returns a new JSON Schema object for the values the converter reads and writes.</summary>
   JsonObject GetSchema();

   /// <summary>Returns a new JSON Schema object for the values the converter reads and writes, with each type that the values hold exported by <paramref name="export"/>.</summary>
   /// <remarks>A converter whose values hold another type overrides it, so a caller with its own export, such as <see cref="Operations.UiContract"/>, reaches that type.</remarks>
   JsonObject GetSchema(Func<JsonTypeInfo, JsonNode> export) => GetSchema();
}
