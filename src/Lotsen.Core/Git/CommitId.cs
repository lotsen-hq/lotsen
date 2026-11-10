using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Lotsen.Json;

namespace Lotsen.Git;

/// <summary>
/// The full object ID of a commit: 40 lowercase hexadecimal digits for SHA-1, or 64 for SHA-256.
/// </summary>
/// <remarks>
/// Only a full ID is accepted, so a commit ID can never be read by git as a branch name, a revision expression or an option.
/// </remarks>
[JsonConverter(typeof(CommitIdJsonConverter))]
public readonly record struct CommitId
{
   /// <summary>A regular expression that matches exactly the IDs as <see cref="Value"/> holds them.</summary>
   public const string Pattern = "^([0-9a-f]{40}|[0-9a-f]{64})$";

   private CommitId(string value) => Value = value;

   /// <summary>The ID in lowercase hexadecimal.</summary>
   public string Value { get; }

   /// <summary>Returns whether the text is a full object ID, in either letter case.</summary>
   public static bool TryParse([NotNullWhen(true)] string? text, out CommitId id)
   {
      id = default;
      if (text is not { Length: 40 or 64 } || !text.All(char.IsAsciiHexDigit))
      {
         return false;
      }

      id = new CommitId(text.ToLowerInvariant());
      return true;
   }

   /// <summary>Returns the commit ID that the text holds, in either letter case.</summary>
   /// <exception cref="FormatException">The text is not a full object ID.</exception>
   public static CommitId Parse(string text) => TryParse(text, out var id) ? id : throw new FormatException($"'{text}' is not a full commit ID.");

   public override string ToString() => Value;
}

/// <summary>Converts a commit ID to and from a JSON string, and refuses one that is not a full object ID.</summary>
internal sealed class CommitIdJsonConverter : JsonConverter<CommitId>, IJsonSchemaConverter
{
   public override CommitId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
      reader.TokenType == JsonTokenType.String && CommitId.TryParse(reader.GetString(), out var id)
         ? id
         : throw new JsonException("The value is not a full commit ID.");

   public override void Write(Utf8JsonWriter writer, CommitId value, JsonSerializerOptions options) =>
      writer.WriteStringValue(value.Value ?? throw new JsonException("A default commit ID names no commit."));

   public JsonObject GetSchema() => new() { ["type"] = "string", ["pattern"] = CommitId.Pattern };
}
