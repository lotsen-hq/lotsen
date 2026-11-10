using System.Diagnostics;
using System.Text;
using Lotsen.Hosting;

namespace Lotsen.Tests;

/// <summary>Finds and stops the background servers of a test's data directory, which outlive the adapters that started them.</summary>
internal static class BackgroundServerProcesses
{
   /// <summary>How long a test's cleanup retries the delete of its data directory: long enough for a background server that <see cref="KillAll"/> cannot find to end the default grace period, <see cref="AdapterProcess.GracePeriod"/>.</summary>
   /// <remarks>A background server runs in its data directory, which Windows does not delete while the server runs.</remarks>
   public static readonly TimeSpan CleanupWait = AdapterProcess.GracePeriod * 5;

   /// <summary>Returns the process IDs of the background servers that run on the data directory.</summary>
   /// <remarks>
   /// On Linux it finds every <c>lotsen daemon</c> process of this user whose environment names the data directory, including one that lost the claim lock and has not exited yet. Elsewhere it
   /// finds only the server in the state file, and never the test process, which some tests write there as a live process that is no server.
   /// </remarks>
   public static IReadOnlyList<int> Running(DataDirectory dataDirectory)
   {
      if (!OperatingSystem.IsLinux())
      {
         return StateFile.TryRead(dataDirectory) is { } state && state.Pid != Environment.ProcessId && ProcessLiveness.IsAlive(state.Pid) ? [state.Pid] : [];
      }

      var marker = Encoding.UTF8.GetBytes($"{DataDirectory.OverrideVariable}={dataDirectory.Path}\0");
      var found = new List<int>();
      foreach (var directory in Directory.EnumerateDirectories("/proc"))
      {
         if (!int.TryParse(Path.GetFileName(directory), out var pid))
         {
            continue;
         }

         try
         {
            var arguments = File.ReadAllText(Path.Combine(directory, "cmdline")).Split('\0');
            if (arguments.Contains(BackgroundServer.Command) && File.ReadAllBytes(Path.Combine(directory, "environ")).AsSpan().IndexOf(marker) >= 0)
            {
               found.Add(pid);
            }
         }
         catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
         {
         }
      }

      return found;
   }

   /// <summary>Returns the running process with this ID, whose <see cref="Process.HasExited"/> then stays about that process.</summary>
   /// <remarks>
   /// The handle stays valid after the process exits, so <see cref="Process.HasExited"/> answers for this process even when another one gets its ID:
   /// https://learn.microsoft.com/windows/win32/procthread/process-handles-and-identifiers
   /// </remarks>
   public static Process Hold(int pid) => ProcessLiveness.TryGet(pid) ?? throw new InvalidOperationException($"No process with ID {pid} runs.");

   /// <summary>Returns whether a process with this ID runs.</summary>
   public static bool IsRunning(int pid) => ProcessLiveness.IsAlive(pid);

   /// <summary>Waits until the process with this ID has exited, and fails the test when it has not within the timeout.</summary>
   public static async Task WaitForExitAsync(int pid, TimeSpan timeout, Func<string> failure)
   {
      var deadline = DateTime.UtcNow + timeout;
      while (IsRunning(pid))
      {
         Assert.True(DateTime.UtcNow < deadline, failure());
         await Task.Delay(50, TestContext.Current.CancellationToken);
      }
   }

   /// <summary>Kills every background server of the data directory, so no test leaves one running.</summary>
   public static void KillAll(DataDirectory dataDirectory)
   {
      foreach (var pid in Running(dataDirectory))
      {
         try
         {
            using var process = ProcessLiveness.TryGet(pid);
            process?.Kill();
            process?.WaitForExit(TimeSpan.FromSeconds(10));
         }
         catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
         {
         }
      }
   }

   /// <summary>Kills every background server of the data directory, then deletes the temporary directory that holds the data directory, as a test's cleanup.</summary>
   /// <remarks>An I/O failure of the delete leaves the directory behind and does not fail the test.</remarks>
   public static void CleanUp(DataDirectory dataDirectory)
   {
      KillAll(dataDirectory);
      try
      {
         TestDirectories.Delete(Path.GetDirectoryName(dataDirectory.Path)!, CleanupWait);
      }
      catch (IOException)
      {
      }
   }
}
