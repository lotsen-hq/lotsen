using Lotsen.Platforms;
using Lotsen.Tours;

namespace Lotsen.Tests;

/// <summary>Covers how <see cref="PullRequestPlatforms"/> finds a target's platform and its mention links, and what it does for a target without one.</summary>
[Trait("Category", "Platform")]
public sealed class PullRequestPlatformsTests
{
   private readonly PullRequestPlatforms _platforms = new([new TestPlatform()]);

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
   public void Repository_target_has_no_platform_mentions_or_label_of_a_platform()
   {
      var target = new RepositoryTarget(new Uri($"https://{TestPlatform.DefaultHost}/shop"), "main", TestPlatform.DefaultId);

      var thrown = Assert.Throws<PlatformException>(() => _platforms.Of(target));

      Assert.Equal($"lotsen reaches a platform only for a pull request's tour, and this tour is of the repository https://{TestPlatform.DefaultHost}/shop.", thrown.Reason);
      Assert.Null(_platforms.MentionsOf(target).UrlOf(MentionKind.PullRequest, 7));
      Assert.Equal($"https://{TestPlatform.DefaultHost}/shop", _platforms.Label(target));
   }

   private sealed record OtherTarget : TourTarget;
}
