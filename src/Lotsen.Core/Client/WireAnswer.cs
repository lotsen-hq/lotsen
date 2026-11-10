using Lotsen.Tours;

namespace Lotsen.Client;

/// <summary>What the server answered a request of <see cref="ServerClient"/> with, as the client decodes it.</summary>
/// <typeparam name="T">The value of an answer that succeeded.</typeparam>
internal closed record WireAnswer<T>
{
   private WireAnswer()
   {
   }

   /// <summary>An answer that succeeded.</summary>
   internal sealed record Output(T Value) : WireAnswer<T>;

   /// <summary>The server's own refusal, an <see cref="OperationError"/> whose code's kind has the answer's status.</summary>
   internal sealed record Refused(OperationError Error) : WireAnswer<T>;

   /// <summary>A bare 401, which the request guards answer before any route runs, as for a server token that the server does not hold.</summary>
   internal sealed record CredentialsRefused : WireAnswer<T>;

   /// <summary>An answer that is not lotsen's, as from another HTTP server on the port.</summary>
   /// <param name="Description">The request, the status and the body, as a message for the user.</param>
   internal sealed record Unexpected(string Description) : WireAnswer<T>;
}
