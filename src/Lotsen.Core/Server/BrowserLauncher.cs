using System.ComponentModel;
using System.Diagnostics;

namespace Lotsen.Server;

/// <summary>Opens a URL in the user's browser.</summary>
internal interface IBrowserLauncher
{
   /// <summary>Opens the URL in the user's browser.</summary>
   /// <returns>Whether a launcher accepted the URL. False means the user must open the URL by hand.</returns>
   Task<bool> TryLaunchAsync(string url, CancellationToken cancellationToken);
}

/// <summary>Opens URLs with the platform's launcher, or with the command named by the <c>BROWSER</c> environment variable.</summary>
/// <remarks>
/// Under WSL the browser is the Windows one, reached through <c>wslview</c> or Windows' <c>cmd.exe</c>. The launcher looks for <c>cmd.exe</c> on <c>PATH</c>, then under <c>/mnt/c</c> for setups that
/// keep the Windows path off it.
/// </remarks>
internal sealed class SystemBrowserLauncher : IBrowserLauncher
{
   private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(10);

   public async Task<bool> TryLaunchAsync(string url, CancellationToken cancellationToken)
   {
      if (OperatingSystem.IsWindows() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BROWSER")))
      {
         return TryShellExecute(url);
      }

      foreach (var (fileName, arguments) in Candidates(url, Environment.GetEnvironmentVariable("BROWSER"), IsWsl()))
      {
         if (await TryRunAsync(fileName, arguments, cancellationToken))
         {
            return true;
         }
      }

      return false;
   }

   /// <summary>Returns whether this process runs in Linux under WSL.</summary>
   public static bool IsWsl() => OperatingSystem.IsLinux() && (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WSL_DISTRO_NAME")) || File.Exists("/proc/sys/fs/binfmt_misc/WSLInterop"));

   /// <summary>Returns the commands that open the URL, in the order to try them.</summary>
   /// <param name="url">The URL to open.</param>
   /// <param name="browser">The value of the <c>BROWSER</c> environment variable, or null when it is not set.</param>
   /// <param name="wsl">Whether this process runs in Linux under WSL.</param>
   internal static IEnumerable<(string FileName, string[] Arguments)> Candidates(string url, string? browser, bool wsl)
   {
      if (!string.IsNullOrEmpty(browser))
      {
         yield return (browser, [url]);
         yield break;
      }

      if (wsl)
      {
         yield return ("wslview", [url]);

         // cmd.exe parses its command line again, so it gets only a URL whose one metacharacter is &, which a caret escapes.
         if (IsSafeForCmd(url))
         {
            var escaped = url.Replace("&", "^&", StringComparison.Ordinal);
            yield return ("cmd.exe", ["/c", "start", escaped]);
            yield return ("/mnt/c/Windows/System32/cmd.exe", ["/c", "start", escaped]);
         }
      }

      yield return ("xdg-open", [url]);
   }

   private static bool IsSafeForCmd(string url) => url.All(c => char.IsAsciiLetterOrDigit(c) || c is ':' or '/' or '.' or '#' or '=' or '-' or '_' or '&');

   private static async Task<bool> TryRunAsync(string fileName, string[] arguments, CancellationToken cancellationToken)
   {
      var startInfo = new ProcessStartInfo(fileName)
      {
         UseShellExecute = false,
      };
      foreach (var argument in arguments)
      {
         startInfo.ArgumentList.Add(argument);
      }

      // cmd.exe warns about a Linux working directory, so start Windows tools from a Windows drive.
      if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && Directory.Exists("/mnt/c"))
      {
         startInfo.WorkingDirectory = "/mnt/c";
      }

      Process process;
      try
      {
         process = Process.Start(startInfo) ?? throw new Win32Exception();
      }
      catch (Win32Exception)
      {
         return false;
      }

      using (process)
      {
         using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
         timeout.CancelAfter(ExitTimeout);
         try
         {
            await process.WaitForExitAsync(timeout.Token);
         }
         catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
         {
            // A launcher that is still running after the timeout has most likely become the browser.
            return true;
         }

         return process.ExitCode == 0;
      }
   }

   private static bool TryShellExecute(string url)
   {
      try
      {
         using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
         return true;
      }
      catch (Win32Exception)
      {
         return false;
      }
   }
}
