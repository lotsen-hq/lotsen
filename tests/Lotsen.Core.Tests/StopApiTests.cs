using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Prose;
using Lotsen.Server;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>
/// Covers <c>put_stop</c> over HTTP: its route, the <c>stopPut</c> event, the HTML of a stored stop's prose, and its refusal of a tour that is not pinned, an unknown tour and a request without
/// the token.
/// </summary>
[Trait("Category", "Api")]
public sealed class StopApiTests
{
   private const string CreateBody = $$"""{ "prUrl": "{{TestPlatform.PrUrl}}", "familiarity": "some" }""";

   private const string Plan = """{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "title": "S", "kind": "inspect", "chapter": "a" }] }""";

   private const string Stop = """
      { "headline": "Line two becomes a digit.", "passages": [{ "side": "head", "path": "src/modified.txt", "startLine": 2, "endLine": 2, "role": "primary" }] }
      """;

   private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(10);

   [Fact]
   public async Task Put_stop_returns_the_stored_stop_and_publishes_it_after_a_rejected_write_published_nothing()
   {
      await using var fixture = await PinnedTourFixture.StartAsync();
      var id = (string)(await fixture.Harness.AgentCallAsync(On("set_plan", fixture.TourId), Plan))["plan"]!["stops"]![0]!["id"]!;
      await using var stream = await EventStream.OpenAsync(fixture.Harness);

      using var rejected = await fixture.Harness.AgentSendJsonAsync(On("put_stop", fixture.TourId, stopId: id), """{ "headline": "No passages." }""");
      var stored = await fixture.Harness.AgentCallAsync(On("put_stop", fixture.TourId, stopId: id), Stop);
      var marker = await fixture.Harness.AgentCallAsync("create_tour", CreateBody);

      Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
      Assert.Equal(id, (string)stored["id"]!);
      Assert.Equal("2", (string)stored["content"]!["passages"]![0]!["text"]!);
      var read = await fixture.Harness.CallAsync(On("read_state", fixture.TourId));
      var put = await stream.NextBesidesControlsAsync(EventTimeout);
      Assert.Equal("stopPut", put.Type);
      Assert.Equal(fixture.TourId, (string)put.Data["tourId"]!);
      Assert.Equal((string)read["updatedAt"]!, (string)put.Data["updatedAt"]!);
      var asTheUiReads = (await ReadAsTheUiAsync(fixture.Harness, fixture.TourId))["plan"]!["stops"]![0];
      Assert.True(JsonNode.DeepEquals(asTheUiReads, put.Data["stop"]), $"The event's stop differs from the UI's read: {put.Data}");
      var withoutHtml = put.Data["stop"]!.DeepClone();
      withoutHtml["content"]!.AsObject().Remove("html");
      Assert.True(JsonNode.DeepEquals(stored, withoutHtml), $"The event's stop differs from put_stop's result: {put.Data}");
      var ready = await stream.NextBesidesControlsAsync(EventTimeout);
      Assert.Equal(("tourStateChanged", "ready"), (ready.Type, (string?)ready.Data["state"]));
      var next = await stream.NextBesidesControlsAsync(EventTimeout);
      Assert.Equal(("tourCreated", (string)marker["id"]!), (next.Type, (string)next.Data["id"]!));
   }

   /// <summary>Only a request with the prose header, as the UI sends it, gets the HTML. The <c>stopPut</c> and <c>planSet</c> events always carry it, because only the UI reads them.</summary>
   [Fact]
   public async Task Tour_route_returns_the_prose_of_a_stored_stop_as_html_with_in_app_ref_links_when_asked()
   {
      await using var fixture = await PinnedTourFixture.StartAsync();
      var id = (string)(await fixture.Harness.AgentCallAsync(On("set_plan", fixture.TourId), Plan))["plan"]!["stops"]![0]!["id"]!;
      await using var stream = await EventStream.OpenAsync(fixture.Harness);
      const string body = """
         {
           "headline": "Line two becomes **a digit**.",
           "how": "It changes [this line](ref:head/src/modified.txt#L2-2) and keeps [that one](ref:base/src/same.txt#L1-1).",
           "checks": [{ "question": "Is `2` right?", "recommendation": "Yes." }, { "question": "Does <https://example.com/> say so?" }],
           "passages": [{ "side": "head", "path": "src/modified.txt", "startLine": 2, "endLine": 2, "role": "primary" }]
         }
         """;

      await fixture.Harness.AgentCallAsync(On("put_stop", fixture.TourId, stopId: id), body);

      var tour = await ReadAsTheUiAsync(fixture.Harness, fixture.TourId);
      var content = tour["plan"]!["stops"]![0]!["content"]!;
      Assert.Equal("Line two becomes **a digit**.", (string)content["headline"]!);
      var html = content["html"]!;
      Assert.Equal(["headline", "how", "checks"], html.AsObject().Select(property => property.Key));
      Assert.Equal("<p>Line two becomes <strong>a digit</strong>.</p>\n", (string)html["headline"]!);
      Assert.Equal(
         "<p>It changes <a data-ref-side=\"head\" data-ref-path=\"src/modified.txt\" data-ref-start-line=\"2\" data-ref-end-line=\"2\">this line</a> and keeps "
         + "<a data-ref-side=\"base\" data-ref-path=\"src/same.txt\" data-ref-start-line=\"1\" data-ref-end-line=\"1\">that one</a>.</p>\n",
         (string)html["how"]!);
      Assert.Equal(
         [("<p>Is <code>2</code> right?</p>\n", "<p>Yes.</p>\n"), ("<p>Does <a href=\"https://example.com/\" rel=\"noopener noreferrer\" target=\"_blank\">https://example.com/</a> say so?</p>\n", null)],
         html["checks"]!.AsArray().Select(check => ((string)check!["question"]!, (string?)check["recommendation"])));
      var put = await stream.NextBesidesControlsAsync(EventTimeout);
      Assert.True(JsonNode.DeepEquals(tour["plan"]!["stops"]![0], put.Data["stop"]), $"The event's stop differs from the UI's read: {put.Data}");
      Assert.Null((await fixture.Harness.CallAsync(On("read_state", fixture.TourId)))["plan"]!["stops"]![0]!["content"]!["html"]);
      Assert.Equal("tourStateChanged", (await stream.NextBesidesControlsAsync(EventTimeout)).Type);

      var replanned = await fixture.Harness.AgentCallAsync(
         On("set_plan", fixture.TourId), $$"""{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "id": "{{id}}", "title": "S", "kind": "inspect", "chapter": "a" }] }""");
      var planSet = await stream.NextBesidesControlsAsync(EventTimeout);
      Assert.Equal("planSet", planSet.Type);
      Assert.True(JsonNode.DeepEquals(html, planSet.Data["plan"]!["stops"]![0]!["content"]!["html"]), $"The planSet event's stop has other HTML: {planSet.Data}");
      Assert.Null(replanned["plan"]!["stops"]![0]!["content"]!["html"]);
   }

   /// <summary>
   /// The headline nests exactly as deep as prose may, and the tour file then gets a <c>how</c> one level deeper, which put_stop would refuse. That field reaches the UI as its escaped text, and
   /// set_plan still succeeds and publishes the plan.
   /// </summary>
   [Fact]
   public async Task Stored_prose_that_does_not_render_reaches_the_ui_as_escaped_text()
   {
      await using var fixture = await PinnedTourFixture.StartAsync();
      var id = (string)(await fixture.Harness.AgentCallAsync(On("set_plan", fixture.TourId), Plan))["plan"]!["stops"]![0]!["id"]!;
      var atTheLimit = string.Concat(Enumerable.Repeat("> ", ProseRenderer.MaxDepth)) + "x";
      var pastTheLimit = string.Concat(Enumerable.Repeat("> ", ProseRenderer.MaxDepth + 1)) + "<b>x</b>";
      await fixture.Harness.AgentCallAsync(On("put_stop", fixture.TourId, stopId: id), $$"""
         { "headline": {{JsonValue.Create(atTheLimit).ToJsonString()}}, "how": "ok", "passages": [{ "side": "head", "path": "src/modified.txt", "startLine": 2, "endLine": 2, "role": "primary" }] }
         """);
      EditStoredContent(fixture, content => content["how"] = pastTheLimit);
      await using var stream = await EventStream.OpenAsync(fixture.Harness);

      var html = (await ReadAsTheUiAsync(fixture.Harness, fixture.TourId))["plan"]!["stops"]![0]!["content"]!["html"]!;
      await fixture.Harness.AgentCallAsync(
         On("set_plan", fixture.TourId), $$"""{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "id": "{{id}}", "title": "T", "kind": "inspect", "chapter": "a" }] }""");

      var escaped = "<p>" + string.Concat(Enumerable.Repeat("&gt; ", ProseRenderer.MaxDepth + 1)) + "&lt;b&gt;x&lt;/b&gt;</p>\n";
      Assert.Equal(("<p>x</p>\n", escaped), ((string)html["headline"]!, (string)html["how"]!));
      var planSet = await stream.NextAsync(EventTimeout);
      Assert.Equal("planSet", planSet.Type);
      Assert.True(JsonNode.DeepEquals(html, planSet.Data["plan"]!["stops"]![0]!["content"]!["html"]), planSet.Data.ToJsonString());
   }

   /// <summary>Only the server renders HTML, so an <c>html</c> written into the tour file by hand is ignored on every read and is not stored again.</summary>
   [Fact]
   public async Task Html_in_the_tour_file_reaches_no_output()
   {
      await using var fixture = await PinnedTourFixture.StartAsync();
      var id = (string)(await fixture.Harness.AgentCallAsync(On("set_plan", fixture.TourId), Plan))["plan"]!["stops"]![0]!["id"]!;
      await fixture.Harness.AgentCallAsync(On("put_stop", fixture.TourId, stopId: id), Stop);
      EditStoredContent(fixture, content => content["html"] = new JsonObject { ["headline"] = "<b>planted</b>" });
      await using var stream = await EventStream.OpenAsync(fixture.Harness);

      var forClaude = await fixture.Harness.CallAsync(On("read_state", fixture.TourId));
      var forTheUi = await ReadAsTheUiAsync(fixture.Harness, fixture.TourId);
      var replanned = await fixture.Harness.AgentCallAsync(
         On("set_plan", fixture.TourId), $$"""{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "id": "{{id}}", "title": "T", "kind": "inspect", "chapter": "a" }] }""");
      var planSet = await stream.NextAsync(EventTimeout);

      Assert.Null(forClaude["plan"]!["stops"]![0]!["content"]!["html"]);
      Assert.Null(replanned["plan"]!["stops"]![0]!["content"]!["html"]);
      const string rendered = "<p>Line two becomes a digit.</p>\n";
      Assert.Equal(rendered, (string)forTheUi["plan"]!["stops"]![0]!["content"]!["html"]!["headline"]!);
      Assert.Equal(rendered, (string)planSet.Data["plan"]!["stops"]![0]!["content"]!["html"]!["headline"]!);
      Assert.DoesNotContain("planted", File.ReadAllText(Path.Combine(fixture.Harness.DataDirectory.ToursPath, fixture.TourId + ".json")), StringComparison.Ordinal);
   }

   /// <summary>The harness's platform has no pull request, so the tour stays pinning.</summary>
   [Fact]
   public async Task Put_stop_on_a_tour_that_is_not_pinned_is_refused_with_its_pin()
   {
      await using var harness = await ServerHarness.StartAsync();
      var tourId = (string)(await harness.AgentCallAsync("create_tour", CreateBody))["id"]!;
      var planned = await harness.AgentCallAsync(On("set_plan", tourId), Plan);
      var id = (string)planned["plan"]!["stops"]![0]!["id"]!;

      using var response = await harness.AgentSendJsonAsync(On("put_stop", tourId, stopId: id), Stop);

      Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
      var error = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
      Assert.Equal("tourNotPinned", (string)error["code"]!);
      Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{ "state": "pinning" }"""), error["pin"]), error.ToJsonString());
      Assert.True(JsonNode.DeepEquals(planned, await harness.CallAsync(On("read_state", tourId))));
   }

   [Theory]
   [InlineData("aaaaaaaaaaaaaaaa")]
   [InlineData("not-a-tour")]
   public async Task Put_stop_on_an_unknown_tour_is_not_found(string tourId)
   {
      await using var harness = await ServerHarness.StartAsync();

      using var response = await harness.AgentSendJsonAsync(On("put_stop", tourId, stopId: "abcdefgh"), Stop);

      Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
      var error = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
      Assert.Equal("tourNotFound", (string)error["code"]!);
   }

   [Fact]
   public async Task Put_stop_without_the_token_is_refused()
   {
      await using var harness = await ServerHarness.StartAsync();

      using var response = await harness.SendAsync(On("put_stop", "aaaaaaaaaaaaaaaa", stopId: "abcdefgh").Request("{}"));

      Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
   }

   // Rewrites the content of the first stop in the fixture tour's file, as a hand edit would.
   private static void EditStoredContent(PinnedTourFixture fixture, Action<JsonObject> edit)
   {
      var path = Path.Combine(fixture.Harness.DataDirectory.ToursPath, fixture.TourId + ".json");
      var file = JsonNode.Parse(File.ReadAllText(path))!;
      edit(file["tour"]!["plan"]!["stops"]![0]!["content"]!.AsObject());
      File.WriteAllText(path, file.ToJsonString());
   }

   // Reads the tour with the header that asks for prose as HTML, as the UI does.
   private static async Task<JsonNode> ReadAsTheUiAsync(ServerHarness harness, string tourId)
   {
      var request = (new PreparedCall(On("read_state", tourId), harness.UiCredential)).WithProseHtml();
      using var response = await harness.SendAsync(request);
      var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
      Assert.True(response.IsSuccessStatusCode, body);
      return SuccessBody.Output(body);
   }
}
