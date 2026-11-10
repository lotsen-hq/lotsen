using Lotsen.Processes;

namespace Lotsen.Tests;

/// <summary>Runs the test child program of <c>tests/Lotsen.TestChild</c>, whose behaviors its <c>Program</c> lists.</summary>
/// <remarks>A test project that runs the program references its project, so the program builds first, beside the test project's own build.</remarks>
internal static class TestChild
{
   private const string Name = "Lotsen.TestChild";

   /// <summary>The directory of the program's build: <c>tests/Lotsen.TestChild/bin/&lt;configuration&gt;/&lt;framework&gt;</c>, in the test project's configuration and framework.</summary>
   private static readonly string BuildDirectory = FindBuild();

   /// <summary>The path of the program's own executable in its build.</summary>
   public static string Executable => Path.Combine(BuildDirectory, Name + ExecutableExtension);

   private static string ExecutableExtension => OperatingSystem.IsWindows() ? ".exe" : "";

   /// <summary>Returns the command that runs the program under <c>dotnet</c> with the arguments.</summary>
   public static ProcessCommand Command(TimeSpan timeout, params string[] arguments) => new("dotnet", [Path.Combine(BuildDirectory, Name + ".dll"), .. arguments], timeout);

   /// <summary>Copies the program into the directory as an executable with the name, and returns the executable's path.</summary>
   /// <remarks>The program reads the files of its behavior from the directory it runs from, so each copy gets the directory of its own.</remarks>
   public static string InstallAs(string directory, string name)
   {
      foreach (var file in (string[])[".dll", ".runtimeconfig.json", ".deps.json"])
      {
         File.Copy(Path.Combine(BuildDirectory, Name + file), Path.Combine(directory, Name + file));
      }

      var executable = Path.Combine(directory, name + ExecutableExtension);
      ShellScript.Install(Executable, executable);
      return executable;
   }

   private static string FindBuild()
   {
      var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
      var configuration = output.Parent!;
      var tests = configuration.Parent!.Parent!.Parent!;
      var directory = Path.Combine(tests.FullName, Name, "bin", configuration.Name, output.Name);
      return File.Exists(Path.Combine(directory, Name + ".dll")) ? directory : throw new FileNotFoundException("The test child is not built beside the tests.", directory);
   }
}
