namespace Lotsen.Hosting;

/// <summary>
/// The claim lock: an OS lock on the lock file in the data directory, which a server holds from before it binds until it exits, so one server at a time writes the tours and the clones.
/// </summary>
/// <remarks>
/// The OS releases the lock when the holder's process ends, even by a crash, as <c>LifecycleTests</c> pins. The lock file stays in place, because a lock file deleted while another process
/// opens it can leave two processes with a lock each, on two different files.
/// </remarks>
internal sealed class ClaimLock : IDisposable
{
   private readonly DataDirectory _dataDirectory;
   private readonly FileStream _file;

   private ClaimLock(DataDirectory dataDirectory, FileStream file)
   {
      _dataDirectory = dataDirectory;
      _file = file;
   }

   /// <summary>Takes the claim lock of the data directory, creating the directory and the lock file when they are missing.</summary>
   /// <returns>The held lock, or null when another holder has it.</returns>
   public static ClaimLock? TryTake(DataDirectory dataDirectory)
   {
      dataDirectory.EnsureExists();

      // FileShare.None makes the open exclusive across processes and within one, as ClaimLockTests and LifecycleTests pin.
      var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
      if (!OperatingSystem.IsWindows())
      {
         options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
      }

      try
      {
         return new ClaimLock(dataDirectory, new FileStream(dataDirectory.LockFilePath, options));
      }
      catch (IOException exception) when (exception.GetType() == typeof(IOException))
      {
         // A held lock fails the open with a plain IOException. Its subclasses name other causes, such as a missing directory.
         return null;
      }
   }

   /// <summary>Returns whether a holder has the claim lock of the data directory.</summary>
   /// <remarks>
   /// The probe takes the lock for a moment, so a server that starts in that moment finds it held and tries again, as <see cref="Server.LotsenServer.ClaimWindow"/> says. A data directory
   /// without its lock file has had no server, so the probe then creates nothing.
   /// </remarks>
   /// <param name="dataDirectory">The data directory whose lock to probe.</param>
   /// <param name="whileProbed">Called while the probe holds the lock, or null. Tests start a server in that moment with it.</param>
   public static bool IsHeld(DataDirectory dataDirectory, Action? whileProbed = null)
   {
      if (!File.Exists(dataDirectory.LockFilePath))
      {
         return false;
      }

      using var probe = TryTake(dataDirectory);
      if (probe is not null)
      {
         whileProbed?.Invoke();
      }

      return probe is null;
   }

   /// <summary>Deletes the state file that a crashed server left, if there is one. Call it after the lock is taken and before the holder writes its own.</summary>
   public void DeleteStaleStateFile() => File.Delete(_dataDirectory.StateFilePath);

   /// <summary>Deletes the state file if this process wrote it.</summary>
   /// <remarks>.NET takes no OS lock when its file locking is off, so another server can then run and own the state file, as <c>LifecycleTests</c> pins.</remarks>
   public void DeleteOwnStateFile()
   {
      if (StateFile.TryRead(_dataDirectory)?.Pid == Environment.ProcessId)
      {
         File.Delete(_dataDirectory.StateFilePath);
      }
   }

   /// <summary>Releases the lock.</summary>
   public void Dispose() => _file.Dispose();
}
