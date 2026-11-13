using System.Text;
using Lotsen.GitHub;
using Lotsen.Platforms;

namespace Lotsen.Tests;

/// <summary>Covers how the GitHub platform's git credential answers git with the provider's token, and what a refusal of it does, against an <see cref="AuthenticatingGitServer"/>.</summary>
[Trait("Category", "GitHub")]
public sealed class GitHubGitCredentialTests : IDisposable
{
   private readonly GitFixture _fixture = new();

   private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

   [Fact]
   public async Task Clone_sends_the_providers_token_as_the_basic_password_of_x_access_token_valid_for_the_transfer()
   {
      _fixture.Commit("Base", ("a.txt", "one\n"));
      var tokens = new FixedAccessTokenProvider("token-one");
      await using var server = await AuthenticatingGitServer.StartAsync(_fixture.OriginPath, BasicHeader("token-one"));
      var transferTimeout = TimeSpan.FromMinutes(7);

      await _fixture.CloneAsync(server.RepositoryUrl, new GitHubGitCredential(tokens), transferTimeout, Cancellation);

      Assert.True(server.AuthorizedRequests > 0);
      Assert.Equal(transferTimeout, tokens.LastValidity);
   }

   /// <summary>The served reason is what the pin's failure and a file read's refusal show the reviewer and Claude.</summary>
   [Fact]
   public async Task Refused_token_is_rejected_at_the_provider_and_the_reason_says_to_run_gh_auth_login()
   {
      _fixture.Commit("Base", ("a.txt", "one\n"));
      var tokens = new FixedAccessTokenProvider("token-one");
      await using var server = await AuthenticatingGitServer.StartAsync(_fixture.OriginPath, BasicHeader("token-two"));

      var exception = await Assert.ThrowsAnyAsync<ExternalFailureException>(
         () => _fixture.CloneAsync(server.RepositoryUrl, new GitHubGitCredential(tokens), TimeSpan.FromMinutes(1), Cancellation));

      Assert.Equal(
         $"Git could not clone {server.RepositoryUrl}: the remote refused lotsen's credential. Run 'gh auth login' with an account that can read the repository, then try again.",
         exception.Message);
      Assert.Equal(["token-one"], tokens.Rejected);
      Assert.DoesNotContain("token-one", exception.ToString(), StringComparison.Ordinal);
      Assert.DoesNotContain(BasicHeader("token-one")["Basic ".Length..], exception.ToString(), StringComparison.Ordinal);
   }

   [Fact]
   public async Task Platform_presents_the_gh_token_to_git()
   {
      using var platform = FakeGitHub.Offline();

      var answer = await platform.GitCredential.AnswerAsync(TimeSpan.FromMinutes(1), Cancellation);

      Assert.Equal("Basic", answer.Scheme);
      Assert.Equal(BasicHeader("token-one"), $"{answer.Scheme} {answer.Credential}");
      Assert.Contains("gh auth login", platform.GitCredential.FixHint, StringComparison.Ordinal);
   }

   public void Dispose() => _fixture.Dispose();

   private static string BasicHeader(string token) => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token}"));
}
