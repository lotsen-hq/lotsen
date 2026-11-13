using System.Collections.Immutable;
using System.Security.Cryptography;
using Lotsen.Credentials;
using Lotsen.GitHub;

namespace Lotsen.Tests;

/// <summary>Covers that the GitHub token stays in memory: the GitHub CLI token provider puts it in no log line, no exception message, no argument list and no file.</summary>
[Trait("Category", "Secrets")]
public sealed class GitHubSecretsTests : IDisposable
{
   // A git transfer's default timeout, the longest validity that lotsen asks.
   private static readonly TimeSpan Validity = TimeSpan.FromMinutes(10);

   private readonly FakeGhCli _gh = new();
   private readonly string _token = "lotsen-test-token-" + Guid.NewGuid().ToString("N");
   private readonly RecordingLoggerProvider _logs = new();

   private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

   [Fact]
   public async Task Provider_keeps_the_token_out_of_logs_arguments_and_files()
   {
      _gh.SignedIn(_token);
      var before = Snapshot();

      var provider = _gh.Provider(_logs.CreateLogger<GhCliTokenProvider>());
      var token = await provider.GetTokenAsync(Validity, Cancellation);
      Assert.Equal(_token, token.Value);
      provider.Reject(token);
      Assert.Equal(_token, (await provider.GetTokenAsync(Validity, Cancellation)).Value);

      var after = Snapshot();
      Assert.Equal(before.Remove("arguments.log"), after.Remove("arguments.log"));
      Assert.All(_gh.Runs, run => Assert.DoesNotContain(_token, run, StringComparison.Ordinal));
      AssertNoTokenInLogs();
   }

   /// <summary>A new provider has no cache, so it asks the GitHub CLI again.</summary>
   [Fact]
   public async Task Provider_keeps_its_cache_in_memory_only()
   {
      _gh.SignedIn(_token);

      await _gh.Provider().GetTokenAsync(Validity, Cancellation);
      await _gh.Provider().GetTokenAsync(Validity, Cancellation);

      Assert.Equal(2, _gh.Runs.Count);
   }

   /// <summary>Each answer holds the token where the provider cannot use it.</summary>
   [Theory]
   [InlineData(0, "TOKEN\nTOKEN\n")]
   [InlineData(0, "TOKEN\r\nmore\n")]
   [InlineData(1, "TOKEN\n")]
   public async Task Provider_failure_on_output_that_holds_the_token_keeps_it_out(int exitCode, string output)
   {
      _gh.Answer(exitCode, output.Replace("TOKEN", _token, StringComparison.Ordinal), "");

      var exception = await Assert.ThrowsAsync<AccessTokenUnavailableException>(() =>
         _gh.Provider(_logs.CreateLogger<GhCliTokenProvider>()).GetTokenAsync(Validity, Cancellation).AsTask());

      Assert.DoesNotContain(_token, exception.ToString(), StringComparison.Ordinal);
      AssertNoTokenInLogs();
   }

   public void Dispose() => _gh.Dispose();

   private void AssertNoTokenInLogs()
   {
      Assert.NotEmpty(_logs.Lines);
      Assert.All(_logs.Lines, line => Assert.DoesNotContain(_token, line, StringComparison.Ordinal));
   }

   /// <summary>Returns the hash of every file under the fake's directory, by its relative path.</summary>
   private ImmutableSortedDictionary<string, string> Snapshot() =>
      Directory.EnumerateFiles(_gh.Root, "*", SearchOption.AllDirectories)
         .ToImmutableSortedDictionary(
            file => Path.GetRelativePath(_gh.Root, file),
            file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))),
            StringComparer.Ordinal);
}
