using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Lotsen.Git;
using Lotsen.Json;
using Lotsen.Operations;
using Lotsen.Tours;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lotsen.Tests;

/// <summary>
/// Covers the input decoder: the refusal of each kind of fault with its field and its problem, the decoders of the catalog's operations, and a mutation of each property of each operation's
/// input, which the decoder and the deserializer must both refuse.
/// </summary>
[Trait("Category", "Api")]
public sealed class InputDecoderTests
{
   private const string Valid = """{ "tourId": "aaaaaaaaaaaaaaaa", "skipped": true, "count": 3, "side": "head" }""";

   private static readonly InputDecoder Decoder = InputDecoder.For(DecoderTestJsonContext.Default.DecoderInput);

   public static TheoryData<string, string, string> Faults => new()
   {
      { "[]", "", "The input must be a JSON object." },
      { "not json", "", "The input is not valid JSON." },
      { """{ "tourId": "aaaaaaaaaaaaaaaa", "count": 3, "side": "head" }""", "skipped", "missing required property." },
      { Valid.Replace("true", "\"yes\"", StringComparison.Ordinal), "skipped", "expected a boolean." },
      { Valid.Replace("true", "null", StringComparison.Ordinal), "skipped", "the value cannot be null." },
      { Valid.Replace("3", "1.5", StringComparison.Ordinal), "count", "expected an integer." },
      { Valid.Replace("3", "\"3\"", StringComparison.Ordinal), "count", "expected an integer." },
      { Valid.Replace("\"head\"", "\"left\"", StringComparison.Ordinal), "side", "expected one of base, head." },
      { Valid.Replace("\"head\"", "\"Head\"", StringComparison.Ordinal), "side", "expected one of base, head." },
      { Valid.Replace("\"head\"", "1", StringComparison.Ordinal), "side", "expected a string." },
      { With(""" "extra": 1 """), "extra", "'extra' is not a property of this input; see the operation's input schema." },
      { With(""" "skipped": false """), "skipped", "the property appears more than once." },
      { With(""" "tags": [1] """), "tags[0]", "expected a string." },
      { With(""" "tags": [null] """), "tags[0]", "the value cannot be null." },
      { With(""" "tags": {} """), "tags", "expected an array." },
      { With(""" "anchor": "tour" """), "anchor", "expected an object." },
      { With(""" "anchor": {} """), "anchor", "the object does not name its kind." },
      { With(""" "anchor": { "kind": "nowhere" } """), "anchor.kind", "expected one of code, stopText, file, stop, tour." },
      { With(""" "anchor": { "kind": 5 } """), "anchor.kind", "expected one of code, stopText, file, stop, tour." },
      { With(""" "anchor": { "kind": "stop" } """), "anchor.stopId", "missing required property." },
      { With(""" "anchor": { "kind": "tour", "stopId": "s1" } """), "anchor.stopId", "'stopId' is not a property of this input; see the operation's input schema." },
      { With(""" "anchor": { "kind": "code", "side": "head", "path": "a.txt", "startLine": true, "endLine": 2 } """), "anchor.startLine", "expected an integer." },
      { With(""" "a.b": 1 """), "a.b", "'a.b' is not a property of this input; see the operation's input schema." },
      { With(""" "commit": "abc" """), "commit", "expected a full commit ID: 40 or 64 lowercase hexadecimal digits." },
      { With($$""" "commit": "{{new string('A', 40)}}" """), "commit", "expected a full commit ID: 40 or 64 lowercase hexadecimal digits." },
      { With(""" "url": "repo.git" """), "url", "expected an absolute URI." },
   };

   [Theory]
   [MemberData(nameof(Faults))]
   public void Fault_is_refused_with_its_field_and_problem(string body, string field, string problem)
   {
      var error = Decode(body).Error;

      Assert.NotNull(error);
      Assert.Equal(OperationErrorCode.InvalidInput, error.Code);
      Assert.Equal(field, error.Field?.ToString() ?? "");
      Assert.Equal(problem, error.Problem);
   }

   [Theory]
   [InlineData("nope")]
   [InlineData("AAAAAAAAAAAAAAAA")]
   [InlineData("aaaaaaaaaaaaaaaa\n")]
   [InlineData("")]
   public void Malformed_tour_id_is_refused_as_no_tour(string tourId)
   {
      var error = Decode(Valid.Replace("aaaaaaaaaaaaaaaa", JsonEncodedText.Encode(tourId).ToString(), StringComparison.Ordinal)).Error;

      Assert.Equal((OperationErrorCode.TourNotFound, "tourId"), (error?.Code, error?.Field?.ToString()));
   }

   /// <summary>An integer outside the range of <see cref="int"/> fits the schema, which has no range, so only the deserializer refuses it.</summary>
   [Fact]
   public void Value_that_fits_the_schema_but_not_the_type_is_refused_at_its_path()
   {
      var error = Decode(Valid.Replace("3", "3000000000", StringComparison.Ordinal)).Error;

      Assert.Equal(("count", InputDecoder.DoesNotFit), (error?.Field?.ToString(), error?.Problem));
   }

   [Fact]
   public void Valid_input_is_read_as_its_type()
   {
      var input = Assert.IsType<DecoderInput>(Decode(With(""" "note": null, "tags": ["a"], "anchor": { "kind": "stop", "stopId": "s1" } """)).Value);

      Assert.Equal(("aaaaaaaaaaaaaaaa", true, 3, FileSide.Head, null), (input.TourId.Value, input.Skipped, input.Count, input.Side, input.Note));
      Assert.Equal(["a"], input.Tags!);
      Assert.Equal("s1", Assert.IsType<AnchorInput.WholeStop>(input.Anchor).StopId);
   }

   [Fact]
   public void Full_commit_id_of_64_digits_and_absolute_uri_are_read()
   {
      var commit = new string('a', 64);

      var input = Assert.IsType<DecoderInput>(Decode(With($$""" "commit": "{{commit}}", "url": "https://example.test/repo.git" """)).Value);

      Assert.Equal((commit, new Uri("https://example.test/repo.git")), (input.Commit?.Value, input.Url));
   }

   [Fact]
   public void Schema_with_a_keyword_that_the_decoder_cannot_check_fails_to_compile()
   {
      var exception = Assert.Throws<NotSupportedException>(() => InputDecoder.Shape.Compile(JsonNode.Parse("""{ "type": "object", "properties": { "a": { "type": "string", "minLength": 1 } } }"""), "$"));

      Assert.Contains("minLength at $.a", exception.Message, StringComparison.Ordinal);
   }

   [Theory]
   [InlineData("""{ "type": "string", "pattern": "^a$" }""", "the pattern ^a$")]
   [InlineData("""{ "description": "Anything." }""", "the type that is missing")]
   [InlineData("""{ "type": "object", "additionalProperties": { "type": "string" } }""", "an additionalProperties schema")]
   [InlineData("""{ "type": "object", "anyOf": [{ "properties": { "kind": { "const": 1 } } }], "required": ["kind"] }""", "one const string")]
   public void Schema_of_a_form_that_the_decoder_cannot_check_fails_to_compile(string schema, string named)
   {
      var exception = Assert.Throws<NotSupportedException>(() => InputDecoder.Shape.Compile(JsonNode.Parse(schema), "$"));

      Assert.Contains(named, exception.Message, StringComparison.Ordinal);
   }

   public static TheoryData<string> Operations => [.. OperationCatalog.All.Select(operation => operation.Name)];

   [Theory]
   [MemberData(nameof(Operations))]
   public void Catalog_decoder_accepts_an_empty_object_exactly_when_nothing_is_required_and_refuses_an_array(string name)
   {
      var operation = OperationCatalog.Get(name);
      var required = operation.InputSchema()["required"]?.AsArray().Count ?? 0;

      Assert.Equal(required == 0, operation.Decoder.Decode("{}"u8.ToArray(), NullLogger.Instance).Value is not null);
      var array = operation.Decoder.Decode("[]"u8.ToArray(), NullLogger.Instance).Error;
      Assert.Equal((OperationErrorCode.InvalidInput, null, "The input must be a JSON object."), (array?.Code, array?.Field, array?.Problem));
   }

   /// <summary>
   /// Each sample is valid by its schema, one per branch of each union. Each mutation breaks one rule of one property: its JSON type, its presence, its null, an unknown property beside it,
   /// or a union's kind.
   /// </summary>
   [Theory]
   [MemberData(nameof(Operations))]
   public void Catalog_decoder_refuses_each_mutation_that_the_deserializer_refuses_and_reads_each_sample(string name)
   {
      var operation = OperationCatalog.Get(name);
      var schema = operation.InputSchema();
      var disagreements = new List<string>();
      var mutations = 0;

      foreach (var sample in SchemaSamples.Of(schema))
      {
         Assert.True(Decode(operation, sample).Value is not null, $"The decoder refused the sample {sample.ToJsonString()}: {Decode(operation, sample).Error?.Reason}");
         Assert.True(Deserializes(operation, sample), $"The deserializer refused the sample {sample.ToJsonString()}.");
         foreach (var (rule, mutated) in SchemaSamples.Mutations(schema, sample))
         {
            mutations++;
            // A refusal that only the deserializer made would hide a rule that the schema check misses.
            var decoderRefuses = Decode(operation, mutated).Error is { } error && error.Problem != InputDecoder.DoesNotFit;
            var deserializerRefuses = !Deserializes(operation, mutated);
            if (!decoderRefuses || !deserializerRefuses)
            {
               disagreements.Add($"{rule}: the decoder {(decoderRefuses ? "refuses" : "accepts")} and the deserializer {(deserializerRefuses ? "refuses" : "accepts")} {mutated.ToJsonString()}");
            }
         }
      }

      Assert.True(mutations > 0, $"{name} has no mutation.");
      Assert.True(disagreements.Count == 0, string.Join("\n", disagreements));
   }

   private static string With(string property) => $"{Valid[..^1].TrimEnd()}, {property.Trim()} }}";

   private static OperationResult<object> Decode(string body) => Decoder.Decode(Encoding.UTF8.GetBytes(body), NullLogger.Instance);

   private static OperationResult<object> Decode(Operation operation, JsonNode input) => operation.Decoder.Decode(Encoding.UTF8.GetBytes(input.ToJsonString()), NullLogger.Instance);

   private static bool Deserializes(Operation operation, JsonNode input)
   {
      try
      {
         return JsonSerializer.Deserialize(input.ToJsonString(), operation.InputType) is not null;
      }
      catch (Exception exception) when (exception is JsonException or NotSupportedException)
      {
         return false;
      }
   }
}

/// <summary>The input type of the decoder tests, with a property of each shape that the catalog's inputs use.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DecoderInput(
   TourId TourId, bool Skipped, int Count, FileSide Side, string? Note = null, IReadOnlyList<string>? Tags = null, AnchorInput? Anchor = null, CommitId? Commit = null, Uri? Url = null);

[JsonSourceGenerationOptions(
   JsonSerializerDefaults.Web, NumberHandling = JsonNumberHandling.Strict, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(DecoderInput))]
internal sealed partial class DecoderTestJsonContext : JsonSerializerContext;
