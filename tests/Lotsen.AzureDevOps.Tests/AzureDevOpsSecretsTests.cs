using System.Collections.Immutable;
using System.Security.Cryptography;
using Lotsen.AzureDevOps;
using Lotsen.Credentials;
using Lotsen.Tours;

namespace Lotsen.Tests;

/// <summary>
/// Covers that the Azure DevOps token stays in memory: the Azure CLI token provider and the Azure DevOps client put it in no log line, no exception message, no argument list and no file.
/// </summary>
[Trait("Category", "Secrets")]
public sealed class AzureDevOpsSecretsTests : IDisposable
{
   // A git transfer's default timeout, the longest validity that lotsen asks.
   private static readonly TimeSpan Validity = TimeSpan.FromMinutes(10);

   private readonly FakeAzureCli _az = new();
   private readonly ManualTimeProvider _clock = new();
   private readonly string _token = "lotsen-test-token-" + Guid.NewGuid().ToString("N");
   private readonly RecordingLoggerProvider _logs = new();

   [Fact]
   public async Task Provider_keeps_the_token_out_of_logs_arguments_and_files()
   {
      _az.SignedIn(_token, _clock.GetUtcNow().AddHours(1));
      var before = Snapshot();
      var token = TestContext.Current.CancellationToken;

      var provider = _az.Provider(_clock, _logs.CreateLogger<AzureCliTokenProvider>());
      Assert.Equal(_token, (await provider.GetTokenAsync(Validity, token)).Value);
      Assert.Equal(_token, (await provider.GetTokenAsync(Validity, token)).Value);

      var after = Snapshot();
      Assert.Equal(before.Remove("arguments.log"), after.Remove("arguments.log"));
      Assert.All(_az.Runs, run => Assert.DoesNotContain(_token, run, StringComparison.Ordinal));
      AssertNoTokenInLogs();
   }

   /// <summary>A new provider has no cache, so it asks the Azure CLI again.</summary>
   [Fact]
   public async Task Provider_keeps_its_cache_in_memory_only()
   {
      _az.SignedIn(_token, _clock.GetUtcNow().AddHours(1));
      var token = TestContext.Current.CancellationToken;

      await _az.Provider(_clock).GetTokenAsync(Validity, token);
      await _az.Provider(_clock).GetTokenAsync(Validity, token);

      Assert.Equal(2, _az.Runs.Count);
   }

   [Theory]
   [InlineData("""{"accessToken": "TOKEN", "expiresOn": 12}""")]
   [InlineData("""{"accessToken": "TOKEN", "expiresOn": "not a time"}""")]
   [InlineData("""{"accessToken": "TOKEN", "expires_on": 1}""")]
   [InlineData("""{"accessToken": "TOKEN" """)]
   public async Task Provider_failure_on_output_that_holds_the_token_keeps_it_out(string output)
   {
      _az.Answer(0, output.Replace("TOKEN", _token, StringComparison.Ordinal), "");

      var exception = await Assert.ThrowsAsync<AccessTokenUnavailableException>(() =>
         _az.Provider(_clock, _logs.CreateLogger<AzureCliTokenProvider>())
            .GetTokenAsync(Validity, TestContext.Current.CancellationToken).AsTask());

      Assert.DoesNotContain(_token, exception.ToString(), StringComparison.Ordinal);
      AssertNoTokenInLogs();
   }

   /// <summary>The fake Azure DevOps server repeats the token in every error message it sends.</summary>
   [Theory]
   [InlineData(401)]
   [InlineData(403)]
   [InlineData(404)]
   [InlineData(500)]
   public async Task Client_keeps_the_token_out_of_logs_and_exception_messages(int status)
   {
      await using var server = new RecordingHttpServer(answer: _ => new HttpAnswer(status, $$"""{"message": "The token {{_token}} is not valid."}"""));
      using var client = new AzureDevOpsClient(new FixedAccessTokenProvider(_token), new AzureDevOpsOptions { ServerOverride = server.BaseUrl }, _logs.CreateLogger<AzureDevOpsClient>());
      Assert.True(AzureDevOpsPullRequest.TryParse(AzureDevOpsClientTests.DevAzureUrl, out var pullRequest));

      var pullRequestFailure = await Assert.ThrowsAsync<AzureDevOpsException>(() => client.GetPullRequestAsync(pullRequest, TestContext.Current.CancellationToken));
      var iterationsFailure = await Assert.ThrowsAsync<AzureDevOpsException>(() => client.GetIterationsAsync(pullRequest, TestContext.Current.CancellationToken));

      Assert.All(server.Requests, request => Assert.Contains($"Authorization: Bearer {_token}\r\n", request, StringComparison.Ordinal));
      Assert.DoesNotContain(_token, pullRequestFailure.ToString(), StringComparison.Ordinal);
      Assert.DoesNotContain(_token, iterationsFailure.ToString(), StringComparison.Ordinal);
      AssertNoTokenInLogs();
   }

   public void Dispose() => _az.Dispose();

   private void AssertNoTokenInLogs()
   {
      Assert.NotEmpty(_logs.Lines);
      Assert.All(_logs.Lines, line => Assert.DoesNotContain(_token, line, StringComparison.Ordinal));
   }

   /// <summary>Returns the hash of every file under the fake's directory, by its relative path.</summary>
   private ImmutableSortedDictionary<string, string> Snapshot() =>
      Directory.EnumerateFiles(_az.Root, "*", SearchOption.AllDirectories)
         .ToImmutableSortedDictionary(
            file => Path.GetRelativePath(_az.Root, file),
            file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))),
            StringComparer.Ordinal);
}
