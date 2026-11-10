using System.Net;
using System.Text.Json.Nodes;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>Covers <c>find_tours</c> through the HTTP API: which tours it finds for an agent session, in which order, and how each row shows the tour's attachment.</summary>
[Trait("Category", "Api")]
public sealed class FindToursApiTests : IAsyncLifetime
{
   private const string PrUrl = TestPlatform.PrUrl;

   private ServerHarness _harness = null!;

   public async ValueTask InitializeAsync() => _harness = await ServerHarness.StartAsync();

   public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

   [Fact]
   public async Task Find_with_a_pull_request_url_returns_the_tour_that_create_tour_made_for_it()
   {
      var created = await CreateTourAsync();
      await _harness.AgentCallAsync(On("set_plan", (string)created["id"]!), """{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "title": "What this PR changes", "kind": "overview", "chapter": "a" }] }""");

      var found = await FindAsync(PrUrl);

      var tour = Assert.Single(found)!;
      Assert.Equal((string)created["id"]!, (string)tour["id"]!);
      Assert.Equal(PrUrl, (string)tour["target"]!["url"]!);
      Assert.Equal(TestPlatform.PrLabel, (string)tour["label"]!);
      Assert.Equal("some", (string)tour["familiarity"]!);
      Assert.Equal((string)created["createdAt"]!, (string)tour["createdAt"]!);
      Assert.Equal(1, (int)tour["stopCount"]!);
      Assert.Equal("pinning", (string)tour["pin"]!["state"]!);
      Assert.Equal("generating", (string)tour["state"]!);
      Assert.Equal("active", (string)tour["pullRequestState"]!);
      Assert.Equal("thisSession", (string)tour["attachment"]!);
   }

   [Fact]
   public async Task Find_with_a_pull_request_url_returns_only_that_pull_requests_tours_newest_first_archived_ones_included()
   {
      var first = (string)(await CreateTourAsync())["id"]!;
      var other = (string)(await CreateTourAsync($"https://{TestPlatform.DefaultHost}/shop/pull/43"))["id"]!;
      var second = (string)(await CreateTourAsync(familiarity: "new"))["id"]!;
      await _harness.CallAsync(On("set_tour_archived", first), """{ "archived": true }""");

      var found = await FindAsync(PrUrl);

      Assert.Equal([second, first], found.Select(tour => (string)tour!["id"]!));
      Assert.Equal(["generating", "archived"], found.Select(tour => (string)tour!["state"]!));
      Assert.DoesNotContain(other, found.Select(tour => (string)tour!["id"]!));
   }

   /// <summary>The test platform keeps the repository's letter case in a stored URL, and compares the repository in any letter case.</summary>
   [Fact]
   public async Task Find_returns_a_pull_requests_tour_through_another_form_of_its_url()
   {
      var created = (string)(await CreateTourAsync())["id"]!;

      var found = await FindAsync($"https://{TestPlatform.DefaultHost}/SHOP/pull/42");

      Assert.Equal(created, (string)Assert.Single(found)!["id"]!);
   }

   [Fact]
   public async Task Find_without_a_pull_request_url_returns_every_tour_newest_first()
   {
      var first = (string)(await CreateTourAsync())["id"]!;
      var other = (string)(await CreateTourAsync($"https://{TestPlatform.DefaultHost}/shop/pull/43"))["id"]!;
      var second = (string)(await CreateTourAsync())["id"]!;

      var found = await FindAsync();

      Assert.Equal([second, other, first], found.Select(tour => (string)tour!["id"]!));
   }

   [Fact]
   public async Task Attachment_is_relative_to_the_calling_session_and_no_row_holds_a_session_id()
   {
      var calls = new ThreadCalls(_harness);
      await using var otherSession = await AgentSessionConnection.OpenAsync(_harness);
      var ours = (string)(await CreateTourAsync())["id"]!;
      var theirs = await calls.CreateAsync(otherSession);
      var unattached = (string)_harness.CreateUnboundTour(ThreadCalls.CreateBody)["id"]!;

      var response = await _harness.AgentCallAsync("find_tours", "{}");

      var attachments = response["tours"]!.AsArray().ToDictionary(tour => (string)tour!["id"]!, tour => (string)tour!["attachment"]!);
      Assert.Equal("thisSession", attachments[ours]);
      Assert.Equal("otherSession", attachments[theirs]);
      Assert.Equal("none", attachments[unattached]);
      var json = response.ToJsonString();
      Assert.DoesNotContain((await _harness.AgentAsync()).Id, json, StringComparison.Ordinal);
      Assert.DoesNotContain(otherSession.Id, json, StringComparison.Ordinal);
   }

   [Fact]
   public async Task Pull_request_url_that_no_platform_recognizes_is_refused_as_create_tour_refuses_it()
   {
      const string Unrecognized = """{ "prUrl": "https://third.test/shop/pull/42" }""";
      await CreateTourAsync();

      using var found = await _harness.AgentSendJsonAsync("find_tours", Unrecognized);
      using var created = await _harness.AgentSendJsonAsync("create_tour", Unrecognized.Replace(" }", """, "familiarity": "some" }""", StringComparison.Ordinal));

      var error = await ThreadCalls.ErrorAsync(found, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("prUrl", (string)error["field"]!);
      Assert.Equal((string)(await ThreadCalls.ErrorAsync(created, HttpStatusCode.BadRequest, "invalidInput"))["reason"]!, (string)error["reason"]!);
   }

   private Task<JsonNode> CreateTourAsync(string prUrl = PrUrl, string familiarity = "some") =>
      _harness.AgentCallAsync("create_tour", $$"""{ "prUrl": "{{prUrl}}", "familiarity": "{{familiarity}}" }""");

   private async Task<JsonArray> FindAsync(string? prUrl = null) =>
      (await _harness.AgentCallAsync("find_tours", prUrl is null ? "{}" : $$"""{ "prUrl": "{{prUrl}}" }"""))["tours"]!.AsArray();
}
