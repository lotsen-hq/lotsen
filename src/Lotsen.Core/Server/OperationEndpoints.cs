using System.Text.Json;
using System.Text.Json.Nodes;
using Lotsen.Json;
using Lotsen.Operations;
using Lotsen.Prose;
using Lotsen.Sessions;
using Lotsen.Tours;

namespace Lotsen.Server;

/// <summary>One operation in the <c>GET /api/operations</c> listing. Its requests go to <see cref="OperationWire.PathOf"/>.</summary>
/// <param name="Name">The operation's tool name.</param>
/// <param name="Description">What the operation does, for an agent.</param>
/// <param name="InputSchema">The JSON Schema of the whole input.</param>
/// <param name="OutputSchema">The JSON Schema of the output.</param>
internal sealed record OperationListing(string Name, string Description, JsonNode InputSchema, JsonNode OutputSchema);

/// <summary>One event kind in the <c>GET /api/operations</c> listing.</summary>
/// <param name="Name">The event's type name on the event stream.</param>
/// <param name="PayloadSchema">The JSON Schema of the payload as the UI receives it, with the HTML of its prose.</param>
internal sealed record EventListing(string Name, JsonNode PayloadSchema);

/// <summary>The response of <c>GET /api/operations</c>: every operation, and every event kind of the event stream.</summary>
internal sealed record OperationCatalogResponse(IReadOnlyList<OperationListing> Operations, IReadOnlyList<EventListing> Events);

/// <summary>
/// Maps every operation in <see cref="OperationCatalog"/> onto <see cref="OperationWire.RouteTemplate"/>, with no logic beyond the admission of the caller, the input's decoder and the HTML that
/// <see cref="ProseHeader"/> asks for.
/// </summary>
/// <remarks>
/// <para>
/// Each request runs the operation's <see cref="Operation.Admit"/> first, so a caller that the operation's access declaration does not admit gets 403 even for malformed input or an unknown tour.
/// </para>
/// <para>
/// A refused operation answers with its <see cref="OperationError"/> as the body, with the status that <see cref="OperationWire.StatusOf"/> gives the kind of its code. An operation that
/// succeeds answers with an <see cref="OperationResponseBody"/>.
/// </para>
/// </remarks>
internal static class OperationEndpoints
{
   /// <summary>
   /// The request header with which the UI asks for prose as HTML. With the value <c>html</c>, an output is written as its operation's <see cref="Operation.Prose"/> renders it.
   /// </summary>
   /// <remarks>The adapter never sends it, so Claude reads only the Markdown it wrote.</remarks>
   public const string ProseHeader = "Lotsen-Prose";

   public static void MapOperations(this IEndpointRouteBuilder endpoints)
   {
      endpoints.MapPost(OperationWire.RouteTemplate, InvokeAsync);
      endpoints.MapGet("/api/operations", ListOperations);
   }

   private static IResult ListOperations() => TypedResults.Json(
      new OperationCatalogResponse(
         [.. OperationCatalog.All.Select(operation => new OperationListing(operation.Name, operation.Description, operation.InputSchema(), operation.OutputSchema()))],
         [.. ServerEvents.All.Select(kind => new EventListing(kind.Name, kind.PayloadSchema()))]),
      LotsenJsonContext.Default.OperationCatalogResponse);

   private static async Task InvokeAsync(HttpContext context)
   {
      var name = context.Request.RouteValues[OperationWire.NameParameter] as string ?? "";
      if (!OperationCatalog.ByName.TryGetValue(name, out var operation))
      {
         await OperationWire.WriteRefusalAsync(context, OperationError.OperationNotFound(name));
         return;
      }

      // Admission comes before the input is read, so a caller that the operation does not admit learns nothing of the input or the tour.
      var admitted = operation.Admit(context.GetCaller());
      if (admitted.Value is not { } call)
      {
         await OperationWire.WriteRefusalAsync(context, admitted.Error!);
         return;
      }

      var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(OperationEndpoints).FullName!);
      var input = operation.Decoder.Decode(await ReadBodyAsync(context), logger);
      if (input.Value is not { } value)
      {
         await OperationWire.WriteRefusalAsync(context, input.Error!);
         return;
      }

      var (output, error) = await call.InvokeAsync(context.RequestServices, value, context.RequestAborted);
      if (error is not null)
      {
         await OperationWire.WriteRefusalAsync(context, error);
         return;
      }

      var written = context.Request.Headers[ProseHeader] == "html" && operation.Prose.Render(output!) is { } html
         ? html
         : JsonSerializer.SerializeToNode(output, operation.OutputType)!;
      var body = new OperationResponseBody(written, call.Revision ?? (output as Tour)?.Revision, ControlsFor(context, operation, value));
      await context.Response.WriteAsJsonAsync(body, LotsenJsonContext.Default.OperationResponseBody, cancellationToken: context.RequestAborted);
   }

   // The UI gets the controls of the tour that the input names, read after the call, where Operation.CarriesControls holds.
   private static TourControls? ControlsFor(HttpContext context, Operation operation, object input) =>
      context.GetCaller() is Caller.Ui && operation.CarriesControls && operation.NamesTour && TourIdOf(operation, input) is { } tourId
         ? context.RequestServices.GetRequiredService<TourWriter>().Controls(tourId)
         : null;

   private static TourId? TourIdOf(Operation operation, object input) =>
      operation.InputType.Properties.FirstOrDefault(property => property.Name == Operation.TourIdProperty)?.Get?.Invoke(input) as TourId;

   private static async Task<ReadOnlyMemory<byte>> ReadBodyAsync(HttpContext context)
   {
      using var buffer = new MemoryStream();
      await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
      return buffer.GetBuffer().AsMemory(0, (int)buffer.Length);
   }
}
