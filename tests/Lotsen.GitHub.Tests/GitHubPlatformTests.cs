using System.Globalization;
using Lotsen.Credentials;
using Lotsen.Git;
using Lotsen.GitHub;
using Lotsen.Hosting;
using Lotsen.Platforms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lotsen.Tests;

/// <summary>Covers how the GitHub platform resolves a pull request through the REST API: the request it sends, the commits, remote and state it reads, and the reason it gives for each failure.</summary>
[Trait("Category", "GitHub")]
public sealed class GitHubPlatformTests : IAsyncDisposable
{
   private const string Token = "lotsen-test-token";

   private static readonly CommitId Head = CommitId.Parse(new string('1', 40));
   private static readonly CommitId Base = CommitId.Parse(new string('a', 40));
   private static readonly Uri PrUrl = new(FakeGitHub.PrUrl);

   private static readonly string MovedReason =
      $"lotsen could not read the pull request {FakeGitHub.PrUrl}. GitHub answered 301: the pull request's repository has moved to an address that lotsen does not follow. "
      + "Start a new tour from the pull request's current URL.";

   private readonly string _root = Directory.CreateTempSubdirectory("lotsen-tests-").FullName;
   private readonly DataDirectory _dataDirectory;
   private readonly FakeGitHub _gitHub = new();
   private readonly FixedAccessTokenProvider _tokens = new(Token);
   private readonly GitHubPlatform _platform;

   public GitHubPlatformTests()
   {
      _dataDirectory = new DataDirectory(Path.Combine(_root, "data"));
      _platform = _gitHub.Platform(_tokens, _dataDirectory);
   }

   private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

   [Fact]
   public async Task Request_names_the_pull_request_and_sends_the_token_and_the_headers_that_GitHub_asks()
   {
      _gitHub.PullRequest(Head, Base);

      await _platform.ResolveAsync(PrUrl, Cancellation);

      var request = Assert.Single(_gitHub.Server.Requests);
      Assert.StartsWith($"GET {FakeGitHub.PullRequestPath} HTTP/1.1\r\n", request, StringComparison.Ordinal);
      Assert.Contains($"Authorization: Bearer {Token}\r\n", request, StringComparison.Ordinal);
      Assert.Contains("Accept: application/vnd.github+json\r\n", request, StringComparison.Ordinal);
      Assert.Contains("X-GitHub-Api-Version: 2022-11-28\r\n", request, StringComparison.Ordinal);
      Assert.Contains("User-Agent: lotsen\r\n", request, StringComparison.Ordinal);
   }

   [Fact]
   public async Task Resolve_reads_the_head_and_base_commits_and_the_base_repositorys_clone_url()
   {
      _gitHub.PullRequest(Head, Base);

      var resolved = await _platform.ResolveAsync(PrUrl, Cancellation);

      Assert.Equal(new ResolvedPullRequest(FakeGitHub.CloneUrl, Head, Base, 1, PullRequestState.Active), resolved);
   }

   [Fact]
   public async Task Fork_pull_request_resolves_to_the_base_repositorys_clone_url()
   {
      _gitHub.PullRequest(Head, Base);
      _gitHub.FromFork(new Uri("https://github.com/octocat/hello-world-fork.git"));

      var resolved = await _platform.ResolveAsync(PrUrl, Cancellation);

      Assert.Equal(FakeGitHub.CloneUrl, resolved.RemoteUrl);
      Assert.Equal(Head, resolved.SourceCommit);
   }

   [Theory]
   [InlineData("open", false, false, PullRequestState.Active)]
   [InlineData("open", false, true, PullRequestState.Active)]
   [InlineData("closed", true, false, PullRequestState.Completed)]
   [InlineData("closed", false, false, PullRequestState.Abandoned)]
   [InlineData("closed", false, true, PullRequestState.Abandoned)]
   [InlineData("reviewing", false, false, PullRequestState.Active)]
   public async Task State_maps_to_lotsens_state(string state, bool merged, bool draft, PullRequestState expected)
   {
      _gitHub.PullRequest(Head, Base, state, merged, draft);

      var resolved = await _platform.ResolveAsync(PrUrl, Cancellation);

      Assert.Equal(expected, resolved.State);
   }

   [Fact]
   public async Task Not_found_names_missing_access_of_the_gh_sign_in()
   {
      _gitHub.Refuse(404, "Not Found");

      var thrown = await Assert.ThrowsAsync<PlatformException>(() => _platform.ResolveAsync(PrUrl, Cancellation));

      Assert.Equal(
         $"lotsen could not read the pull request {FakeGitHub.PrUrl}. GitHub answered 404: the pull request was not found, or your gh sign-in has no access to it. "
         + "Check the URL, or run 'gh auth login' with an account that can see the repository, then try again.",
         thrown.Reason);
   }

   [Theory]
   [InlineData(401, true)]
   [InlineData(403, true)]
   [InlineData(404, true)]
   [InlineData(400, false)]
   [InlineData(422, false)]
   [InlineData(429, false)]
   [InlineData(500, false)]
   public async Task Refusal_that_a_new_gh_sign_in_can_fix_rejects_the_token_at_its_provider(int status, bool rejects)
   {
      _gitHub.Refuse(status);

      await Assert.ThrowsAsync<PlatformException>(() => _platform.ResolveAsync(PrUrl, Cancellation));

      Assert.Equal(rejects ? [Token] : [], _tokens.Rejected);
   }

   /// <summary>The reviewer signs in to gh with another account after the refusal, as the refusal's reason advises.</summary>
   [Theory]
   [InlineData(403)]
   [InlineData(404)]
   public async Task Resolve_after_a_refusal_of_access_sends_the_token_of_the_new_gh_sign_in(int status)
   {
      using var gh = new FakeGhCli();
      gh.SignedIn("token-one");
      using var platform = _gitHub.Platform(gh.Provider(), _dataDirectory);
      _gitHub.Refuse(status);
      await Assert.ThrowsAsync<PlatformException>(() => platform.ResolveAsync(PrUrl, Cancellation));
      gh.SignedIn("token-two");
      _gitHub.PullRequest(Head, Base);

      await platform.ResolveAsync(PrUrl, Cancellation);

      Assert.Contains("Authorization: Bearer token-two\r\n", _gitHub.Server.Requests[^1], StringComparison.Ordinal);
   }

   [Fact]
   public async Task Refused_token_is_rejected_at_its_provider_and_names_gh_auth_login()
   {
      _gitHub.Refuse(401, "Bad credentials");

      var thrown = await Assert.ThrowsAsync<PlatformException>(() => _platform.ResolveAsync(PrUrl, Cancellation));

      Assert.Equal([Token], _tokens.Rejected);
      Assert.Equal(
         $"lotsen could not read the pull request {FakeGitHub.PrUrl}. GitHub answered 401: the token of your gh sign-in was refused. Run 'gh auth login', then try again.",
         thrown.Reason);
   }

   [Theory]
   [InlineData(429, "You have exceeded a secondary rate limit.")]
   [InlineData(500, "Server Error")]
   [InlineData(503, "Service unavailable")]
   public async Task Other_refusal_gives_the_status_and_GitHubs_message(int status, string message)
   {
      _gitHub.Refuse(status, message);

      var thrown = await Assert.ThrowsAsync<PlatformException>(() => _platform.ResolveAsync(PrUrl, Cancellation));

      Assert.StartsWith($"lotsen could not read the pull request {FakeGitHub.PrUrl}. GitHub answered {status}: {message}", thrown.Reason, StringComparison.Ordinal);
   }

   [Fact]
   public async Task Forbidden_names_access_and_rate_limits()
   {
      _gitHub.Refuse(403, "Resource protected by organization SAML enforcement.");

      var thrown = await Assert.ThrowsAsync<PlatformException>(() => _platform.ResolveAsync(PrUrl, Cancellation));

      Assert.Contains("GitHub answered 403: Resource protected by organization SAML enforcement.", thrown.Reason, StringComparison.Ordinal);
      Assert.Contains("gh sign-in", thrown.Reason, StringComparison.Ordinal);
      Assert.Contains("rate limit", thrown.Reason, StringComparison.Ordinal);
   }

   /// <summary>GitHub answers a renamed or transferred repository's old path with a redirect, absolute or relative, which the client follows with the token.</summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public async Task Redirect_within_the_api_is_followed_with_the_token(bool absolute)
   {
      const string Moved = "/repositories/7/pulls/42";
      _gitHub.PullRequest(Head, Base);
      _gitHub.Redirect(FakeGitHub.PullRequestPath, absolute ? new Uri(_gitHub.Server.BaseUrl, Moved).AbsoluteUri : Moved);

      var resolved = await _platform.ResolveAsync(PrUrl, Cancellation);

      Assert.Equal(Head, resolved.SourceCommit);
      Assert.Equal(2, _gitHub.Server.Requests.Count);
      Assert.StartsWith($"GET {Moved} HTTP/1.1\r\n", _gitHub.Server.Requests[1], StringComparison.Ordinal);
      Assert.Contains($"Authorization: Bearer {Token}\r\n", _gitHub.Server.Requests[1], StringComparison.Ordinal);
   }

   /// <summary>Each location differs from the API's origin in one part: the host, the scheme or the port.</summary>
   [Theory]
   [InlineData("http://localhost:{0}/repositories/7/pulls/42")]
   [InlineData("https://127.0.0.1:{0}/repositories/7/pulls/42")]
   [InlineData("http://127.0.0.1:9/repositories/7/pulls/42")]
   public async Task Redirect_out_of_the_api_origin_is_not_followed(string location)
   {
      _gitHub.PullRequest(Head, Base);
      _gitHub.Redirect(FakeGitHub.PullRequestPath, string.Format(CultureInfo.InvariantCulture, location, _gitHub.Server.BaseUrl.Port));

      var thrown = await Assert.ThrowsAsync<PlatformException>(() => _platform.ResolveAsync(PrUrl, Cancellation));

      Assert.Equal(MovedReason, thrown.Reason);
      Assert.Single(_gitHub.Server.Requests);
   }

   [Fact]
   public async Task Second_redirect_is_not_followed()
   {
      _gitHub.PullRequest(Head, Base);
      _gitHub.Redirect(FakeGitHub.PullRequestPath, "/repositories/7/pulls/42");
      _gitHub.Redirect("/repositories/7/pulls/42", "/repositories/8/pulls/42");

      var thrown = await Assert.ThrowsAsync<PlatformException>(() => _platform.ResolveAsync(PrUrl, Cancellation));

      Assert.Equal(MovedReason, thrown.Reason);
      Assert.Equal(2, _gitHub.Server.Requests.Count);
   }

   [Fact]
   public async Task Moved_repository_without_a_location_names_a_new_tour_from_the_current_url_as_the_fix()
   {
      _gitHub.Refuse(301, "Moved Permanently");

      var thrown = await Assert.ThrowsAsync<PlatformException>(() => _platform.ResolveAsync(PrUrl, Cancellation));

      Assert.Equal(MovedReason, thrown.Reason);
   }

   [Fact]
   public async Task Refusal_without_a_message_gives_the_status()
   {
      _gitHub.Respond(new HttpAnswer(502, "<html>Bad gateway</html>", "text/html"));

      var thrown = await Assert.ThrowsAsync<PlatformException>(() => _platform.ResolveAsync(PrUrl, Cancellation));

      Assert.Equal($"lotsen could not read the pull request {FakeGitHub.PrUrl}. GitHub answered 502.", thrown.Reason);
   }

   [Theory]
   [InlineData("<html>not json</html>")]
   [InlineData("""{ "state": "open" }""")]
   [InlineData("""{ "state": "open", "merged": false, "head": { "sha": "1111111111111111111111111111111111111111" }, "base": { "sha": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" } }""")]
   [InlineData("""{ "state": "open", "merged": false, "head": { "sha": "abc" }, "base": { "sha": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "repo": { "clone_url": "https://github.com/a/b.git" } } }""")]
   [InlineData("""{ "state": "open", "merged": false, "head": { "sha": "1111111111111111111111111111111111111111" }, "base": { "sha": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "repo": { "clone_url": "b.git" } } }""")]
   [InlineData("""{ "state": "open", "merged": false, "head": { "sha": "1111111111111111111111111111111111111111" }, "base": { "sha": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "repo": { "clone_url": "https://user:secret@github.com/a/b.git" } } }""")]
   [InlineData("""{ "state": null, "merged": false, "head": { "sha": "1111111111111111111111111111111111111111" }, "base": { "sha": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "repo": { "clone_url": "https://github.com/a/b.git" } } }""")]
   public async Task Unreadable_answer_is_a_failure_with_a_reason(string body)
   {
      _gitHub.Respond(new HttpAnswer(200, body));

      var thrown = await Assert.ThrowsAsync<PlatformException>(() => _platform.ResolveAsync(PrUrl, Cancellation));

      Assert.Equal($"lotsen could not read the pull request {FakeGitHub.PrUrl}. GitHub sent an answer that lotsen cannot read.", thrown.Reason);
   }

   [Fact]
   public async Task Unreachable_host_is_a_failure_that_names_the_host()
   {
      using var refusing = HeldPort.Refusing();
      using var platform = FakeGitHub.PlatformAt(new Uri($"http://127.0.0.1:{refusing.Port}/"), _tokens, _dataDirectory);

      var thrown = await Assert.ThrowsAsync<PlatformException>(() => platform.ResolveAsync(PrUrl, Cancellation));

      Assert.StartsWith($"lotsen could not read the pull request {FakeGitHub.PrUrl}. lotsen could not reach GitHub at 127.0.0.1:", thrown.Reason, StringComparison.Ordinal);
   }

   [Fact]
   public async Task Silent_host_is_a_failure_that_names_the_timeout()
   {
      await using var silent = new FakeGitHub(hold: true);
      using var platform = FakeGitHub.PlatformAt(silent.Server.BaseUrl, _tokens, _dataDirectory, timeout: TimeSpan.FromMilliseconds(300));

      var thrown = await Assert.ThrowsAsync<PlatformException>(() => platform.ResolveAsync(PrUrl, Cancellation));

      Assert.Equal($"lotsen could not read the pull request {FakeGitHub.PrUrl}. GitHub did not answer within 0.3 seconds.", thrown.Reason);
   }

   [Fact]
   public async Task Missing_token_stops_the_request()
   {
      using var platform = _gitHub.Platform(new GhCliTokenProvider(new GhCliOptions { Executable = Path.Combine(_root, "no-gh") }, TimeProvider.System, NullLogger<GhCliTokenProvider>.Instance), _dataDirectory);

      await Assert.ThrowsAsync<AccessTokenUnavailableException>(() => platform.ResolveAsync(PrUrl, Cancellation));

      Assert.Empty(_gitHub.Server.Requests);
   }

   /// <summary>A directory in the place of the pull request's iteration record makes the store fail to open it.</summary>
   [Fact]
   public async Task Iteration_record_that_cannot_be_opened_is_a_platform_failure_with_a_reason()
   {
      _gitHub.PullRequest(Head, Base);
      var record = new GitHubIterationStore(_dataDirectory, NullLogger<GitHubIterationStore>.Instance).PathOf(Parse(FakeGitHub.PrUrl));
      Directory.CreateDirectory(record);

      var thrown = await Assert.ThrowsAsync<PlatformException>(() => _platform.ResolveAsync(PrUrl, Cancellation));

      Assert.StartsWith($"lotsen could not store the iteration of the pull request {FakeGitHub.PrUrl} in its data directory: ", thrown.Reason, StringComparison.Ordinal);
      Assert.EndsWith("Check that you can read and write that file, then try again.", thrown.Reason, StringComparison.Ordinal);
   }

   [Fact]
   public async Task Url_of_another_platform_is_a_platform_failure_that_names_the_forms()
   {
      var thrown = await Assert.ThrowsAsync<PlatformException>(() => _platform.ResolveAsync(new Uri("https://example.com/octo-org/hello-world/pull/42"), Cancellation));

      Assert.Contains(GitHubPullRequest.ExpectedForms, thrown.Reason, StringComparison.Ordinal);
   }

   [Fact]
   public async Task Factory_builds_the_platform_from_the_servers_services_and_the_gh_sign_in()
   {
      using var gh = new FakeGhCli();
      gh.SignedIn(Token);
      _gitHub.PullRequest(Head, Base);
      await using var services = new ServiceCollection().AddLogging().AddSingleton(_dataDirectory).AddSingleton(TimeProvider.System).BuildServiceProvider();

      var factory = GitHubPlatform.Factory(new GitHubOptions { ApiBaseUrl = _gitHub.Server.BaseUrl }, new GhCliOptions { Executable = gh.Executable }, accessTokens: null);
      using var platform = Assert.IsType<GitHubPlatform>(factory(services));
      var resolved = await platform.ResolveAsync(PrUrl, Cancellation);

      Assert.Equal(GitHubPlatform.PlatformId, platform.Id);
      Assert.Equal(new ResolvedPullRequest(FakeGitHub.CloneUrl, Head, Base, 1, PullRequestState.Active), resolved);
      Assert.Contains($"Authorization: Bearer {Token}\r\n", Assert.Single(_gitHub.Server.Requests), StringComparison.Ordinal);
      Assert.True(File.Exists(new GitHubIterationStore(_dataDirectory, services.GetRequiredService<ILogger<GitHubIterationStore>>()).PathOf(Parse(FakeGitHub.PrUrl))));
   }

   public async ValueTask DisposeAsync()
   {
      _platform.Dispose();
      await _gitHub.DisposeAsync();
      TestDirectories.Delete(_root, TimeSpan.FromSeconds(5));
   }

   private static GitHubPullRequest Parse(string url) => GitHubPullRequest.TryParse(url, out var pullRequest) ? pullRequest : throw new ArgumentException($"'{url}' is no pull request URL.");
}
