using Lotsen.AzureDevOps;
using Lotsen.Credentials;
using Lotsen.Git;
using Lotsen.Pinning;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lotsen.Tests;

/// <summary>Covers how the Azure DevOps platform's git credential answers git with the provider's token, and what a refusal of it does, against an <see cref="AuthenticatingGitServer"/>.</summary>
[Trait("Category", "AzureDevOps")]
public sealed class AzureDevOpsGitCredentialTests : IDisposable
{
   private readonly GitFixture _fixture = new();

   private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

   [Fact]
   public async Task Clone_sends_the_providers_token_as_a_bearer_credential_valid_for_the_transfer()
   {
      _fixture.Commit("Base", ("a.txt", "one\n"));
      var tokens = new FixedAccessTokenProvider("token-one");
      await using var server = await AuthenticatingGitServer.StartAsync(_fixture.OriginPath, "Bearer token-one");
      var options = new GitOptions { TransferTimeout = TimeSpan.FromMinutes(7) };

      await NewCache(options).OpenAsync(TestRemote.Of(server.RepositoryUrl, new AzureDevOpsGitCredential(tokens)), [], FetchPolicy.Sync, Cancellation);

      Assert.True(server.AuthorizedRequests > 0);
      Assert.Equal(options.TransferTimeout, tokens.LastValidity);
   }

   /// <summary>The served reason is what the pin's failure and a file read's refusal show the reviewer and Claude.</summary>
   [Fact]
   public async Task Refused_token_is_rejected_at_the_provider_and_the_reason_says_to_run_az_login()
   {
      _fixture.Commit("Base", ("a.txt", "one\n"));
      var tokens = new FixedAccessTokenProvider("token-one");
      await using var server = await AuthenticatingGitServer.StartAsync(_fixture.OriginPath, "Bearer token-two");

      var exception = await Assert.ThrowsAsync<GitAuthException>(
         () => NewCache(new GitOptions()).OpenAsync(TestRemote.Of(server.RepositoryUrl, new AzureDevOpsGitCredential(tokens)), [], FetchPolicy.Sync, Cancellation));

      Assert.Equal(
         $"Git could not clone {server.RepositoryUrl}: the remote refused lotsen's credential. Run 'az login' with an account that can read the repository, then try again.",
         exception.Message);
      Assert.Equal(["token-one"], tokens.Rejected);
   }

   public void Dispose() => _fixture.Dispose();

   private CloneCache NewCache(GitOptions options) => _fixture.NewCache(options);
}
