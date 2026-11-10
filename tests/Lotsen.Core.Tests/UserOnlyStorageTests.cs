using Lotsen.Hosting;

namespace Lotsen.Tests;

/// <summary>Covers the atomic replacement of a user-only file.</summary>
[Trait("Category", "Store")]
public sealed class UserOnlyStorageTests : IDisposable
{
   private readonly string _directory = Directory.CreateTempSubdirectory("lotsen-tests-").FullName;

   [Fact]
   public void Write_replaces_the_file()
   {
      var path = Path.Combine(_directory, "file.json");
      File.WriteAllText(path, "old");

      UserOnlyStorage.WriteFileAtomically(path, stream => stream.Write("new"u8));

      Assert.Equal("new", File.ReadAllText(path));
      Assert.Single(Directory.GetFiles(_directory));
   }

   /// <summary>The reader shares delete, as the readers of the tours and the state file do, and keeps reading the file it opened.</summary>
   [Fact]
   public void Write_replaces_a_file_that_a_reader_holds_open()
   {
      var path = Path.Combine(_directory, "file.json");
      File.WriteAllText(path, "old");
      using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));

      UserOnlyStorage.WriteFileAtomically(path, stream => stream.Write("new"u8));

      Assert.Equal("new", File.ReadAllText(path));
      Assert.Equal("old", reader.ReadToEnd());
      Assert.Single(Directory.GetFiles(_directory));
   }

   [Fact]
   public void Write_that_fails_part_way_leaves_the_previous_file_intact()
   {
      var path = Path.Combine(_directory, "file.json");
      File.WriteAllText(path, "old");

      Assert.Throws<IOException>(() => UserOnlyStorage.WriteFileAtomically(path, stream =>
      {
         stream.Write("partial new cont"u8);
         stream.Flush();
         throw new IOException("Simulated failure mid-write.");
      }));

      Assert.Equal("old", File.ReadAllText(path));
      Assert.Single(Directory.GetFiles(_directory));
   }

   [Fact]
   public void Written_file_is_readable_by_the_user_only()
   {
      if (OperatingSystem.IsWindows())
      {
         Assert.Skip("Unix file modes do not apply on Windows.");
         return;
      }

      var path = Path.Combine(_directory, "file.json");

      UserOnlyStorage.WriteFileAtomically(path, stream => stream.Write("new"u8));

      Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
   }

   /// <summary>A crash can leave the temporary file behind with any mode.</summary>
   [Fact]
   public void Leftover_temporary_file_does_not_loosen_the_written_file()
   {
      if (OperatingSystem.IsWindows())
      {
         Assert.Skip("Unix file modes do not apply on Windows.");
         return;
      }

      var path = Path.Combine(_directory, "file.json");
      var leftover = $"{path}.{Environment.ProcessId}.tmp";
      File.WriteAllText(leftover, "partial");
      File.SetUnixFileMode(leftover, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);

      UserOnlyStorage.WriteFileAtomically(path, stream => stream.Write("new"u8));

      Assert.Equal("new", File.ReadAllText(path));
      Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
   }

   [Fact]
   public void Created_directory_is_accessible_by_the_user_only()
   {
      if (OperatingSystem.IsWindows())
      {
         Assert.Skip("Unix file modes do not apply on Windows.");
         return;
      }

      var path = Path.Combine(_directory, "nested", "child");

      UserOnlyStorage.CreateDirectory(path);

      Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(path));
   }

   public void Dispose() => Directory.Delete(_directory, recursive: true);
}
