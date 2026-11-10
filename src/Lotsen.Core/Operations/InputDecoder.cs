using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Lotsen.Git;
using Lotsen.Json;
using Lotsen.Tours;

namespace Lotsen.Operations;

/// <summary>
/// Reads an operation's input from the JSON object that a request sends, after it checks the object against the input schema that <see cref="OperationSchemas.Export(JsonTypeInfo)"/> gives the agent.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="For"/> compiles the exported schema into a tree of <see cref="Shape"/>s, and throws for a schema keyword that the tree cannot check. An operation builds its decoder when it is
/// defined, so such a schema stops the catalog from loading.
/// </para>
/// <para>
/// <see cref="Decode"/> refuses the first fault it finds, with its field and a problem text of the decoder's own. Only an object that passes reaches the deserializer.
/// </para>
/// </remarks>
internal sealed class InputDecoder
{
   /// <summary>The problem of a value that passed the schema but that the deserializer still refused, such as an integer outside the range of its .NET type.</summary>
   public const string DoesNotFit = "the value does not fit the input schema.";

   private readonly JsonTypeInfo _inputType;
   private readonly Shape.ObjectShape _shape;

   private InputDecoder(JsonTypeInfo inputType, Shape.ObjectShape shape)
   {
      _inputType = inputType;
      _shape = shape;
   }

   /// <summary>Returns the decoder of an input type, compiled from the type's exported schema.</summary>
   /// <exception cref="NotSupportedException">The schema holds a keyword or a form that the decoder cannot check, or its root is not an object.</exception>
   public static InputDecoder For(JsonTypeInfo inputType) =>
      new(inputType, Shape.Compile(OperationSchemas.Export(inputType), "$") as Shape.ObjectShape ?? throw new NotSupportedException($"The input schema of {inputType.Type.Name} is not an object."));

   /// <summary>Returns the input that a request's body holds.</summary>
   /// <param name="body">The request's body, which must hold one JSON object.</param>
   /// <param name="logger">Receives the deserializer's refusal of a body that passed the schema, which the schema then does not describe exactly.</param>
   /// <returns>A value of the input type, or the refusal of the first fault, with its field.</returns>
   public OperationResult<object> Decode(ReadOnlyMemory<byte> body, ILogger logger)
   {
      JsonDocument document;
      try
      {
         document = JsonDocument.Parse(body);
      }
      catch (JsonException)
      {
         return OperationError.Invalid(FieldPath.Root, "The input is not valid JSON.");
      }

      using (document)
      {
         if (document.RootElement.ValueKind != JsonValueKind.Object)
         {
            return OperationError.Invalid(FieldPath.Root, "The input must be a JSON object.");
         }

         if (_shape.Check(document.RootElement, FieldPath.Root) is { } fault)
         {
            return fault;
         }
      }

      try
      {
         return OperationResult<object>.Of(JsonSerializer.Deserialize(body.Span, _inputType)!);
      }
      catch (JsonException exception)
      {
         // A caller's out-of-range integer reaches here too, so the refusal is the caller's and the log is a trace, not a warning.
         logger.LogDebug("An input of {Type} passed its schema but did not deserialize at {Path}.", _inputType.Type.Name, exception.Path);
         return OperationError.Invalid(FieldPath.FromJsonPath(exception.Path), DoesNotFit);
      }
   }

   /// <summary>One node of a compiled input schema: what one JSON value must be.</summary>
   internal abstract record Shape
   {
      private static readonly HashSet<string> Annotations = ["description", "default"];

      private Shape()
      {
      }

      /// <summary>Returns the refusal of the value's first fault, or null when the value fits the shape.</summary>
      /// <param name="value">The value to check.</param>
      /// <param name="path">Where the value sits in the input.</param>
      public abstract OperationError? Check(JsonElement value, FieldPath path);

      /// <summary>Returns the shape of a schema node.</summary>
      /// <param name="schema">The schema node.</param>
      /// <param name="where">The node's place in the schema, which a refusal to compile names.</param>
      /// <exception cref="NotSupportedException">The node holds a keyword or a form that no shape checks.</exception>
      public static Shape Compile(JsonNode? schema, string where)
      {
         if (schema is not JsonObject node)
         {
            throw Unsupported(where, $"the schema {schema?.ToJsonString() ?? "null"}");
         }

         foreach (var (key, _) in node)
         {
            if (!Annotations.Contains(key) && key is not ("type" or "properties" or "required" or "additionalProperties" or "items" or "enum" or "pattern" or "format" or "anyOf"))
            {
               throw Unsupported(where, $"the keyword {key}");
            }
         }

         var (type, nullable) = TypeOf(node, where);
         Shape shape = node.ContainsKey("anyOf") ? UnionShape.Compile(node, type, where) : type switch
         {
            "object" => ObjectShape.Compile(node, where),
            "array" => new ArrayShape(Compile(node["items"], $"{where}[]")),
            "string" => StringShape.Compile(node, where),
            "integer" => new IntegerShape(),
            "number" => new NumberShape(),
            "boolean" => new BooleanShape(),
            _ => throw Unsupported(where, $"the type {type}"),
         };
         if (type != "string" && (node.ContainsKey("enum") || node.ContainsKey("pattern") || node.ContainsKey("format")))
         {
            throw Unsupported(where, $"enum, pattern or format on the type {type}");
         }

         return nullable ? new NullableShape(shape) : shape;
      }

      private static (string Type, bool Nullable) TypeOf(JsonObject node, string where) => node["type"] switch
      {
         JsonValue value when value.TryGetValue<string>(out var type) => (type, false),
         JsonArray { Count: 2 } pair when pair[0] is JsonValue first && first.TryGetValue<string>(out var type) && (string?)pair[1] == "null" => (type, true),
         var other => throw Unsupported(where, $"the type {other?.ToJsonString() ?? "that is missing"}"),
      };

      private static NotSupportedException Unsupported(string where, string what) => new($"The input decoder cannot check {what} at {where}.");

      private static OperationError Invalid(FieldPath path, string problem) => OperationError.Invalid(path, problem);

      // A property name of a request need not fit the field path grammar, so the path names it as it is written.
      private static FieldPath Child(FieldPath path, string name) => FieldPath.Parse(path.IsRoot ? name : $"{path}.{name}");

      /// <summary>A value that is null, or else fits <paramref name="Inner"/>.</summary>
      internal sealed record NullableShape(Shape Inner) : Shape
      {
         public override OperationError? Check(JsonElement value, FieldPath path) => value.ValueKind == JsonValueKind.Null ? null : Inner.Check(value, path);
      }

      /// <summary>A JSON object with these properties, in the schema's order.</summary>
      /// <param name="Properties">The declared properties and their shapes, in the schema's order.</param>
      /// <param name="Required">The properties that the object must hold.</param>
      /// <param name="Closed">Whether a property that the schema does not declare is refused.</param>
      internal sealed record ObjectShape(IReadOnlyList<(string Name, Shape Shape)> Properties, IReadOnlySet<string> Required, bool Closed) : Shape
      {
         public static ObjectShape Compile(JsonObject node, string where)
         {
            var properties = node["properties"] is JsonObject declared
               ? [.. declared.Select(property => (property.Key, Shape.Compile(property.Value, $"{where}.{property.Key}")))]
               : new List<(string, Shape)>();
            var required = node["required"] is JsonArray names ? [.. names.Select(name => (string)name!)] : new HashSet<string>(StringComparer.Ordinal);
            var closed = node["additionalProperties"] switch
            {
               null => false,
               JsonValue value when value.TryGetValue<bool>(out var allowed) => !allowed,
               _ => throw Unsupported(where, "an additionalProperties schema"),
            };
            return new ObjectShape(properties, required, closed);
         }

         public override OperationError? Check(JsonElement value, FieldPath path)
         {
            if (value.ValueKind == JsonValueKind.Null)
            {
               return Invalid(path, "the value cannot be null.");
            }

            if (value.ValueKind != JsonValueKind.Object)
            {
               return Invalid(path, "expected an object.");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
               if (!seen.Add(property.Name))
               {
                  return Invalid(Child(path, property.Name), "the property appears more than once.");
               }
            }

            foreach (var (name, shape) in Properties)
            {
               if (value.TryGetProperty(name, out var member))
               {
                  if (shape.Check(member, path.Then(name)) is { } fault)
                  {
                     return fault;
                  }
               }
               else if (Required.Contains(name))
               {
                  return Invalid(path.Then(name), "missing required property.");
               }
            }

            if (Closed && seen.FirstOrDefault(name => !Properties.Any(property => property.Name == name)) is { } unknown)
            {
               return Invalid(Child(path, unknown), $"'{unknown}' is not a property of this input; see the operation's input schema.");
            }

            return null;
         }
      }

      /// <summary>A JSON object whose <paramref name="Discriminator"/> property names its kind, which picks the object shape of its branch.</summary>
      internal sealed record UnionShape(string Discriminator, IReadOnlyList<(string Kind, ObjectShape Branch)> Branches) : Shape
      {
         public static UnionShape Compile(JsonObject node, string type, string where)
         {
            if (type != "object" || node["anyOf"] is not JsonArray branches || node["required"] is not JsonArray { Count: 1 } required || node.ContainsKey("properties"))
            {
               throw Unsupported(where, "an anyOf that is not a union of objects with one required kind");
            }

            var discriminator = (string)required[0]!;
            var compiled = new List<(string, ObjectShape)>();
            for (var i = 0; i < branches.Count; i++)
            {
               var branchWhere = $"{where}|{i}";
               if (branches[i] is not JsonObject branch || branch["properties"]?[discriminator] is not JsonObject kindSchema || kindSchema["const"] is not JsonValue constant
                  || !constant.TryGetValue<string>(out var kind) || kindSchema.Count != 1)
               {
                  throw Unsupported(branchWhere, $"a branch whose {discriminator} is not one const string");
               }

               // The branch checks its kind as a string with one allowed value, so the closed branch admits the discriminator.
               var withKind = (JsonObject)branch.DeepClone();
               withKind["type"] = "object";
               withKind["properties"]![discriminator] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(kind) };
               compiled.Add((kind, ObjectShape.Compile(withKind, branchWhere)));
            }

            return new UnionShape(discriminator, compiled);
         }

         public override OperationError? Check(JsonElement value, FieldPath path)
         {
            if (value.ValueKind == JsonValueKind.Null)
            {
               return Invalid(path, "the value cannot be null.");
            }

            if (value.ValueKind != JsonValueKind.Object)
            {
               return Invalid(path, "expected an object.");
            }

            if (!value.TryGetProperty(Discriminator, out var kind))
            {
               return Invalid(path, "the object does not name its kind.");
            }

            var branch = kind.ValueKind == JsonValueKind.String ? Branches.FirstOrDefault(branch => branch.Kind == kind.GetString()).Branch : null;
            return branch is null ? Invalid(path.Then(Discriminator), $"expected one of {string.Join(", ", Branches.Select(each => each.Kind))}.") : branch.Check(value, path);
         }
      }

      /// <summary>A JSON array whose every element fits <paramref name="Items"/>.</summary>
      internal sealed record ArrayShape(Shape Items) : Shape
      {
         public override OperationError? Check(JsonElement value, FieldPath path)
         {
            if (value.ValueKind != JsonValueKind.Array)
            {
               return Invalid(path, value.ValueKind == JsonValueKind.Null ? "the value cannot be null." : "expected an array.");
            }

            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
               if (Items.Check(item, path.At(index++)) is { } fault)
               {
                  return fault;
               }
            }

            return null;
         }
      }

      /// <summary>A JSON string, of one of <paramref name="Values"/> when given, in the form that <paramref name="Form"/> names.</summary>
      /// <param name="Values">The allowed values, or null for any string.</param>
      /// <param name="Form">The form that the schema's pattern or format gives the string.</param>
      internal sealed record StringShape(IReadOnlyList<string>? Values, StringForm Form) : Shape
      {
         public static StringShape Compile(JsonObject node, string where)
         {
            var values = node["enum"] is JsonArray allowed ? allowed.Where(value => value is not null).Select(value => (string)value!).ToList() : null;
            var form = ((string?)node["pattern"], (string?)node["format"]) switch
            {
               (null, null) => StringForm.Any,
               (TourId.Pattern, null) => StringForm.TourId,
               (CommitId.Pattern, null) => StringForm.CommitId,
               (null, "uri") => StringForm.AbsoluteUri,
               (string pattern, _) => throw Unsupported(where, $"the pattern {pattern}"),
               (_, var format) => throw Unsupported(where, $"the format {format}"),
            };
            return new StringShape(values, form);
         }

         public override OperationError? Check(JsonElement value, FieldPath path)
         {
            if (value.ValueKind != JsonValueKind.String)
            {
               return Invalid(path, value.ValueKind == JsonValueKind.Null ? "the value cannot be null." : "expected a string.");
            }

            var text = value.GetString()!;
            if (Values is not null && !Values.Contains(text, StringComparer.Ordinal))
            {
               return Invalid(path, $"expected one of {string.Join(", ", Values)}.");
            }

            return Form switch
            {
               // A malformed tour ID names no tour, as an unknown one does.
               StringForm.TourId when !TourId.TryParse(text, out _) => OperationError.TourNotFound(text),
               StringForm.CommitId when !(CommitId.TryParse(text, out var commit) && commit.Value == text) => Invalid(path, "expected a full commit ID: 40 or 64 lowercase hexadecimal digits."),
               StringForm.AbsoluteUri when !Uri.TryCreate(text, UriKind.Absolute, out _) => Invalid(path, "expected an absolute URI."),
               _ => null,
            };
         }
      }

      /// <summary>The form of a string that a schema's pattern or format names.</summary>
      internal enum StringForm
      {
         /// <summary>Any string.</summary>
         Any,

         /// <summary>A tour ID, by <see cref="Tours.TourId.Pattern"/>.</summary>
         TourId,

         /// <summary>A full commit ID in lowercase, by <see cref="Git.CommitId.Pattern"/>.</summary>
         CommitId,

         /// <summary>An absolute URI, by the format <c>uri</c>.</summary>
         AbsoluteUri,
      }

      /// <summary>A JSON number without a fraction or an exponent that fits in 64 bits.</summary>
      internal sealed record IntegerShape : Shape
      {
         public override OperationError? Check(JsonElement value, FieldPath path) => value.ValueKind switch
         {
            JsonValueKind.Number when value.TryGetInt64(out _) => null,
            JsonValueKind.Null => Invalid(path, "the value cannot be null."),
            _ => Invalid(path, "expected an integer."),
         };
      }

      /// <summary>A JSON number.</summary>
      internal sealed record NumberShape : Shape
      {
         public override OperationError? Check(JsonElement value, FieldPath path) => value.ValueKind switch
         {
            JsonValueKind.Number => null,
            JsonValueKind.Null => Invalid(path, "the value cannot be null."),
            _ => Invalid(path, "expected a number."),
         };
      }

      /// <summary>JSON true or false.</summary>
      internal sealed record BooleanShape : Shape
      {
         public override OperationError? Check(JsonElement value, FieldPath path) => value.ValueKind switch
         {
            JsonValueKind.True or JsonValueKind.False => null,
            JsonValueKind.Null => Invalid(path, "the value cannot be null."),
            _ => Invalid(path, "expected a boolean."),
         };
      }
   }
}
