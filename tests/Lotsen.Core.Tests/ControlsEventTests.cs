using System.Text.Json.Nodes;
using Lotsen.Operations;
using static Lotsen.Tests.OperationCall;
using static Lotsen.Tests.ThreadCalls;

namespace Lotsen.Tests;

/// <summary>
/// Covers the tour's revision and the events that carry it: every store and every binding change raises the revision, each tour-scoped event carries the stored revision, and
/// <c>controlsChanged</c> follows a change exactly when the tour's offers changed.
/// </summary>
[Trait("Category", "Writer")]
public sealed class ControlsEventTests : IAsyncLifetime
{
   private const string PlanBody = """{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "title": "S", "kind": "inspect", "chapter": "a" }] }""";

   private ServerHarness _harness = null!;
   private ThreadCalls _calls = null!;

   public async ValueTask InitializeAsync()
   {
      _harness = await ServerHarness.StartAsync();
      _calls = new ThreadCalls(_harness);
   }

   public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

   [Fact]
   public async Task Store_raises_the_revision_by_one_and_a_store_that_changes_nothing_keeps_it()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var stopId = (string)(await _calls.CallAsync(_calls.Agent(session, On("set_plan", id)), PlanBody))["plan"]!["stops"]![0]!["id"]!;
      var before = Revision(id);
      var progress = $$"""{ "stopId": "{{stopId}}" }""";

      await _calls.CallAsync(await _calls.UiAsync(On("record_reading_progress", id)), progress);
      var moved = Revision(id);
      await _calls.CallAsync(await _calls.UiAsync(On("record_reading_progress", id)), progress);

      Assert.Equal(before + 1, moved);
      Assert.Equal(moved, Revision(id));
   }

   [Theory]
   [InlineData("attach")]
   [InlineData("takeOver")]
   [InlineData("ack")]
   [InlineData("drop")]
   public async Task Binding_change_raises_the_revision_and_publishes_the_binding_at_it(string change)
   {
      var session = await AgentSessionConnection.OpenAsync(_harness);
      await using var other = await AgentSessionConnection.OpenAsync(_harness);
      var id = change == "attach" ? (string)_harness.CreateUnboundTour(CreateBody)["id"]! : await _calls.CreateAsync(session);
      var nonce = change == "ack" ? await session.ProbeAsync() : null;
      var before = Revision(id);
      await using var stream = await EventStream.OpenAsync(_harness);

      switch (change)
      {
         case "attach":
            await _calls.CallAsync(_calls.Agent(session, On("attach_tour", id)), "{}");
            break;
         case "takeOver":
            await _calls.CallAsync(_calls.Agent(other, On("attach_tour", id)), """{ "takeOver": true }""");
            break;
         case "ack":
            await _calls.AckAsync(session, nonce!);
            break;
         default:
            await _harness.DropSessionAsync(session);
            break;
      }

      var binding = await NextOfAsync(stream, ServerEvents.TourBindingChanged.Name, id);

      // The session's end after the test raises the revision again, so the revision is read before it.
      var after = Revision(id);
      await session.DisposeAsync();

      Assert.Equal(before + 1, after);
      Assert.Equal(after, (long)binding["revision"]!);
   }

   [Fact]
   public async Task Ask_that_adds_a_thread_publishes_controls_changed_with_its_offers_at_the_stored_revision()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      await using var stream = await EventStream.OpenAsync(_harness);

      var thread = await _calls.AskAsync(id);
      var controls = await NextOfAsync(stream, ServerEvents.ControlsChanged.Name, id);

      Assert.Equal(Revision(id), (long)controls["revision"]!);
      Assert.NotNull(controls["offers"]!["threads"]![ThreadIdOf(thread)]);
   }

   /// <summary>The ask after the progress write is the marker: its <c>threadCreated</c> comes before the next <c>controlsChanged</c>, so the progress write published none.</summary>
   [Fact]
   public async Task Store_that_changes_no_offer_publishes_no_controls_changed()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      var stopId = (string)(await _calls.CallAsync(_calls.Agent(session, On("set_plan", id)), PlanBody))["plan"]!["stops"]![0]!["id"]!;
      await using var stream = await EventStream.OpenAsync(_harness);

      await _calls.CallAsync(await _calls.UiAsync(On("record_reading_progress", id)), $$"""{ "stopId": "{{stopId}}" }""");
      var marker = await _calls.AskAsync(id);
      var types = new List<string>();
      while (true)
      {
         var next = await stream.NextAsync(EventWait);
         types.Add(next.Type);
         if (next.Type == ServerEvents.ThreadCreated.Name && (string?)next.Data["thread"]?["id"] == ThreadIdOf(marker))
         {
            break;
         }
      }

      Assert.DoesNotContain(ServerEvents.ControlsChanged.Name, types);
      Assert.Equal(ServerEvents.ControlsChanged.Name, (await stream.NextAsync(EventWait)).Type);
   }

   [Fact]
   public async Task Session_end_publishes_controls_closed_for_the_missing_session()
   {
      var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      await using var stream = await EventStream.OpenAsync(_harness);

      await _harness.DropSessionAsync(session);
      var controls = await NextOfAsync(stream, ServerEvents.ControlsChanged.Name, id);

      Assert.Equal(Revision(id), (long)controls["revision"]!);
      Assert.Equal(("closed", "noSessionAttached"), ((string?)controls["offers"]!["ask"]!["state"], (string?)controls["offers"]!["ask"]!["code"]));
      Assert.Equal("closed", (string?)controls["offers"]!["replan"]!["state"]);
   }

   [Fact]
   public async Task Each_event_of_a_write_carries_the_revision_that_its_response_shows()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      await using var stream = await EventStream.OpenAsync(_harness);

      var planned = await EnvelopeAsync(_calls.Agent(session, On("set_plan", id)), PlanBody);
      var planSet = await NextOfAsync(stream, ServerEvents.PlanSet.Name, id);
      var asked = await EnvelopeAsync(await _calls.UiAsync(On("ask_question", id)), NoteBody("Why?"));
      var created = await NextOfAsync(stream, ServerEvents.ThreadCreated.Name, id);

      Assert.Equal((long)planned["revision"]!, (long)planSet["revision"]!);
      Assert.Equal((long)asked["revision"]!, (long)created["revision"]!);
      Assert.Equal(Revision(id), (long)created["revision"]!);
   }

   [Fact]
   public async Task Tour_deleted_carries_no_revision()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await _calls.CreateAsync(session);
      await using var stream = await EventStream.OpenAsync(_harness);

      await _calls.CallAsync(await _calls.UiAsync(On("delete_tour", id)));
      var deleted = await NextOfAsync(stream, ServerEvents.TourDeleted.Name, id);

      Assert.False(deleted.AsObject().ContainsKey(ServerEvent.RevisionProperty), $"tourDeleted: {deleted}");
   }

   [Fact]
   public void Publish_of_a_tour_scoped_event_without_a_revision_throws()
   {
      var events = new EventBroadcaster();

      Assert.Throws<InvalidOperationException>(() => events.Publish(ServerEvents.ForTourPinned(TourValues.Tour())));
   }

   private long Revision(string tourId) => _harness.StoredTour(Lotsen.Tours.TourId.TryParse(tourId, out var id) ? id : throw new InvalidOperationException("The tour ID does not parse."))!.Revision;

   // Returns the whole success body, the envelope around the output.
   private async Task<JsonNode> EnvelopeAsync(PreparedCall call, string json)
   {
      using var response = await _calls.SendAsync(call, json);
      var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
      Assert.True(response.IsSuccessStatusCode, $"{call.Call.Name} returned {(int)response.StatusCode}: {body}");
      return JsonNode.Parse(body)!;
   }

   private static async Task<JsonNode> NextOfAsync(EventStream stream, string type, string tourId)
   {
      while (true)
      {
         var next = await stream.NextAsync(EventWait);
         if (next.Type == type && ((string?)next.Data["tourId"] ?? (string?)next.Data["id"]) == tourId)
         {
            return next.Data;
         }
      }
   }
}
