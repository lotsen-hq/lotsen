namespace Lotsen.Hosting;

/// <summary>
/// Creates directories and files in the data directory that only the user can access.
/// </summary>
/// <remarks>
/// On Windows the directories and files keep the ACL they inherit, which under <c>%LOCALAPPDATA%</c> is user-only.
/// </remarks>
public static class UserOnlyStorage
{
   private const UnixFileMode UserReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;

   /// <summary>Creates the directory when it is missing and restricts it to the user (0700 on Unix).</summary>
   public static void CreateDirectory(string path)
   {
      if (OperatingSystem.IsWindows())
      {
         Directory.CreateDirectory(path);
         return;
      }

      const UnixFileMode userOnly = UserReadWrite | UnixFileMode.UserExecute;
      Directory.CreateDirectory(path, userOnly);
      File.SetUnixFileMode(path, userOnly);
   }

   /// <summary>Replaces the file at this path with what <paramref name="write"/> puts in it, readable by the user only (0600 on Unix).</summary>
   /// <remarks>
   /// <para>
   /// The content goes to a temporary file in the same directory, which is flushed to disk and then renamed over the target. So a reader sees the old file or the new one, and a crash at any point
   /// leaves the old file intact. A crash can leave the temporary file behind, so readers of the directory must skip names that end in <c>.tmp</c>.
   /// </para>
   /// <para>
   /// Readers must open the file with <see cref="FileShare.Delete"/>. On Windows the rename then replaces the file while they read it, as <see cref="WindowsRename"/> describes.
   /// </para>
   /// <para>
   /// A process must write each path one at a time, because the temporary file's name holds only the process ID, so two writes of one path at once would share it.
   /// </para>
   /// </remarks>
   /// <param name="path">The file to replace. Its directory must exist.</param>
   /// <param name="write">Writes the new content to the stream it is given.</param>
   public static void WriteFileAtomically(string path, Action<Stream> write)
   {
      var temporaryPath = $"{path}.{Environment.ProcessId}.tmp";

      // A file left by a crash keeps its own mode, so the temporary file must be new for the 0600 mode to apply.
      File.Delete(temporaryPath);
      var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
      if (!OperatingSystem.IsWindows())
      {
         options.UnixCreateMode = UserReadWrite;
      }

      try
      {
         using (var stream = new FileStream(temporaryPath, options))
         {
            write(stream);
            stream.Flush(flushToDisk: true);
         }

         if (!OperatingSystem.IsWindows() || !WindowsRename.TryReplace(temporaryPath, path))
         {
            File.Move(temporaryPath, path, overwrite: true);
         }
      }
      catch
      {
         File.Delete(temporaryPath);
         throw;
      }
   }
}
