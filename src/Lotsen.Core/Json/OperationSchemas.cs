using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;

namespace Lotsen.Json;

/// <summary>Exports JSON Schemas from source-generated type metadata, as agent tools need them.</summary>
internal static class OperationSchemas
{
   private static readonly JsonSchemaExporterOptions Options = new()
   {
      TreatNullObliviousAsNonNullable = true,
      TransformSchemaNode = Transform,
   };

   public static JsonNode Export(JsonTypeInfo typeInfo) => typeInfo.GetJsonSchemaAsNode(Options);

   /// <summary>Returns the schema that <see cref="Export(JsonTypeInfo)"/> returns, with <paramref name="then"/> applied to each node after this class's own changes.</summary>
   public static JsonNode Export(JsonTypeInfo typeInfo, Func<JsonSchemaExporterContext, JsonNode, JsonNode> then) => typeInfo.GetJsonSchemaAsNode(new JsonSchemaExporterOptions
   {
      TreatNullObliviousAsNonNullable = Options.TreatNullObliviousAsNonNullable,
      TransformSchemaNode = (context, schema) => then(context, Transform(context, schema)),
   });

   private static JsonNode Transform(JsonSchemaExporterContext context, JsonNode schema)
   {
      if (context.TypeInfo.Converter is IJsonSchemaConverter custom)
      {
         // The exporter sees nothing through a custom converter, so it exports true, or an object with only a default.
         return Described(custom, schema, context.PropertyInfo, nullable: false);
      }

      if (schema is JsonObject exportedObject && exportedObject["properties"] is JsonObject properties)
      {
         // The exporter calls no transform for a property of a value type with a custom converter that is nullable or has a default, such as a nullable enum, so its object describes it.
         foreach (var property in context.TypeInfo.Properties)
         {
            var underlying = Nullable.GetUnderlyingType(property.PropertyType);
            if (context.TypeInfo.Options.GetTypeInfo(underlying ?? property.PropertyType).Converter is IJsonSchemaConverter converter
               && properties[property.Name] is { } exported
               && (underlying is not null || exported is not JsonObject { } described || !described.ContainsKey("type")))
            {
               properties[property.Name] = Described(converter, exported, property, nullable: underlying is not null);
            }
         }
      }

      return WithDescription(schema, context.PropertyInfo);
   }

   private static JsonObject Described(IJsonSchemaConverter converter, JsonNode exported, JsonPropertyInfo? property, bool nullable)
   {
      var described = converter.GetSchema();
      if (nullable)
      {
         described["type"] = new JsonArray(described["type"]?.DeepClone(), "null");
         if (described["enum"] is JsonArray values)
         {
            values.Add(null);
         }
      }

      foreach (var (key, value) in exported as JsonObject ?? [])
      {
         if (!described.ContainsKey(key))
         {
            described[key] = value?.DeepClone();
         }
      }

      return (JsonObject)WithDescription(described, property);
   }

   private static JsonNode WithDescription(JsonNode schema, JsonPropertyInfo? property)
   {
      if (schema is JsonObject result
         && !result.ContainsKey("description")
         && property?.AttributeProvider?.GetCustomAttributes(typeof(DescriptionAttribute), inherit: false) is [DescriptionAttribute { Description: var description }, ..])
      {
         result.Insert(0, "description", description);
      }

      return schema;
   }
}
