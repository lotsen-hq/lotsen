using System.Globalization;
using Lotsen.AzureDevOps;
using Lotsen.Credentials;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lotsen.Tests;

/// <summary>A copy of the test child program that stands in for the Azure CLI. It records its arguments, then prints and exits with what the test set.</summary>
/// <remarks>The program reads the files that the test sets from the directory of its copy, as its <c>FakeCli</c> method says.</remarks>
internal sealed class FakeAzureCli : IDisposable
{
   private readonly string _root = Directory.CreateTempSubdirectory("lotsen-az-").FullName;

   public FakeAzureCli()
   {
      Executable = TestChild.InstallAs(_root, "az");
      Answer(1, "", "ERROR: Please run 'az login' to setup account.");
   }

   public string Executable { get; }

   /// <summary>The directory that holds the program and every file it reads or writes.</summary>
   public string Root => _root;

   /// <summary>The file that the program prints as its standard output.</summary>
   public string OutputPath => Path.Combine(_root, "stdout");

   /// <summary>The argument list of each run so far, joined by spaces.</summary>
   public IReadOnlyList<string> Runs => File.Exists(ArgumentsLog) ? File.ReadAllLines(ArgumentsLog) : [];

   private string ArgumentsLog => Path.Combine(_root, "arguments.log");

   private string ErrorPath => Path.Combine(_root, "stderr");

   private string ExitCodePath => Path.Combine(_root, "exit-code");

   private string HangPath => Path.Combine(_root, "hang");

   /// <summary>Makes the program print the token as <c>az account get-access-token</c> does.</summary>
   /// <param name="localExpiry">Whether to give the expiry only as <c>expiresOn</c>, in local time, as older versions do.</param>
   public void SignedIn(string token, DateTimeOffset expiresAt, bool localExpiry = false)
   {
      var expiresOn = TimeZoneInfo.ConvertTime(expiresAt, TimeZoneInfo.Local).ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
      var expiresOnUnix = localExpiry ? "" : $"\"expires_on\": {expiresAt.ToUnixTimeSeconds()},";
      Answer(0, $$"""
         {
           "accessToken": "{{token}}",
           "expiresOn": "{{expiresOn}}",
           {{expiresOnUnix}}
           "subscription": "00000000-0000-0000-0000-000000000001",
           "tenant": "00000000-0000-0000-0000-000000000002",
           "tokenType": "Bearer"
         }
         """, "");
   }

   public void Answer(int exitCode, string output, string error)
   {
      File.WriteAllText(OutputPath, output);
      File.WriteAllText(ErrorPath, error);
      File.WriteAllText(ExitCodePath, exitCode.ToString(CultureInfo.InvariantCulture));
   }

   /// <summary>Makes the program wait a minute without an answer.</summary>
   public void Hang() => File.WriteAllText(HangPath, "");

   public AzureCliTokenProvider Provider(TimeProvider clock, ILogger<AzureCliTokenProvider>? logger = null, TimeSpan? timeout = null) =>
      new(
         new AzureCliOptions { Executable = Executable, Timeout = timeout ?? TimeSpan.FromSeconds(30) },
         clock,
         logger ?? NullLogger<AzureCliTokenProvider>.Instance);

   public void Dispose() => TestDirectories.Delete(_root, TimeSpan.FromSeconds(5));
}
