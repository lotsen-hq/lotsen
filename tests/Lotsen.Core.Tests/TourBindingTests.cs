using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Lotsen.Operations;
using Lotsen.Sessions;
using Lotsen.Tours;
using Microsoft.Extensions.DependencyInjection;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>Covers the binding of a tour to one agent session: how a session gets it, how another takes it over, when it ends, and which callers may write the tour's plan and stops.</summary>
[Trait("Category", "Roles")]
public sealed class TourBindingTests : IAsyncLifetime
{
   private const string CreateBody = $$"""{ "prUrl": "{{TestPlatform.PrUrl}}", "familiarity": "some" }""";

   private const string PlanBody = """{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "title": "S", "kind": "context", "chapter": "a" }] }""";

   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

   private ServerHarness _harness = null!;

   public async ValueTask InitializeAsync() => _harness = await ServerHarness.StartAsync();

   public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

   [Fact]
   public async Task Created_tour_is_bound_to_the_session_that_created_it()
   {
      await using var stream = await EventStream.OpenAsync(_harness);
      await using var session = await AgentSessionConnection.OpenAsync(_harness);

      var created = await CallAsync(session.Request(_harness, "create_tour"), CreateBody);
      var id = (string)created["id"]!;
      var unbound = _harness.CreateUnboundTour(CreateBody);

      var createdEvent = await stream.NextAsync(Timeout);
      Assert.Equal(session.Id, await _harness.BoundSessionOfAsync(id));
      Assert.Equal(("tourCreated", session.Id), (createdEvent.Type, BoundSessionOf(createdEvent.Data)));
      Assert.Null(await _harness.BindingOfAsync((string)unbound["id"]!));
   }

   [Fact]
   public async Task Second_session_is_refused_without_take_over()
   {
      await using var first = await AgentSessionConnection.OpenAsync(_harness);
      await using var second = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(first);
      await using var stream = await EventStream.OpenAsync(_harness);

      using var response = await AttachAsync(second, id, takeOver: null);
      var marker = _harness.CreateUnboundTour(CreateBody);

      var error = await ErrorAsync(response, HttpStatusCode.Conflict, "attachedToAnotherSession");
      Assert.StartsWith($"tourId: tour {id} is attached to another session.", (string)error["reason"]!, StringComparison.Ordinal);
      Assert.Contains("only after the user confirms", (string)error["reason"]!, StringComparison.Ordinal);
      Assert.Contains("takeOver true", (string)error["reason"]!, StringComparison.Ordinal);
      Assert.Equal(first.Id, await _harness.BoundSessionOfAsync(id));
      await AssertNextIsCreationOfAsync(stream, marker);
   }

   [Fact]
   public async Task Take_over_moves_the_binding_and_tells_the_old_session()
   {
      await using var first = await AgentSessionConnection.OpenAsync(_harness);
      await using var second = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(first);
      await first.ProbeAsync();
      await using var stream = await EventStream.OpenAsync(_harness);

      var attached = await CallAsync(second.Request(_harness, On("attach_tour", id)), AttachBody(takeOver: true));
      var told = await first.Stream.NextAsync(Timeout);
      var changed = await stream.NextAsync(Timeout);
      using var oldWrite = await SendAsync(first.Request(_harness, On("set_plan", id)), PlanBody);
      var newWrite = await CallAsync(second.Request(_harness, On("set_plan", id)), PlanBody);

      Assert.Equal(id, (string)attached["id"]!);
      Assert.Equal(second.Id, await _harness.BoundSessionOfAsync(id));
      Assert.Equal((SessionEvents.TourTakenOver, id), (told.Type, (string)told.Data["tourId"]!));
      Assert.Equal((ServerEvents.TourBindingChanged.Name, id, second.Id), (changed.Type, (string)changed.Data["tourId"]!, BoundSessionOf(changed.Data)));
      await ErrorAsync(oldWrite, HttpStatusCode.Conflict, "attachedToAnotherSession");
      Assert.Equal(id, (string)newWrite["id"]!);
   }

   [Fact]
   public async Task Tour_that_no_session_holds_is_attached_without_take_over()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = (string)_harness.CreateUnboundTour(CreateBody)["id"]!;
      await using var stream = await EventStream.OpenAsync(_harness);

      var attached = await CallAsync(session.Request(_harness, On("attach_tour", id)), AttachBody(takeOver: null));
      var changed = await stream.NextAsync(Timeout);

      Assert.Equal(id, (string)attached["id"]!);
      Assert.Equal(session.Id, await _harness.BoundSessionOfAsync(id));
      Assert.Equal((ServerEvents.TourBindingChanged.Name, session.Id), (changed.Type, BoundSessionOf(changed.Data)));
   }

   [Fact]
   public async Task Session_that_attaches_its_own_tour_again_changes_nothing()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(session);
      await using var stream = await EventStream.OpenAsync(_harness);

      var again = await CallAsync(session.Request(_harness, On("attach_tour", id)), AttachBody(takeOver: true));
      var marker = _harness.CreateUnboundTour(CreateBody);

      Assert.Equal(id, (string)again["id"]!);
      Assert.Equal(session.Id, await _harness.BoundSessionOfAsync(id));
      await AssertNextIsCreationOfAsync(stream, marker);
   }

   [Fact]
   public async Task Attach_of_an_unknown_tour_is_not_found()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);

      using var response = await AttachAsync(session, TourId.New().ToString(), takeOver: true);

      await ErrorAsync(response, HttpStatusCode.NotFound, "tourNotFound");
   }

   public static TheoryData<string, string> Writers => new()
   {
      { "notBound", "plan" },
      { "notBound", "stop" },
      { "takenOver", "plan" },
      { "takenOver", "stop" },
   };

   /// <summary>
   /// The tour is not pinned, so the refusal of put_stop comes from the binding, which is checked before the pin. The UI and the CLI are refused by the operations' access, as
   /// <c>AccessTests</c> covers.
   /// </summary>
   [Theory]
   [MemberData(nameof(Writers))]
   public async Task Plan_and_stop_writes_from_a_session_that_does_not_hold_the_tour_are_refused_and_store_nothing(string writer, string write)
   {
      await using var bound = await AgentSessionConnection.OpenAsync(_harness);
      await using var other = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(bound);
      var stopId = (string)(await CallAsync(bound.Request(_harness, On("set_plan", id)), PlanBody))["plan"]!["stops"]![0]!["id"]!;
      if (writer == "takenOver")
      {
         await CallAsync(other.Request(_harness, On("attach_tour", id)), AttachBody(takeOver: true));
      }

      var before = await ReadAsync(id);
      await using var stream = await EventStream.OpenAsync(_harness);
      var (call, body) = write == "plan"
         ? (On("set_plan", id), PlanBody.Replace("\"S\"", "\"Changed\"", StringComparison.Ordinal))
         : (On("put_stop", id, stopId: stopId), """{ "headline": "Changed." }""");
      var request = writer == "notBound" ? other.Request(_harness, call) : bound.Request(_harness, call);

      using var response = await SendAsync(request, body);
      var marker = _harness.CreateUnboundTour(CreateBody);

      var error = await ErrorAsync(response, HttpStatusCode.Conflict, "attachedToAnotherSession");
      Assert.Equal("tourId", (string)error["field"]!);
      Assert.Contains($"tour {id} is attached to another session, so this session cannot write its plan and stops.", (string)error["reason"]!, StringComparison.Ordinal);
      Assert.True(JsonNode.DeepEquals(before, await ReadAsync(id)), $"The tour changed: {await ReadAsync(id)}");
      await AssertNextIsCreationOfAsync(stream, marker);
   }

   public static TheoryData<string, string> InvalidWriters => new(
      from writer in new[] { "notBound", "unattached", "takenOver" }
      from input in new[] { "plan", "stopFields", "stopReferences" }
      select (writer, input));

   /// <summary>A session that does not hold the tour learns that before it learns what is wrong with its input.</summary>
   [Theory]
   [MemberData(nameof(InvalidWriters))]
   public async Task Invalid_plan_and_stop_writes_from_a_session_that_does_not_hold_the_tour_are_refused_for_the_binding(string writer, string input)
   {
      await using var bound = await AgentSessionConnection.OpenAsync(_harness);
      await using var other = await AgentSessionConnection.OpenAsync(_harness);
      var id = writer == "unattached" ? (string)_harness.CreateUnboundTour(CreateBody)["id"]! : await CreateAsync(bound);
      if (writer == "takenOver")
      {
         await CallAsync(other.Request(_harness, On("attach_tour", id)), AttachBody(takeOver: true));
      }

      var before = await ReadAsync(id);
      var (call, body) = InvalidWrite(id, input);
      var request = writer == "takenOver" ? bound.Request(_harness, call) : other.Request(_harness, call);

      using var response = await SendAsync(request, body);

      await ErrorAsync(response, HttpStatusCode.Conflict, writer == "unattached" ? "noSessionAttached" : "attachedToAnotherSession");
      Assert.True(JsonNode.DeepEquals(before, await ReadAsync(id)), $"The tour changed: {await ReadAsync(id)}");
   }

   [Theory]
   [InlineData("plan", "chapters")]
   [InlineData("stopFields", "headline")]
   [InlineData("stopReferences", "how")]
   public async Task Invalid_plan_and_stop_writes_from_the_bound_session_are_invalid_input(string input, string field)
   {
      await using var bound = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(bound);
      var (call, body) = InvalidWrite(id, input);

      using var response = await SendAsync(bound.Request(_harness, call), body);

      var error = await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal(field, (string)error["field"]!);
   }

   [Theory]
   [InlineData("plan")]
   [InlineData("stopFields")]
   public async Task Invalid_plan_and_stop_writes_to_an_unknown_tour_are_not_found(string input)
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var (call, body) = InvalidWrite(TourId.New().ToString(), input);

      using var response = await SendAsync(session.Request(_harness, call), body);

      await ErrorAsync(response, HttpStatusCode.NotFound, "tourNotFound");
   }

   [Fact]
   public async Task Session_without_any_binding_is_told_to_attach_first()
   {
      await using var session = await AgentSessionConnection.OpenAsync(_harness);
      var id = (string)_harness.CreateUnboundTour(CreateBody)["id"]!;

      using var response = await SendAsync(session.Request(_harness, On("set_plan", id)), PlanBody);

      var error = await ErrorAsync(response, HttpStatusCode.Conflict, "noSessionAttached");
      Assert.Equal($"tourId: tour {id} is attached to no session, so this session cannot write its plan and stops. Call attach_tour with the tourId first.", (string)error["reason"]!);
      Assert.Null((await ReadAsync(id))["plan"]);
   }

   [Fact]
   public async Task Binding_is_released_when_the_bound_sessions_connection_drops_and_another_session_attaches_without_take_over()
   {
      var first = await AgentSessionConnection.OpenAsync(_harness);
      await using var second = await AgentSessionConnection.OpenAsync(_harness);
      var id = await CreateAsync(first);
      await using var stream = await EventStream.OpenAsync(_harness);

      await first.DisposeAsync();
      var released = await stream.NextAsync(Timeout);
      var unbound = await _harness.BindingOfAsync(id);
      await CallAsync(second.Request(_harness, On("attach_tour", id)), AttachBody(takeOver: null));

      Assert.Equal(ServerEvents.TourBindingChanged.Name, released.Type);
      Assert.Equal(id, (string)released.Data["tourId"]!);
      Assert.True(released.Data.AsObject().ContainsKey("binding"));
      Assert.Null(released.Data["binding"]);
      Assert.Null(unbound);
      Assert.Equal(second.Id, await _harness.BoundSessionOfAsync(id));
   }

   [Fact]
   public async Task Session_that_ended_creates_a_tour_that_no_session_holds_and_cannot_attach()
   {
      var services = _harness.Server.Services;
      var ended = _harness.Sessions.TryOpen()!;
      var caller = _harness.CallerOf(ended);
      ended.End();

      var created = services.GetRequiredService<TourPins>().CreateTour(Passes.Admit<Access.AnySession.Pass>("create_tour", caller), new CreateTourInput(TestPlatform.PrUrl, Familiarity.Some));
      var attached = services.GetRequiredService<SessionOperations>().AttachTour(Passes.Admit<Access.AnySession.Pass>("attach_tour", caller), new AttachTourInput(created.Value!.Id, TakeOver: true));

      Assert.Null(services.GetRequiredService<TourWriter>().Exclusive(locked => locked.Holder(created.Value!.Id)));
      Assert.Equal(OperationErrorCode.CallerNotAllowed, attached.Error?.Code);
      Assert.Contains("this agent session has ended", attached.Error!.Reason, StringComparison.Ordinal);
   }

   private static string AttachBody(bool? takeOver) => takeOver is { } value ? $$"""{ "takeOver": {{(value ? "true" : "false")}} }""" : "{}";

   private static (OperationCall Call, string Body) InvalidWrite(string tourId, string input) => input switch
   {
      "plan" => (On("set_plan", tourId), """{ "chapters": [], "stops": [{ "title": "S", "kind": "context", "chapter": "a" }] }"""),
      "stopFields" => (On("put_stop", tourId, stopId: "s1"), """{ "headline": " " }"""),
      _ => (On("put_stop", tourId, stopId: "s1"), """{ "headline": "H.", "how": "See [x](ref:left/src/a.txt#L1-1)." }"""),
   };

   private static string BoundSessionOf(JsonNode payload) => (string?)payload["binding"]?["sessionId"] ?? throw new Xunit.Sdk.XunitException($"No session is bound: {payload.ToJsonString()}");

   private static async Task<JsonNode> ErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
   {
      var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
      Assert.True(response.StatusCode == status, $"Expected {(int)status}, got {(int)response.StatusCode}: {body}");
      var error = JsonNode.Parse(body)!;
      Assert.Equal(code, (string)error["code"]!);
      return error;
   }

   private Task<HttpResponseMessage> AttachAsync(AgentSessionConnection session, string tourId, bool? takeOver) => SendAsync(session.Request(_harness, On("attach_tour", tourId)), AttachBody(takeOver));

   private async Task<string> CreateAsync(AgentSessionConnection session) => (string)(await CallAsync(session.Request(_harness, "create_tour"), CreateBody))["id"]!;

   private Task<JsonNode> ReadAsync(string tourId) => _harness.CallAsync(On("read_state", tourId));

   private Task<HttpResponseMessage> SendAsync(PreparedCall call, string? json = null) => _harness.SendAsync(call, json);

   private async Task<JsonNode> CallAsync(PreparedCall call, string? json = null)
   {
      using var response = await SendAsync(call, json);
      var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
      Assert.True(response.IsSuccessStatusCode, $"{call.Call.Name} returned {(int)response.StatusCode}: {body}");
      return SuccessBody.Output(body);
   }

   // The marker tour is created after the action under test, so an event that the action published would arrive before the marker's.
   private static async Task AssertNextIsCreationOfAsync(EventStream stream, JsonNode marker)
   {
      var next = await stream.NextAsync(Timeout);
      Assert.Equal(("tourCreated", (string)marker["id"]!), (next.Type, (string?)next.Data["id"]));
   }
}

/// <summary>Covers a put_stop of the bound session that a take-over overtakes while the server checks the stop's passages.</summary>
[Trait("Category", "Roles")]
public sealed class TourTakeOverInFlightTests
{
   [Fact]
   public async Task Put_stop_in_flight_when_a_take_over_lands_stores_nothing()
   {
      await using var fixture = await PinnedTourFixture.StartAsync();
      await using var other = await AgentSessionConnection.OpenAsync(fixture.Harness);
      var stopId = (string)(await fixture.Harness.AgentCallAsync(
         On("set_plan", fixture.TourId), """{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "title": "S", "kind": "inspect", "chapter": "a" }] }"""))["plan"]!["stops"]![0]!["id"]!;
      var writer = fixture.Harness.Server.Services.GetRequiredService<TourWriter>();
      writer.Hook = WriterHooks.AfterGit(async _ =>
      {
         var request = other.Request(fixture.Harness, On("attach_tour", fixture.TourId));
         using var attached = await fixture.Harness.SendAsync(request, """{ "takeOver": true }""");
         Assert.Equal(HttpStatusCode.OK, attached.StatusCode);
      }, "put_stop");

      using var response = await fixture.Harness.AgentSendJsonAsync(
         On("put_stop", fixture.TourId, stopId: stopId),
         """{ "headline": "Line two becomes a digit.", "passages": [{ "side": "head", "path": "src/modified.txt", "startLine": 2, "endLine": 2, "role": "primary" }] }""");
      var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
      var read = await fixture.Harness.CallAsync(On("read_state", fixture.TourId));
      var agent = await fixture.Harness.AgentAsync();
      await agent.ProbeAsync();
      var told = await agent.Stream.NextAsync(TimeSpan.FromSeconds(10));

      Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
      Assert.Equal("attachedToAnotherSession", (string)body["code"]!);
      Assert.Contains("attached to another session", (string)body["reason"]!, StringComparison.Ordinal);
      Assert.Equal("pending", (string)read["plan"]!["stops"]![0]!["status"]!);
      Assert.Equal(other.Id, await fixture.Harness.BoundSessionOfAsync(fixture.TourId));
      Assert.Equal((SessionEvents.TourTakenOver, fixture.TourId), (told.Type, (string)told.Data["tourId"]!));
   }
}
