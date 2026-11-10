using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using Lotsen.Json;
using Lotsen.Prose;
using Lotsen.Tours;

namespace Lotsen.Operations;

/// <summary>
/// Exports the JSON Schemas of what the UI sends to the server and receives from it: the inputs and outputs of the operations that the UI calls, the event payloads and the refusals. The UI's
/// contract file, <c>ui/src/lib/contract.generated.ts</c>, is generated from them.
/// </summary>
/// <remarks>
/// <para>
/// A schema of what the UI receives describes the JSON as the server writes it. A property that the serializer omits when it is null or its default is optional and never null, and every
/// other property is required. A type that <see cref="ProseHtml"/> adds properties to has them, as <see cref="ProseHtml.Added"/> lists them.
/// </para>
/// <para>A union lists its variants alone, and each variant requires its discriminator, so the TypeScript union of the variants narrows on it.</para>
/// </remarks>
internal static class UiContract
{
   /// <summary>Which way a JSON value travels between the UI and the server.</summary>
   public enum Flow
   {
      /// <summary>An input that the UI sends.</summary>
      Sent,

      /// <summary>An output, an event payload or a refusal that the UI receives, with its prose as HTML.</summary>
      Received,
   }

   /// <summary>The operations whose access declaration admits the UI, in catalog order.</summary>
   public static IReadOnlyList<Operation> Operations { get; } = [.. OperationCatalog.All.Where(operation => operation.Access.Admits.HasFlag(CallerKinds.Ui))];

   /// <summary>Returns a new JSON Schema of the type as it travels.</summary>
   /// <param name="type">The JSON type.</param>
   /// <param name="flow">Which way the type's values travel.</param>
   /// <param name="define">
   /// Receives the schema of each object, union and enum of a lotsen type, with whether it is a variant of a union, and returns the name of the definition that holds it. The export then refers
   /// to <c>#/definitions/&lt;name&gt;</c> in place of the schema. Null keeps every schema in place.
   /// </param>
   public static JsonNode Export(JsonTypeInfo type, Flow flow, Func<Type, bool, JsonObject, string>? define = null) => new Exporter(flow, define).Export(type);

   private sealed class Exporter
   {
      private static readonly string[] SiteKeys = ["description", "default"];
      private static readonly string MessagesProperty = JsonNamingPolicy.CamelCase.ConvertName(nameof(QuestionThread.Sent.Messages));

      private readonly Flow _flow;
      private readonly Func<Type, bool, JsonObject, string>? _define;

      // The schema of each type at its first node, without what belongs to the property there, for the later nodes that the exporter writes as a pointer to the first.
      private readonly Dictionary<Type, JsonObject> _first = [];

      public Exporter(Flow flow, Func<Type, bool, JsonObject, string>? define)
      {
         _flow = flow;
         _define = define;
      }

      public JsonNode Export(JsonTypeInfo type) => OperationSchemas.Export(type, Transform);

      private JsonNode Transform(JsonSchemaExporterContext context, JsonNode schema)
      {
         if (schema is not JsonObject node)
         {
            return schema;
         }

         var type = context.TypeInfo.Type;
         if (node["$ref"] is JsonValue pointer && !((string)pointer!).StartsWith("#/definitions/", StringComparison.Ordinal))
         {
            // A definition may have moved the node that the pointer names, so the node becomes a copy of the type's first node.
            var copy = _first[type].DeepClone().AsObject();
            foreach (var (key, value) in node.Where(entry => entry.Key != "$ref"))
            {
               copy[key] = value?.DeepClone();
            }

            return ShapeProperty(context.PropertyInfo, copy);
         }

         if (context.TypeInfo.Converter is IJsonSchemaConverter custom)
         {
            // The exporter sees nothing through a custom converter, so a type that the converter's values hold gets this export's shaping only through the converter.
            foreach (var (key, value) in custom.GetSchema(Held))
            {
               node[key] = value?.DeepClone();
            }
         }

         ShapeType(context, node);
         if (_define is not null && IsNamed(type, node))
         {
            node = Defined(Nullable.GetUnderlyingType(type) ?? type, context.BaseTypeInfo is not null, node);
         }

         _first.TryAdd(type, WithoutSiteKeys(node));
         return ShapeProperty(context.PropertyInfo, node);
      }

      private JsonNode Held(JsonTypeInfo type) => new Exporter(_flow, _define).Export(type);

      private void ShapeType(JsonSchemaExporterContext context, JsonObject node)
      {
         if (context.TypeInfo.PolymorphismOptions is not null && context.BaseTypeInfo is null && node["anyOf"] is JsonArray variants)
         {
            // The union is its variants, which each require the discriminator, so the outer object's type and required list go.
            if (StripNull(node))
            {
               variants.Add((JsonNode)new JsonObject { ["type"] = "null" });
            }

            node.Remove("type");
            node.Remove("required");
            return;
         }

         if (node["properties"] is not JsonObject properties)
         {
            return;
         }

         DescribeConverterProperties(context.TypeInfo, properties);
         if (QuestionThread.Sent.FirstMessages.TryGetValue(context.TypeInfo.Type, out var first) && properties[MessagesProperty] is JsonObject messages)
         {
            // The thread's messages start with the variant that its kind declares, which the type of the list cannot say.
            properties[MessagesProperty] = new JsonObject
            {
               ["type"] = "array",
               ["items"] = new JsonArray(_first[first].DeepClone()),
               ["additionalItems"] = messages["items"]!.DeepClone(),
               ["minItems"] = 1,
            };
         }

         if (_flow == Flow.Sent)
         {
            if (context.BaseTypeInfo?.PolymorphismOptions?.TypeDiscriminatorPropertyName is { } discriminator)
            {
               var required = node["required"] as JsonArray ?? [];
               if (!required.Any(name => (string?)name == discriminator))
               {
                  required.Insert(0, (JsonNode)JsonValue.Create(discriminator));
               }

               node["required"] = required;
            }

            return;
         }

         foreach (var property in context.TypeInfo.Properties.Where(property => !property.IsGetNullable))
         {
            // The schema allows null where the setter or the constructor accepts it, but the serializer writes what the getter returns.
            if (properties[property.Name] is JsonObject never)
            {
               properties[property.Name] = NeverNull(never);
            }
         }

         var added = ProseHtml.Added.Where(added => added.Owner == context.TypeInfo.Type).ToList();
         foreach (var property in added)
         {
            properties[property.Name] = Held(property.Value);
         }

         var omitted = context.TypeInfo.Properties.Where(property => property.ShouldSerialize is not null).Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
         omitted.UnionWith(added.Where(property => !property.Always).Select(property => property.Name));

         // A variant may hide a property of its discriminator's name from JSON, which the serializer still writes as the discriminator.
         if (context.BaseTypeInfo?.PolymorphismOptions?.TypeDiscriminatorPropertyName is { } written)
         {
            omitted.Remove(written);
         }

         node["required"] = new JsonArray([.. properties.Select(property => property.Key).Where(name => !omitted.Contains(name)).Select(name => JsonValue.Create(name))]);
      }

      // OperationSchemas describes a property whose type has a custom converter on its object, because the exporter may call no transform for it, so its definition and shaping go here.
      private void DescribeConverterProperties(JsonTypeInfo owner, JsonObject properties)
      {
         foreach (var property in owner.Properties)
         {
            var underlying = Nullable.GetUnderlyingType(property.PropertyType);
            var valueType = underlying ?? property.PropertyType;
            if (owner.Options.GetTypeInfo(valueType).Converter is not IJsonSchemaConverter converter || properties[property.Name] is not JsonObject described)
            {
               continue;
            }

            var schema = converter.GetSchema(Held);
            if (underlying is not null || (!valueType.IsValueType && property.IsGetNullable))
            {
               schema["type"] = new JsonArray(schema["type"]?.DeepClone(), "null");
               if (schema["enum"] is JsonArray values)
               {
                  values.Add(null);
               }
            }

            foreach (var key in SiteKeys.Where(described.ContainsKey))
            {
               schema[key] = described[key]?.DeepClone();
            }

            properties[property.Name] = ShapeProperty(property, _define is not null && IsNamed(valueType, schema) ? Defined(valueType, variant: false, schema) : schema);
         }
      }

      private JsonObject ShapeProperty(JsonPropertyInfo? property, JsonObject node)
      {
         if (_flow == Flow.Received && property?.ShouldSerialize is not null)
         {
            // The serializer omits the property when it is null or its default, so it is never null and its default is its absence.
            node.Remove("default");
            return NeverNull(node);
         }

         return node;
      }

      private static JsonObject NeverNull(JsonObject node)
      {
         StripNull(node);
         return node["anyOf"] is JsonArray { Count: 1 } single && single[0] is JsonObject only && node.Count == 1 ? (JsonObject)only.DeepClone() : node;
      }

      private JsonObject Defined(Type type, bool variant, JsonObject node)
      {
         var site = new JsonObject();
         foreach (var key in SiteKeys)
         {
            if (node.Remove(key, out var value))
            {
               site[key] = value;
            }
         }

         var nullable = StripNull(node);
         var reference = new JsonObject { ["$ref"] = "#/definitions/" + _define!(type, variant, node) };
         if (!nullable && site.Count == 0)
         {
            return reference;
         }

         // A reference keeps no sibling keywords, so a description or null goes beside it in a union of one or two.
         site["anyOf"] = nullable ? new JsonArray(reference, new JsonObject { ["type"] = "null" }) : new JsonArray(reference);
         return site;
      }

      // A lotsen type whose schema is an object, a union or an enum gets a TypeScript name. A type with a string form, such as a tour ID, stays a string.
      private static bool IsNamed(Type type, JsonObject node) =>
         (Nullable.GetUnderlyingType(type) ?? type).Namespace?.StartsWith("Lotsen", StringComparison.Ordinal) == true
         && (node.ContainsKey("anyOf") || node.ContainsKey("enum") || node["properties"] is not null || TypeOf(node) == "object");

      private static string? TypeOf(JsonObject node) => node["type"] switch
      {
         JsonValue single => (string?)single,
         JsonArray types => types.Select(each => (string?)each).FirstOrDefault(each => each != "null"),
         _ => null,
      };

      // Removes null from the values that the node allows, and returns whether it allowed null.
      private static bool StripNull(JsonObject node)
      {
         var found = false;
         if (node["type"] is JsonArray types && types.FirstOrDefault(each => (string?)each == "null") is { } nullType)
         {
            types.Remove(nullType);
            node["type"] = types.Count == 1 ? types[0]!.DeepClone() : types.DeepClone();
            found = true;
         }

         if (node["enum"] is JsonArray values && values.Any(each => each is null))
         {
            node["enum"] = new JsonArray([.. values.Where(each => each is not null).Select(each => each!.DeepClone())]);
            found = true;
         }

         if (node["anyOf"] is JsonArray branches && branches.FirstOrDefault(each => each is JsonObject { Count: 1 } branch && (string?)branch["type"] == "null") is { } nullBranch)
         {
            branches.Remove(nullBranch);
            found = true;
         }

         return found;
      }

      private static JsonObject WithoutSiteKeys(JsonObject node)
      {
         var copy = node.DeepClone().AsObject();
         foreach (var key in SiteKeys)
         {
            copy.Remove(key);
         }

         return copy;
      }
   }
}
