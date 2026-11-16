using Lotsen.Tours;
using static Lotsen.Tests.TourValues;

namespace Lotsen.Tests;

/// <summary>Covers which refusal <see cref="OperationError.TourNotPinned"/> and <see cref="OperationError.NoIterationToCheck"/> return for each kind of target in each pin state.</summary>
[Trait("Category", "Rules")]
public sealed class PinRefusalTests
{
   private static readonly Dictionary<string, TourPin> Pins = new()
   {
      ["pinning"] = new TourPin.Pinning(),
      ["pinned"] = Pinned,
      ["snapshot"] = Snapshot,
      ["failed"] = new TourPin.Failed("Sign in again."),
   };

   public static TheoryData<string, string> Cases => new()
   {
      { "pullRequest", "pinning" },
      { "pullRequest", "failed" },
      { "repository", "pinning" },
      { "repository", "pinned" },
      { "repository", "snapshot" },
      { "repository", "failed" },
   };

   [Theory]
   [MemberData(nameof(Cases))]
   public void Refusal_names_a_repository_tour_whatever_its_pin_and_a_pull_requests_tour_by_its_pin(string target, string pin)
   {
      var tour = (target == "repository" ? RepositoryTour() : Tour()) with { Pin = Pins[pin] };
      var expected = target == "repository" ? OperationErrorCode.NotPullRequestTour : OperationErrorCode.TourNotPinned;

      Assert.Equal((expected, expected), (OperationError.TourNotPinned(tour).Code, OperationError.NoIterationToCheck(tour).Code));
   }
}
