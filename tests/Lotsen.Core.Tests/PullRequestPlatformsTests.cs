using Lotsen.Platforms;
using Lotsen.Tours;

namespace Lotsen.Tests;

/// <summary>Covers how <see cref="PullRequestPlatforms.Of"/> finds a target's platform, and what it throws for a target without one.</summary>
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

   private sealed record OtherTarget : TourTarget;
}
