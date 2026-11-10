using System.Diagnostics;

namespace Lotsen.Hosting;

/// <summary>Moves and deletes directory trees that may hold git's files.</summary>
internal static class DirectoryTree
{
   /// <summary>How long <see cref="Move"/> tries again on Windows.</summary>
   public static readonly TimeSpan MoveTimeout = TimeSpan.FromSeconds(5);

   private static readonly TimeSpan MoveInterval = TimeSpan.FromMilliseconds(10);

   /// <summary>Moves the directory to a path that does not exist.</summary>
   /// <remarks>
   /// Windows refuses to rename a directory while any process holds a file under it open, and a clone's files can stay held for a moment after its git has exited. So on Windows the move
   /// runs again until it succeeds or <see cref="MoveTimeout"/> passes, and then the last failure propagates.
   /// </remarks>
   public static void Move(string source, string destination)
   {
      var elapsed = Stopwatch.StartNew();
      while (true)
      {
         try
         {
            Directory.Move(source, destination);
            return;
         }
         catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && OperatingSystem.IsWindows() && !Directory.Exists(destination) && elapsed.Elapsed < MoveTimeout)
         {
            Thread.Sleep(MoveInterval);
         }
      }
   }

   /// <summary>Deletes the directory and everything under it, if the directory exists.</summary>
   /// <remarks>Git writes its pack files read-only, which stops a recursive delete on Windows, so the files lose that attribute first.</remarks>
   public static void Delete(string path)
   {
      if (!Directory.Exists(path))
      {
         return;
      }

      if (OperatingSystem.IsWindows())
      {
         foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
         {
            File.SetAttributes(file, FileAttributes.Normal);
         }
      }

      Directory.Delete(path, recursive: true);
   }
}
