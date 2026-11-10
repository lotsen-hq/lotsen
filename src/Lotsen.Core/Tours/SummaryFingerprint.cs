using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Lotsen.Json;

namespace Lotsen.Tours;

/// <summary>The digest of a review summary at the tour's pin, which tells whether a synthesis predates a later summary: 64 hexadecimal digits in lowercase. JSON writes it as a string.</summary>
[JsonConverter(typeof(Json))]
internal readonly record struct SummaryFingerprint
{
   private const string Pattern = "^[0-9a-f]{64}$";

   private readonly string _value;

   private SummaryFingerprint(string value)
   {
      _value = value;
   }

   /// <summary>Returns the fingerprint of a SHA-256 digest.</summary>
   /// <exception cref="ArgumentException">The digest is not 32 bytes long.</exception>
   public static SummaryFingerprint Of(ReadOnlySpan<byte> sha256) =>
      sha256.Length == 32 ? new(Convert.ToHexStringLower(sha256)) : throw new ArgumentException("A SHA-256 digest is 32 bytes long.", nameof(sha256));

   /// <summary>Returns whether the text has the form of a fingerprint, and the fingerprint when it has.</summary>
   public static bool TryParse(string? text, out SummaryFingerprint fingerprint)
   {
      fingerprint = text is { Length: 64 } && text.All(char.IsAsciiHexDigitLower) ? new(text) : default;
      return fingerprint._value is not null;
   }

   /// <summary>Whether the value is a fingerprint, which the default value of the type is not.</summary>
   public bool IsValid => _value is not null;

   public override string ToString() => _value ?? "";

   /// <summary>Converts a fingerprint to and from a JSON string, and refuses a malformed one.</summary>
   internal sealed class Json : JsonConverter<SummaryFingerprint>, IJsonSchemaConverter
   {
      public override SummaryFingerprint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
         reader.TokenType == JsonTokenType.String && TryParse(reader.GetString(), out var fingerprint) ? fingerprint : throw new JsonException("The value is not a summary fingerprint.");

      public override void Write(Utf8JsonWriter writer, SummaryFingerprint value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());

      public JsonObject GetSchema() => new() { ["type"] = "string", ["pattern"] = Pattern };
   }
}
