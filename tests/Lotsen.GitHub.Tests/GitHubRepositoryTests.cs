using Lotsen.Git;
using Lotsen.GitHub;
using Lotsen.Hosting;
using Lotsen.Platforms;

namespace Lotsen.Tests;

/// <summary>Covers how the GitHub platform resolves a repository's ref through the REST API: the requests it sends, the commit it reads, and the reason it gives for each failure.</summary>
[Trait("Category", "GitHub")]
public sealed class GitHubRepositoryTests : IAsyncDisposable
{
   private const string Token = "lotsen-test-token";
   private const string Prefix = $"lotsen could not read the repository {FakeGitHubRepository.Url}. ";

   private static readonly CommitId Head = CommitId.Parse(new string('1', 40));
   private static readonly CommitId Tagged = CommitId.Parse(new string('3', 40));
   private static readonly string TagObject = new('9', 40);
   private static readonly Uri Url = new(FakeGitHubRepository.Url);

   private readonly string _root = Directory.CreateTempSubdirectory("lotsen-tests-").FullName;
   private readonly FakeGitHubRepository _gitHub = new();
   private readonly FixedAccessTokenProvider _tokens = new(Token);
   private readonly GitHubPlatform _platform;

   public GitHubRepositoryTests()
   {
      _platform = FakeGitHub.PlatformAt(_gitHub.Server.BaseUrl, _tokens, new DataDirectory(Path.Combine(_root, "data")));
      _gitHub.Branch("main", Head);
   }

   private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

   [Fact]
   public async Task Requests_read_the_repository_then_a_tag_then_a_branch_and_send_the_token_and_the_headers_that_GitHub_asks()
   {
      var resolved = await _platform.ResolveRepositoryAsync(Url, "main", Cancellation);

      Assert.Equal(new ResolvedRepository(FakeGitHubRepository.CloneUrl, "main", RepositoryRefKind.Branch, Head), resolved);
      Assert.Collection(
         _gitHub.Server.Requests,
         request => Assert.StartsWith($"GET {FakeGitHubRepository.RepositoryPath} HTTP/1.1\r\n", request, StringComparison.Ordinal),
         request => Assert.StartsWith($"GET {FakeGitHubRepository.RefPath("tags", "main")} HTTP/1.1\r\n", request, StringComparison.Ordinal),
         request => Assert.StartsWith($"GET {FakeGitHubRepository.RefPath("heads", "main")} HTTP/1.1\r\n", request, StringComparison.Ordinal));
      Assert.All(_gitHub.Server.Requests, request =>
      {
         Assert.Contains($"Authorization: Bearer {Token}\r\n", request, StringComparison.Ordinal);
         Assert.Contains("Accept: application/vnd.github+json\r\n", request, StringComparison.Ordinal);
         Assert.Contains("X-GitHub-Api-Version: 2022-11-28\r\n", request, StringComparison.Ordinal);
         Assert.Contains("User-Agent: lotsen\r\n", request, StringComparison.Ordinal);
      });
   }

   [Fact]
   public async Task Default_branch_is_the_one_that_GitHub_names()
   {
      _gitHub.Repository("develop");
      _gitHub.Branch("develop", Tagged);

      var resolved = await _platform.ResolveRepositoryAsync(Url, null, Cancellation);

      Assert.Equal(("develop", RepositoryRefKind.Branch, Tagged), (resolved.Ref, resolved.Kind, resolved.Commit));
      Assert.Equal(2, _gitHub.Server.Requests.Count);
   }

   [Fact]
   public async Task Requests_after_the_first_use_the_repository_name_that_GitHub_answers()
   {
      _gitHub.Repository("main", "octo-org/hello-universe");
      _gitHub.Branch("main", Head);

      await _platform.ResolveRepositoryAsync(Url, null, Cancellation);

      Assert.StartsWith($"GET {FakeGitHubRepository.RefPath("heads", "main", "octo-org/hello-universe")} HTTP/1.1\r\n", _gitHub.Server.Requests[^1], StringComparison.Ordinal);
   }

   [Theory]
   [InlineData("feature/grüße", "/repos/octo-org/hello-world/git/ref/heads/feature/gr%C3%BC%C3%9Fe")]
   [InlineData("fix#1", "/repos/octo-org/hello-world/git/ref/heads/fix%231")]
   public async Task Ref_name_is_escaped_in_each_segment_of_the_reference_path(string name, string path)
   {
      _gitHub.Branch(name, Head);

      await _platform.ResolveRepositoryAsync(Url, name, Cancellation);

      Assert.StartsWith($"GET {path} HTTP/1.1\r\n", _gitHub.Server.Requests[^1], StringComparison.Ordinal);
   }

   [Fact]
   public async Task Annotated_tag_resolves_to_the_commit_that_its_tag_objects_name()
   {
      _gitHub.Tag("v1.0.0", TagObject, "tag");
      _gitHub.TagObject(TagObject, new string('8', 40), "tag");
      _gitHub.TagObject(new string('8', 40), Tagged.Value, "commit");

      var resolved = await _platform.ResolveRepositoryAsync(Url, "v1.0.0", Cancellation);

      Assert.Equal(("v1.0.0", RepositoryRefKind.Tag, Tagged), (resolved.Ref, resolved.Kind, resolved.Commit));
   }

   [Fact]
   public async Task Tag_of_a_tree_is_a_failure_that_names_what_it_tags()
   {
      _gitHub.Tag("tree-tag", TagObject, "tag");
      _gitHub.TagObject(TagObject, new string('8', 40), "tree");

      var thrown = await Assert.ThrowsAsync<PlatformException>(() => _platform.ResolveRepositoryAsync(Url, "tree-tag", Cancellation));

      Assert.Equal(Prefix + "The tag 'tree-tag' names a tree, not a commit. Name a branch, a tag of a commit or a full commit ID, then try again.", thrown.Reason);
   }

   /// <summary>A server whose tag object names itself would otherwise take requests forever.</summary>
   [Fact]
   public async Task Tag_objects_that_name_each_other_stop_after_a_bound()
   {
      _gitHub.Tag("loop", TagObject, "tag");
      _gitHub.TagObject(TagObject, TagObject, "tag");

      var thrown = await Assert.ThrowsAsync<PlatformException>(() => _platform.ResolveRepositoryAsync(Url, "loop", Cancellation));

      Assert.Equal(Prefix + "GitHub sent an answer that lotsen cannot read.", thrown.Reason);
      Assert.True(_gitHub.Server.Requests.Count < 20, $"The resolve sent {_gitHub.Server.Requests.Count} requests.");
   }

   [Fact]
   public async Task Full_commit_id_is_read_in_lowercase_from_the_commits_of_the_repository()
   {
      _gitHub.Commit(Tagged);

      var resolved = await _platform.ResolveRepositoryAsync(Url, Tagged.Value.ToUpperInvariant(), Cancellation);

      Assert.Equal(new ResolvedRepository(FakeGitHubRepository.CloneUrl, Tagged.Value, RepositoryRefKind.Commit, Tagged), resolved);
      Assert.StartsWith($"GET {FakeGitHubRepository.RepositoryPath}/git/commits/{Tagged} HTTP/1.1\r\n", _gitHub.Server.Requests[^1], StringComparison.Ordinal);
   }

   [Theory]
   [InlineData("gone")]
   [InlineData("3333333")]
   [InlineData("7777777777777777777777777777777777777777")]
   public async Task Missing_ref_names_the_ref_and_keeps_the_token(string @ref)
   {
      var thrown = await Assert.ThrowsAsync<PlatformException>(() => _platform.ResolveRepositoryAsync(Url, @ref, Cancellation));

      Assert.Equal(Prefix + $"GitHub has no branch, tag or commit '{@ref}' in the repository. Check the ref, and give a commit by its full ID, then try again.", thrown.Reason);
      Assert.Empty(_tokens.Rejected);
   }

   /// <summary>A ref with dot segments would otherwise make another API path, such as the reference of the branch <c>main</c>.</summary>
   [Theory]
   [InlineData("x/../../heads/main")]
   [InlineData(" ")]
   [InlineData("main..x")]
   [InlineData("a:b")]
   public async Task Invalid_ref_names_the_ref_and_sends_no_request(string @ref)
   {
      var thrown = await Assert.ThrowsAsync<PlatformException>(() => _platform.ResolveRepositoryAsync(Url, @ref, Cancellation));

      Assert.Equal(Prefix + $"GitHub has no branch, tag or commit '{@ref}' in the repository. Check the ref, and give a commit by its full ID, then try again.", thrown.Reason);
      Assert.Empty(_gitHub.Server.Requests);
   }

   [Fact]
   public async Task Missing_default_branch_is_a_failure_that_names_the_branch()
   {
      _gitHub.Repository("empty");

      var thrown = await Assert.ThrowsAsync<PlatformException>(() => _platform.ResolveRepositoryAsync(Url, null, Cancellation));

      Assert.Equal(Prefix + "GitHub has no default branch 'empty' in the repository, so the repository has no commit to show yet. Push a commit to the repository, or name a ref, then try again.", thrown.Reason);
   }

   [Fact]
   public async Task Missing_repository_names_missing_access_of_the_gh_sign_in_and_rejects_the_token()
   {
      _gitHub.Refuse(404, "Not Found");

      var thrown = await Assert.ThrowsAsync<PlatformException>(() => _platform.ResolveRepositoryAsync(Url, null, Cancellation));

      Assert.Equal(
         Prefix + "GitHub answered 404: the repository was not found, or your gh sign-in has no access to it. Check the URL, or run 'gh auth login' with an account that can see the repository, then try again.",
         thrown.Reason);
      Assert.Equal([Token], _tokens.Rejected);
   }

   [Fact]
   public async Task Moved_repository_without_a_location_names_a_new_tour_from_the_current_url_as_the_fix()
   {
      _gitHub.Refuse(301, "Moved Permanently");

      var thrown = await Assert.ThrowsAsync<PlatformException>(() => _platform.ResolveRepositoryAsync(Url, null, Cancellation));

      Assert.Equal(
         Prefix + "GitHub answered 301: the repository has moved to an address that lotsen does not follow. Start a new tour from the repository's current URL.",
         thrown.Reason);
   }

   [Theory]
   [InlineData("https://github.com/octo-org/hello-world/tree/main")]
   [InlineData("https://example.com/octo-org/hello-world")]
   public async Task Url_that_is_no_normalized_repository_url_is_a_platform_failure_that_names_the_form(string url)
   {
      var thrown = await Assert.ThrowsAsync<PlatformException>(() => _platform.ResolveRepositoryAsync(new Uri(url), null, Cancellation));

      Assert.Equal($"'{url}' is not a GitHub repository URL without a ref. Expected https://github.com/{{owner}}/{{repository}}.", thrown.Reason);
      Assert.Empty(_gitHub.Server.Requests);
   }

   public async ValueTask DisposeAsync()
   {
      _platform.Dispose();
      await _gitHub.DisposeAsync();
      TestDirectories.Delete(_root, TimeSpan.FromSeconds(5));
   }
}
