using System.Diagnostics.CodeAnalysis;

namespace Lotsen.Git;

/// <summary>The rule for a path that a request names in a repository, such as an anchor's file or a file to read.</summary>
internal static class RepositoryPath
{
   /// <summary>What <see cref="IsValid"/> requires, as a phrase for a refusal.</summary>
   public const string Expected = "a relative repository path with forward slashes and no empty, '.' or '..' segments";

   /// <summary>Returns whether the path is relative, uses forward slashes, and has no empty, <c>.</c> or <c>..</c> segment, so it cannot reach outside the repository.</summary>
   public static bool IsValid([NotNullWhen(true)] string? path) =>
      !string.IsNullOrWhiteSpace(path)
      && !path.Contains('\\')
      && !(path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
      && path.Split('/').All(segment => segment is not ("" or "." or ".."));
}
