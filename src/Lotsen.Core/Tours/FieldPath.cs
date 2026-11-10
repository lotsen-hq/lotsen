using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Lotsen.Json;

namespace Lotsen.Tours;

/// <summary>
/// The path of a field in a tour or in an operation's input, such as <c>stops[2].kind</c> or <c>checks[ab2c].question</c>. The root path names the value as a whole. JSON writes the rendered
/// path as a string.
/// </summary>
/// <remarks>
/// A domain path and an input path share one grammar, so a rule that checks a tour value names the input field when the input has the value's shape: a property follows a dot, and an index or
/// a key sits in brackets. A path relative to a list, as <see cref="After"/> returns it, starts with its index.
/// </remarks>
[JsonConverter(typeof(Json))]
internal readonly record struct FieldPath
{
   private readonly string? _text;

   private FieldPath(string text)
   {
      _text = text.Length == 0 ? null : text;
   }

   /// <summary>The path of the value as a whole.</summary>
   public static FieldPath Root => default;

   /// <summary>Whether the path names the value as a whole.</summary>
   public bool IsRoot => _text is null;

   /// <summary>Returns the path of one property of the root.</summary>
   /// <exception cref="ArgumentException">The text is empty, or holds a dot or a bracket, so it names more than one property.</exception>
   public static implicit operator FieldPath(string property) => Root.Then(property);

   /// <summary>Returns the path of a property of the field.</summary>
   /// <exception cref="ArgumentException">The property is empty, or holds a dot or a bracket.</exception>
   public FieldPath Then(string property)
   {
      if (property.Length == 0 || property.AsSpan().IndexOfAny(".[]") >= 0)
      {
         throw new ArgumentException($"'{property}' is not the name of one property.", nameof(property));
      }

      return new(_text is null ? property : $"{_text}.{property}");
   }

   /// <summary>Returns the path of an element of the list that the field holds.</summary>
   public FieldPath At(int index) => new($"{_text}[{index}]");

   /// <summary>Returns the path of the element with this ID in the list that the field holds, as a stop text anchor names a check's prose by the check's ID.</summary>
   public FieldPath Key(string id) => new($"{_text}[{id}]");

   /// <summary>Returns the rest of the path below the prefix.</summary>
   /// <returns>The rest, which is the root when the path is the prefix, or null when the path is not under the prefix.</returns>
   public FieldPath? After(FieldPath prefix)
   {
      if (prefix._text is not { } start)
      {
         return this;
      }

      if (_text is not { } text || !text.StartsWith(start, StringComparison.Ordinal))
      {
         return null;
      }

      if (text.Length == start.Length)
      {
         return Root;
      }

      return text[start.Length] switch
      {
         '.' => new FieldPath(text[(start.Length + 1)..]),
         '[' => new FieldPath(text[start.Length..]),
         _ => (FieldPath?)null,
      };
   }

   /// <summary>Returns the path below the prefix, so a path relative to a value becomes the path in the value that holds it.</summary>
   public FieldPath Under(FieldPath prefix) => (prefix._text, _text) switch
   {
      (null, _) => this,
      (_, null) => prefix,
      (var start, var rest) when rest[0] == '[' => new($"{start}{rest}"),
      (var start, var rest) => new($"{start}.{rest}"),
   };

   public override string ToString() => _text ?? "";

   /// <summary>Returns the path that this text renders, as a JSON reader's path or a stored error names it, without checking its grammar.</summary>
   internal static FieldPath Parse(string text) => new(text);

   /// <summary>Returns the path that a <see cref="JsonException.Path"/> names, such as <c>$.stops[2].kind</c>, without its <c>$</c> root.</summary>
   /// <param name="path">The JSON path, or null when the exception names none, which is the root.</param>
   internal static FieldPath FromJsonPath(string? path) => path switch
   {
      null or "$" => Root,
      ['$', '.', .. var rest] => new(rest),
      ['$', .. var rest] => new(rest),
      _ => new(path),
   };

   /// <summary>Converts a field path to and from its rendered string.</summary>
   internal sealed class Json : JsonConverter<FieldPath>, IJsonSchemaConverter
   {
      public override FieldPath Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
         reader.TokenType == JsonTokenType.String ? Parse(reader.GetString()!) : throw new JsonException("A field path is a string.");

      public override void Write(Utf8JsonWriter writer, FieldPath value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());

      public JsonObject GetSchema() => new() { ["type"] = "string" };
   }
}

/// <summary>A rule that a tour value breaks: where, and what is wrong.</summary>
/// <param name="Path">The broken field, relative to the value that the rule checked.</param>
/// <param name="Problem">What is wrong, written for Claude and the reviewer, as an error's reason says it after the field.</param>
internal sealed record RuleFault(FieldPath Path, string Problem)
{
   /// <summary>Returns the fault with its path below the prefix, as the value that holds the checked one names the field.</summary>
   public RuleFault Under(FieldPath prefix) => this with { Path = Path.Under(prefix) };
}
