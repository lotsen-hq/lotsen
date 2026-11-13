using System.Collections.Immutable;
using System.Security.Cryptography;
using Lotsen.Credentials;
using Lotsen.Git;
using Lotsen.GitHub;
using Lotsen.Hosting;
using Lotsen.Platforms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lotsen.Tests;

/// <summary>
/// Covers that the GitHub token stays in memory: the GitHub CLI token provider and the platform put it in no log line, no exception message, no argument list, no file and no iteration record.
/// </summary>
[Trait("Category", "Secrets")]
public sealed class GitHubSecretsTests : IDisposable
{
   // A git transfer's default timeout, the longest validity that lotsen asks.
   private static readonly TimeSpan Validity = TimeSpan.FromMinutes(10);

   private readonly FakeGhCli _gh = new();
   private readonly string _token = "lotsen-test-token-" + Guid.NewGuid().ToString("N");
   private readonly RecordingLoggerProvider _logs = new();

   private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

   /// <summary>The fake GitHub repeats the token in every refusal and in a malformed answer, and a refused token makes the provider run the GitHub CLI again.</summary>
   [Fact]
   public async Task Platform_keeps_the_token_out_of_logs_exception_messages_and_iteration_records()
   {
      _gh.SignedIn(_token);
      var dataDirectory = new DataDirectory(Path.Combine(_gh.Root, "data"));
      await using var gitHub = new FakeGitHub();
      await using var services = new ServiceCollection()
         .AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(_logs))
         .AddSingleton(dataDirectory)
         .BuildServiceProvider();
      var factory = GitHubPlatform.Factory(new GitHubOptions { ApiBaseUrl = gitHub.Server.BaseUrl }, new GhCliOptions { Executable = _gh.Executable }, accessTokens: null);
      using var platform = Assert.IsType<GitHubPlatform>(factory(services));
      var url = new Uri(FakeGitHub.PrUrl);
      gitHub.PullRequest(CommitId.Parse(new string('1', 40)), CommitId.Parse(new string('a', 40)));
      await platform.ResolveAsync(url, Cancellation);
      gitHub.PullRequest(CommitId.Parse(new string('2', 40)), CommitId.Parse(new string('a', 40)));
      await platform.ResolveAsync(url, Cancellation);
      List<Exception> thrown = [];

      foreach (var status in new[] { 401, 403, 404, 422, 500 })
      {
         gitHub.Refuse(status, $"The token {_token} is not valid.");
         thrown.Add(await Assert.ThrowsAsync<PlatformException>(() => platform.ResolveAsync(url, Cancellation)));
      }

      gitHub.Respond(new HttpAnswer(200, $$"""{ "state": "{{_token}}" }"""));
      thrown.Add(await Assert.ThrowsAsync<PlatformException>(() => platform.ResolveAsync(url, Cancellation)));

      Assert.Contains(gitHub.Server.Requests, request => request.Contains($"Bearer {_token}", StringComparison.Ordinal));
      Assert.All(thrown, exception => Assert.DoesNotContain(_token, exception.ToString(), StringComparison.Ordinal));
      var files = Directory.GetFiles(dataDirectory.Path, "*", SearchOption.AllDirectories);
      Assert.NotEmpty(files);
      Assert.All(files, file => Assert.DoesNotContain(_token, File.ReadAllText(file), StringComparison.Ordinal));
      AssertNoTokenInLogs();
   }

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
