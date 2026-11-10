using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Lotsen.Json;
using Lotsen.Tours;

namespace Lotsen.Operations;

/// <summary>
/// How every operation travels over HTTP: a <c>POST</c> to <see cref="RouteTemplate"/> with the operation's name, whose JSON body is the operation's whole input, the object of arguments that an
/// agent tool passes. The response is an <see cref="OperationResponseBody"/> that holds the output, or an <see cref="OperationError"/> with the status of its code's <see cref="RefusalKind"/>.
/// </summary>
/// <remarks>
/// The endpoint, <see cref="Client.ServerClient"/> and the test harness build and read requests here. The UI builds the same path in <c>operationPath</c> and reads the same body in <c>call</c>, both
/// in <c>ui/src/lib/api.ts</c>.
/// </remarks>
internal static class OperationWire
{
   /// <summary>The property of a response body that holds the operation's output.</summary>
   public const string OutputProperty = "output";

   /// <summary>Returns the output's JSON from a successful response's body.</summary>
   /// <exception cref="JsonException">The body is no JSON object with an output.</exception>
   public static JsonElement OutputOf(string body)
   {
      using var document = JsonDocument.Parse(body);
      return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(OutputProperty, out var output)
         ? output.Clone()
         : throw new JsonException($"The response body has no {OutputProperty}.");
   }

   /// <summary>The route of every operation, with the operation's name as its one route value.</summary>
   public const string RouteTemplate = "/api/ops/{" + NameParameter + "}";

   /// <summary>The route value of <see cref="RouteTemplate"/> that names the operation.</summary>
   public const string NameParameter = "name";

   private const string PathPrefix = "/api/ops/";

   /// <summary>Returns the path of the operation's requests, such as <c>/api/ops/read_state</c>.</summary>
   public static string PathOf(Operation operation) => PathPrefix + operation.Name;

   /// <summary>Returns the request that calls the operation with this input.</summary>
   /// <param name="operation">The operation to call.</param>
   /// <param name="input">The UTF-8 JSON of the input object, sent as it is, so the server alone checks it.</param>
   public static HttpRequestMessage Request(Operation operation, ReadOnlyMemory<byte> input)
   {
      var content = new ReadOnlyMemoryContent(input);
      content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
      return new HttpRequestMessage(HttpMethod.Post, PathOf(operation)) { Content = content };
   }

   /// <summary>Returns the request that calls the operation with this typed input.</summary>
   public static HttpRequestMessage Request<TInput, TOutput>(Operation<TInput, TOutput> operation, TInput input)
      where TInput : class
      where TOutput : class => Request(operation, JsonSerializer.SerializeToUtf8Bytes(input, operation.TypedInput));

   /// <summary>Returns the HTTP status of a refusal of this kind.</summary>
   public static HttpStatusCode StatusOf(RefusalKind kind) => kind switch
   {
      RefusalKind.BadInput => HttpStatusCode.BadRequest,
      RefusalKind.NotFound => HttpStatusCode.NotFound,
      RefusalKind.CallerRefused => HttpStatusCode.Forbidden,
      RefusalKind.Conflict => HttpStatusCode.Conflict,
      RefusalKind.Upstream => HttpStatusCode.BadGateway,
      RefusalKind.Broken => HttpStatusCode.InternalServerError,
      RefusalKind.Unavailable => HttpStatusCode.ServiceUnavailable,
      RefusalKind.Unauthenticated => HttpStatusCode.Unauthorized,
   };

   /// <summary>Answers the request with the refusal as the body, with the status of its code's kind.</summary>
   public static Task WriteRefusalAsync(HttpContext context, OperationError error)
   {
      context.Response.StatusCode = (int)StatusOf(error.Code.Kind);
      return context.Response.WriteAsJsonAsync(error, LotsenJsonContext.Default.OperationError, cancellationToken: context.RequestAborted);
   }
}

/// <summary>The body of an operation's successful response.</summary>
/// <param name="Output">The output, with its prose as HTML when the request asked for it.</param>
/// <param name="Revision">The revision of the tour that the output shows, or null for an operation that showed no tour. JSON leaves it out when null.</param>
/// <param name="Controls">
/// The tour's controls for the UI after the call, at their own revision, or null for any other caller, for an operation that names no tour and for a file read, as
/// <see cref="Operation.CarriesControls"/> says. JSON leaves it out when null.
/// </param>
internal sealed record OperationResponseBody(
   JsonNode Output,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Revision,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TourControls? Controls);
