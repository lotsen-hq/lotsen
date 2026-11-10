using System.Net;
using Lotsen.Hosting;
using Lotsen.Sessions;

namespace Lotsen.Server;

/// <summary>Maps the shutdown route, <c>POST /api/shutdown</c>, through which <c>lotsen stop</c> ends a server the same way on every OS.</summary>
/// <remarks>The route takes the server token alone, so neither the UI credential nor an agent session ends the server.</remarks>
internal static class ShutdownEndpoint
{
   public static void MapShutdown(this IEndpointRouteBuilder endpoints) => endpoints.MapPost("/api/shutdown", Shutdown);

   private static Task Shutdown(HttpContext context, IHostApplicationLifetime lifetime, ILoggerFactory loggers, VersionInfo version)
   {
      switch (context.GetCaller())
      {
         case Caller.Ui:
            return RequestGuards.Refuse(context, HttpStatusCode.Forbidden, $"The UI cannot stop the lotsen server. Run {StopCommand(version)}.");
         case Caller.Agent:
            return RequestGuards.Refuse(context, HttpStatusCode.Forbidden, $"An agent session cannot stop the lotsen server. The user runs {StopCommand(version)}.");
      }

      loggers.CreateLogger(typeof(ShutdownEndpoint).FullName!).LogInformation("Stopping, as the CLI asked.");

      // The host stops as on Ctrl-C once the answer is sent, so the caller receives the answer before the server closes its connections.
      context.Response.OnCompleted(() =>
      {
         lifetime.StopApplication();
         return Task.CompletedTask;
      });
      context.Response.StatusCode = StatusCodes.Status202Accepted;
      return Task.CompletedTask;
   }

   // A user with only the plugin has no lotsen on PATH, so the reason also names the npx form of the server's build.
   private static string StopCommand(VersionInfo version) => LotsenCommand.Of(version) is var npx && npx != LotsenCommand.Installed
      ? $"`{LotsenCommand.Installed.With("stop")}` in a terminal, or `{npx.With("stop")}` where lotsen is not on PATH"
      : $"`{LotsenCommand.Installed.With("stop")}` in a terminal";
}
