using System.Text.Json;
using Lotsen.Hosting;
using Lotsen.Json;
using Lotsen.Operations;
using Lotsen.Sessions;
using Lotsen.Tours;

namespace Lotsen.Server;

/// <summary>
/// Maps <c>POST /api/agent-session</c>, the connection that an adapter holds open for its agent session, and <c>GET /api/caller</c>, which tells a request's caller.
/// </summary>
/// <remarks>
/// <para>
/// The adapter opens its session with the server token alone and an <see cref="AgentSessionRequest"/> as the body, with its versions. Each refusal is an <see cref="OperationError"/>, with the
/// status that <see cref="OperationWire.StatusOf"/> gives its code's kind, as for an operation. The server refuses the UI credential and an agent session's own credential with
/// <see cref="OperationErrorCode.CallerNotAllowed"/>, and another build with <see cref="OperationErrorCode.BuildMismatch"/> before it registers a session. A background server that has decided to
/// exit refuses with <see cref="OperationErrorCode.ServerExiting"/>.
/// </para>
/// <para>
/// A background server with no session open decides its exit at once for an adapter of another build, and refuses it with <see cref="OperationErrorCode.ServerExiting"/>. The adapter then waits
/// for the exit and starts a server of its own build, so a newer install replaces an idle server after an update. With a session open the server refuses with
/// <see cref="OperationErrorCode.BuildMismatch"/> and keeps running, so no session loses its server.
/// </para>
/// <para>
/// The answer is an event stream, as <see cref="ServerSentEvents"/> writes it. Its first event after the hello is <see cref="AgentSessions.OpenedEvent"/>, with the session's ID and session
/// credential, and the events that the server sends this session alone follow it. The session ends when the connection drops or the server stops.
/// </para>
/// </remarks>
internal static class AgentSessionEndpoint
{
   private const string ExitingReason = "The lotsen server is stopping, because no agent session used it for its grace period. Call the tool again, and lotsen starts a server when none runs.";

   private const string ReplacedReason =
      "The running lotsen server is another build than this adapter, and no agent session uses it, so it is stopping for a server of this adapter's build. Call the tool again, and lotsen starts a server when none runs.";

   public static void MapAgentSession(this IEndpointRouteBuilder endpoints)
   {
      endpoints.MapPost("/api/agent-session", OpenAsync);
      endpoints.MapGet("/api/caller", (HttpContext context) => TypedResults.Json(CallerInfo.Of(context.GetCaller()), LotsenJsonContext.Default.CallerInfo));
   }

   private static async Task OpenAsync(HttpContext context, AgentSessions sessions, VersionInfo serverVersion, ServerSentEvents streams, IHostApplicationLifetime lifetime, ILoggerFactory loggers)
   {
      switch (context.GetCaller())
      {
         case Caller.Ui:
            await OperationWire.WriteRefusalAsync(context, new OperationError(OperationErrorCode.CallerNotAllowed, "The UI cannot open an agent session. Only the adapter opens one, with the server token."));
            return;
         case Caller.Agent:
            await OperationWire.WriteRefusalAsync(context, new OperationError(OperationErrorCode.CallerNotAllowed, "An agent session cannot open another. Open it with the server token alone."));
            return;
      }

      AgentSessionRequest? request;
      try
      {
         request = await context.Request.ReadFromJsonAsync(LotsenJsonContext.Default.AgentSessionRequest, context.RequestAborted);
      }
      catch (Exception exception) when (exception is JsonException or InvalidOperationException)
      {
         request = null;
      }

      if (request is null)
      {
         await OperationWire.WriteRefusalAsync(context, new OperationError(OperationErrorCode.InvalidInput, "The body must be the adapter's versions as JSON, with its build and protocol."));
         return;
      }

      if (request.Version != serverVersion)
      {
         var refusal = sessions.TryExitNowWhenIdle()
            ? new OperationError(OperationErrorCode.ServerExiting, ReplacedReason)
            : new OperationError(OperationErrorCode.BuildMismatch, VersionInfo.MismatchReason(serverVersion, request.Version));
         await OperationWire.WriteRefusalAsync(context, refusal);
         return;
      }

      if (sessions.TryOpen() is not { } session)
      {
         await OperationWire.WriteRefusalAsync(context, new OperationError(OperationErrorCode.ServerExiting, ExitingReason));
         return;
      }

      var logger = loggers.CreateLogger(typeof(AgentSessionEndpoint).FullName!);
      logger.LogInformation("Opened {Session}.", session);

      // An open stream would otherwise hold shutdown until the host's shutdown timeout.
      using var end = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
      try
      {
         var writer = await streams.StartAsync(context, end.Token);
         var opened = JsonSerializer.Serialize(new AgentSessionOpened(session.Id, session.Credential), LotsenJsonContext.Default.AgentSessionOpened);
         await writer.WriteAsync(new SessionEvent(AgentSessions.OpenedEvent, opened), end.Token);
         await writer.PumpAsync(session.Events, end.Token);
      }
      catch (OperationCanceledException) when (end.IsCancellationRequested)
      {
      }
      finally
      {
         session.End();
         logger.LogInformation("Ended {Session}.", session);
      }
   }
}
