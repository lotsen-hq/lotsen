using System.Diagnostics;
using Lotsen.Hosting;

namespace Lotsen.Tests;

/// <summary>Deletes and moves the directories of tests whose processes may still run in them.</summary>
internal static class TestDirectories
{
   private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(10);

   /// <summary>Deletes the directory and everything under it, git's read-only files included, if the directory exists.</summary>
   /// <remarks>
   /// Windows refuses the delete while a process holds a file in the directory or runs in it, which an exited or killed process can do for some milliseconds and a background server for its
   /// grace period. So the delete runs again until it succeeds or the timeout passes, and then the last failure propagates.
   /// </remarks>
   public static void Delete(string path, TimeSpan timeout) => Retry(() => DirectoryTree.Delete(path), timeout);

   /// <summary>Moves the directory to the destination, which must not exist, retrying as <see cref="Delete"/> does while a process holds a file in it.</summary>
   public static void Move(string source, string destination, TimeSpan timeout) => Retry(() => Directory.Move(source, destination), timeout);

   private static void Retry(Action action, TimeSpan timeout)
   {
      var elapsed = Stopwatch.StartNew();
      while (true)
      {
         try
         {
            action();
            return;
         }
         catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && elapsed.Elapsed < timeout)
         {
            Thread.Sleep(Interval);
         }
      }
   }
}
