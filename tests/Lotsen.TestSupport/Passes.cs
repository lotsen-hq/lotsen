using Lotsen.Operations;
using Lotsen.Server;
using Lotsen.Sessions;

namespace Lotsen.Tests;

/// <summary>Callers and passes for tests that call the operations directly, each from the same path that a request takes: the credentials and the catalog's own declarations.</summary>
internal static class Passes
{
   /// <summary>Returns the caller that the credentials name, as <see cref="Caller.Identify"/> derives it for a request.</summary>
   /// <exception cref="InvalidOperationException">The credentials name no caller.</exception>
   public static Caller Identify(ServerCredentials credentials, AgentSessions sessions, string bearer, string? sessionCredential = null) =>
      Caller.Identify(credentials, sessions, bearer, sessionCredential is null ? default : new(sessionCredential), out var failure)
      ?? throw new InvalidOperationException($"The credentials name no caller: {failure}.");

   /// <summary>Returns the UI as a caller.</summary>
   public static Caller Ui(ServerCredentials credentials, AgentSessions sessions) => Identify(credentials, sessions, credentials.Ui.Value);

   /// <summary>Returns the open session as a caller.</summary>
   public static Caller Agent(ServerCredentials credentials, AgentSessions sessions, AgentSession session) => Identify(credentials, sessions, credentials.ServerToken.Value, session.Credential);

   /// <summary>Returns the pass that the operation's declaration in the catalog issues to the caller.</summary>
   /// <exception cref="InvalidOperationException">The operation's declaration issues another kind of pass, or refuses the caller.</exception>
   public static TPass Admit<TPass>(string operation, Caller caller)
      where TPass : Access.Pass
   {
      var declared = OperationCatalog.Get(operation);
      if (declared.Access is not Access.For<TPass> access)
      {
         throw new InvalidOperationException($"{operation} declares {declared.Access.GetType().Name}, which issues no {typeof(TPass).Name}.");
      }

      var admitted = access.Admit(caller, declared);
      return admitted.Value ?? throw new InvalidOperationException($"{operation} refuses {caller}: {admitted.Error!.Reason}");
   }
}
