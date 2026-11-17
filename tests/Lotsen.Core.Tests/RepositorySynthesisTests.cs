using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Tours;
using static Lotsen.Tests.OperationCall;
using static Lotsen.Tests.ThreadCalls;

namespace Lotsen.Tests;

/// <summary>Covers <c>write_synthesis</c> and <c>read_summary</c> on a repository tour, whose synthesis keeps the snapshot that its <c>ref:</c> links read at.</summary>
[Trait("Category", "Summary")]
public sealed class RepositorySynthesisTests : IClassFixture<RepositoryTourFixture>
{
   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

   private readonly RepositoryTourFixture _tour;

   public RepositorySynthesisTests(RepositoryTourFixture fixture)
   {
      _tour = fixture;
   }

   private ServerHarness Harness => _tour.Harness;

   [Fact]
   public async Task Bound_session_writes_the_synthesis_of_a_repository_tour_at_its_snapshot_and_both_summaries_return_it()
   {
      var tourId = _tour.TourIds["orient"];
      const string Text = "The app counts to three. See [the middle](ref:head/src/app.txt#L2-2).";

      var written = await Harness.AgentCallAsync(On("write_synthesis", tourId), Body(Text, await FingerprintAsync(tourId)));
      var ui = await Harness.CallAsync(On("read_summary", tourId));
      var agent = await Harness.AgentCallAsync(On("read_summary", tourId));

      Assert.Equal(tourId, (string)written["tourId"]!);
      Assert.All([ui, agent], summary =>
      {
         var synthesis = summary["synthesis"]!;
         Assert.Equal(Text, (string)synthesis["text"]!);
         Assert.Equal((DateTimeOffset)written["writtenAt"]!, (DateTimeOffset)synthesis["writtenAt"]!);
         Assert.False((bool)synthesis["predatesSummary"]!);
         Assert.Equal(_tour.Head.Value, (string)synthesis["pin"]!["head"]!);
         Assert.Null(synthesis["pin"]!["mergeBase"]);
      });
   }

   [Fact]
   public async Task Synthesis_of_a_repository_tour_with_a_ref_link_at_the_base_is_refused_and_stores_nothing()
   {
      var tourId = _tour.TourIds["prepare"];

      using var response = await Harness.AgentSendJsonAsync(On("write_synthesis", tourId), Body("See [the old line](ref:base/src/app.txt#L2-2).", await FingerprintAsync(tourId)));

      var error = await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("text", (string)error["field"]!);
      Assert.Null((await Harness.CallAsync(On("read_summary", tourId)))["synthesis"]);
   }

   [Fact]
   public async Task Synthesis_of_a_repository_tour_that_is_still_pinning_is_refused_with_its_pin()
   {
      var tourId = (string)(await Harness.AgentCallAsync("create_tour", RepositoryPinningTests.CreateBody(@ref: null, purpose: "evaluate")))["id"]!;
      await Harness.PinsIdleAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);
      Assert.True(TourId.TryParse(tourId, out var id), $"The tour ID {tourId} does not parse.");
      Harness.ChangeStored(id, latest => latest with { Pin = new TourPin.Pinning() });

      using var response = await Harness.AgentSendJsonAsync(On("write_synthesis", tourId), Body("A synthesis without links.", await FingerprintAsync(tourId)));

      var error = await ErrorAsync(response, HttpStatusCode.Conflict, "tourNotPinned");
      Assert.Equal("pinning", (string)error["pin"]!["state"]!);
      Assert.Null((await Harness.CallAsync(On("read_summary", tourId)))["synthesis"]);
   }

   private static string Body(string text, string basedOn) => new JsonObject { ["text"] = text, ["basedOn"] = basedOn }.ToJsonString();

   private async Task<string> FingerprintAsync(string tourId) => (string)(await Harness.AgentCallAsync(On("read_summary", tourId)))["summaryFingerprint"]!;
}
