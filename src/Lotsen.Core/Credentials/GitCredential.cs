namespace Lotsen.Credentials;

/// <summary>Provides the credential that git presents to the remotes of one platform.</summary>
/// <remarks>
/// A platform supplies one, and the git layer asks it only when a remote asks git for authentication. The platform chooses the authentication scheme, so the git layer names none.
/// </remarks>
public interface IGitCredential
{
   /// <summary>The fix for a credential that the remote refused, as a sentence for the reviewer and Claude, such as "Run 'az login', then try again."</summary>
   string FixHint { get; }

   /// <summary>Returns the answer that git sends to the remote.</summary>
   /// <param name="minimumValidity">How long the answer must stay valid: at least the longest that the git run which sends it may take.</param>
   /// <param name="cancellationToken">Stops the request.</param>
   /// <exception cref="AccessTokenUnavailableException">No credential could be obtained. The message names the fix.</exception>
   ValueTask<GitCredentialAnswer> AnswerAsync(TimeSpan minimumValidity, CancellationToken cancellationToken);
}

/// <summary>
/// A credential for git to send to a remote, in the form of git's credential helper protocol, with what to do when the remote refuses it.
/// </summary>
/// <remarks>
/// Git sends the credential as <c>Authorization: &lt;scheme&gt; &lt;credential&gt;</c>, through the protocol's <c>authtype</c> and <c>credential</c> fields
/// (https://git-scm.com/docs/git-credential). The answer acts as the user, so <see cref="ToString"/> hides it.
/// </remarks>
public sealed class GitCredentialAnswer
{
   private readonly Action _refused;

   /// <param name="scheme">The HTTP authentication scheme, such as the one the platform documents for its tokens.</param>
   /// <param name="credential">The value after the scheme in the <c>Authorization</c> header.</param>
   /// <param name="refused">Runs when the remote refuses the answer, such as to drop the token from the provider's cache.</param>
   /// <exception cref="ArgumentException">The scheme or the credential is empty, or holds a line break, a NUL or, for the scheme, a space.</exception>
   public GitCredentialAnswer(string scheme, string credential, Action refused)
   {
      ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
      ArgumentException.ThrowIfNullOrWhiteSpace(credential);

      // The helper protocol is one field per line, so a line break would start a field of the value's choosing.
      if (scheme.AsSpan().ContainsAny(" \r\n\0") || credential.AsSpan().ContainsAny("\r\n\0"))
      {
         throw new ArgumentException("A git credential's scheme and value cannot contain a line break or a NUL, and its scheme cannot contain a space.");
      }

      Scheme = scheme;
      Credential = credential;
      _refused = refused;
   }

   /// <summary>The HTTP authentication scheme.</summary>
   public string Scheme { get; }

   /// <summary>The value after the scheme. It acts as the user.</summary>
   public string Credential { get; }

   /// <summary>Tells the answer's source that the remote refused it.</summary>
   internal void Refuse() => _refused();

   public override string ToString() => "GitCredentialAnswer { redacted }";
}
