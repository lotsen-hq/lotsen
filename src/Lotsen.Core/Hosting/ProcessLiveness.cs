using System.Diagnostics;

namespace Lotsen.Hosting;

/// <summary>Checks whether a process runs, by its ID.</summary>
/// <remarks>
/// A process that the OS gave the ID of an exited one passes the check, so a caller that must know the process is a lotsen server asks <see cref="ServerDiscovery"/>, which also probes the
/// server's port.
/// </remarks>
internal static class ProcessLiveness
{
   /// <summary>Returns whether a process with this ID runs.</summary>
   public static bool IsAlive(int pid)
   {
      using var process = TryGet(pid);
      return process is not null;
   }

   /// <summary>Returns the running process with this ID, which the caller disposes.</summary>
   /// <returns>The process, or null when no process with this ID runs.</returns>
   public static Process? TryGet(int pid)
   {
      Process process;
      try
      {
         process = Process.GetProcessById(pid);
      }
      catch (ArgumentException)
      {
         return null;
      }

      if (!process.HasExited)
      {
         return process;
      }

      process.Dispose();
      return null;
   }
}
