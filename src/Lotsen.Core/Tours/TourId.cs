using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Lotsen.Json;

namespace Lotsen.Tours;

/// <summary>The ID of a tour: 16 random characters from the lowercase base32 alphabet (a to z and 2 to 7), 80 bits in all.</summary>
/// <remarks>
/// The ID names the tour's file and appears in URLs, so the alphabet has no path separators, no dots and no letters that differ only in case on a case-insensitive file system.
/// Every instance holds a valid ID.
/// </remarks>
[JsonConverter(typeof(TourIdJsonConverter))]
internal sealed record TourId
{
   private const int Length = 16;
   private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";
   private static readonly SearchValues<char> AlphabetValues = SearchValues.Create(Alphabet);

   /// <summary>A regular expression that matches exactly the well-formed IDs.</summary>
   public const string Pattern = "^[a-z2-7]{16}$";

   private TourId(string value) => Value = value;

   /// <summary>The ID as text.</summary>
   public string Value { get; }

   /// <summary>Returns a new random ID.</summary>
   public static TourId New() => new(RandomNumberGenerator.GetString(Alphabet, Length));

   /// <summary>Returns whether the text is a well-formed tour ID.</summary>
   /// <param name="text">The text to parse, such as a value of an operation's input.</param>
   /// <param name="id">The parsed ID, or null when the text is not a tour ID.</param>
   public static bool TryParse([NotNullWhen(true)] string? text, [NotNullWhen(true)] out TourId? id)
   {
      if (text is { Length: Length } && !text.AsSpan().ContainsAnyExcept(AlphabetValues))
      {
         id = new TourId(text);
         return true;
      }

      id = null;
      return false;
   }

   public override string ToString() => Value;
}

/// <summary>Converts a tour ID to and from a JSON string, and refuses a malformed ID.</summary>
internal sealed class TourIdJsonConverter : JsonConverter<TourId>, IJsonSchemaConverter
{
   // The schema of a tour ID has no null, so Read refuses a JSON null as the input decoder does.
   public override bool HandleNull => true;

   public override TourId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
      reader.TokenType == JsonTokenType.String && TourId.TryParse(reader.GetString(), out var id)
         ? id
         : throw new JsonException("The value is not a tour ID.");

   public override void Write(Utf8JsonWriter writer, TourId value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);

   public JsonObject GetSchema() => new() { ["type"] = "string", ["pattern"] = TourId.Pattern };
}
