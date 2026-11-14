using System.Globalization;
using Lotsen.GitHub;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lotsen.Tests;

/// <summary>A copy of the test child program that stands in for the GitHub CLI. It records its arguments, then prints and exits with what the test set.</summary>
/// <remarks>The program reads the files that the test sets from the directory of its copy, as its <c>FakeCli</c> method says.</remarks>
internal sealed class FakeGhCli : IDisposable
{
   private readonly string _root = Directory.CreateTempSubdirectory("lotsen-gh-").FullName;

   public FakeGhCli()
   {
      Executable = TestChild.InstallAs(_root, "gh");
      Answer(1, "", "no oauth token found for github.com\n");
   }

   public string Executable { get; }

   /// <summary>The directory that holds the program and every file it reads or writes.</summary>
   public string Root => _root;

   /// <summary>The argument list of each run so far, joined by spaces.</summary>
   public IReadOnlyList<string> Runs => File.Exists(ArgumentsLog) ? File.ReadAllLines(ArgumentsLog) : [];

   private string ArgumentsLog => Path.Combine(_root, "arguments.log");

   private string OutputPath => Path.Combine(_root, "stdout");

   private string ErrorPath => Path.Combine(_root, "stderr");

   private string ExitCodePath => Path.Combine(_root, "exit-code");

   private string HangPath => Path.Combine(_root, "hang");

   /// <summary>Makes the program print the token on a line of its own, as <c>gh auth token</c> does.</summary>
   public void SignedIn(string token, string lineEnd = "\n") => Answer(0, token + lineEnd, "");

   public void Answer(int exitCode, string output, string error)
   {
      File.WriteAllText(OutputPath, output);
      File.WriteAllText(ErrorPath, error);
      File.WriteAllText(ExitCodePath, exitCode.ToString(CultureInfo.InvariantCulture));
   }

   /// <summary>Makes the program wait a minute without an answer.</summary>
   public void Hang() => File.WriteAllText(HangPath, "");

   public GhCliTokenProvider Provider(ILogger<GhCliTokenProvider>? logger = null, TimeSpan? timeout = null, TimeProvider? clock = null) =>
      new(new GhCliOptions { Executable = Executable, Timeout = timeout ?? TimeSpan.FromSeconds(30) }, clock ?? TimeProvider.System, logger ?? NullLogger<GhCliTokenProvider>.Instance);

   public void Dispose() => TestDirectories.Delete(_root, TimeSpan.FromSeconds(5));
}
