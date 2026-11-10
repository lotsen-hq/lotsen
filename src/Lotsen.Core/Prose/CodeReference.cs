using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Lotsen.Git;
using Lotsen.Tours;

namespace Lotsen.Prose;

/// <summary>
/// A code reference: the lines of one file at one side that a <c>ref:</c> link in prose points at.
/// </summary>
/// <remarks>
/// Only <see cref="TryParse"/> creates one, so every instance came from a well-formed link. Nothing here checks that the file or the lines exist at the commit.
/// </remarks>
internal sealed record CodeReference
{
   /// <summary>The scheme of a link that holds a code reference. A link matches it in any letter case.</summary>
   public const string Scheme = "ref:";

   private const string Example = "ref:head/src/App.cs#L10-20";
   private const string BaseName = "base";
   private const string HeadName = "head";

   private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

   private CodeReference(string link, FileSide side, string path, int startLine, int endLine)
   {
      Link = link;
      Side = side;
      Path = path;
      StartLine = startLine;
      EndLine = endLine;
   }

   /// <summary>The link's destination as the prose holds it, with the path still percent-encoded.</summary>
   public string Link { get; }

   public FileSide Side { get; }

   /// <summary>The repository path of the file, percent-decoded.</summary>
   public string Path { get; }

   /// <summary>The first line, 1-based.</summary>
   public int StartLine { get; }

   /// <summary>The last line, 1-based and inclusive, never before <see cref="StartLine"/>.</summary>
   public int EndLine { get; }

   /// <summary>The side as a link writes it: <c>base</c> or <c>head</c>.</summary>
   public string SideName => Side == FileSide.Base ? BaseName : HeadName;

   /// <summary>Returns whether a link's destination has the <c>ref:</c> scheme, well-formed or not.</summary>
   public static bool HasScheme(string destination) => destination.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

   /// <summary>Reads a link's destination of the form <c>ref:&lt;side&gt;/&lt;path&gt;#L&lt;start&gt;-&lt;end&gt;</c>, with the path percent-encoded.</summary>
   /// <param name="destination">The destination as Markdown gives it, with its backslash escapes and entities already resolved.</param>
   /// <param name="reference">The code reference, or null when the destination is malformed.</param>
   /// <param name="reason">Why the destination is malformed, for Claude, or null when it is well-formed.</param>
   public static bool TryParse(string destination, [NotNullWhen(true)] out CodeReference? reference, [NotNullWhen(false)] out string? reason)
   {
      reference = null;
      if (!HasScheme(destination))
      {
         reason = $"A code reference link starts with '{Scheme}', as in {Example}.";
         return false;
      }

      var rest = destination.AsSpan(Scheme.Length);
      var slash = rest.IndexOf('/');
      FileSide side;
      switch (slash < 0 ? [] : rest[..slash])
      {
         case BaseName:
            side = FileSide.Base;
            break;
         case HeadName:
            side = FileSide.Head;
            break;
         default:
            reason = $"A '{Scheme}' link names its side, '{BaseName}' or '{HeadName}' in lowercase, then a slash and the path, as in {Example}.";
            return false;
      }

      var target = rest[(slash + 1)..];
      var hash = target.IndexOf('#');
      if (hash < 0)
      {
         reason = $"A '{Scheme}' link ends with its lines as #L<start>-<end>, as in {Example}.";
         return false;
      }

      var startLine = 0;
      var endLine = 0;
      reason = DecodePath(target[..hash], out var path) ?? ReadLines(target[(hash + 1)..], out startLine, out endLine);
      if (reason is not null)
      {
         return false;
      }

      reference = new CodeReference(destination, side, path, startLine, endLine);
      return true;
   }

   /// <summary>Returns the destination of a link to the lines, which <see cref="TryParse"/> reads back to them.</summary>
   /// <remarks>
   /// The path keeps its ASCII letters, digits, slashes and <c>-._~</c>, and every other UTF-8 byte is percent-encoded, so the destination holds no character that ends a Markdown link.
   /// </remarks>
   /// <param name="path">A repository path, as <see cref="RepositoryPath.IsValid"/> accepts it.</param>
   public static string Format(FileSide side, string path, int startLine, int endLine)
   {
      var encoded = new StringBuilder();
      foreach (var value in Encoding.UTF8.GetBytes(path))
      {
         if (char.IsAsciiLetterOrDigit((char)value) || value is (byte)'/' or (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~')
         {
            encoded.Append((char)value);
         }
         else
         {
            encoded.Append(CultureInfo.InvariantCulture, $"%{value:X2}");
         }
      }

      return string.Create(CultureInfo.InvariantCulture, $"{Scheme}{(side == FileSide.Base ? BaseName : HeadName)}/{encoded}#L{startLine}-{endLine}");
   }

   /// <returns>Why the path is malformed, or null when <paramref name="path"/> holds the decoded repository path.</returns>
   private static string? DecodePath(ReadOnlySpan<char> encoded, out string path)
   {
      path = "";
      var bytes = new List<byte>(encoded.Length);
      while (!encoded.IsEmpty)
      {
         var percent = encoded.IndexOf('%');
         bytes.AddRange(Encoding.UTF8.GetBytes((percent < 0 ? encoded : encoded[..percent]).ToString()));
         if (percent < 0)
         {
            break;
         }

         if (encoded.Length < percent + 3 || !byte.TryParse(encoded.Slice(percent + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
         {
            return "The path is not valid percent-encoding: each '%' starts a byte as two hexadecimal digits, so a literal '%' is written as %25.";
         }

         bytes.Add(value);
         encoded = encoded[(percent + 3)..];
      }

      try
      {
         path = StrictUtf8.GetString([.. bytes]);
      }
      catch (DecoderFallbackException)
      {
         return "The percent-encoded bytes of the path are not valid UTF-8.";
      }

      if (path.Any(char.IsControl))
      {
         return "The path holds a control character.";
      }

      return RepositoryPath.IsValid(path) ? null : $"The path, once percent-decoded, must be {RepositoryPath.Expected}.";
   }

   /// <returns>Why the lines are malformed, or null when <paramref name="startLine"/> and <paramref name="endLine"/> hold them.</returns>
   private static string? ReadLines(ReadOnlySpan<char> lines, out int startLine, out int endLine)
   {
      startLine = 0;
      endLine = 0;
      var dash = lines.IndexOf('-');
      if (lines is not ['L', ..] || dash < 0 || !IsDigits(lines[1..dash]) || !IsDigits(lines[(dash + 1)..]))
      {
         return $"A '{Scheme}' link ends with its lines as #L<start>-<end>, with both numbers in decimal digits, as in {Example}.";
      }

      if (!int.TryParse(lines[1..dash], NumberStyles.None, CultureInfo.InvariantCulture, out startLine)
         || !int.TryParse(lines[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out endLine))
      {
         return "A line number in the link is too large.";
      }

      if (startLine < 1)
      {
         return "Line numbers start at 1.";
      }

      return endLine < startLine ? "The end line comes before the start line." : null;
   }

   private static bool IsDigits(ReadOnlySpan<char> text) => !text.IsEmpty && !text.ContainsAnyExceptInRange('0', '9');
}
