namespace Lotsen.Platforms;

/// <summary>The base of every exception for a failure outside the process: a platform's request, a credential, a git call or a remote URL that lotsen does not clone from.</summary>
/// <remarks>
/// <para>
/// Only Core's exception types derive from it, because its constructor is private protected: <see cref="PlatformException"/>, <see cref="Credentials.AccessTokenUnavailableException"/>,
/// <c>GitException</c> with <c>GitAuthException</c>, and <c>UnsupportedRemoteException</c>. A platform library throws the first two.
/// </para>
/// <para>
/// A site that turns an external failure into a result catches this type, not a subtype unless the subtype changes what the site does, and serves <see cref="Reason"/>. Any other exception
/// is a fault of lotsen and propagates.
/// </para>
/// </remarks>
public abstract class ExternalFailureException : Exception
{
   /// <param name="reason">The served reason, as <see cref="Reason"/> describes it.</param>
   /// <param name="innerException">The exception that caused the failure, or null for none.</param>
   private protected ExternalFailureException(string reason, Exception? innerException)
      : base(reason, innerException)
   {
   }

   /// <summary>What failed, written for the reviewer and Claude, which names the fix where there is one. It never holds a token, a credential or git's command line.</summary>
   public string Reason => Message;
}
