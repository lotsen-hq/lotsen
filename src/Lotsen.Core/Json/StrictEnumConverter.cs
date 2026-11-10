using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Lotsen.Json;

/// <summary>
/// Converts an enum to and from its member name in camelCase, and refuses any other value.
/// </summary>
/// <remarks>
/// A name must match exactly, so other letter case, whitespace, comma-separated lists, integers and null fail deserialization. A value outside the closed set therefore never loads.
/// </remarks>
internal sealed class StrictEnumConverter<TEnum> : JsonConverter<TEnum>, IJsonSchemaConverter
   where TEnum : struct, Enum
{
   private static readonly FrozenDictionary<TEnum, string> NamesByValue = Enum.GetValues<TEnum>().ToFrozenDictionary(
      value => value,
      value => JsonNamingPolicy.CamelCase.ConvertName(Enum.GetName(value)!));

   private static readonly FrozenDictionary<string, TEnum> ValuesByName = NamesByValue.ToFrozenDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

   private static readonly string[] Names = [.. Enum.GetValues<TEnum>().Select(value => NamesByValue[value])];

   public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
      reader.TokenType == JsonTokenType.String && ValuesByName.TryGetValue(reader.GetString()!, out var value)
         ? value
         : throw new JsonException($"Expected one of {string.Join(", ", Names)}.");

   public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
      writer.WriteStringValue(NamesByValue.TryGetValue(value, out var name)
         ? name
         : throw new JsonException($"The value {value} is not a defined {typeof(TEnum).Name}."));

   public JsonObject GetSchema() => new()
   {
      ["type"] = "string",
      ["enum"] = new JsonArray([.. Names.Select(name => JsonValue.Create(name))]),
   };
}
