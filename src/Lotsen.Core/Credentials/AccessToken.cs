using Lotsen.Platforms;

namespace Lotsen.Credentials;

/// <summary>Provides the access token that lotsen presents to a platform as the user, such as to Azure DevOps.</summary>
/// <remarks>
/// A platform's git credential and its API client depend on this interface and never on a concrete provider. A provider holds the token in memory only, so a call may return a cached token.
/// </remarks>
public interface IAccessTokenProvider
{
   /// <summary>Returns a token that stays valid for at least the time asked, when the provider can get one.</summary>
   /// <param name="minimumValidity">How long the token must stay valid: the longest that the call which sends it may take.</param>
   /// <param name="cancellationToken">Stops the request.</param>
   /// <returns>The token. A provider whose source has no token valid that long returns the one with the longest validity left.</returns>
   /// <exception cref="AccessTokenUnavailableException">No token could be obtained. The message names the fix.</exception>
   ValueTask<AccessToken> GetTokenAsync(TimeSpan minimumValidity, CancellationToken cancellationToken);

   /// <summary>Drops the token from the provider's cache, so the next request obtains a new one.</summary>
   /// <remarks>
   /// Call it when the platform refuses the token or its account, so a new sign-in takes effect. A provider that caches nothing, or that has already replaced the token, does nothing.
   /// </remarks>
   /// <param name="token">The token that the platform refused, as this provider returned it.</param>
   void Reject(AccessToken token)
   {
   }
}

/// <summary>
/// A token for a platform's API and git remotes.
/// </summary>
/// <remarks>
/// The token acts as the user, so it is never written to disk, logged, sent to the UI or returned to Claude. <see cref="ToString"/> hides it, so string formatting cannot reveal it.
/// </remarks>
public sealed class AccessToken
{
   public AccessToken(string value)
   {
      ArgumentException.ThrowIfNullOrWhiteSpace(value);

      // A line break would let the token end the HTTP header it travels in and start another.
      if (value.AsSpan().ContainsAny('\r', '\n'))
      {
         throw new ArgumentException("An access token cannot contain a line break.", nameof(value));
      }

      Value = value;
   }

   /// <summary>The token as the platform's authentication scheme sends it.</summary>
   public string Value { get; }

   public override string ToString() => "AccessToken { redacted }";
}

/// <summary>The exception that a token provider throws when it cannot obtain a token.</summary>
public sealed class AccessTokenUnavailableException : ExternalFailureException
{
   /// <param name="message">The reason, which names the fix, such as signing in. It must not contain a token.</param>
   public AccessTokenUnavailableException(string message)
      : base(message, null)
   {
   }
}
