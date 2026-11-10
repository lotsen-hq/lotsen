using System.Text;
using Lotsen.Credentials;
using Lotsen.Git;
using Lotsen.Pinning;
using Microsoft.Extensions.Logging;

namespace Lotsen.Tests;

/// <summary>
/// Covers how the git layer authenticates to a remote that requires it: git asks the remote's credential only when the remote asks for authentication, a refused credential is classified and
/// told to its source, and the credential reaches the remote and nothing else, against an <see cref="AuthenticatingGitServer"/>.
/// </summary>
[Trait("Category", "Secrets")]
public sealed class GitAuthTests : IAsyncLifetime
{
   private readonly GitFixture _fixture = new();
   private readonly string _token = "lotsen-test-token-" + Guid.NewGuid().ToString("N");
   private readonly RecordingLoggerProvider _logs = new();
   private readonly TestGitCredential _credential;
   private AuthenticatingGitServer _server = null!;
   private CommitId _base;
   private CommitId _head;

   public GitAuthTests()
   {
      _credential = new TestGitCredential(_token);
   }

   private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

   public async ValueTask InitializeAsync()
   {
      _base = _fixture.Commit("Base", ("a.txt", "one\n"), ("dir/b.txt", "bee\n"));
      _head = _fixture.Commit("Head", ("a.txt", "two\n"), ("c.txt", "new\n"));
      _server = await AuthenticatingGitServer.StartAsync(_fixture.OriginPath, _credential.Header);
   }

   public async ValueTask DisposeAsync()
   {
      await _server.DisposeAsync();
      _fixture.Dispose();
   }

   [Fact]
   public async Task Partial_clone_and_lazy_download_of_a_file_content_authenticate()
   {
      var repository = await NewCache().OpenAsync(Remote(), [], FetchPolicy.Sync, Cancellation);

      Assert.Equal("two\n", await repository.ReadFileAsync(_head, "a.txt", Cancellation));
      Assert.Equal("true", _fixture.Git(repository.Path, "config", "--get", "remote.origin.promisor").Trim());
      Assert.True(_server.AuthorizedRequests > 0);
      Assert.Equal(0, _server.RefusedRequests);
   }

   /// <summary>The clone holds every commit and tree, so these queries read the clone alone.</summary>
   [Fact]
   public async Task Command_that_needs_no_network_asks_the_credential_for_nothing()
   {
      var repository = await NewCache().OpenAsync(Remote(), [], FetchPolicy.Sync, Cancellation);
      var answers = _credential.Answers;
      var requests = _server.AuthorizedRequests + _server.RequestsWithoutHeader;
      _credential.Unavailable = "Sign in, then try again.";

      Assert.Equal(_base, await repository.GetMergeBaseAsync(_base, _head, Cancellation));
      Assert.NotNull(await repository.ListDirectoryAsync(_head, "dir", Cancellation));

      Assert.Equal(answers, _credential.Answers);
      Assert.Equal(requests, _server.AuthorizedRequests + _server.RequestsWithoutHeader);
   }

   /// <summary>In a blob-less clone a blob's size comes only from the blob, so finding a file downloads it.</summary>
   [Fact]
   public async Task Command_that_downloads_a_missing_file_content_asks_the_credential_once()
   {
      var repository = await NewCache().OpenAsync(Remote(), [], FetchPolicy.Sync, Cancellation);
      var answers = _credential.Answers;

      Assert.NotNull(await repository.FindFileAsync(_base, "dir/b.txt", Cancellation));

      Assert.Equal(answers + 1, _credential.Answers);
   }

   [Fact]
   public async Task Answer_is_asked_to_stay_valid_for_the_git_run_that_sends_it()
   {
      var options = new GitOptions { TransferTimeout = TimeSpan.FromMinutes(7), QueryTimeout = TimeSpan.FromMinutes(3) };
      var repository = await NewCache(options).OpenAsync(Remote(), [], FetchPolicy.Sync, Cancellation);
      await repository.ReadFileAsync(_head, "a.txt", Cancellation);

      Assert.Equal(options.TransferTimeout, _credential.Validities[0]);
      Assert.Equal(options.QueryTimeout, _credential.Validities[^1]);
   }

   [Fact]
   public async Task Refused_credential_is_classified_and_told_to_its_source_once()
   {
      _server.Accept("Bearer another-token");

      var exception = await Assert.ThrowsAsync<GitAuthException>(() => NewCache().OpenAsync(Remote(), [], FetchPolicy.Sync, Cancellation));

      Assert.Equal($"Git could not clone {_server.RepositoryUrl}: the remote refused lotsen's credential. {TestGitCredential.Hint}", exception.Message);
      Assert.Equal(1, _credential.Refusals);
      Assert.Empty(_fixture.Clones());
   }

   /// <summary>A refusal during the lazy download inside a query is classified as one during a fetch is.</summary>
   [Fact]
   public async Task Refusal_of_a_lazy_download_is_classified()
   {
      var repository = await NewCache().OpenAsync(Remote(), [], FetchPolicy.Sync, Cancellation);
      _server.Accept("Bearer another-token");

      var exception = await Assert.ThrowsAsync<GitAuthException>(() => repository.ReadFileAsync(_head, "c.txt", Cancellation));

      Assert.EndsWith(TestGitCredential.Hint, exception.Message, StringComparison.Ordinal);
      Assert.Equal(1, _credential.Refusals);
   }

   [Fact]
   public async Task Next_call_after_a_refusal_sends_the_credentials_new_answer()
   {
      _server.Accept("Bearer another-token");
      await Assert.ThrowsAsync<GitAuthException>(() => NewCache().OpenAsync(Remote(), [], FetchPolicy.Sync, Cancellation));

      _credential.Use("another-token");
      var repository = await NewCache().OpenAsync(Remote(), [], FetchPolicy.Sync, Cancellation);

      Assert.Equal("two\n", await repository.ReadFileAsync(_head, "a.txt", Cancellation));
   }

   [Fact]
   public async Task Credential_without_a_token_fails_the_call_with_its_own_reason()
   {
      _credential.Unavailable = "Sign in to the test platform, then try again.";

      var exception = await Assert.ThrowsAsync<AccessTokenUnavailableException>(() => NewCache().OpenAsync(Remote(), [], FetchPolicy.Sync, Cancellation));

      Assert.Equal("Sign in to the test platform, then try again.", exception.Message);
      Assert.Equal(0, _credential.Refusals);
   }

   /// <summary>Other failures of a remote that requires authentication stay plain git failures.</summary>
   [Fact]
   public async Task Missing_repository_on_an_authenticating_remote_is_no_auth_failure()
   {
      var remote = TestRemote.Of(new Uri(_server.RepositoryUrl.AbsoluteUri + "/missing"), _credential);

      var exception = await Assert.ThrowsAsync<GitException>(() => NewCache().OpenAsync(remote, [], FetchPolicy.Sync, Cancellation));

      Assert.IsNotType<GitAuthException>(exception);
      Assert.Equal(0, _credential.Refusals);
   }

   /// <summary>Git prints a remote's <c>ERR</c> packet with its line breaks, so the remote can print a line that reads as the helper's marker.</summary>
   [Theory]
   [InlineData("lotsen: credential refused", false)]
   [InlineData("lotsen: credential wanted", true)]
   public async Task Remote_error_that_reads_as_a_marker_is_no_auth_failure(string marker, bool beforeAuthentication)
   {
      _server.SendError($"boom\n{marker}", beforeAuthentication);

      var exception = await Assert.ThrowsAsync<GitException>(() => NewCache().OpenAsync(Remote(), [], FetchPolicy.Sync, Cancellation));

      Assert.IsNotType<GitAuthException>(exception);
      Assert.Contains("remote error: boom", exception.Message, StringComparison.Ordinal);
      Assert.Equal(0, _credential.Refusals);
      Assert.Equal(beforeAuthentication ? 0 : 1, _credential.Answers);
   }

   /// <summary>The remote's own host redirects its first request to the same repository at another host, port or path, which then asks for authentication.</summary>
   [Theory]
   [InlineData("another host")]
   [InlineData("another path on the same host")]
   [InlineData("another port")]
   public async Task Redirect_outside_the_remote_gets_no_credential(string target)
   {
      await using var other = await AuthenticatingGitServer.StartAsync(_fixture.OriginPath, _credential.Header, servedAt: "/elsewhere");
      var (redirecting, remote) = target switch
      {
         "another host" => (_server, Localhost(_server.RepositoryUrl)),
         "another path on the same host" => (_server, _server.UrlOf("/old")),
         _ => (other, other.UrlOf("/repo")),
      };
      redirecting.Redirect(remote, _server.RepositoryUrl);

      var exception = await Assert.ThrowsAsync<GitException>(() => NewCache().OpenAsync(TestRemote.Of(remote, _credential), [], FetchPolicy.Sync, Cancellation));

      Assert.IsNotType<GitAuthException>(exception);
      Assert.True(_server.RequestsWithoutHeader > 0, "The redirect target never asked for authentication.");
      Assert.Equal(0, _server.AuthorizedRequests + _server.RefusedRequests);
      Assert.Equal(0, _credential.Answers);
      Assert.Equal(0, _credential.Refusals);
   }

   [Fact]
   public async Task Redirect_to_a_path_inside_the_remote_authenticates()
   {
      await using var moved = await AuthenticatingGitServer.StartAsync(_fixture.OriginPath, _credential.Header, servedAt: "/repo/moved");
      var remote = moved.UrlOf("/repo");
      moved.Redirect(remote, moved.RepositoryUrl);

      var repository = await NewCache().OpenAsync(TestRemote.Of(remote, _credential), [], FetchPolicy.Sync, Cancellation);

      Assert.Equal("two\n", await repository.ReadFileAsync(_head, "a.txt", Cancellation));
      Assert.True(moved.AuthorizedRequests > 0);
   }

   /// <summary>
   /// Azure DevOps allows these characters in a Git repository name (https://learn.microsoft.com/en-us/azure/devops/organizations/settings/naming-restrictions), and git percent-encodes them
   /// in the address that it asks a credential for.
   /// </summary>
   [Theory]
   [InlineData("R(x)", false)]
   [InlineData("R(x)", true)]
   [InlineData("R&D", false)]
   [InlineData("R&D", true)]
   [InlineData("it's", false)]
   [InlineData("it's", true)]
   [InlineData("go!", false)]
   [InlineData("go!", true)]
   [InlineData("a@b", false)]
   [InlineData("a@b", true)]
   public async Task Remote_whose_path_holds_a_reserved_character_authenticates(string name, bool encoded)
   {
      await using var server = await AuthenticatingGitServer.StartAsync(_fixture.OriginPath, _credential.Header, servedAt: "/team/" + name);
      var remote = encoded ? server.UrlOf("/team/" + Uri.EscapeDataString(name)) : server.RepositoryUrl;
      Assert.Equal(encoded, remote.AbsoluteUri.Contains('%', StringComparison.Ordinal));

      var repository = await NewCache().OpenAsync(TestRemote.Of(remote, _credential), [], FetchPolicy.Sync, Cancellation);

      Assert.Equal("two\n", await repository.ReadFileAsync(_head, "a.txt", Cancellation));
      Assert.True(server.AuthorizedRequests > 0);
   }

   /// <summary>
   /// <c>git credential fill</c> asks for a credential the way a request does, so it shows which addresses the runner's helper answers. A redirect to https cannot reach a loopback test server.
   /// </summary>
   [Theory]
   [InlineData("http", true)]
   [InlineData("https", false)]
   public async Task Helper_answers_only_the_remotes_scheme(string scheme, bool answered)
   {
      var url = _server.RepositoryUrl;
      var call = new GitCall("Git could not fill a credential", ["credential", "fill"], TimeSpan.FromSeconds(30))
      {
         WorkingDirectory = _fixture.ScratchPath,
         Remote = Remote().Remote,
         Input = $"capability[]=authtype\nprotocol={scheme}\nhost={url.Authority}\npath={url.AbsolutePath.TrimStart('/')}\n\n",
      };
      Task<GitOutput> fill() => new GitRunner(new GitOptions()).RunAsync(call, Cancellation);

      if (answered)
      {
         Assert.True((await fill()).Text.Contains("authtype=Bearer\n", StringComparison.Ordinal), "The helper gave no answer.");
      }
      else
      {
         Assert.IsNotType<GitAuthException>(await Assert.ThrowsAsync<GitException>((Func<Task<GitOutput>>)fill));
         Assert.Equal(0, _credential.Answers);
      }
   }

   /// <summary>A <see cref="RecordingGit"/> records every git run, so the argument lists of git and of its child processes are recorded, the helper's among them.</summary>
   [Fact]
   public async Task Credential_stays_out_of_arguments_traces_files_logs_and_errors()
   {
      var git = new RecordingGit(_fixture.ScratchPath);
      var cache = NewCache(git.Options);

      var repository = await cache.OpenAsync(Remote(), [], FetchPolicy.Sync, Cancellation);
      await cache.OpenAsync(Remote(), [_head], FetchPolicy.Sync, Cancellation);
      await repository.ListChangedFilesAsync(_base, _head, Cancellation);
      await repository.DiffFileAsync(_base, _head, "a.txt", Cancellation);
      _server.Accept("Bearer another-token");
      var refused = await Assert.ThrowsAsync<GitAuthException>(() => repository.ReadFileAsync(_head, "dir/b.txt", Cancellation));

      Assert.True(_server.AuthorizedRequests > 0);
      Assert.True(git.RunsWithCredential > 0, "No git run received the credential through its environment.");
      Assert.Contains("clone", git.Arguments, StringComparison.Ordinal);
      Assert.Contains("trace: ", git.Trace, StringComparison.Ordinal);
      Assert.DoesNotContain(_token, git.Arguments, StringComparison.Ordinal);
      Assert.DoesNotContain(_token, git.Trace, StringComparison.Ordinal);
      Assert.DoesNotContain(_token, refused.Message, StringComparison.Ordinal);
      var tokenBytes = Encoding.UTF8.GetBytes(_token);
      Assert.NotEmpty(_fixture.DataFiles());
      Assert.All(_fixture.DataFiles(), file => Assert.True(File.ReadAllBytes(file).AsSpan().IndexOf(tokenBytes) < 0, $"The token is in {file}."));
      Assert.NotEmpty(_logs.Lines);
      Assert.All(_logs.Lines, line => Assert.DoesNotContain(_token, line, StringComparison.Ordinal));
   }

   /// <summary>A git double stands in for a git older than 2.46, which has no <c>git credential capability</c>.</summary>
   [Fact]
   public async Task Git_without_the_authtype_capability_is_refused_with_the_minimum_version()
   {
      var oldGit = GitDouble.Options(run => run.Subcommand == "credential" ? Task.FromResult(GitRun.Exit(129)) : run.GitAsync());

      var exception = await Assert.ThrowsAsync<GitException>(() => NewCache(oldGit).OpenAsync(Remote(), [], FetchPolicy.Sync, Cancellation));

      Assert.Equal("lotsen needs git 2.46 or newer, to pass credentials to git safely. Install a newer git.", exception.Message);
      Assert.Equal(0, _credential.Answers);
   }

   private static Uri Localhost(Uri url) => new UriBuilder(url) { Host = "localhost" }.Uri;

   private TourRemote Remote() => TestRemote.Of(_server.RepositoryUrl, _credential);

   private CloneCache NewCache(GitOptions? options = null) => _fixture.NewCache(options, logger: _logs.CreateLogger<CloneCache>());
}
