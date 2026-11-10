using System.Buffers.Text;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Lotsen.Server;

/// <summary>Creates random values for tokens, codes and nonces.</summary>
internal static class Secrets
{
   /// <summary>Returns a value of this many random bytes, as unpadded base64url text.</summary>
   public static string NewRandomValue(int byteCount) => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(byteCount));

   /// <summary>Returns whether a presented secret equals the expected one, in time that does not depend on where they differ.</summary>
   public static bool FixedTimeEquals(ReadOnlySpan<char> presented, ReadOnlySpan<char> expected) =>
      CryptographicOperations.FixedTimeEquals(MemoryMarshal.AsBytes(presented), MemoryMarshal.AsBytes(expected));
}

/// <summary>A bearer credential for the server's API, new at every server start.</summary>
internal sealed class Credential
{
   private Credential(string value)
   {
      Value = value;
   }

   /// <summary>The credential as the client sends it after <c>Bearer</c>.</summary>
   public string Value { get; }

   public static Credential CreateRandom() => new(Secrets.NewRandomValue(32));

   /// <summary>Returns whether a presented credential is this one, in time that does not depend on where they differ.</summary>
   public bool Matches(ReadOnlySpan<char> presented) => Secrets.FixedTimeEquals(presented, Value);

   public override string ToString() => "Credential { redacted }";
}

/// <summary>The two bearer credentials that tell the server's callers apart.</summary>
/// <param name="ServerToken">The token in the state file, which the adapter and the CLI send.</param>
/// <param name="Ui">The credential that the UI gets for a one-time code. A request with it is the UI.</param>
internal sealed record ServerCredentials(Credential ServerToken, Credential Ui)
{
   public static ServerCredentials CreateRandom() => new(Credential.CreateRandom(), Credential.CreateRandom());
}
