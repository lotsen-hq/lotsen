using System.Net;
using Lotsen.Operations;
using Lotsen.Sessions;
using Lotsen.Tours;
using Microsoft.Extensions.DependencyInjection;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>Covers how the UI deletes a tour that no session holds, and how a delete releases the tour's binding.</summary>
[Trait("Category", "Roles")]
public sealed class TourDeleteRoleTests : IAsyncLifetime
{
   private ServerHarness _harness = null!;

   private ThreadCalls Calls => new(_harness);

   public async ValueTask InitializeAsync() => _harness = await ServerHarness.StartAsync();

   public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

   [Fact]
   public async Task Ui_deletes_a_tour_that_no_session_is_attached_to()
   {
      var tourId = (string)_harness.CreateUnboundTour(ThreadCalls.CreateBody)["id"]!;

      await Calls.CallAsync(await Calls.UiAsync(On("delete_tour", tourId)));

      Assert.False(File.Exists(Path.Combine(_harness.DataDirectory.ToursPath, $"{tourId}.json")));
   }

   [Fact]
   public async Task Deleting_a_bound_tour_releases_its_binding_and_the_sessions_later_calls_are_refused_as_not_found()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var tourId = await Calls.CreateAsync(session);
      Assert.True(TourId.TryParse(tourId, out var id));
      Assert.NotNull(Holder(id));

      await Calls.CallAsync(await Calls.UiAsync(On("delete_tour", tourId)));

      Assert.Null(Holder(id));
      var notFound = $"tourId: no tour has the ID '{tourId}'.";
      foreach (var (call, body) in new[]
      {
         (On("read_state", tourId), (string?)null),
         (On("set_plan", tourId), """{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "title": "S", "chapter": "a", "kind": "inspect" }] }"""),
         (On("attach_tour", tourId), null),
      })
      {
         using var response = await Calls.SendAsync(Calls.Agent(session, call), body);
         var error = await ThreadCalls.ErrorAsync(response, HttpStatusCode.NotFound, "tourNotFound");
         Assert.Equal(notFound, (string)error["reason"]!);
      }

      Assert.Null(Holder(id));
      Assert.False(File.Exists(Path.Combine(_harness.DataDirectory.ToursPath, $"{tourId}.json")));
   }

   private Recipient? Holder(TourId id) => _harness.Writer.Exclusive(locked => locked.Holder(id));
}
