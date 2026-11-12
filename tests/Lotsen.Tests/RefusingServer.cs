using System.Net;
using Lotsen.Hosting;
using Lotsen.Json;
using Lotsen.Server;
using Lotsen.Tours;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Lotsen.Tests;

/// <summary>
/// A server in the test process that answers as a lotsen server of some build does, and refuses every operation as a build without it: <see cref="OperationErrorCode.OperationNotFound"/>.
/// Its state file names the test process, so a command finds it as the running server of its data directory.
/// </summary>
internal sealed class RefusingServer : IAsyncDisposable
{
   private const string Token = "refusing-server-token";

   private readonly WebApplication _app;

   private RefusingServer(WebApplication app)
   {
      _app = app;
   }

   /// <summary>Starts the server and writes its state file into the data directory.</summary>
   /// <param name="dataDirectory">The data directory whose running server it is.</param>
   /// <param name="version">The versions that it answers the version request with, given the token.</param>
   public static async Task<RefusingServer> StartAsync(DataDirectory dataDirectory, VersionInfo version)
   {
      var builder = WebApplication.CreateSlimBuilder();
      builder.Logging.ClearProviders();
      builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
      var app = builder.Build();
      // A request without the token gets the refusal that a lotsen server's request guards give, which is what the discovery's probe checks.
      app.MapGet("/api/version", context =>
         context.Request.Headers.Authorization == $"Bearer {Token}"
            ? context.Response.WriteAsJsonAsync(version, LotsenJsonContext.Default.VersionInfo, cancellationToken: context.RequestAborted)
            : Results.Text(RequestGuards.NeedsCredentialReason, statusCode: StatusCodes.Status401Unauthorized).ExecuteAsync(context));
      app.MapPost("/api/ops/{name}", (string name, HttpContext context) =>
      {
         var error = new OperationError(OperationErrorCode.OperationNotFound, $"lotsen has no operation named '{name}'.");
         context.Response.StatusCode = StatusCodes.Status404NotFound;
         return context.Response.WriteAsJsonAsync(error, LotsenJsonContext.Default.OperationError, cancellationToken: context.RequestAborted);
      });
      await app.StartAsync(TestContext.Current.CancellationToken);
      StateFile.Write(dataDirectory, new ServerState(Environment.ProcessId, "127.0.0.1", new Uri(app.Urls.Single()).Port, Token));
      return new RefusingServer(app);
   }

   public async ValueTask DisposeAsync()
   {
      await _app.StopAsync(CancellationToken.None);
      await _app.DisposeAsync();
   }
}
