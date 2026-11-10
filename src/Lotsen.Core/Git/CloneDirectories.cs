using System.Text.RegularExpressions;
using Lotsen.Hosting;

namespace Lotsen.Git;

/// <summary>What a directory in <c>clones/</c> holds.</summary>
internal enum CloneDirectoryKind
{
   /// <summary>A clone, under its clone's name.</summary>
   Clone,

   /// <summary>A temporary clone.</summary>
   Temporary,

   /// <summary>A clone that a prune moved out of the cache and that waits for its delete.</summary>
   Removed,
}

/// <summary>A directory directly in <c>clones/</c> whose name the clone cache gives.</summary>
/// <param name="Path">The directory's absolute path.</param>
/// <param name="CloneName">The name of the clone that the directory belongs to: the first 32 lowercase hexadecimal digits of the SHA-256 of the remote URL.</param>
/// <param name="Kind">What the directory holds.</param>
internal sealed record CloneDirectory(string Path, string CloneName, CloneDirectoryKind Kind);

/// <summary>Provides the filesystem side of the clone cache's prune and maintenance: the directories in <c>clones/</c>, and their deletes.</summary>
internal interface ICloneDirectories
{
   /// <summary>Returns the directories directly in <c>clones/</c> whose names the clone cache gives. It follows no link and returns none.</summary>
   /// <returns>The directories, none when <c>clones/</c> does not exist, or null when <c>clones/</c> is a link, in which the cache acts on nothing.</returns>
   IReadOnlyList<CloneDirectory>? List();

   /// <summary>Deletes the directory and everything under it, without following a link inside it.</summary>
   /// <exception cref="IOException">The directory or a file in it could not be deleted.</exception>
   /// <exception cref="UnauthorizedAccessException">The user may not delete the directory or a file in it.</exception>
   void Remove(CloneDirectory directory);
}

/// <summary>The clone directories in the data directory's <c>clones/</c>.</summary>
internal sealed partial class CloneDirectories : ICloneDirectories
{
   private readonly DataDirectory _dataDirectory;

   public CloneDirectories(DataDirectory dataDirectory)
   {
      _dataDirectory = dataDirectory;
   }

   public IReadOnlyList<CloneDirectory>? List()
   {
      var clones = new DirectoryInfo(_dataDirectory.ClonesPath);
      if (!clones.Exists)
      {
         return [];
      }

      if (clones.LinkTarget is not null)
      {
         return null;
      }

      var directories = new List<CloneDirectory>();
      foreach (var entry in clones.EnumerateDirectories())
      {
         var name = NamePattern().Match(entry.Name);
         if (!name.Success || entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
         {
            continue;
         }

         var kind = name.Groups["kind"].Value switch
         {
            "tmp" => CloneDirectoryKind.Temporary,
            "removed" => CloneDirectoryKind.Removed,
            _ => CloneDirectoryKind.Clone,
         };
         directories.Add(new CloneDirectory(entry.FullName, name.Groups["clone"].Value, kind));
      }

      return directories;
   }

   public void Remove(CloneDirectory directory) => DirectoryTree.Delete(directory.Path);

   // A clone's name, or a temporary or removed clone's: the clone's name, a dot, a process ID or another unique part, and the suffix.
   [GeneratedRegex(@"^(?<clone>[0-9a-f]{32})(?:\.[0-9A-Za-z]+\.(?<kind>tmp|removed))?\z", RegexOptions.CultureInvariant)]
   private static partial Regex NamePattern();
}
