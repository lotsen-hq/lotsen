using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Lotsen.Hosting;

/// <summary>
/// A fact cache size: the budget of the fact memo in whole mebibytes, from <see cref="Minimum"/> to <see cref="int.MaxValue"/>, as the user configures it or <see cref="Default"/>.
/// </summary>
internal sealed class FactCacheSize
{
   /// <summary>The environment variable that configures the size when the command line does not.</summary>
   public const string Variable = "LOTSEN_FACT_CACHE_MB";

   /// <summary>The smallest size. A memo of 0 bytes would keep no fact, because every fact's estimated size is at least 64 bytes.</summary>
   public const int Minimum = 1;

   private const long BytesPerMebibyte = 1024 * 1024;

   private FactCacheSize(int mebibytes)
   {
      Mebibytes = mebibytes;
   }

   /// <summary>The size without an option or a variable: 64 MiB.</summary>
   public static FactCacheSize Default { get; } = new(64);

   /// <summary>The size in mebibytes.</summary>
   public int Mebibytes { get; }

   /// <summary>The size in bytes.</summary>
   public long Bytes => Mebibytes * BytesPerMebibyte;

   /// <summary>Reads a number of mebibytes written in decimal digits alone, without a sign, a unit or white space.</summary>
   /// <returns>Whether the text is a whole number from <see cref="Minimum"/> to <see cref="int.MaxValue"/>.</returns>
   public static bool TryParse(string text, [NotNullWhen(true)] out FactCacheSize? size)
   {
      if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= Minimum)
      {
         size = new FactCacheSize(value);
         return true;
      }

      size = null;
      return false;
   }

   public override string ToString() => Mebibytes.ToString(CultureInfo.InvariantCulture);
}
