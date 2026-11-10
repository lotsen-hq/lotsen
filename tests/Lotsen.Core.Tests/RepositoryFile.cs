using System.Runtime.CompilerServices;

namespace Lotsen.Tests;

/// <summary>Finds the files of the repository from the source path of this file, which sits two directories below the repository root.</summary>
internal static class RepositoryFile
{
   /// <summary>Returns the full path of a file of the repository.</summary>
   /// <param name="relativePath">The file's path from the repository root, with forward slashes.</param>
   public static string PathOf(string relativePath) => Path.GetFullPath(Path.Combine(Root(), relativePath));

   private static string Root([CallerFilePath] string source = "") => Path.Combine(Path.GetDirectoryName(source)!, "..", "..");
}
