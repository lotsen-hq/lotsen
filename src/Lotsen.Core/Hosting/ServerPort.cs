using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Lotsen.Hosting;

/// <summary>
/// A configured port: the TCP port of 127.0.0.1 that the user asks the server to listen on, from 1 to 65535, and not one that browsers refuse.
/// </summary>
internal sealed class ServerPort
{
   /// <summary>The environment variable that configures the port when the command line does not.</summary>
   public const string Variable = "LOTSEN_PORT";

   // The bad ports of the Fetch standard (https://fetch.spec.whatwg.org/#port-blocking), which browsers and Node's fetch refuse to connect to.
   private static readonly FrozenSet<int> Blocked = FrozenSet.Create(
   [
      1, 7, 9, 11, 13, 15, 17, 19, 20, 21, 22, 23, 25, 37, 42, 43, 53, 69, 77, 79, 87, 95, 101, 102, 103, 104, 109, 110, 111, 113, 115, 117, 119, 123, 135, 137, 139, 143, 161, 179, 389, 427,
      465, 512, 513, 514, 515, 526, 530, 531, 532, 540, 548, 554, 556, 563, 587, 601, 636, 989, 990, 993, 995, 1719, 1720, 1723, 2049, 3659, 4045, 4190, 5060, 5061, 6000, 6566, 6665, 6666,
      6667, 6668, 6669, 6679, 6697, 10080,
   ]);

   private ServerPort(int value)
   {
      Value = value;
   }

   /// <summary>The port number.</summary>
   public int Value { get; }

   /// <summary>Reads a port number written in decimal digits alone, without a sign or white space.</summary>
   /// <returns>Whether the text is a port number from 1 to 65535 that <see cref="IsBlocked"/> does not refuse.</returns>
   public static bool TryParse(string text, [NotNullWhen(true)] out ServerPort? port)
   {
      if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value is >= 1 and <= ushort.MaxValue && !IsBlocked(value))
      {
         port = new ServerPort(value);
         return true;
      }

      port = null;
      return false;
   }

   /// <summary>Returns whether browsers refuse to connect to the port, so the UI could not open on it.</summary>
   public static bool IsBlocked(int port) => Blocked.Contains(port);

   /// <summary>Returns whether the text is a port number from 1 to 65535 that <see cref="IsBlocked"/> refuses, for the reason of a refusal.</summary>
   public static bool IsBlockedNumber(string text) =>
      int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value is >= 1 and <= ushort.MaxValue && IsBlocked(value);

   public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
