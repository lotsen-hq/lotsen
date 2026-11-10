using System.Text.Json.Serialization;
using Lotsen.Server;
using Microsoft.Extensions.Primitives;

namespace Lotsen.Sessions;

/// <summary>Why <see cref="Caller.Identify"/> found no caller for a request's credentials.</summary>
internal enum IdentifyFailure
{
   /// <summary>No failure: the credentials name a caller.</summary>
   None,

   /// <summary>The bearer credential is neither the server token nor the UI credential.</summary>
   NoCredential,

   /// <summary>The server token came with a session credential that names no open agent session, or with more than one.</summary>
   SessionUnknown,

   /// <summary>The UI credential came with a session credential.</summary>
   UiAsSession,
}

/// <summary>
/// Who made a request, as the server derives it from the request's credentials: the UI, an agent session, or the CLI.
/// </summary>
/// <remarks>
/// The request guards set the caller of every API request they accept, and nothing in a request's body or query names it. The private constructors keep the three kinds the only ones, and
/// a <see cref="Ui"/> or an <see cref="Agent"/> comes only from a check of the credential that it stands for, so holding one proves that credential. <see cref="Identify"/> runs those checks.
/// </remarks>
internal abstract class Caller
{
   private Caller()
   {
   }

   /// <summary>The kind's name in the API: <c>ui</c>, <c>agent</c> or <c>cli</c>.</summary>
   public abstract string Kind { get; }

   /// <summary>Returns the caller that the credentials of a request name.</summary>
   /// <param name="credentials">The server's credentials.</param>
   /// <param name="sessions">The open agent sessions, which a session credential names one of.</param>
   /// <param name="bearer">The credential after <c>Bearer</c>.</param>
   /// <param name="sessionCredential">The values of the session credential header, none for a request without it.</param>
   /// <param name="failure">Why the credentials name no caller, or <see cref="IdentifyFailure.None"/>.</param>
   /// <returns>The caller, or null when the credentials name none.</returns>
   public static Caller? Identify(ServerCredentials credentials, AgentSessions sessions, ReadOnlySpan<char> bearer, StringValues sessionCredential, out IdentifyFailure failure)
   {
      Caller? identified;
      if (credentials.ServerToken.Matches(bearer))
      {
         identified = sessionCredential.Count == 0 ? Cli.Instance : sessionCredential is [{ } single] ? Agent.Presenting(credentials, sessions, bearer, single) : null;
         failure = identified is null ? IdentifyFailure.SessionUnknown : IdentifyFailure.None;
         return identified;
      }

      identified = Ui.Presenting(credentials, bearer);
      failure = identified is null ? IdentifyFailure.NoCredential : sessionCredential.Count == 0 ? IdentifyFailure.None : IdentifyFailure.UiAsSession;
      return failure == IdentifyFailure.None ? identified : null;
   }

   /// <summary>The UI, which speaks for the user: a request with the UI credential.</summary>
   public sealed class Ui : Caller
   {
      private Ui()
      {
      }

      public override string Kind => "ui";

      private static Ui Instance { get; } = new();

      /// <summary>Returns the UI when the bearer credential is the UI credential, or null when it is not.</summary>
      internal static Ui? Presenting(ServerCredentials credentials, ReadOnlySpan<char> bearer) => credentials.Ui.Matches(bearer) ? Instance : null;

      public override string ToString() => "the UI";
   }

   /// <summary>One agent session: a request with the server token and that session's credential.</summary>
   public sealed class Agent : Caller
   {
      private Agent(Recipient recipient)
      {
         Recipient = recipient;
      }

      /// <summary>The session that made the request. It may have ended since the request arrived, and the session ledger then refuses it or reads it as holding no tour.</summary>
      public Recipient Recipient { get; }

      public override string Kind => "agent";

      public override string ToString() => Recipient.ToString();

      /// <summary>Returns the open agent session that the session credential names.</summary>
      /// <returns>The agent, or null when the bearer credential is not the server token or the session credential names no open session.</returns>
      internal static Agent? Presenting(ServerCredentials credentials, AgentSessions sessions, ReadOnlySpan<char> bearer, string sessionCredential) =>
         credentials.ServerToken.Matches(bearer) && sessions.Authenticate(sessionCredential) is { } session ? new(session.Recipient) : null;
   }

   /// <summary>A request with the server token and no session credential, as from the CLI, or from an adapter that opens its session. It proves nothing beyond the server token.</summary>
   public sealed class Cli : Caller
   {
      private Cli()
      {
      }

      public static Cli Instance { get; } = new();

      public override string Kind => "cli";

      public override string ToString() => "the CLI";
   }
}

/// <summary>The response of <c>GET /api/caller</c>: who the server takes the caller of the request to be.</summary>
/// <param name="Kind">The caller's kind: <c>ui</c>, <c>agent</c> or <c>cli</c>.</param>
/// <param name="SessionId">The agent session's ID for an agent, or null for any other kind. JSON leaves it out when null.</param>
internal sealed record CallerInfo(string Kind, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SessionId = null)
{
   public static CallerInfo Of(Caller caller) => new(caller.Kind, (caller as Caller.Agent)?.Recipient.Id);
}
