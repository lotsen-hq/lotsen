using Lotsen.Credentials;
using Lotsen.GitHub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lotsen.Tests;

/// <summary>
/// Covers how the GitHub CLI token provider asks <c>gh</c> for the github.com token, keeps it for a reuse window or until GitHub rejects it, and turns each way <c>gh</c> fails into a reason that names the fix.
/// </summary>
[Trait("Category", "GitHub")]
public sealed class GhCliTokenProviderTests : IDisposable
{
   private const string Fix = "Run 'gh auth login', then try again.";

   // A git transfer's default timeout, the longest validity that lotsen asks.
   private static readonly TimeSpan Validity = TimeSpan.FromMinutes(10);

   private readonly FakeGhCli _gh = new();
   private readonly ManualTimeProvider _clock = new();

   private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

   [Fact]
   public async Task Token_comes_from_gh_auth_token_for_github_com()
   {
      _gh.SignedIn("token-one");

      var token = await _gh.Provider().GetTokenAsync(Validity, Cancellation);

      Assert.Equal("token-one", token.Value);
      Assert.Equal(["auth token --hostname github.com"], _gh.Runs);
   }

   [Fact]
   public async Task Token_on_a_windows_line_is_read()
   {
      _gh.SignedIn("token-one", "\r\n");

      var token = await _gh.Provider().GetTokenAsync(Validity, Cancellation);

      Assert.Equal("token-one", token.Value);
   }

   /// <summary>The GitHub CLI reports no expiry, so no validity that a request asks makes the provider run <c>gh</c> again.</summary>
   [Fact]
   public async Task Cached_token_is_reused_whatever_validity_a_request_asks()
   {
      var provider = _gh.Provider(clock: _clock);
      _gh.SignedIn("token-one");

      Assert.Equal("token-one", (await provider.GetTokenAsync(Validity, Cancellation)).Value);
      _gh.SignedIn("token-two");
      Assert.Equal("token-one", (await provider.GetTokenAsync(TimeSpan.FromDays(365), Cancellation)).Value);
      Assert.Equal("token-one", (await provider.GetTokenAsync(TimeSpan.Zero, Cancellation)).Value);
      Assert.Single(_gh.Runs);
   }

   /// <summary>A sign-out or account switch in the GitHub CLI reaches lotsen once the window ends, with no refusal from GitHub.</summary>
   [Fact]
   public async Task Cached_token_is_reused_until_the_reuse_window_ends()
   {
      var provider = _gh.Provider(clock: _clock);
      _gh.SignedIn("token-one");

      Assert.Equal("token-one", (await provider.GetTokenAsync(Validity, Cancellation)).Value);
      _gh.SignedIn("token-two");
      _clock.Advance(GhCliTokenProvider.ReuseWindow - TimeSpan.FromTicks(1));
      Assert.Equal("token-one", (await provider.GetTokenAsync(Validity, Cancellation)).Value);
      _clock.Advance(TimeSpan.FromTicks(1));
      Assert.Equal("token-two", (await provider.GetTokenAsync(Validity, Cancellation)).Value);
      Assert.Equal(2, _gh.Runs.Count);
   }

   /// <summary>A window counts from the run of <c>gh</c> that gave its token, so a request late in the window leaves the next run where it was.</summary>
   [Fact]
   public async Task Reuse_does_not_extend_the_window()
   {
      var provider = _gh.Provider(clock: _clock);
      _gh.SignedIn("token-one");

      await provider.GetTokenAsync(Validity, Cancellation);
      _clock.Advance(GhCliTokenProvider.ReuseWindow - TimeSpan.FromSeconds(1));
      await provider.GetTokenAsync(Validity, Cancellation);
      _gh.SignedIn("token-two");
      _clock.Advance(TimeSpan.FromSeconds(1));

      Assert.Equal("token-two", (await provider.GetTokenAsync(Validity, Cancellation)).Value);
   }

   [Fact]
   public async Task Rejected_token_is_dropped_so_the_next_request_runs_gh_again()
   {
      var provider = _gh.Provider();
      _gh.SignedIn("token-one");
      var rejected = await provider.GetTokenAsync(Validity, Cancellation);
      _gh.SignedIn("token-two");

      provider.Reject(rejected);

      Assert.Equal("token-two", (await provider.GetTokenAsync(Validity, Cancellation)).Value);
      Assert.Equal(2, _gh.Runs.Count);
   }

   /// <summary>A rejection that arrives after a newer token replaced the rejected one leaves the newer token cached.</summary>
   [Fact]
   public async Task Rejecting_a_token_other_than_the_cached_one_keeps_the_cache()
   {
      var provider = _gh.Provider();
      _gh.SignedIn("token-one");
      await provider.GetTokenAsync(Validity, Cancellation);

      provider.Reject(new AccessToken("token-one"));

      Assert.Equal("token-one", (await provider.GetTokenAsync(Validity, Cancellation)).Value);
      Assert.Single(_gh.Runs);
   }

   [Fact]
   public async Task Concurrent_requests_share_one_gh_run()
   {
      var provider = _gh.Provider();
      _gh.SignedIn("token-one");

      var tokens = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => provider.GetTokenAsync(Validity, Cancellation).AsTask()));

      Assert.All(tokens, token => Assert.Equal("token-one", token.Value));
      Assert.Single(_gh.Runs);
   }

   [Fact]
   public async Task Missing_gh_names_its_install_and_gh_auth_login()
   {
      var provider = new GhCliTokenProvider(new GhCliOptions { Executable = Path.Combine(_gh.Root, "missing", "gh") }, TimeProvider.System, NullLogger<GhCliTokenProvider>.Instance);

      var exception = await Assert.ThrowsAsync<AccessTokenUnavailableException>(() => provider.GetTokenAsync(Validity, Cancellation).AsTask());

      Assert.Contains("Install the GitHub CLI", exception.Message, StringComparison.Ordinal);
      Assert.Contains(Fix, exception.Message, StringComparison.Ordinal);
   }

   [Fact]
   public async Task Signed_out_gh_gives_its_reason_and_names_gh_auth_login()
   {
      _gh.Answer(1, "", "no oauth token found for github.com\n");

      var exception = await Assert.ThrowsAsync<AccessTokenUnavailableException>(() => _gh.Provider().GetTokenAsync(Validity, Cancellation).AsTask());

      Assert.Contains(Fix, exception.Message, StringComparison.Ordinal);
      Assert.EndsWith("The GitHub CLI said: no oauth token found for github.com", exception.Message, StringComparison.Ordinal);
   }

   /// <summary>Each answer is one way that <c>gh</c> can exit without a token that lotsen can use.</summary>
   [Theory]
   [InlineData(1, "", "")]
   [InlineData(4, "", "error connecting to the keyring\nsecond line\n")]
   [InlineData(0, "", "")]
   [InlineData(0, " \n", "")]
   [InlineData(0, "token-one\nsecond line\n", "")]
   public async Task Every_failure_names_gh_auth_login(int exitCode, string output, string error)
   {
      _gh.Answer(exitCode, output, error);

      var exception = await Assert.ThrowsAsync<AccessTokenUnavailableException>(() => _gh.Provider().GetTokenAsync(Validity, Cancellation).AsTask());

      Assert.Contains(Fix, exception.Message, StringComparison.Ordinal);
   }

   [Fact]
   public async Task Gh_that_does_not_answer_is_stopped_at_the_timeout()
   {
      _gh.Hang();
      var provider = _gh.Provider(timeout: TimeSpan.FromSeconds(1));

      var exception = await Assert.ThrowsAsync<AccessTokenUnavailableException>(() => provider.GetTokenAsync(Validity, Cancellation).AsTask());

      Assert.Contains("did not answer within 1 seconds", exception.Message, StringComparison.Ordinal);
      Assert.Contains(Fix, exception.Message, StringComparison.Ordinal);
   }

   [Fact]
   public async Task Failure_is_not_cached()
   {
      var provider = _gh.Provider();
      await Assert.ThrowsAsync<AccessTokenUnavailableException>(() => provider.GetTokenAsync(Validity, Cancellation).AsTask());
      _gh.SignedIn("token-one");

      Assert.Equal("token-one", (await provider.GetTokenAsync(Validity, Cancellation)).Value);
      Assert.Equal(2, _gh.Runs.Count);
   }

   public void Dispose() => _gh.Dispose();
}
