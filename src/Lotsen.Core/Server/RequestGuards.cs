using System.Net;
using Lotsen.Operations;
using Lotsen.Sessions;
using Lotsen.Tours;

namespace Lotsen.Server;

/// <summary>
/// Holds every check that decides whether the server answers a request, in one place.
/// </summary>
/// <remarks>
/// <para>
/// The <c>Host</c> header must name the loopback origin the request arrived on, <c>127.0.0.1:port</c> or <c>localhost:port</c>,
/// so a DNS rebinding page cannot reach the server under its own name (421 otherwise).
/// </para>
/// <para>
/// The <c>Origin</c> header must be absent, as from a command-line client, or the origin the <c>Host</c> header names, as from the UI (403 otherwise).
/// The server sends no CORS headers and sets no cookies.
/// </para>
/// <para>
/// Every path under <c>/api</c>, mapped or not, needs <c>Authorization: Bearer</c> with one of the <see cref="ServerCredentials"/> (401 otherwise), unless its endpoint is marked with <see
/// cref="AllowWithoutToken{TBuilder}"/>. UI files need no token because they hold no data. The credentials decide the request's <see cref="Caller"/>, which <see cref="GetCaller"/> returns.
/// </para>
/// <para>
/// Each refusal of the guards is plain text, except <see cref="SessionUnknown"/>, which is an <see cref="OperationError"/> for the adapter to read.
/// </para>
/// </remarks>
internal static class RequestGuards
{
   /// <summary>The reason of the refusal of an API request without a credential, which also tells a lotsen server from another HTTP server.</summary>
   public const string NeedsCredentialReason = "The API needs the server token or the UI credential.";

   /// <summary>The refusal of a request whose session credential names a session that has ended or that the server never opened.</summary>
   /// <remarks>The adapter opens a new session when a call meets it, so the reason is for a caller that does not.</remarks>
   public static readonly OperationError SessionUnknown =
      new(OperationErrorCode.SessionUnknown, "The agent session has ended or is unknown. The adapter opens a new session by itself, so call the tool again.");

   /// <summary>Adds the checks to the pipeline, around the routing that the token check needs.</summary>
   public static void UseRequestGuards(this WebApplication app)
   {
      app.Use(CheckHostAndOrigin);
      app.UseRouting();
      app.Use(CheckCredentials);
   }

   /// <summary>Exempts an endpoint under <c>/api</c> from the token check, for the exchange that hands out the token.</summary>
   public static TBuilder AllowWithoutToken<TBuilder>(this TBuilder builder)
      where TBuilder : IEndpointConventionBuilder =>
      builder.WithMetadata(new AllowWithoutTokenMetadata());

   /// <summary>Returns the caller of an API request that the guards accepted.</summary>
   /// <exception cref="InvalidOperationException">The request has no caller, as on an endpoint that needs no token.</exception>
   public static Caller GetCaller(this HttpContext context) => context.Features.Get<Caller>() ?? throw new InvalidOperationException("The request has no caller.");

   /// <summary>Answers with a status and a plain-text reason.</summary>
   public static Task Refuse(HttpContext context, HttpStatusCode status, string reason)
   {
      context.Response.StatusCode = (int)status;
      context.Response.ContentType = "text/plain; charset=utf-8";
      return context.Response.WriteAsync(reason);
   }

   /// <summary>Returns whether the path is under <c>/api</c>, in any letter case, as routing matches it.</summary>
   public static bool IsApiPath(PathString path) => path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);

   private static Task CheckHostAndOrigin(HttpContext context, RequestDelegate next)
   {
      var headers = context.Response.Headers;
      headers.XContentTypeOptions = "nosniff";
      headers["Referrer-Policy"] = "no-referrer";
      ContentSecurityPolicy.Apply(context);

      var host = context.Request.Headers.Host.ToString();
      var port = context.Connection.LocalPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
      if (!IsLoopbackHost(host, port))
      {
         return Refuse(context, HttpStatusCode.MisdirectedRequest, "The Host header must be 127.0.0.1 or localhost with the server's port.");
      }

      if (context.Request.Headers.TryGetValue("Origin", out var origin) && !string.Equals(origin.ToString(), $"http://{host}", StringComparison.OrdinalIgnoreCase))
      {
         return Refuse(context, HttpStatusCode.Forbidden, "Requests from other origins are refused.");
      }

      return next(context);
   }

   private static Task CheckCredentials(HttpContext context, RequestDelegate next)
   {
      if (!IsApiPath(context.Request.Path))
      {
         return next(context);
      }

      context.Response.Headers.CacheControl = "no-store";
      if (context.GetEndpoint()?.Metadata.GetMetadata<AllowWithoutTokenMetadata>() is not null)
      {
         return next(context);
      }

      var (identified, refuse) = Identify(context);
      if (identified is null)
      {
         return refuse!(context);
      }

      context.Features.Set(identified);

      // The path holds no credential, and the query string stays out of the log.
      context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(RequestGuards).FullName!)
         .LogDebug("{Method} {Path} from {Caller}", context.Request.Method, context.Request.Path, identified);
      return next(context);
   }

   // Returns the caller, or the refusal that answers the request without one.
   private static (Caller? Caller, Func<HttpContext, Task>? Refuse) Identify(HttpContext context)
   {
      const string scheme = "Bearer ";
      var authorization = context.Request.Headers.Authorization;
      if (authorization.Count != 1 || authorization[0] is not { } value || !value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
      {
         return (null, NeedsCredential);
      }

      var services = context.RequestServices;
      var caller = Caller.Identify(
         services.GetRequiredService<ServerCredentials>(), services.GetRequiredService<AgentSessions>(), value.AsSpan(scheme.Length), context.Request.Headers[AgentSessions.CredentialHeader], out var failure);
      return failure switch
      {
         IdentifyFailure.None => (caller, null),
         IdentifyFailure.SessionUnknown => (null, UnknownSession),
         IdentifyFailure.UiAsSession => (null, refused => Refuse(refused, HttpStatusCode.Forbidden, "A request with the UI credential cannot act as an agent session.")),
         _ => (null, NeedsCredential),
      };
   }

   private static Task NeedsCredential(HttpContext context)
   {
      context.Response.Headers.WWWAuthenticate = "Bearer";
      return Refuse(context, HttpStatusCode.Unauthorized, NeedsCredentialReason);
   }

   private static Task UnknownSession(HttpContext context)
   {
      context.Response.Headers.WWWAuthenticate = "Bearer";
      return OperationWire.WriteRefusalAsync(context, SessionUnknown);
   }

   private static bool IsLoopbackHost(string host, string port)
   {
      var separator = host.LastIndexOf(':');
      if (separator < 0 || !host.AsSpan(separator + 1).SequenceEqual(port))
      {
         return false;
      }

      var name = host.AsSpan(0, separator);
      return name.Equals("127.0.0.1", StringComparison.Ordinal) || name.Equals("localhost", StringComparison.OrdinalIgnoreCase);
   }

   private sealed class AllowWithoutTokenMetadata;
}
