using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Adapter;
using Lotsen.Operations;
using Lotsen.Sessions;
using Microsoft.Extensions.DependencyInjection;
using static Lotsen.Tests.OperationCall;
using static Lotsen.Tests.ThreadCalls;
using static Lotsen.Tests.TourActionTests;

namespace Lotsen.Tests;

/// <summary>
/// Covers plan first: a tour created with the option refuses <c>put_stop</c> while its plan awaits the reviewer's approval, and <c>approve_plan</c> stores the reviewer's skips and level, lifts
/// the wait and reports the approval to the bound session in a decided thread with the proposed order, the skips, the level and the reviewer's words.
/// </summary>
[Trait("Category", "TourActions")]
public sealed class PlanFirstTests : IClassFixture<StopTourFixture>, IAsyncLifetime
{
   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

   private const string PlanFirstBody = $$"""{ "prUrl": "{{TestPlatform.PrUrl}}", "familiarity": "some", "planFirst": true }""";

   private const string Plan = """
      {
        "chapters": [{ "id": "code", "title": "Code" }],
        "stops": [
          { "title": "First", "kind": "inspect", "chapter": "code" },
          { "title": "Second", "kind": "inspect", "chapter": "code" },
          { "title": "Third", "kind": "inspect", "chapter": "code" }
        ]
      }
      """;

   private readonly PinnedTourFixture _tour;

   public PlanFirstTests(StopTourFixture fixture)
   {
      _tour = fixture.Tour;
   }

   private ServerHarness Harness => _tour.Harness;

   private AnswerCalls Answers => new(Harness);

   private ThreadCalls Calls => new(Harness);

   private TourWriter Writer => Harness.Server.Services.GetRequiredService<TourWriter>();

   public ValueTask InitializeAsync() => ValueTask.CompletedTask;

   public ValueTask DisposeAsync()
   {
      Writer.Hook = null;
      return ValueTask.CompletedTask;
   }

   private static string Ids(IEnumerable<string> ids) => $"[{string.Join(", ", ids.Select(id => $"\"{id}\""))}]";

   private static string ApprovalBody(IEnumerable<string> order, IEnumerable<string> skipped, string level = "some", string? text = null) => text is null
      ? $$"""{ "stopIds": {{Ids(order)}}, "skippedStops": {{Ids(skipped)}}, "familiarity": "{{level}}" }"""
      : $$"""{ "stopIds": {{Ids(order)}}, "skippedStops": {{Ids(skipped)}}, "familiarity": "{{level}}", "text": "{{text}}" }""";

   private async Task<JsonNode> ApproveAsync(string tourId, IEnumerable<string> order, IEnumerable<string>? skipped = null, string level = "some", string? text = null) =>
      await Calls.CallAsync(await Calls.UiAsync(On("approve_plan", tourId)), ApprovalBody(order, skipped ?? [], level, text));

   private async Task<HttpResponseMessage> TryApproveAsync(string tourId, IEnumerable<string> order, IEnumerable<string>? skipped = null, string level = "some", string? text = null) =>
      await Calls.SendAsync(await Calls.UiAsync(On("approve_plan", tourId)), ApprovalBody(order, skipped ?? [], level, text));

   private async Task<JsonNode> ReadAsync(string tourId) => await Harness.CallAsync(On("read_state", tourId));

   private async Task<string[]> SetPlanAsync(AgentSessionConnection session, string tourId, string plan = Plan)
   {
      var tour = await Calls.CallAsync(Calls.Agent(session, On("set_plan", tourId)), plan);
      return [.. tour["plan"]!["stops"]!.AsArray().Select(stop => (string)stop!["id"]!)];
   }

   private async Task<HttpResponseMessage> TryPutStopAsync(AgentSessionConnection session, string tourId, string stopId) =>
      await Calls.SendAsync(Calls.Agent(session, On("put_stop", tourId, stopId: stopId)), $$"""{ "headline": "A headline.", "passages": [{{AnswerCalls.Passage}}] }""");

   /// <summary>Creates a pinned plan-first tour bound to the session, with <see cref="Plan"/> set, and returns its ID and its stops' IDs.</summary>
   private async Task<(string TourId, string[] StopIds)> WaitingAsync(AgentSessionConnection session)
   {
      var tourId = (string)(await Calls.CallAsync(Calls.Agent(session, "create_tour"), PlanFirstBody))["id"]!;
      await Harness.PinsIdleAsync();
      return (tourId, await SetPlanAsync(session, tourId));
   }

   private static string PlanOf(params string[] stopIds) =>
      $$"""{ "chapters": [{ "id": "code", "title": "Code" }], "stops": [{{string.Join(", ", stopIds.Select(id => $$"""{ "id": "{{id}}", "title": "Kept", "kind": "inspect", "chapter": "code" }"""))}}] }""";

   [Fact]
   public async Task Plan_first_tour_refuses_put_stop_until_approval_naming_the_wait_and_takes_set_plan_meanwhile()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tourId = (string)(await Calls.CallAsync(Calls.Agent(session, "create_tour"), PlanFirstBody))["id"]!;
      await Harness.PinsIdleAsync();
      var created = await ReadAsync(tourId);
      var stops = await SetPlanAsync(session, tourId);

      using var refused = await TryPutStopAsync(session, tourId, stops[0]);
      var error = await ErrorAsync(refused, HttpStatusCode.Conflict, "planAwaitsApproval");
      var revised = await SetPlanAsync(session, tourId, PlanOf(stops[1], stops[0], stops[2]));
      var waiting = await ReadAsync(tourId);
      await ApproveAsync(tourId, revised);
      using var accepted = await TryPutStopAsync(session, tourId, stops[0]);

      Assert.Equal("awaited", (string?)created["planApproval"]);
      Assert.Contains("awaits the reviewer's approval", (string)error["reason"]!, StringComparison.Ordinal);
      Assert.Equal([stops[1], stops[0], stops[2]], revised);
      Assert.Equal(("awaited", 0), ((string?)waiting["planApproval"], (int?)waiting["plan"]!["stops"]![0]!["version"] ?? 0));
      Assert.True(accepted.IsSuccessStatusCode, await accepted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
      Assert.Equal("given", (string?)(await ReadAsync(tourId))["planApproval"]);
   }

   /// <summary>The plan waits only once it is set, so put_stop before set_plan names the missing plan, which is what Claude must write first.</summary>
   [Fact]
   public async Task Plan_first_tour_without_a_plan_refuses_put_stop_for_the_missing_plan()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tourId = (string)(await Calls.CallAsync(Calls.Agent(session, "create_tour"), PlanFirstBody))["id"]!;
      await Harness.PinsIdleAsync();

      using var refused = await TryPutStopAsync(session, tourId, "zzzzzzzz");

      var error = await ErrorAsync(refused, HttpStatusCode.NotFound, "stopNotFound");
      Assert.Contains("has no plan yet", (string)error["reason"]!, StringComparison.Ordinal);
      Assert.Equal("awaited", (string?)(await ReadAsync(tourId))["planApproval"]);
   }

   [Fact]
   public async Task Tour_without_the_option_accepts_put_stop_right_after_set_plan()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tourId = await Calls.CreateAsync(session);
      await Harness.PinsIdleAsync();
      var stops = await SetPlanAsync(session, tourId);

      using var accepted = await TryPutStopAsync(session, tourId, stops[0]);

      Assert.True(accepted.IsSuccessStatusCode, await accepted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
      Assert.Null((await ReadAsync(tourId))["planApproval"]);
   }

   [Fact]
   public async Task Approval_stores_the_skips_and_level_and_sends_one_approve_plan_action_with_the_order_skips_level_and_words()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var (tourId, stops) = await WaitingAsync(session);
      var nonce = await session.ProbeAsync();
      await using var stream = await EventStream.OpenAsync(Harness);
      string[] order = [stops[2], stops[0], stops[1]];

      var approved = await ApproveAsync(tourId, order, [stops[0]], "new", "Keep the overview short.");
      var sent = await session.Stream.NextAsync(Timeout);
      var events = new[] { await stream.NextAsync(EventWait), await stream.NextAsync(EventWait), await stream.NextAsync(EventWait), await stream.NextAsync(EventWait) };

      var thread = approved["thread"]!;
      var question = Assert.Single(thread["messages"]!.AsArray())!;
      var action = new JsonObject
      {
         ["kind"] = "planApproved",
         ["stopIds"] = new JsonArray([.. order.Select(id => JsonValue.Create(id))]),
         ["skippedStops"] = new JsonArray(stops[0]),
         ["familiarity"] = "new",
      };
      Assert.True(JsonNode.DeepEquals(action, thread["decision"]), $"action: {thread["decision"]}");
      Assert.True(JsonNode.DeepEquals(new JsonObject { ["kind"] = "tour" }, thread["anchor"]), $"anchor: {thread["anchor"]}");
      Assert.Equal("Keep the overview short.", (string)question["text"]!);
      Assert.Equal(("pending", session.Id), ((string)RequestOf(thread)["state"]!, (string)RequestOf(thread)["sessionId"]!));
      var read = await ReadAsync(tourId);
      Assert.Equal(("given", "new"), ((string?)read["planApproval"], (string)read["familiarity"]!));
      Assert.Equal([stops[0]], TourJson.SkippedIn(read));
      Assert.Equal(("new", "given"), ((string)approved["familiarity"]!, (string)approved["planApproval"]!));
      Assert.True(JsonNode.DeepEquals(thread, Assert.Single(read["threads"]!.AsArray())), "The stored thread differs from the approval's result.");
      Assert.Equal(SessionEvents.Question, sent.Type);
      Assert.Equal(("planApproved", nonce, QuestionIdOf(thread)), ((string?)sent.Data["decision"], (string?)sent.Data["nonce"], (string?)sent.Data["questionId"]));
      Assert.Equal("planApproved", (string?)ChannelNotifications.ParamsFor(new SessionEvent(sent.Type, sent.Data.ToJsonString()))!["meta"]!["decision"]);
      Assert.Equal(
         [ServerEvents.PlanApproved.Name, ServerEvents.SkipsUpdated.Name, ServerEvents.FamiliarityChanged.Name, ServerEvents.ThreadCreated.Name],
         events.Select(received => received.Type));
      var updatedAt = (string)approved["updatedAt"]!;
      Assert.True(JsonNode.DeepEquals(new JsonObject { ["tourId"] = tourId, ["updatedAt"] = updatedAt, ["planApproval"] = "given" }, EventStream.WithoutRevision(events[0].Data)), $"planApproved: {events[0].Data}");
      Assert.True(JsonNode.DeepEquals(new JsonObject { ["tourId"] = tourId, ["updatedAt"] = updatedAt, ["stopIds"] = new JsonArray(stops[0]) }, EventStream.WithoutRevision(events[1].Data)), $"skipsUpdated: {events[1].Data}");
   }

   [Fact]
   public async Task Approval_that_keeps_the_skips_and_level_publishes_neither_and_takes_the_skips_in_plan_order()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var (tourId, stops) = await WaitingAsync(session);
      await Calls.CallAsync(await Calls.UiAsync(On("set_stop_skipped", tourId, stopId: stops[2])), """{ "skipped": true }""");
      await Calls.CallAsync(await Calls.UiAsync(On("set_stop_skipped", tourId, stopId: stops[0])), """{ "skipped": true }""");
      await using var stream = await EventStream.OpenAsync(Harness);

      var approved = await ApproveAsync(tourId, stops, [stops[2], stops[0]]);
      var events = new[] { await stream.NextAsync(EventWait), await stream.NextAsync(EventWait) };

      Assert.Equal([ServerEvents.PlanApproved.Name, ServerEvents.ThreadCreated.Name], events.Select(received => received.Type));
      Assert.Equal([stops[0], stops[2]], approved["skippedStops"]!.AsArray().Select(id => (string)id!));
      Assert.Equal("some", (string)(await ReadAsync(tourId))["familiarity"]!);
   }

   [Fact]
   public async Task Approval_without_an_attached_session_is_refused_and_the_plan_still_waits()
   {
      var session = await AgentSessionConnection.OpenAsync(Harness);
      var (tourId, stops) = await WaitingAsync(session);
      await Harness.DropSessionAsync(session);

      using var response = await TryApproveAsync(tourId, stops, [stops[1]], "new");

      var error = await ErrorAsync(response, HttpStatusCode.Conflict, "noSessionAttached");
      Assert.Contains("no session can receive the plan approval", (string)error["reason"]!, StringComparison.Ordinal);
      var read = await ReadAsync(tourId);
      Assert.Equal(("awaited", "some", 0, 0), ((string?)read["planApproval"], (string)read["familiarity"]!, TourJson.SkippedIn(read).Length, read["threads"]!.AsArray().Count));
   }

   [Fact]
   public async Task Approval_after_a_take_over_goes_to_the_new_session()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      await using var other = await AgentSessionConnection.OpenAsync(Harness);
      var (tourId, stops) = await WaitingAsync(session);
      await Calls.CallAsync(Calls.Agent(other, On("attach_tour", tourId)), """{ "takeOver": true }""");

      var approved = await ApproveAsync(tourId, stops);

      Assert.Equal(other.Id, (string)RequestOf(approved["thread"]!)["sessionId"]!);
      await other.ProbeAsync();
      var sent = await other.Stream.NextAsync(Timeout);
      Assert.Equal(("planApproved", QuestionIdOf(approved["thread"]!)), ((string?)sent.Data["decision"], (string?)sent.Data["questionId"]));
   }

   [Theory]
   [InlineData("leavesOut", "stopIds")]
   [InlineData("repeats", "stopIds[2]")]
   [InlineData("unknown", "stopIds[2]")]
   [InlineData("skipUnknown", "stopId")]
   [InlineData("controlCharacter", "text")]
   public async Task Approval_with_a_wrong_order_skip_or_words_is_refused_and_the_plan_still_waits(string wrong, string field)
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var (tourId, stops) = await WaitingAsync(session);
      string[] order = wrong switch
      {
         "leavesOut" => stops[..2],
         "repeats" => [stops[0], stops[1], stops[0]],
         "unknown" => [stops[0], stops[1], "zzzzzzzz"],
         _ => stops,
      };
      string[] skipped = wrong == "skipUnknown" ? ["zzzzzzzz"] : [];
      var text = wrong == "controlCharacter" ? "Bell \\u0007" : null;

      using var response = await TryApproveAsync(tourId, order, skipped, text: text);

      var error = await ErrorAsync(response, wrong == "skipUnknown" ? HttpStatusCode.NotFound : HttpStatusCode.BadRequest, wrong == "skipUnknown" ? "stopNotFound" : "invalidInput");
      Assert.Equal(field, (string)error["field"]!);
      Assert.Equal(("awaited", 0), ((string?)(await ReadAsync(tourId))["planApproval"], (await Calls.ThreadsAsync(tourId)).Count));
   }

   [Theory]
   [InlineData("withoutOption")]
   [InlineData("approved")]
   [InlineData("noPlan")]
   public async Task Approval_of_a_tour_whose_plan_does_not_wait_is_refused_and_stores_nothing(string tour)
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      string tourId;
      string[] stops;
      if (tour == "withoutOption")
      {
         tourId = await Calls.CreateAsync(session);
         await Harness.PinsIdleAsync();
         stops = await SetPlanAsync(session, tourId);
      }
      else if (tour == "approved")
      {
         (tourId, stops) = await WaitingAsync(session);
         await ApproveAsync(tourId, stops);
      }
      else
      {
         tourId = (string)(await Calls.CallAsync(Calls.Agent(session, "create_tour"), PlanFirstBody))["id"]!;
         stops = [];
      }

      var threads = await Calls.ThreadsAsync(tourId);

      using var response = await TryApproveAsync(tourId, stops);

      var error = await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("tourId", (string)error["field"]!);
      Assert.True(JsonNode.DeepEquals(threads, await Calls.ThreadsAsync(tourId)), "The refused approval changed the threads.");
   }

   /// <summary>The approval lifted the wait when it was stored, so a cancel leaves the plan approved, and a re-send goes while Claude has written no stop.</summary>
   [Fact]
   public async Task Canceled_approval_leaves_the_plan_approved_and_goes_again_while_no_stop_has_content()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var (tourId, stops) = await WaitingAsync(session);
      await session.ProbeAsync();
      var approved = await ApproveAsync(tourId, stops);
      await session.Stream.NextAsync(Timeout);
      var questionId = QuestionIdOf(approved["thread"]!);
      await Calls.CallAsync(await Calls.UiAsync(On("cancel_question", tourId, questionId: questionId)));

      var resent = await Calls.CallAsync(await Calls.UiAsync(On("resend_question", tourId, questionId: questionId)));
      var sent = await session.Stream.NextAsync(Timeout);

      Assert.Equal("given", (string?)(await ReadAsync(tourId))["planApproval"]);
      Assert.Equal("pending", (string)RequestOf(resent, index: 1)["state"]!);
      Assert.Equal(("planApproved", questionId), ((string?)sent.Data["decision"], (string?)sent.Data["questionId"]));
   }

   [Fact]
   public async Task Resend_of_an_approval_once_a_stop_has_content_is_refused()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var (tourId, stops) = await WaitingAsync(session);
      var approved = await ApproveAsync(tourId, stops);
      var questionId = QuestionIdOf(approved["thread"]!);
      await Calls.CallAsync(await Calls.UiAsync(On("cancel_question", tourId, questionId: questionId)));
      await Answers.PutStopAsync(session, tourId, stops[0], "[]");
      var threads = await Calls.ThreadsAsync(tourId);

      using var response = await Calls.SendAsync(await Calls.UiAsync(On("resend_question", tourId, questionId: questionId)));

      var error = await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("decision.kind", (string)error["field"]!);
      Assert.True(JsonNode.DeepEquals(threads, await Calls.ThreadsAsync(tourId)), "The refused re-send changed the threads.");
   }

   /// <summary>The stored approval names the level and skips at the approval, so a re-send after either changed would tell Claude a level or skips that the tour no longer holds.</summary>
   [Theory]
   [InlineData("level", "decision.familiarity")]
   [InlineData("skip", "decision.skippedStops")]
   public async Task Resend_of_an_approval_after_the_level_or_skips_changed_is_refused_and_changes_neither(string change, string field)
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var (tourId, stops) = await WaitingAsync(session);
      var approved = await ApproveAsync(tourId, stops, [stops[0]]);
      var questionId = QuestionIdOf(approved["thread"]!);
      await Calls.CallAsync(await Calls.UiAsync(On("cancel_question", tourId, questionId: questionId)));
      if (change == "level")
      {
         await Calls.CallAsync(await Calls.UiAsync(On("change_familiarity", tourId)), """{ "familiarity": "new" }""");
      }
      else
      {
         await Calls.CallAsync(await Calls.UiAsync(On("set_stop_skipped", tourId, stopId: stops[1])), """{ "skipped": true }""");
      }

      var before = await ReadAsync(tourId);

      using var response = await Calls.SendAsync(await Calls.UiAsync(On("resend_question", tourId, questionId: questionId)));

      var error = await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal(field, (string)error["field"]!);
      var after = await ReadAsync(tourId);
      Assert.True(JsonNode.DeepEquals(before, after), "The refused re-send changed the tour.");
   }

   /// <summary>A set_plan after the approval drops a stop of the approved order, so a re-send would tell Claude an order of stops that the plan no longer has.</summary>
   [Fact]
   public async Task Resend_of_an_approval_after_a_plan_without_one_of_its_stops_is_refused()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var (tourId, stops) = await WaitingAsync(session);
      var approved = await ApproveAsync(tourId, stops);
      var questionId = QuestionIdOf(approved["thread"]!);
      await Calls.CallAsync(await Calls.UiAsync(On("cancel_question", tourId, questionId: questionId)));
      await SetPlanAsync(session, tourId, PlanOf(stops[0], stops[1]));
      var threads = await Calls.ThreadsAsync(tourId);

      using var response = await Calls.SendAsync(await Calls.UiAsync(On("resend_question", tourId, questionId: questionId)));

      var error = await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("decision.stopIds", (string)error["field"]!);
      Assert.Contains("the approval does not go again, because Claude changed the plan's stops since", (string)error["reason"]!, StringComparison.Ordinal);
      Assert.True(JsonNode.DeepEquals(threads, await Calls.ThreadsAsync(tourId)), "The refused re-send changed the threads.");
   }

   /// <summary>A reorder of the plan after the approval changes the order of the tour's skips but not which stops they are, so the approval still goes again.</summary>
   [Fact]
   public async Task Resend_of_an_approval_after_a_reorder_that_keeps_the_skips_goes_again()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var (tourId, stops) = await WaitingAsync(session);
      await session.ProbeAsync();
      var approved = await ApproveAsync(tourId, stops, [stops[0], stops[2]]);
      await session.Stream.NextAsync(Timeout);
      var questionId = QuestionIdOf(approved["thread"]!);
      await Calls.CallAsync(await Calls.UiAsync(On("cancel_question", tourId, questionId: questionId)));
      await SetPlanAsync(session, tourId, PlanOf(stops[2], stops[1], stops[0]));

      var resent = await Calls.CallAsync(await Calls.UiAsync(On("resend_question", tourId, questionId: questionId)));

      Assert.Equal("pending", (string)RequestOf(resent, index: 1)["state"]!);
   }

   /// <summary>Plan first applies once, before any stop is written, so a later set_plan neither brings the wait back nor refuses a stop.</summary>
   [Fact]
   public async Task Set_plan_after_the_approval_keeps_the_plan_approved_and_put_stop_accepted()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var (tourId, stops) = await WaitingAsync(session);
      await ApproveAsync(tourId, stops);

      await SetPlanAsync(session, tourId, PlanOf(stops[2], stops[1], stops[0]));
      using var accepted = await TryPutStopAsync(session, tourId, stops[2]);

      Assert.True(accepted.IsSuccessStatusCode, await accepted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
      Assert.Equal("given", (string?)(await ReadAsync(tourId))["planApproval"]);
   }

   /// <summary>A plan-first tour takes its level with the approval, so a change before it, with or without a plan, would leave a familiarity change waiting beside the plan approval.</summary>
   [Theory]
   [InlineData("noPlan", "Pick the level in the plan proposal once Claude sets the plan")]
   [InlineData("planSet", "Pick the level in the plan proposal, then approve the plan")]
   public async Task Change_familiarity_before_the_approval_is_refused_naming_where_the_level_goes(string plan, string reason)
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var tourId = (string)(await Calls.CallAsync(Calls.Agent(session, "create_tour"), PlanFirstBody))["id"]!;
      await Harness.PinsIdleAsync();
      if (plan == "planSet")
      {
         await SetPlanAsync(session, tourId);
      }

      using var refused = await Calls.SendAsync(await Calls.UiAsync(On("change_familiarity", tourId)), """{ "familiarity": "new" }""");

      var error = await ErrorAsync(refused, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("tourId", (string)error["field"]!);
      Assert.Contains(reason, (string)error["reason"]!, StringComparison.Ordinal);
      var read = await ReadAsync(tourId);
      Assert.Equal(("some", 0), ((string)read["familiarity"]!, read["threads"]!.AsArray().Count));
   }

   /// <summary>Claude writes the approved plan's stops at the approval's level, so a change while the approval waits would leave the stops at a level that the tour no longer holds.</summary>
   [Fact]
   public async Task Change_familiarity_while_the_approval_waits_is_refused_naming_it_and_accepted_after_the_reply()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var (tourId, stops) = await WaitingAsync(session);
      var approved = await ApproveAsync(tourId, stops);
      var threadId = (string)approved["thread"]!["id"]!;

      using var refused = await Calls.SendAsync(await Calls.UiAsync(On("change_familiarity", tourId)), """{ "familiarity": "new" }""");

      var error = await ErrorAsync(refused, HttpStatusCode.Conflict, "questionWaiting");
      Assert.Equal("familiarity", (string)error["field"]!);
      Assert.Contains("Claude still writes the stops of the plan that you approved.", (string)error["reason"]!, StringComparison.Ordinal);
      Assert.DoesNotContain(threadId, (string)error["reason"]!, StringComparison.Ordinal);
      var read = await ReadAsync(tourId);
      Assert.Equal(("some", 1), ((string)read["familiarity"]!, read["threads"]!.AsArray().Count));
      await Calls.CallAsync(Calls.Agent(session, On("reply", tourId, questionId: QuestionIdOf(approved["thread"]!))), """{ "resolution": "done", "text": "Written." }""");
      var changed = await Calls.CallAsync(await Calls.UiAsync(On("change_familiarity", tourId)), """{ "familiarity": "new" }""");
      Assert.Equal("new", (string)changed["familiarity"]!);
   }

   [Fact]
   public async Task Read_state_and_the_tour_list_show_the_wait_to_the_bound_session()
   {
      await using var session = await AgentSessionConnection.OpenAsync(Harness);
      var (tourId, _) = await WaitingAsync(session);

      var read = await Calls.CallAsync(Calls.Agent(session, On("read_state", tourId)));
      var listed = (await Harness.CallAsync("list_tours"))["tours"]!.AsArray().Single(tour => (string)tour!["id"]! == tourId)!;

      Assert.Equal(("awaited", "awaited"), ((string?)read["planApproval"], (string?)listed["planApproval"]));
   }
}
