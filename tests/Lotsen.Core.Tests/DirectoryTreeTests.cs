using System.Diagnostics;
using Lotsen.Hosting;

namespace Lotsen.Tests;

/// <summary>Covers the move of a directory whose files another process may still hold open.</summary>
[Trait("Category", "Store")]
public sealed class DirectoryTreeTests : IDisposable
{
   private readonly string _directory = Directory.CreateTempSubdirectory("lotsen-tests-").FullName;

   [Fact]
   public async Task Move_waits_until_no_file_under_the_directory_is_open()
   {
      Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to rename a directory while a file under it is open.");
      var source = Path.Combine(_directory, "clone.tmp");
      var destination = Path.Combine(_directory, "clone");
      var file = Path.Combine(source, "objects", "pack");
      Directory.CreateDirectory(Path.GetDirectoryName(file)!);
      File.WriteAllText(file, "pack");
      await using var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
      Assert.Throws<IOException>(() => Directory.Move(source, destination));

      var closed = Task.Run(async () =>
      {
         await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
         await held.DisposeAsync();
      }, TestContext.Current.CancellationToken);
      DirectoryTree.Move(source, destination);
      await closed;

      Assert.False(Directory.Exists(source));
      Assert.Equal("pack", File.ReadAllText(Path.Combine(destination, "objects", "pack")));
   }

   [Fact]
   public void Move_onto_an_existing_directory_fails_at_once()
   {
      var source = Directory.CreateDirectory(Path.Combine(_directory, "clone.tmp")).FullName;
      var destination = Directory.CreateDirectory(Path.Combine(_directory, "clone")).FullName;
      var elapsed = Stopwatch.StartNew();

      Assert.Throws<IOException>(() => DirectoryTree.Move(source, destination));

      Assert.True(elapsed.Elapsed < DirectoryTree.MoveTimeout, $"The move failed after {elapsed.Elapsed}.");
      Assert.True(Directory.Exists(source));
   }

   public void Dispose() => DirectoryTree.Delete(_directory);
}
