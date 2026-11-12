using System.Net;

namespace Lotsen.Client;

/// <summary>
/// The answer of a server that refused a request, or answered it with something other than a result. A server answered, unlike for the base <see cref="HttpRequestException"/>,
/// which also covers a connection that failed.
/// </summary>
internal sealed class RefusedRequestException : HttpRequestException
{
   /// <param name="reason">The server's reason, or a description of its answer, which the message holds.</param>
   /// <param name="statusCode">The status of the answer, or null when it does not matter to the caller.</param>
   public RefusedRequestException(string reason, HttpStatusCode? statusCode = null)
      : base(reason, inner: null, statusCode)
   {
   }
}
