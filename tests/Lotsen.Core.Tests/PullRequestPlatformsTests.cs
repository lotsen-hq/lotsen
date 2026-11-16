using Lotsen.Credentials;
using Lotsen.Platforms;
using Lotsen.Tours;

namespace Lotsen.Tests;

/// <summary>Covers how <see cref="PullRequestPlatforms"/> finds a target's platform, its label and its mention links, and what it does for a target without one.</summary>
[Trait("Category", "Platform")]
public sealed class PullRequestPlatformsTests
{
   private const string RepositoryUrl = $"https://{TestPlatform.DefaultHost}/shop";

   private readonly TestPlatform _platform = new();
   private readonly PullRequestPlatforms _platforms;

   public PullRequestPlatformsTests()
   {
      _platforms = new PullRequestPlatforms([_platform]);
   }

   [Fact]
   public void Target_of_a_registered_platform_gets_that_platform()
   {
      Assert.Equal(TestPlatform.DefaultId, _platforms.Of(new PullRequestTarget(TestPlatform.DefaultId, new Uri(TestPlatform.PrUrl))).Id);
   }

   [Fact]
   public void Target_of_an_unknown_platform_is_an_external_failure_that_names_the_platform()
   {
      var thrown = Assert.Throws<PlatformException>(() => _platforms.Of(new PullRequestTarget("gone", new Uri(TestPlatform.PrUrl))));

      Assert.StartsWith($"lotsen does not know the platform 'gone' of the pull request {TestPlatform.PrUrl}", thrown.Reason, StringComparison.Ordinal);
   }

   /// <summary>No such target exists outside tests, so a lookup for one is a fault of lotsen.</summary>
   [Fact]
   public void Target_of_another_kind_is_a_fault_and_no_external_failure()
   {
      var thrown = Assert.ThrowsAny<Exception>(() => _platforms.Of(new OtherTarget()));

      Assert.IsNotType<ExternalFailureException>(thrown, exactMatch: false);
   }

   [Fact]
   public void Mentions_of_a_target_resolve_through_its_platform()
   {
      var mentions = _platforms.MentionsOf(new PullRequestTarget(TestPlatform.DefaultId, new Uri(TestPlatform.PrUrl)));

      Assert.Equal($"https://{TestPlatform.DefaultHost}/shop/workitems/1234", mentions.UrlOf(MentionKind.WorkItem, 1234)?.AbsoluteUri);
      Assert.Equal($"https://{TestPlatform.DefaultHost}/shop/pull/7", mentions.UrlOf(MentionKind.PullRequest, 7)?.AbsoluteUri);
   }

   [Fact]
   public void Mentions_of_a_target_of_an_unknown_platform_place_nothing()
   {
      var mentions = _platforms.MentionsOf(new PullRequestTarget("gone", new Uri(TestPlatform.PrUrl)));

      Assert.Null(mentions.UrlOf(MentionKind.WorkItem, 1234));
      Assert.Null(mentions.UrlOf(MentionKind.PullRequest, 7));
   }

   [Fact]
   public void Repository_target_reaches_no_pull_request_platform()
   {
      var target = new RepositoryTarget(new Uri(RepositoryUrl), "main", TestPlatform.DefaultId);

      var thrown = Assert.Throws<PlatformException>(() => _platforms.Of(target));

      Assert.Equal($"lotsen reaches a platform only for a pull request's tour, and this tour is of the repository {RepositoryUrl}.", thrown.Reason);
   }

   [Theory]
   [InlineData("https://platform.test/shop.git", null)]
   [InlineData("https://platform.test/shop/tree/feature/greeting", "feature/greeting")]
   public void Repository_url_is_recognized_as_a_target_of_its_platform(string text, string? @ref)
   {
      Assert.Equal(new RepositoryTarget(new Uri(RepositoryUrl), @ref, TestPlatform.DefaultId), _platforms.RecognizeRepository(text));
   }

   [Fact]
   public void Repository_of_a_platform_without_repositories_is_not_recognized()
   {
      var platforms = new PullRequestPlatforms([new PullRequestOnlyPlatform(new TestPlatform())]);

      Assert.Null(platforms.RecognizeRepository(RepositoryUrl));
      Assert.Equal("", platforms.ExpectedRepositoryForms);
      Assert.NotNull(platforms.Recognize(TestPlatform.PrUrl));
   }

   [Fact]
   public void Repository_url_of_no_platform_and_pull_request_url_are_not_recognized_as_repositories()
   {
      Assert.Null(_platforms.RecognizeRepository("https://example.invalid/shop"));
      Assert.Null(_platforms.RecognizeRepository(TestPlatform.PrUrl));
   }

   /// <summary>A target refuses a whitespace ref, so recognizing one would throw a fault for the user's text.</summary>
   [Fact]
   public void Repository_url_at_a_whitespace_ref_is_not_recognized()
   {
      Assert.Null(_platforms.RecognizeRepository($"{RepositoryUrl}/tree/%20"));
   }

   [Fact]
   public void Repository_target_gets_its_platforms_repository_side()
   {
      Assert.Same(_platform, _platforms.RepositoryOf(new RepositoryTarget(new Uri(RepositoryUrl), platform: TestPlatform.DefaultId)));
   }

   [Fact]
   public void Repository_target_of_an_unknown_platform_is_an_external_failure_that_names_the_platform()
   {
      var thrown = Assert.Throws<PlatformException>(() => _platforms.RepositoryOf(new RepositoryTarget(new Uri(RepositoryUrl), platform: "gone")));

      Assert.StartsWith($"lotsen does not know the platform 'gone' of the repository {RepositoryUrl}", thrown.Reason, StringComparison.Ordinal);
   }

   [Fact]
   public void Repository_target_of_a_platform_without_repositories_is_an_external_failure_that_names_the_platform()
   {
      var platforms = new PullRequestPlatforms([new PullRequestOnlyPlatform(new TestPlatform())]);

      var thrown = Assert.Throws<PlatformException>(() => platforms.RepositoryOf(new RepositoryTarget(new Uri(RepositoryUrl), platform: TestPlatform.DefaultId)));

      Assert.StartsWith($"lotsen does not know the platform '{TestPlatform.DefaultId}' of the repository {RepositoryUrl}", thrown.Reason, StringComparison.Ordinal);
   }

   /// <summary>A caller decides on the path without a platform before it asks for one, so a lookup for such a target is a fault of lotsen.</summary>
   [Fact]
   public void Repository_target_without_a_platform_is_a_fault_and_no_external_failure()
   {
      var thrown = Assert.ThrowsAny<Exception>(() => _platforms.RepositoryOf(new RepositoryTarget(new Uri(RepositoryUrl))));

      Assert.IsNotType<ExternalFailureException>(thrown, exactMatch: false);
   }

   [Theory]
   [InlineData(null, "shop")]
   [InlineData("v1.2.0", "shop@v1.2.0")]
   public void Repository_target_is_labeled_by_its_platform_with_its_ref(string? @ref, string label)
   {
      Assert.Equal(label, _platforms.Label(new RepositoryTarget(new Uri(RepositoryUrl), @ref, TestPlatform.DefaultId)));
   }

   [Theory]
   [InlineData(TestPlatform.DefaultId, null, RepositoryUrl)]
   [InlineData("gone", "main", RepositoryUrl + "@main")]
   [InlineData(null, "main", "https://example.invalid/shop.git@main")]
   public void Repository_target_that_no_registered_platform_labels_is_labeled_by_its_url_with_its_ref(string? platform, string? @ref, string label)
   {
      var url = platform is null ? new Uri("https://example.invalid/shop.git") : new Uri(RepositoryUrl);
      var platforms = platform == TestPlatform.DefaultId ? new PullRequestPlatforms([new PullRequestOnlyPlatform(new TestPlatform())]) : _platforms;

      Assert.Equal(label, platforms.Label(new RepositoryTarget(url, @ref, platform)));
   }

   [Fact]
   public void Mentions_of_a_repository_target_resolve_through_its_platform()
   {
      var mentions = _platforms.MentionsOf(new RepositoryTarget(new Uri(RepositoryUrl), "main", TestPlatform.DefaultId));

      Assert.Equal($"{RepositoryUrl}/workitems/1234", mentions.UrlOf(MentionKind.WorkItem, 1234)?.AbsoluteUri);
      Assert.Equal($"{RepositoryUrl}/pull/7", mentions.UrlOf(MentionKind.PullRequest, 7)?.AbsoluteUri);
   }

   [Theory]
   [InlineData("gone")]
   [InlineData(null)]
   public void Mentions_of_a_repository_target_that_no_registered_platform_hosts_place_nothing(string? platform)
   {
      var mentions = _platforms.MentionsOf(new RepositoryTarget(new Uri(RepositoryUrl), "main", platform));

      Assert.Null(mentions.UrlOf(MentionKind.WorkItem, 1234));
      Assert.Null(mentions.UrlOf(MentionKind.PullRequest, 7));
   }

   private sealed record OtherTarget : TourTarget;

   /// <summary>The pull request side of a platform alone, as a platform without repositories has.</summary>
   private sealed class PullRequestOnlyPlatform : IPullRequestPlatform
   {
      private readonly IPullRequestPlatform _platform;

      public PullRequestOnlyPlatform(IPullRequestPlatform platform)
      {
         _platform = platform;
      }

      public string Id => _platform.Id;

      public string ExpectedForms => _platform.ExpectedForms;

      public IGitCredential GitCredential => _platform.GitCredential;

      public Uri? Recognize(string text) => _platform.Recognize(text);

      public bool IsSamePullRequest(Uri url, Uri other) => _platform.IsSamePullRequest(url, other);

      public string? Label(Uri url) => _platform.Label(url);

      public Uri? MentionUrl(Uri url, MentionKind kind, int id) => _platform.MentionUrl(url, kind, id);

      public Task<ResolvedPullRequest> ResolveAsync(Uri url, CancellationToken cancellationToken) => _platform.ResolveAsync(url, cancellationToken);
   }
}
