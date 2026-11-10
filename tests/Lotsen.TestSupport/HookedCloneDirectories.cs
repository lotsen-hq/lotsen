using Lotsen.Git;

namespace Lotsen.Tests;

/// <summary>The real clone directories, with a hook that runs before each delete, so a test can act or cancel while a prune removes clones.</summary>
internal sealed class HookedCloneDirectories : ICloneDirectories
{
   private readonly CloneDirectories _directories;

   public HookedCloneDirectories(GitFixture fixture)
   {
      _directories = new CloneDirectories(fixture.DataDirectory);
   }

   /// <summary>Runs before each delete, with the directory about to be deleted, or null for nothing.</summary>
   public Action<CloneDirectory>? BeforeRemove { get; set; }

   /// <summary>The directories deleted so far, in order.</summary>
   public List<CloneDirectory> Removed { get; } = [];

   public IReadOnlyList<CloneDirectory>? List() => _directories.List();

   public void Remove(CloneDirectory directory)
   {
      BeforeRemove?.Invoke(directory);
      _directories.Remove(directory);
      Removed.Add(directory);
   }
}
