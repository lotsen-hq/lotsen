namespace Lotsen.Hosting;

/// <summary>
/// The user's lotsen data directory, which holds the state file, the lock file, the log file, the preference file, the tours and the clones.
/// </summary>
/// <remarks>
/// On Unix the directory is user-only (0700) because the state file in it holds the server's token.
/// </remarks>
internal sealed class DataDirectory
{
   /// <summary>The environment variable that overrides the platform's default location.</summary>
   public const string OverrideVariable = "LOTSEN_DATA_DIR";

   public DataDirectory(string path)
   {
      Path = System.IO.Path.GetFullPath(path);
   }

   /// <summary>The absolute path of the directory.</summary>
   public string Path { get; }

   /// <summary>The absolute path of the file that tells clients how to reach the running server.</summary>
   public string StateFilePath => System.IO.Path.Combine(Path, "server.json");

   /// <summary>The absolute path of the file that the running server holds the claim lock on.</summary>
   public string LockFilePath => System.IO.Path.Combine(Path, "server.lock");

   /// <summary>The absolute path of the background server's log file.</summary>
   public string LogFilePath => System.IO.Path.Combine(Path, "server.log");

   /// <summary>The absolute path of the file that holds the page's preferences.</summary>
   public string PreferencesFilePath => System.IO.Path.Combine(Path, "preferences.json");

   /// <summary>
   /// Returns the data directory for this user: the override variable when set, otherwise <c>%LOCALAPPDATA%\lotsen</c> on Windows and <c>$XDG_DATA_HOME/lotsen</c> (by default
   /// <c>~/.local/share/lotsen</c>) elsewhere.
   /// </summary>
   public static DataDirectory Resolve()
   {
      var overridden = Environment.GetEnvironmentVariable(OverrideVariable);
      if (!string.IsNullOrEmpty(overridden))
      {
         return new DataDirectory(overridden);
      }

      if (OperatingSystem.IsWindows())
      {
         return new DataDirectory(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "lotsen"));
      }

      // The XDG spec says to ignore a relative XDG_DATA_HOME.
      var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
      var dataHome = !string.IsNullOrEmpty(xdgDataHome) && System.IO.Path.IsPathRooted(xdgDataHome)
         ? xdgDataHome
         : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
      return new DataDirectory(System.IO.Path.Combine(dataHome, "lotsen"));
   }

   /// <summary>The absolute path of the directory that holds one file per tour.</summary>
   public string ToursPath => System.IO.Path.Combine(Path, "tours");

   /// <summary>The absolute path of the directory that holds one git clone per repository.</summary>
   public string ClonesPath => System.IO.Path.Combine(Path, "clones");

   /// <summary>Creates the directory when it is missing and restricts it to the user.</summary>
   public void EnsureExists() => UserOnlyStorage.CreateDirectory(Path);

   public override string ToString() => Path;
}
