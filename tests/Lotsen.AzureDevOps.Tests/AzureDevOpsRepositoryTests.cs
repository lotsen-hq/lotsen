using Lotsen.AzureDevOps;
using Lotsen.Git;
using Lotsen.Platforms;

namespace Lotsen.Tests;

/// <summary>Covers how the Azure DevOps platform resolves a repository's ref through the REST API: the requests it sends, the commit it reads, and the reason it gives for each failure.</summary>
[Trait("Category", "AzureDevOps")]
public sealed class AzureDevOpsRepositoryTests : IAsyncDisposable
{
   private const string Token = "lotsen-test-token";
   private const string Prefix = $"lotsen could not read the repository {FakeAzureDevOpsRepository.Url}. ";

   private static readonly CommitId Head = CommitId.Parse(new string('1', 40));
   private static readonly CommitId Other = CommitId.Parse(new string('2', 40));
   private static readonly CommitId Tagged = CommitId.Parse(new string('3', 40));
   private static readonly Uri Url = new(FakeAzureDevOpsRepository.Url);

   private readonly FakeAzureDevOpsRepository _azureDevOps = new();
   private readonly FixedAccessTokenProvider _tokens = new(Token);
   private readonly AzureDevOpsPlatform _platform;

   public AzureDevOpsRepositoryTests()
   {
      _platform = FakeAzureDevOpsRepository.PlatformAt(_azureDevOps.Server.BaseUrl, _tokens);
      _azureDevOps.Branch("main", Head);
   }

   private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

   [Fact]
   public async Task Requests_read_the_repository_by_name_then_its_tags_then_its_branches_by_its_id_and_send_the_token()
   {
      var resolved = await _platform.ResolveRepositoryAsync(Url, "main", Cancellation);

      Assert.Equal(new ResolvedRepository(FakeAzureDevOpsRepository.CloneUrl, "main", RepositoryRefKind.Branch, Head), resolved);
      Assert.Equal(FakeAzureDevOpsRepository.Url, resolved.RemoteUrl.OriginalString);
      Assert.Collection(
         _azureDevOps.Server.Requests,
         request => Assert.StartsWith($"GET {FakeAzureDevOpsRepository.RepositoryPath}?api-version=7.1 HTTP/1.1\r\n", request, StringComparison.Ordinal),
         request => Assert.StartsWith($"GET {FakeAzureDevOpsRepository.RefsTarget("tags/main")} HTTP/1.1\r\n", request, StringComparison.Ordinal),
         request => Assert.StartsWith($"GET {FakeAzureDevOpsRepository.RefsTarget("heads/main")} HTTP/1.1\r\n", request, StringComparison.Ordinal));
      Assert.All(_azureDevOps.Server.Requests, request =>
      {
         Assert.Contains($"Authorization: Bearer {Token}\r\n", request, StringComparison.Ordinal);
         Assert.Contains("X-TFS-FedAuthRedirect: Suppress\r\n", request, StringComparison.Ordinal);
      });
   }

   [Fact]
   public async Task Default_branch_is_the_one_that_Azure_DevOps_names()
   {
      _azureDevOps.Repository("develop");
      _azureDevOps.Branch("develop", Other);

      var resolved = await _platform.ResolveRepositoryAsync(Url, null, Cancellation);

      Assert.Equal(new ResolvedRepository(FakeAzureDevOpsRepository.CloneUrl, "develop", RepositoryRefKind.Branch, Other), resolved);
      Assert.Equal(
         [$"{FakeAzureDevOpsRepository.RepositoryPath}?api-version=7.1", FakeAzureDevOpsRepository.RefsTarget("heads/develop")],
         _azureDevOps.Server.Requests.Select(request => request.Split(' ')[1]));
   }

   /// <summary>The filter of the refs request matches every ref whose name starts with it, so only a ref of the exact name counts.</summary>
   [Fact]
   public async Task Branch_resolves_by_its_exact_name_among_the_refs_that_start_with_it()
   {
      _azureDevOps.Branch("release-2", Other);
      _azureDevOps.Branch("release/x", Other);
      _azureDevOps.Branch("release", Tagged);

      var resolved = await _platform.ResolveRepositoryAsync(Url, "release", Cancellation);

      Assert.Equal((RepositoryRefKind.Branch, Tagged), (resolved.Kind, resolved.Commit));
   }

   [Fact]
   public async Task Annotated_tag_resolves_to_the_commit_that_Azure_DevOps_peels_it_to()
   {
      _azureDevOps.AnnotatedTag("v1.2.0", new string('9', 40), Tagged);

      var resolved = await _platform.ResolveRepositoryAsync(Url, "v1.2.0", Cancellation);

      Assert.Equal(new ResolvedRepository(FakeAzureDevOpsRepository.CloneUrl, "v1.2.0", RepositoryRefKind.Tag, Tagged), resolved);
   }

   [Fact]
   public async Task Full_commit_id_is_read_in_lowercase_from_the_commits_of_the_repository()
   {
      _azureDevOps.Commit(Tagged);

      var resolved = await _platform.ResolveRepositoryAsync(Url, Tagged.Value.ToUpperInvariant(), Cancellation);

      Assert.Equal(new ResolvedRepository(FakeAzureDevOpsRepository.CloneUrl, Tagged.Value, RepositoryRefKind.Commit, Tagged), resolved);
      Assert.Equal($"{FakeAzureDevOpsRepository.ByIdPath}/commits/{Tagged}?api-version=7.1", _azureDevOps.Server.Requests[^1].Split(' ')[1]);
   }

   [Theory]
   [InlineData("gone")]
   [InlineData("mai")]
   [InlineData("1111111")]
   [InlineData("7777777777777777777777777777777777777777")]
   public async Task Missing_ref_names_the_ref_and_keeps_the_token(string @ref)
   {
      var thrown = await Assert.ThrowsAsync<PlatformException>(() => _platform.ResolveRepositoryAsync(Url, @ref, Cancellation));

      Assert.Equal(Prefix + $"Azure DevOps has no branch, tag or commit '{@ref}' in the repository. Check the ref, and give a commit by its full ID, then try again.", thrown.Reason);
      Assert.Empty(_tokens.Rejected);
   }

   [Theory]
   [InlineData("x/../../heads/main")]
   [InlineData(" ")]
   [InlineData("main..x")]
   [InlineData("a:b")]
   public async Task Invalid_ref_names_the_ref_and_sends_no_request(string @ref)
   {
      var thrown = await Assert.ThrowsAsync<PlatformException>(() => _platform.ResolveRepositoryAsync(Url, @ref, Cancellation));

      Assert.Equal(Prefix + $"Azure DevOps has no branch, tag or commit '{@ref}' in the repository. Check the ref, and give a commit by its full ID, then try again.", thrown.Reason);
      Assert.Empty(_azureDevOps.Server.Requests);
   }

   [Fact]
   public async Task Repository_without_a_default_branch_is_a_failure_that_names_a_push_or_a_ref_as_the_fix()
   {
      _azureDevOps.Repository(null);

      var thrown = await Assert.ThrowsAsync<PlatformException>(() => _platform.ResolveRepositoryAsync(Url, null, Cancellation));

      Assert.Equal(Prefix + "Azure DevOps names no default branch of the repository, so the repository has no commit to show yet. Push a commit to the repository, or name a ref, then try again.", thrown.Reason);
   }

   [Fact]
   public async Task Missing_default_branch_is_a_failure_that_names_the_branch()
   {
      _azureDevOps.Repository("empty");

      var thrown = await Assert.ThrowsAsync<PlatformException>(() => _platform.ResolveRepositoryAsync(Url, null, Cancellation));

      Assert.Equal(Prefix + "Azure DevOps has no default branch 'empty' in the repository, so the repository has no commit to show yet. Push a commit to the repository, or name a ref, then try again.", thrown.Reason);
   }

   [Fact]
   public async Task Missing_repository_names_the_url_and_the_access_of_the_signed_in_account()
   {
      _azureDevOps.Refuse(404, "The repository was not found.");

      var thrown = await Assert.ThrowsAsync<PlatformException>(() => _platform.ResolveRepositoryAsync(Url, null, Cancellation));

      Assert.Equal(Prefix + "Azure DevOps answered 404: repository not found. Check the URL, and that the signed-in account can see the repository.", thrown.Reason);
   }

   [Fact]
   public async Task Refused_token_names_az_login_and_rejects_the_token()
   {
      _azureDevOps.Refuse(401, "The token was refused.");

      var thrown = await Assert.ThrowsAsync<PlatformException>(() => _platform.ResolveRepositoryAsync(Url, null, Cancellation));

      Assert.Equal(Prefix + "Azure DevOps answered 401: not signed in or token rejected. Run 'az login' with an account that can see the repository, then try again.", thrown.Reason);
      Assert.Equal([Token], _tokens.Rejected);
   }

   [Fact]
   public async Task Repository_on_the_visualstudio_host_is_read_below_that_host()
   {
      await Assert.ThrowsAsync<PlatformException>(() => _platform.ResolveRepositoryAsync(new Uri("https://contoso.visualstudio.com/shop/_git/web"), "main", Cancellation));

      Assert.StartsWith("GET /shop/_apis/git/repositories/web?api-version=7.1 HTTP/1.1\r\n", Assert.Single(_azureDevOps.Server.Requests), StringComparison.Ordinal);
   }

   [Fact]
   public async Task Names_with_spaces_are_escaped_in_the_request_path()
   {
      var thrown = await Assert.ThrowsAsync<PlatformException>(() => _platform.ResolveRepositoryAsync(new Uri("https://dev.azure.com/contoso/My%20Shop/_git/web%20app"), null, Cancellation));

      Assert.StartsWith("lotsen could not read the repository https://dev.azure.com/contoso/My%20Shop/_git/web%20app. Azure DevOps answered 404", thrown.Reason, StringComparison.Ordinal);
      Assert.StartsWith("GET /contoso/My%20Shop/_apis/git/repositories/web%20app?api-version=7.1 HTTP/1.1\r\n", Assert.Single(_azureDevOps.Server.Requests), StringComparison.Ordinal);
   }

   [Theory]
   [InlineData("https://dev.azure.com/contoso/shop/_git/web?version=GBmain", "'https://dev.azure.com/contoso/shop/_git/web?version=GBmain'")]
   [InlineData("https://example.com/contoso/shop/_git/web", "'https://example.com/contoso/shop/_git/web'")]
   [InlineData("https://contoso:secret@dev.azure.com/contoso/shop/_git/web", "'https://dev.azure.com/contoso/shop/_git/web'")]
   public async Task Url_that_is_no_normalized_repository_url_is_a_platform_failure_that_names_the_form_without_user_information(string url, string named)
   {
      var thrown = await Assert.ThrowsAsync<PlatformException>(() => _platform.ResolveRepositoryAsync(new Uri(url), null, Cancellation));

      Assert.Equal($"{named} is not an Azure DevOps repository URL without a ref. Expected https://dev.azure.com/{{organization}}/{{project}}/_git/{{repository}}.", thrown.Reason);
      Assert.Empty(_azureDevOps.Server.Requests);
   }

   public async ValueTask DisposeAsync()
   {
      _platform.Dispose();
      await _azureDevOps.DisposeAsync();
   }
}
