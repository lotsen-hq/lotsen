using System.Text.Json.Nodes;
using Lotsen.Operations;

namespace Lotsen.Tests;

/// <summary>Reads the body of an operation's successful response, an <see cref="OperationResponseBody"/>.</summary>
internal static class SuccessBody
{
   /// <summary>Returns the operation's output from the body.</summary>
   public static JsonNode Output(string body) => JsonNode.Parse(body)![OperationWire.OutputProperty]!.DeepClone();

   /// <summary>Returns the output when the response succeeded, or else the body, which is then the refusal.</summary>
   public static JsonNode OutputOrError(HttpResponseMessage response, string body) => response.IsSuccessStatusCode ? Output(body) : JsonNode.Parse(body)!;
}
