using Microsoft.AspNetCore.Http.HttpResults;

namespace Lotsen.Server;

/// <summary>A request to exchange a one-time code for the UI credential.</summary>
/// <param name="Code">The one-time code from the link the page was opened with.</param>
internal sealed record UiCredentialRequest(string? Code);

/// <summary>The UI credential that a one-time code was exchanged for.</summary>
/// <param name="Credential">The bearer credential that the UI sends to the server's API.</param>
internal sealed record UiCredentialResponse(string Credential)
{
   public override string ToString() => "UiCredentialResponse { Credential = redacted }";
}

/// <summary>Maps <c>POST /api/ui-credential</c>, the route that hands the UI credential to the page.</summary>
internal static class UiCredentialEndpoint
{
   public static void MapUiCredential(this IEndpointRouteBuilder endpoints)
   {
      endpoints.MapPost("/api/ui-credential", ExchangeCode).AllowWithoutToken();
   }

   private static Results<Ok<UiCredentialResponse>, UnauthorizedHttpResult> ExchangeCode(UiCredentialRequest request, SessionCodes codes, ServerCredentials credentials) =>
      codes.TryRedeem(request.Code)
         ? TypedResults.Ok(new UiCredentialResponse(credentials.Ui.Value))
         : TypedResults.Unauthorized();
}
