using System.Text;
using Lotsen.AzureDevOps;
using Lotsen.Credentials;

namespace Lotsen.Tests;

/// <summary>
/// Covers how the Azure CLI token provider asks <c>az</c> for an Azure DevOps token, keeps it while it stays valid for as long as each request asks, and turns each way <c>az</c> fails into a reason that names the fix.
/// </summary>
[Trait("Category", "AzureDevOps")]
public sealed class AzureCliTokenProviderTests : IDisposable
{
   // A git transfer's default timeout, the longest validity that lotsen asks.
   private static readonly TimeSpan Validity = TimeSpan.FromMinutes(10);

   private readonly FakeAzureCli _az = new();
   private readonly ManualTimeProvider _clock = new();

   [Fact]
   public async Task Token_comes_from_the_azure_cli_for_the_azure_devops_resource()
   {
      _az.SignedIn("token-one", _clock.GetUtcNow().AddHours(1));

      var token = await _az.Provider(_clock).GetTokenAsync(Validity, TestContext.Current.CancellationToken);

      Assert.Equal("token-one", token.Value);
      Assert.Equal(["account get-access-token --resource 499b84ac-1321-427f-aa17-267ca6975798 --output json --only-show-errors"], _az.Runs);
   }

   /// <summary>A request for ten minutes of validity refreshes a token ten minutes before it expires, so 50 minutes is the last reuse.</summary>
   [Fact]
   public async Task Cached_token_is_reused_until_it_is_close_to_expiry()
   {
      var provider = _az.Provider(_clock);
      var token = TestContext.Current.CancellationToken;
      _az.SignedIn("token-one", _clock.GetUtcNow().AddHours(1));

      Assert.Equal("token-one", (await provider.GetTokenAsync(Validity, token)).Value);
      _az.SignedIn("token-two", _clock.GetUtcNow().AddHours(2));
      _clock.Advance(TimeSpan.FromMinutes(49));
      Assert.Equal("token-one", (await provider.GetTokenAsync(Validity, token)).Value);
      Assert.Single(_az.Runs);

      _clock.Advance(TimeSpan.FromMinutes(2));
      Assert.Equal("token-two", (await provider.GetTokenAsync(Validity, token)).Value);
      Assert.Equal(2, _az.Runs.Count);
   }

   /// <summary>A git transfer asks for its whole timeout, so a token that would expire during the transfer is replaced first, while a short request still reuses it.</summary>
   [Fact]
   public async Task Cached_token_is_reused_only_by_requests_that_it_outlives()
   {
      var provider = _az.Provider(_clock);
      var token = TestContext.Current.CancellationToken;
      _az.SignedIn("token-one", _clock.GetUtcNow().AddHours(1));
      await provider.GetTokenAsync(TimeSpan.FromSeconds(30), token);
      _az.SignedIn("token-two", _clock.GetUtcNow().AddHours(2));
      _clock.Advance(TimeSpan.FromMinutes(55));

      Assert.Equal("token-one", (await provider.GetTokenAsync(TimeSpan.FromMinutes(2), token)).Value);
      Assert.Equal("token-two", (await provider.GetTokenAsync(TimeSpan.FromMinutes(10), token)).Value);
      Assert.Equal(2, _az.Runs.Count);
   }

   [Fact]
   public async Task Rejected_token_is_dropped_so_the_next_request_runs_the_azure_cli_again()
   {
      var provider = _az.Provider(_clock);
      var token = TestContext.Current.CancellationToken;
      _az.SignedIn("token-one", _clock.GetUtcNow().AddHours(1));
      var rejected = await provider.GetTokenAsync(Validity, token);
      _az.SignedIn("token-two", _clock.GetUtcNow().AddHours(1));

      provider.Reject(rejected);

      Assert.Equal("token-two", (await provider.GetTokenAsync(Validity, token)).Value);
      Assert.Equal(2, _az.Runs.Count);
   }

   /// <summary>A rejection that arrives after a newer token replaced the rejected one leaves the newer token cached.</summary>
   [Fact]
   public async Task Rejecting_a_token_other_than_the_cached_one_keeps_the_cache()
   {
      var provider = _az.Provider(_clock);
      var token = TestContext.Current.CancellationToken;
      _az.SignedIn("token-one", _clock.GetUtcNow().AddHours(1));
      await provider.GetTokenAsync(Validity, token);

      provider.Reject(new AccessToken("token-one"));

      Assert.Equal("token-one", (await provider.GetTokenAsync(Validity, token)).Value);
      Assert.Single(_az.Runs);
   }

   /// <summary>The Azure CLI returns the same token until 5 minutes before it expires, so a token with 8 minutes left serves requests for ten minutes until the one-minute floor.</summary>
   [Fact]
   public async Task Token_that_arrives_with_less_validity_than_asked_is_reused_until_the_floor()
   {
      var provider = _az.Provider(_clock);
      var token = TestContext.Current.CancellationToken;
      _az.SignedIn("token-one", _clock.GetUtcNow().AddMinutes(8));

      Assert.Equal("token-one", (await provider.GetTokenAsync(Validity, token)).Value);
      _az.SignedIn("token-two", _clock.GetUtcNow().AddHours(1));
      _clock.Advance(TimeSpan.FromMinutes(6.5));
      Assert.Equal("token-one", (await provider.GetTokenAsync(Validity, token)).Value);
      Assert.Single(_az.Runs);

      _clock.Advance(TimeSpan.FromMinutes(1));
      Assert.Equal("token-two", (await provider.GetTokenAsync(Validity, token)).Value);
      Assert.Equal(2, _az.Runs.Count);
   }

   [Fact]
   public async Task Token_that_arrives_below_the_floor_is_returned_but_not_reused()
   {
      var provider = _az.Provider(_clock);
      var token = TestContext.Current.CancellationToken;
      _az.SignedIn("token-one", _clock.GetUtcNow().AddSeconds(30));

      Assert.Equal("token-one", (await provider.GetTokenAsync(Validity, token)).Value);
      Assert.Equal("token-one", (await provider.GetTokenAsync(Validity, token)).Value);
      Assert.Equal(2, _az.Runs.Count);
   }

   [Fact]
   public async Task Expiry_in_local_time_from_an_older_azure_cli_is_read()
   {
      var provider = _az.Provider(_clock);
      var token = TestContext.Current.CancellationToken;
      _az.SignedIn("token-one", _clock.GetUtcNow().AddHours(1), localExpiry: true);

      await provider.GetTokenAsync(Validity, token);
      _clock.Advance(TimeSpan.FromMinutes(49));
      await provider.GetTokenAsync(Validity, token);
      Assert.Single(_az.Runs);

      _clock.Advance(TimeSpan.FromMinutes(2));
      await provider.GetTokenAsync(Validity, token);
      Assert.Equal(2, _az.Runs.Count);
   }

   /// <summary>Each output starts with the byte order mark of its encoding, as a Python that is set to write that encoding gives it.</summary>
   [Theory]
   [InlineData("utf-8")]
   [InlineData("utf-16")]
   [InlineData("utf-16BE")]
   [InlineData("utf-32")]
   public async Task Token_in_output_with_a_byte_order_mark_is_read(string encoding)
   {
      _az.SignedIn("token-one", _clock.GetUtcNow().AddHours(1));
      File.WriteAllText(_az.OutputPath, File.ReadAllText(_az.OutputPath), Encoding.GetEncoding(encoding));

      var token = await _az.Provider(_clock).GetTokenAsync(Validity, TestContext.Current.CancellationToken);

      Assert.Equal("token-one", token.Value);
   }

   [Fact]
   public async Task Concurrent_requests_share_one_azure_cli_run()
   {
      var provider = _az.Provider(_clock);
      _az.SignedIn("token-one", _clock.GetUtcNow().AddHours(1));

      var tokens = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => provider.GetTokenAsync(Validity, TestContext.Current.CancellationToken).AsTask()));

      Assert.All(tokens, token => Assert.Equal("token-one", token.Value));
      Assert.Single(_az.Runs);
   }

   [Fact]
   public async Task Missing_azure_cli_names_its_install()
   {
      var provider = new AzureCliTokenProvider(
         new AzureCliOptions { Executable = Path.Combine(_az.Root, "missing", "az") },
         _clock,
         Microsoft.Extensions.Logging.Abstractions.NullLogger<AzureCliTokenProvider>.Instance);

      var exception = await Assert.ThrowsAsync<AccessTokenUnavailableException>(() => provider.GetTokenAsync(Validity, TestContext.Current.CancellationToken).AsTask());

      Assert.Contains("Install the Azure CLI", exception.Message, StringComparison.Ordinal);
      Assert.Contains("az login", exception.Message, StringComparison.Ordinal);
   }

   [Fact]
   public async Task Signed_out_azure_cli_names_az_login()
   {
      _az.Answer(1, "", "ERROR: Please run 'az login' to setup account.\n");

      var exception = await Assert.ThrowsAsync<AccessTokenUnavailableException>(() => _az.Provider(_clock).GetTokenAsync(Validity, TestContext.Current.CancellationToken).AsTask());

      Assert.Contains("not signed in", exception.Message, StringComparison.Ordinal);
      Assert.Contains("Run 'az login', then try again.", exception.Message, StringComparison.Ordinal);
   }

   /// <summary>An expired sign-in prints the fix on lines of their own, after the error line.</summary>
   [Theory]
   [InlineData("ERROR: AADSTS700082: The refresh token has expired due to inactivity.\nTo re-authenticate, please run:\naz login\n", "Run 'az login'")]
   [InlineData(
      "ERROR: AADSTS50076: Due to a configuration change made by your administrator, you must use multi-factor authentication.\nInteractive authentication is needed. Please run:\naz logout\naz login --scope \"499b84ac-1321-427f-aa17-267ca6975798/.default\"\n",
      "Run 'az logout', then 'az login --scope \"499b84ac-1321-427f-aa17-267ca6975798/.default\"'")]
   public async Task Expired_sign_in_names_the_command_the_azure_cli_gives(string error, string fix)
   {
      _az.Answer(1, "", error);

      var exception = await Assert.ThrowsAsync<AccessTokenUnavailableException>(() => _az.Provider(_clock).GetTokenAsync(Validity, TestContext.Current.CancellationToken).AsTask());

      Assert.Contains("not signed in, or its sign-in has expired", exception.Message, StringComparison.Ordinal);
      Assert.Contains(fix + ", then try again.", exception.Message, StringComparison.Ordinal);
      Assert.Contains(error.Split('\n')[0]["ERROR: ".Length..], exception.Message, StringComparison.Ordinal);
   }

   [Fact]
   public async Task Failing_azure_cli_gives_its_reason_and_the_fix()
   {
      _az.Answer(2, "", "WARNING: something minor\nERROR: The network is unreachable.\n");

      var exception = await Assert.ThrowsAsync<AccessTokenUnavailableException>(() => _az.Provider(_clock).GetTokenAsync(Validity, TestContext.Current.CancellationToken).AsTask());

      Assert.Contains("The network is unreachable.", exception.Message, StringComparison.Ordinal);
      Assert.DoesNotContain("WARNING", exception.Message, StringComparison.Ordinal);
      Assert.DoesNotContain("not signed in", exception.Message, StringComparison.Ordinal);
      Assert.Contains("az login", exception.Message, StringComparison.Ordinal);
   }

   [Theory]
   [InlineData("not json")]
   [InlineData("""{"tokenType": "Bearer"}""")]
   [InlineData("""{"accessToken": "token-one", "tokenType": "Bearer"}""")]
   [InlineData("""{"accessToken": "token-one", "expiresOn": "tomorrow"}""")]
   [InlineData("""{"accessToken": "token\nwith a line break", "expires_on": 4102444800}""")]
   public async Task Unreadable_azure_cli_output_names_the_update(string output)
   {
      _az.Answer(0, output, "");

      var exception = await Assert.ThrowsAsync<AccessTokenUnavailableException>(() => _az.Provider(_clock).GetTokenAsync(Validity, TestContext.Current.CancellationToken).AsTask());

      Assert.Contains("Update the Azure CLI", exception.Message, StringComparison.Ordinal);
   }

   [Fact]
   public async Task Expired_token_from_the_azure_cli_names_az_login()
   {
      _az.SignedIn("token-one", _clock.GetUtcNow().AddMinutes(-1));

      var exception = await Assert.ThrowsAsync<AccessTokenUnavailableException>(() => _az.Provider(_clock).GetTokenAsync(Validity, TestContext.Current.CancellationToken).AsTask());

      Assert.Contains("az login", exception.Message, StringComparison.Ordinal);
   }

   [Fact]
   public async Task Azure_cli_that_does_not_answer_is_stopped_at_the_timeout()
   {
      _az.Hang();
      var provider = _az.Provider(_clock, timeout: TimeSpan.FromSeconds(1));

      var exception = await Assert.ThrowsAsync<AccessTokenUnavailableException>(() => provider.GetTokenAsync(Validity, TestContext.Current.CancellationToken).AsTask());

      Assert.Contains("did not answer within 1 seconds", exception.Message, StringComparison.Ordinal);
   }

   [Fact]
   public async Task Failure_is_not_cached()
   {
      var provider = _az.Provider(_clock);
      var token = TestContext.Current.CancellationToken;
      await Assert.ThrowsAsync<AccessTokenUnavailableException>(() => provider.GetTokenAsync(Validity, token).AsTask());
      _az.SignedIn("token-one", _clock.GetUtcNow().AddHours(1));

      Assert.Equal("token-one", (await provider.GetTokenAsync(Validity, token)).Value);
      Assert.Equal(2, _az.Runs.Count);
   }

   [Fact]
   public void Windows_finds_az_cmd_through_PATHEXT()
   {
      var first = Directory.CreateDirectory(Path.Combine(_az.Root, "first")).FullName;
      var second = Directory.CreateDirectory(Path.Combine(_az.Root, "second")).FullName;
      File.WriteAllText(Path.Combine(first, "az"), "");
      File.WriteAllText(Path.Combine(second, "az.cmd"), "");
      var path = string.Join(Path.PathSeparator, ["", first, second]);

      Assert.Equal(Path.Combine(second, "az.cmd"), AzureCliTokenProvider.FindOnPath("az", path, ".COM;.EXE;.BAT;.CMD", windows: true));
      Assert.Equal(Path.Combine(first, "az"), AzureCliTokenProvider.FindOnPath("az", path, null, windows: false));
      Assert.Null(AzureCliTokenProvider.FindOnPath("az", first, ".EXE", windows: true));
      Assert.Null(AzureCliTokenProvider.FindOnPath("az", null, null, windows: false));
   }

   public void Dispose() => _az.Dispose();
}
