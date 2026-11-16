using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Lotsen.Git;

/// <summary>Provides the rule for the name of a branch or a tag that a repository target can follow.</summary>
public static class RefName
{
   private const string LockSuffix = ".lock";

   private static readonly SearchValues<char> Forbidden = SearchValues.Create("~^:?*[\\");

   /// <summary>Returns whether the text is a branch's or a tag's name that git accepts, with no whitespace or invisible format character of any kind.</summary>
   /// <remarks>
   /// The rules are those of <c>git check-ref-format --allow-onelevel</c>: https://git-scm.com/docs/git-check-ref-format. <c>RefNameTests</c> pins them against git. A name of one segment
   /// is valid, so a branch's short name such as <c>main</c> passes. Git accepts whitespace and format characters beyond ASCII, such as U+200B, which this refuses, so a valid name is never
   /// blank and shows every character it holds.
   /// </remarks>
   public static bool IsValid([NotNullWhen(true)] string? name)
   {
      if (string.IsNullOrEmpty(name)
         || name == "@"
         || name.EndsWith('.')
         || name.Contains("..", StringComparison.Ordinal)
         || name.Contains("@{", StringComparison.Ordinal)
         || name.AsSpan().ContainsAny(Forbidden)
         || name.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || char.GetUnicodeCategory(character) == UnicodeCategory.Format))
      {
         return false;
      }

      // An empty segment comes from a leading or a trailing slash, or from two slashes in a row.
      return name.Split('/').All(segment => segment.Length > 0 && !segment.StartsWith('.') && !segment.EndsWith(LockSuffix, StringComparison.Ordinal));
   }
}
