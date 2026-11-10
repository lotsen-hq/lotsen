using Lotsen.Pinning;
using Lotsen.Platforms;
using Lotsen.Tours;
using static Lotsen.Tests.IterationRows;

namespace Lotsen.Tests;

/// <summary>Covers that every remote whose clone the first pin, the iteration check, the iteration update and the file operations open is one that <see cref="TourRemote.Of"/> lists for the tour.</summary>
[Trait("Category", "Remap")]
public sealed class TourRemotesTests
{
   private static readonly Uri Moved = new("https://platform.test/moved/shop.git");

   private static readonly TestGitCredential Credential = new("lotsen-test-token");

   /// <summary>The platform resolves the pull request's repository at another URL than the tour's pin names, as after the repository moved.</summary>
   [Fact]
   public void First_pin_stores_the_remote_that_it_opens()
   {
      var latest = new ResolvedPullRequest(Moved, NewHead, Target, 2, PullRequestState.Active);
      var opened = TourRemote.OfIteration(latest, Credential);

      var pin = Assert.IsType<TourPin.Pinned>(Decided(IterationCore.FirstPin(PullRequest.Url, latest, Facts().Build())));

      Assert.Contains(TourRemote.Of(TourOf([]) with { Pin = pin }, Credential), remote => remote.Url == opened.Url);
   }

   [Fact]
   public void Iteration_check_stores_the_remote_that_it_opens_on_the_available_iteration()
   {
      var answer = new PlatformAnswer.Resolved(new ResolvedPullRequest(Moved, NewHead, Target, 2, PullRequestState.Active), Now);
      var tour = TourOf([]);
      var opened = TourRemote.OfIteration(answer.Latest, Credential);

      var found = Assert.IsType<CheckOutcome.Found>(Decided(IterationCore.Check(tour, answer, Facts().Build(), Now)));

      Assert.Equal([Remote, Moved], TourRemote.Of(tour with { AvailableIteration = found.Available }, Credential).Select(remote => remote.Url));
      Assert.Contains(TourRemote.Of(tour with { AvailableIteration = found.Available }, Credential), remote => remote.Url == opened.Url);
   }

   /// <summary>The update opens the available iteration's remote, and the file operations open the pin's.</summary>
   [Fact]
   public void Iteration_update_and_file_operations_open_remotes_that_the_tour_lists()
   {
      var tour = TourOf([]) with { AvailableIteration = new AvailableIteration(NewPin with { RemoteUrl = Moved }, [], Now) };

      var listed = TourRemote.Of(tour, Credential).Select(remote => remote.Url).ToList();

      Assert.Contains(TourRemote.OfPin(tour.AvailableIteration!.Pin, Credential).Url, listed);
      Assert.Contains(TourRemote.OfPin((TourPin.Pinned)tour.Pin, Credential).Url, listed);
   }

   [Fact]
   public void Tour_whose_available_iteration_shares_the_pins_remote_lists_it_once_with_the_credential()
   {
      var tour = TourOf([]) with { AvailableIteration = new AvailableIteration(NewPin, [], Now) };

      var remote = Assert.Single(TourRemote.Of(tour, Credential));

      Assert.Equal(Remote, remote.Url);
      Assert.Same(Credential, remote.Remote.Credential);
   }

   [Fact]
   public void Tour_that_is_not_pinned_uses_no_remote() => Assert.Empty(TourRemote.Of(TourOf([]) with { Pin = new TourPin.Pinning() }, Credential));
}
