using System.Text;
using System.Text.Json;
using Lotsen.Json;
using Lotsen.Operations;

namespace Lotsen.Tests;

/// <summary>An operation that a test calls, with the ID properties of its input that the test gives apart from the JSON it writes for the rest.</summary>
/// <param name="Name">The operation's name in the catalog.</param>
/// <param name="Ids">The ID properties, which <see cref="InputOf"/> writes before the rest of the input.</param>
internal sealed record OperationCall(string Name, IReadOnlyList<(string Name, string Value)> Ids)
{
   /// <summary>The catalog's operation of this name.</summary>
   /// <exception cref="KeyNotFoundException">The catalog has no operation of the name.</exception>
   public Operation Operation => OperationCatalog.Get(Name);

   public static implicit operator OperationCall(string name) => new(name, []);

   /// <summary>Returns a call of an operation on one tour, with each other ID that its input names when given.</summary>
   public static OperationCall On(
      string name, string tourId, string? stopId = null, string? checkId = null, string? threadId = null, string? questionId = null, string? batchId = null) =>
      new(name, [.. new[] { ("tourId", tourId), ("stopId", stopId), ("checkId", checkId), ("threadId", threadId), ("questionId", questionId), ("batchId", batchId) }
         .Where(id => id.Item2 is not null).Select(id => (id.Item1, id.Item2!))]);

   /// <summary>Returns the UTF-8 input of the call: the IDs, then the properties of <paramref name="json"/> as the test wrote them, duplicates and order included.</summary>
   /// <param name="json">A JSON object, or null for an input of the IDs alone. Without IDs, any text, which the request carries as it is.</param>
   /// <exception cref="ArgumentException">The call has IDs, and <paramref name="json"/> is not an object.</exception>
   public byte[] InputOf(string? json)
   {
      if (Ids.Count == 0)
      {
         return Encoding.UTF8.GetBytes(json ?? "{}");
      }

      var rest = json?.Trim() ?? "{}";
      if (rest is not ['{', .., '}'])
      {
         throw new ArgumentException($"A call with IDs takes a JSON object, not {rest}.", nameof(json));
      }

      var ids = string.Join(", ", Ids.Select(id => $"{JsonSerializer.Serialize(id.Name, LotsenJsonContext.Default.String)}: {JsonSerializer.Serialize(id.Value, LotsenJsonContext.Default.String)}"));
      var properties = rest[1..^1].Trim();
      return Encoding.UTF8.GetBytes(properties.Length == 0 ? $"{{ {ids} }}" : $"{{ {ids}, {properties} }}");
   }

   /// <summary>Returns the request of the call, built by <see cref="OperationWire.Request(Operation, ReadOnlyMemory{byte})"/>.</summary>
   public HttpRequestMessage Request(string? json) => OperationWire.Request(Operation, InputOf(json));
}

/// <summary>An operation call with the credentials of the caller that sends it.</summary>
/// <param name="SessionCredential">The agent session's credential, or null for the UI and the CLI.</param>
internal sealed record PreparedCall(OperationCall Call, string Credential, string? SessionCredential = null)
{
   /// <summary>Whether the request asks for prose as HTML, as the UI does with <see cref="Server.OperationEndpoints.ProseHeader"/>.</summary>
   public bool ProseAsHtml { get; init; }

   /// <summary>Returns the call with a request that asks for prose as HTML.</summary>
   public PreparedCall WithProseHtml() => this with { ProseAsHtml = true };

   /// <summary>Returns the request of the call with the rest of its input, as <see cref="OperationCall.InputOf"/> takes it.</summary>
   public HttpRequestMessage Request(string? json = null)
   {
      var request = ServerHarness.Request(Call, Credential, SessionCredential, json);
      if (ProseAsHtml)
      {
         request.Headers.Add(Server.OperationEndpoints.ProseHeader, "html");
      }

      return request;
   }
}
