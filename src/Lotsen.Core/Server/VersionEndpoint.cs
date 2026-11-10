using Lotsen.Hosting;
using Lotsen.Json;

namespace Lotsen.Server;

/// <summary>Maps <c>GET /api/version</c>, which returns the server's <see cref="VersionInfo"/> for the adapter to compare with its own.</summary>
internal static class VersionEndpoint
{
   public static void MapVersion(this IEndpointRouteBuilder endpoints) => endpoints.MapGet("/api/version", (VersionInfo version) => TypedResults.Json(version, LotsenJsonContext.Default.VersionInfo));
}
