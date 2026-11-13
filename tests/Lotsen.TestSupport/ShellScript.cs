using System.Diagnostics;

namespace Lotsen.Tests;

/// <summary>Writes the shell scripts that git runs for a test, such as a hook, and copies the executables that tests run in place of the Azure CLI, the GitHub CLI or a browser.</summary>
/// <remarks>
/// A child that <c>Process.Start</c> forks on another thread holds a copy of this process's open files until it execs. If the copy includes a write handle to an executable, a run of that
/// executable fails with ETXTBSY ("Text file busy"). So <c>install</c> writes each executable in its own process, and this process never opens one for writing.
/// </remarks>
internal static class ShellScript
{
   /// <summary>Writes the script to the path and makes it executable by the user alone.</summary>
   public static void Write(string path, string text)
   {
      if (OperatingSystem.IsWindows())
      {
         File.WriteAllText(path, text);
         return;
      }

      var source = path + ".source";
      File.WriteAllText(source, text);
      Install(source, path);
      File.Delete(source);
   }

   /// <summary>Copies the executable at the source to the path, executable by the user alone.</summary>
   public static void Install(string source, string path)
   {
      if (OperatingSystem.IsWindows())
      {
         File.Copy(source, path);
         return;
      }

      using var install = Process.Start(new ProcessStartInfo("install", ["-m", "700", source, path]) { RedirectStandardError = true }) ?? throw new InvalidOperationException("install did not start.");
      var error = install.StandardError.ReadToEnd();
      install.WaitForExit();
      if (install.ExitCode != 0)
      {
         throw new InvalidOperationException($"install could not write {path}: {error}");
      }
   }
}
