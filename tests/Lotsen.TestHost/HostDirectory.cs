using Lotsen.Tests;

namespace Lotsen.TestHost;

/// <summary>The directory of one run of the test host, <c>lotsen-host-*</c> in the temporary directory, with the owner lock that shows the run is alive.</summary>
/// <remarks>
/// The owner lock is an OS lock on <c>owner.lock</c> in the directory, which the run holds until it removes the directory. The OS releases the lock when the run's process ends, even by
/// SIGKILL, so a host that starts removes each host directory whose owner lock it can take. A directory without the lock file is left alone, because its host may not have taken the lock yet.
/// </remarks>
internal sealed class HostDirectory : IDisposable
{
   private const string Prefix = "lotsen-host-";
   private const string LockName = "owner.lock";
   private const int Attempts = 3;
   private static readonly TimeSpan DeleteWait = TimeSpan.FromSeconds(5);

   private readonly FileStream _ownerLock;

   private HostDirectory(string path, FileStream ownerLock)
   {
      Path = path;
      _ownerLock = ownerLock;
   }

   /// <summary>The directory's full path.</summary>
   public string Path { get; }

   /// <summary>Creates this run's directory and takes its owner lock, then removes the directories of hosts that ended without removing their own.</summary>
   public static HostDirectory Create()
   {
      var directory = CreateOwn();
      try
      {
         if (LocksExclude(directory.Path))
         {
            RemoveStale(System.IO.Path.GetTempPath());
         }

         return directory;
      }
      catch
      {
         directory.Dispose();
         throw;
      }
   }

   /// <summary>Removes the directory, and releases the owner lock once only the lock file is left.</summary>
   public void Dispose() => Remove(Path, _ownerLock);

   private static HostDirectory CreateOwn()
   {
      for (var attempt = 1; ; attempt++)
      {
         var path = Directory.CreateTempSubdirectory(Prefix).FullName;
         try
         {
            var ownerLock = OpenLock(path, FileMode.CreateNew);

            // On Unix .NET creates the file before it locks it, so a sweep can lock and delete the new file in between. This run's lock then holds a deleted file (Remove):
            // https://github.com/dotnet/runtime/blob/cbefd14ed7ded7ebaf37e3fe053c8cfc87359a4a/src/libraries/System.Private.CoreLib/src/Microsoft/Win32/SafeHandles/SafeFileHandle.Unix.cs#L416-L417
            if (File.Exists(LockPath(path)))
            {
               return new HostDirectory(path, ownerLock);
            }

            ownerLock.Dispose();
            throw new IOException($"Another test host removed {path} while this host created it.");
         }
         catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
         {
            RemoveQuietly(path);
            if (attempt == Attempts)
            {
               throw;
            }
         }
      }
   }

   // .NET takes no OS lock when its file locking is off. Every open of a lock file then succeeds, so no lock shows whether its host runs, and the sweep removes nothing.
   private static bool LocksExclude(string ownPath)
   {
      try
      {
         using var probe = OpenLock(ownPath, FileMode.Open);
         return false;
      }
      // A held lock fails the open with a plain IOException, as ClaimLock.TryTake relies on.
      catch (IOException exception) when (exception.GetType() == typeof(IOException))
      {
         return true;
      }
   }

   private static void RemoveStale(string temp)
   {
      foreach (var directory in Directory.EnumerateDirectories(temp, $"{Prefix}*"))
      {
         // A link with a host directory's name can point at any directory, so the sweep never follows one.
         if (new DirectoryInfo(directory).LinkTarget is not null)
         {
            continue;
         }

         FileStream ownerLock;
         try
         {
            ownerLock = OpenLock(directory, FileMode.Open);
         }
         // The lock is held, the lock file is missing, or the directory is another user's.
         catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
         {
            continue;
         }

         try
         {
            Remove(directory, ownerLock);
         }
         // A stale directory that another host removes at the same time is no reason to fail this start.
         catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
         {
         }
      }
   }

   // On Unix the lock file goes while the lock is held, so a host whose new lock follows this release finds its file gone (CreateOwn). Windows refuses to delete a file that a process holds
   // open, so there the lock file goes after the release. Windows creates and locks a file in one step, so no new host can lock the file between the two.
   private static void Remove(string directory, FileStream ownerLock)
   {
      using (ownerLock)
      {
         foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Where(entry => System.IO.Path.GetFileName(entry) != LockName))
         {
            if (Directory.Exists(entry))
            {
               TestDirectories.Delete(entry, DeleteWait);
            }
            else
            {
               File.Delete(entry);
            }
         }

         if (!OperatingSystem.IsWindows())
         {
            File.Delete(LockPath(directory));
         }
      }

      TestDirectories.Delete(directory, DeleteWait);
   }

   // A failed attempt's directory can lack a lock file, and no sweep removes such a directory, so the attempt removes its own.
   private static void RemoveQuietly(string path)
   {
      try
      {
         TestDirectories.Delete(path, DeleteWait);
      }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
      {
      }
   }

   private static string LockPath(string directory) => System.IO.Path.Combine(directory, LockName);

   // FileShare.None takes an OS lock that excludes every other open with FileShare.None, as the claim lock does (ClaimLock.TryTake).
   private static FileStream OpenLock(string directory, FileMode mode) => new(LockPath(directory), new FileStreamOptions { Mode = mode, Access = FileAccess.ReadWrite, Share = FileShare.None });
}
