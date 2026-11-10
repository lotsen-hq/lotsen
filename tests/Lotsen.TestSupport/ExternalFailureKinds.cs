using Lotsen.Credentials;
using Lotsen.Git;
using Lotsen.Platforms;

namespace Lotsen.Tests;

/// <summary>One exception of each concrete type of <see cref="ExternalFailureException"/>, by name, for the rows of a test of a site that maps external failures.</summary>
internal static class ExternalFailureKinds
{
   /// <summary>The names of the concrete types.</summary>
   public static TheoryData<string> Names =>
      [nameof(PlatformException), nameof(AccessTokenUnavailableException), nameof(GitException), nameof(GitAuthException), nameof(UnsupportedRemoteException)];

   /// <summary>Returns an exception of the named type with this reason.</summary>
   public static ExternalFailureException Of(string name, string reason) => name switch
   {
      nameof(PlatformException) => new PlatformException(reason),
      nameof(AccessTokenUnavailableException) => new AccessTokenUnavailableException(reason),
      nameof(GitException) => new GitException(reason),
      nameof(GitAuthException) => new GitAuthException(reason),
      nameof(UnsupportedRemoteException) => new UnsupportedRemoteException(reason),
      _ => throw new ArgumentOutOfRangeException(nameof(name), name, "No external failure has this name."),
   };
}
