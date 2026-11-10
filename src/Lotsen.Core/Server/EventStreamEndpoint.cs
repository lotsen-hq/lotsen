using System.Net;
using Lotsen.Operations;
using Lotsen.Sessions;

namespace Lotsen.Server;

/// <summary>Maps <c>GET /api/events</c>: a Server-Sent Events stream of every change, for every connected client.</summary>
/// <remarks>
/// <para>
/// The event types are those in <see cref="ServerEvents"/>, written as <see cref="ServerSentEvents"/> describes. The stream opens with its hello once the client is subscribed.
/// </para>
/// <para>
/// Only the UI may open it. Every other caller is refused with 403.
/// </para>
/// <para>
/// Events are not replayed. A client that reconnects, including one the server disconnected for falling behind, reloads the state it shows.
/// </para>
/// </remarks>
internal static class EventStreamEndpoint
{
   public static void MapEventStream(this IEndpointRouteBuilder endpoints) => endpoints.MapGet("/api/events", StreamAsync);

   private static async Task StreamAsync(HttpContext context, EventBroadcaster events, ServerSentEvents streams, IHostApplicationLifetime lifetime)
   {
      // The stream carries the reviewer's notes, which only the UI may read.
      if (context.GetCaller() is not Caller.Ui)
      {
         await RequestGuards.Refuse(context, HttpStatusCode.Forbidden, "Only the lotsen UI can open the event stream. An agent session receives its session's events on its own connection.");
         return;
      }

      using var subscription = events.Subscribe();

      // An open stream would otherwise hold shutdown until the host's shutdown timeout.
      using var end = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
      EventStreamWriter writer;
      try
      {
         writer = await streams.StartAsync(context, end.Token);
      }
      catch (OperationCanceledException) when (end.IsCancellationRequested)
      {
         return;
      }

      await writer.PumpAsync(subscription.Reader, end.Token);
   }
}
