using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Lotsen.Tests;

/// <summary>
/// A loopback git host that serves one repository through <c>git http-backend</c> and answers 401 to every request without the one <c>Authorization</c> header it accepts.
/// </summary>
/// <remarks>
/// A 401 carries <c>WWW-Authenticate: Bearer</c>, as Azure DevOps sends. The server counts requests by outcome and keeps no header value. A redirect set with <see cref="Redirect"/> answers
/// before the authentication check.
/// </remarks>
internal sealed class AuthenticatingGitServer : IAsyncDisposable
{
   private readonly WebApplication _app;
   private readonly string _repositoryPath;
   private readonly string _servedAt;
   private volatile string _accepted;
   private volatile Redirection? _redirection;
   private volatile RemoteError? _remoteError;
   private int _authorized;
   private int _refused;
   private int _withoutHeader;

   private AuthenticatingGitServer(WebApplication app, string repositoryPath, string accepted, string servedAt)
   {
      _app = app;
      _repositoryPath = repositoryPath;
      _accepted = accepted;
      _servedAt = servedAt;
   }

   /// <summary>The URL of the served repository. The server serves it only under this URL's host, not under another name for the same address.</summary>
   public Uri RepositoryUrl => UrlOf(_servedAt);

   /// <summary>The number of requests that carried the accepted header.</summary>
   public int AuthorizedRequests => Volatile.Read(ref _authorized);

   /// <summary>The number of requests whose <c>Authorization</c> header the server refused.</summary>
   public int RefusedRequests => Volatile.Read(ref _refused);

   /// <summary>The number of requests without an <c>Authorization</c> header.</summary>
   public int RequestsWithoutHeader => Volatile.Read(ref _withoutHeader);

   /// <summary>Starts a server for the repository, with or without a working tree.</summary>
   /// <param name="repositoryPath">The repository to serve.</param>
   /// <param name="accepted">The whole <c>Authorization</c> header value that the server accepts, such as <c>Bearer abc</c>.</param>
   /// <param name="servedAt">The URL path of the served repository.</param>
   public static async Task<AuthenticatingGitServer> StartAsync(string repositoryPath, string accepted, string servedAt = "/repo")
   {
      var builder = WebApplication.CreateSlimBuilder();
      builder.Configuration.Sources.Clear();
      builder.Logging.ClearProviders();
      builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
      var app = builder.Build();
      var server = new AuthenticatingGitServer(app, repositoryPath, accepted, servedAt);
      app.Run(server.AnswerAsync);
      await app.StartAsync();
      return server;
   }

   /// <summary>Returns the URL of a path on this server.</summary>
   public Uri UrlOf(string path) => new($"{_app.Urls.Single()}{path}");

   /// <summary>Makes the server accept this header value from now on, and refuse every other.</summary>
   public void Accept(string accepted) => _accepted = accepted;

   /// <summary>Makes the server answer each request outside the served repository whose host and path start with <paramref name="from"/> with a redirect to the rest of its path under <paramref name="to"/>.</summary>
   public void Redirect(Uri from, Uri to) => _redirection = new Redirection(from, to);

   /// <summary>Makes the server answer each request for the served repository with a git <c>ERR</c> packet that holds the message, which git prints as a remote error.</summary>
   /// <param name="message">The packet's text, which may hold line breaks.</param>
   /// <param name="beforeAuthentication">Whether the packet answers a request without the accepted header too, or only one with it.</param>
   public void SendError(string message, bool beforeAuthentication) => _remoteError = new RemoteError(message, beforeAuthentication);

   public async ValueTask DisposeAsync()
   {
      await _app.StopAsync(CancellationToken.None);
      await _app.DisposeAsync();
   }

   private async Task AnswerAsync(HttpContext context)
   {
      var rest = PathString.Empty;
      var served = string.Equals(context.Request.Host.Value, RepositoryUrl.Authority, StringComparison.OrdinalIgnoreCase) && context.Request.Path.StartsWithSegments(_servedAt, out rest);
      if (!served && _redirection is { } redirection
         && string.Equals(context.Request.Host.Value, redirection.From.Authority, StringComparison.OrdinalIgnoreCase)
         && context.Request.Path.StartsWithSegments(redirection.From.AbsolutePath, out var tail))
      {
         context.Response.Redirect(redirection.To.AbsoluteUri + tail + context.Request.QueryString);
         return;
      }

      var header = context.Request.Headers.Authorization;
      if (served && _remoteError is { } remoteError && (remoteError.BeforeAuthentication || header == _accepted))
      {
         await SendErrorPacketAsync(context, remoteError.Message);
         return;
      }

      if (header.Count == 0)
      {
         Interlocked.Increment(ref _withoutHeader);
         Refuse(context);
         return;
      }

      if (header != _accepted)
      {
         Interlocked.Increment(ref _refused);
         Refuse(context);
         return;
      }

      Interlocked.Increment(ref _authorized);
      if (!served)
      {
         context.Response.StatusCode = StatusCodes.Status404NotFound;
         return;
      }

      await RunBackendAsync(context, rest.Value ?? "");
   }

   // A smart HTTP advertisement of the upload-pack service whose first packet after the service line is the ERR packet.
   private static async Task SendErrorPacketAsync(HttpContext context, string message)
   {
      static string Packet(string line) => $"{System.Text.Encoding.UTF8.GetByteCount(line) + 4:x4}{line}";

      context.Response.ContentType = "application/x-git-upload-pack-advertisement";
      await context.Response.WriteAsync(Packet("# service=git-upload-pack\n") + "0000" + Packet($"ERR {message}\n"), context.RequestAborted);
   }

   private static void Refuse(HttpContext context)
   {
      context.Response.StatusCode = StatusCodes.Status401Unauthorized;
      context.Response.Headers.WWWAuthenticate = "Bearer";
   }

   // Runs git http-backend as a CGI program (https://git-scm.com/docs/git-http-backend). The body is buffered, because Kestrel removes a chunked transfer encoding and CGI needs a length.
   private async Task RunBackendAsync(HttpContext context, string pathInRepository)
   {
      using var body = new MemoryStream();
      await context.Request.Body.CopyToAsync(body, context.RequestAborted);
      var startInfo = new ProcessStartInfo("git", ["http-backend"])
      {
         RedirectStandardInput = true,
         RedirectStandardOutput = true,
         RedirectStandardError = true,
         UseShellExecute = false,
      };
      var environment = startInfo.Environment;
      foreach (var name in environment.Keys.Where(name => name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToList())
      {
         environment.Remove(name);
      }

      environment["GIT_CONFIG_NOSYSTEM"] = "1";
      environment["GIT_CONFIG_GLOBAL"] = Path.Combine(Path.GetDirectoryName(_repositoryPath)!, "no-global-config");
      environment["GIT_PROJECT_ROOT"] = Path.GetDirectoryName(_repositoryPath)!;
      environment["GIT_HTTP_EXPORT_ALL"] = "1";
      environment["PATH_INFO"] = "/" + Path.GetFileName(_repositoryPath) + pathInRepository;
      environment["QUERY_STRING"] = context.Request.QueryString.HasValue ? context.Request.QueryString.Value![1..] : "";
      environment["REQUEST_METHOD"] = context.Request.Method;
      environment["CONTENT_TYPE"] = context.Request.ContentType ?? "";
      environment["CONTENT_LENGTH"] = body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
      environment["REMOTE_USER"] = "reviewer";
      environment["REMOTE_ADDR"] = "127.0.0.1";
      if (context.Request.Headers["Git-Protocol"] is [{ } protocol])
      {
         environment["GIT_PROTOCOL"] = protocol;
      }

      if (context.Request.Headers.ContentEncoding is [{ } encoding])
      {
         environment["HTTP_CONTENT_ENCODING"] = encoding;
      }

      using var process = Process.Start(startInfo)!;
      var error = process.StandardError.ReadToEndAsync(context.RequestAborted);
      body.Position = 0;
      await body.CopyToAsync(process.StandardInput.BaseStream, context.RequestAborted);
      process.StandardInput.Close();

      using var output = new MemoryStream();
      await process.StandardOutput.BaseStream.CopyToAsync(output, context.RequestAborted);
      await process.WaitForExitAsync(context.RequestAborted);
      await error;
      await WriteCgiOutputAsync(context, output.ToArray());
   }

   private static async Task WriteCgiOutputAsync(HttpContext context, byte[] output)
   {
      var headEnd = output.AsSpan().IndexOf("\r\n\r\n"u8);
      var separator = 4;
      if (headEnd < 0)
      {
         headEnd = output.AsSpan().IndexOf("\n\n"u8);
         separator = 2;
      }

      if (headEnd < 0)
      {
         context.Response.StatusCode = StatusCodes.Status500InternalServerError;
         return;
      }

      foreach (var line in System.Text.Encoding.ASCII.GetString(output, 0, headEnd).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
      {
         var colon = line.IndexOf(':');
         var (name, value) = (line[..colon].Trim(), line[(colon + 1)..].Trim());
         if (name.Equals("Status", StringComparison.OrdinalIgnoreCase))
         {
            context.Response.StatusCode = int.Parse(value.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
         }
         else
         {
            context.Response.Headers.Append(name, value);
         }
      }

      await context.Response.Body.WriteAsync(output.AsMemory(headEnd + separator), context.RequestAborted);
   }

   private sealed record Redirection(Uri From, Uri To);

   private sealed record RemoteError(string Message, bool BeforeAuthentication);
}
