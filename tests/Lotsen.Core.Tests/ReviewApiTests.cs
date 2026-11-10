using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Hosting;
using Lotsen.Tours;
using Microsoft.Extensions.DependencyInjection;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>
/// Covers the review marks that the reviewer sets and clears on changed files through the HTTP API, and which marks the changed-file list counts at the pinned head.
/// </summary>
[Trait("Category", "Api")]
public sealed class ReviewApiTests : IAsyncLifetime
{
   private PinnedTourFixture _fixture = null!;

   public async ValueTask InitializeAsync() => _fixture = await PinnedTourFixture.StartAsync();

   public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

   [Fact]
   public async Task Marked_file_counts_as_reviewed_until_it_is_cleared()
   {
      var added = HeadObjectId("src/added.txt");

      var marked = await MarkAsync("src/added.txt", added);
      Assert.Equal(["src/added.txt"], await ReviewedPathsAsync());
      Assert.True(JsonNode.DeepEquals(JsonNode.Parse($$"""{ "marks": [{ "path": "src/added.txt", "headObjectId": "{{added}}" }] }"""), marked), marked.ToJsonString());

      await MarkAsync("src/added.txt", added);
      Assert.Equal(["src/added.txt"], await ReviewedPathsAsync());

      var cleared = await ClearAsync("src/added.txt");
      Assert.Empty(await ReviewedPathsAsync());
      Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{ "marks": [] }"""), cleared), cleared.ToJsonString());

      await ClearAsync("src/added.txt");
      Assert.Empty(await ReviewedPathsAsync());
   }

   [Fact]
   public async Task Deleted_file_marked_without_an_object_id_counts_while_it_stays_deleted()
   {
      await MarkAsync("src/deleted.txt", null);

      Assert.Equal(["src/deleted.txt"], await ReviewedPathsAsync());
   }

   /// <summary>Each mark names a version of its file that the pinned head does not hold.</summary>
   [Fact]
   public async Task Mark_whose_object_id_differs_from_the_head_counts_as_cleared()
   {
      await MarkAsync("src/added.txt", HeadObjectId("src/modified.txt"));
      await MarkAsync("src/deleted.txt", HeadObjectId("src/added.txt"));
      await MarkAsync("src/modified.txt", null);

      Assert.Empty(await ReviewedPathsAsync());
   }

   [Fact]
   public async Task Mark_made_at_an_earlier_head_counts_only_for_the_files_that_the_new_head_leaves_alone()
   {
      await MarkAsync("src/added.txt", HeadObjectId("src/added.txt"));
      await MarkAsync("src/modified.txt", HeadObjectId("src/modified.txt"));
      await MarkAsync("src/deleted.txt", null);
      _fixture.Git.Checkout("feature");
      var newHead = _fixture.Git.Commit("Next iteration", ("src/modified.txt", PinnedTourFixture.ModifiedHead + "six\n"), ("src/deleted.txt", "back\n"));

      _fixture.Harness.ChangeStored(TourIdOf(), tour => tour with { Pin = ((TourPin.Pinned)tour.Pin) with { Head = newHead, Iteration = 2 } });

      Assert.Equal(["src/added.txt"], await ReviewedPathsAsync());
   }

   [Fact]
   public async Task Review_marks_survive_a_server_restart()
   {
      await MarkAsync("src/added.txt", HeadObjectId("src/added.txt"));

      await _fixture.RestartServerAsync();

      Assert.Equal(["src/added.txt"], await ReviewedPathsAsync());
   }

   [Fact]
   public async Task Review_marks_stay_out_of_the_tour_that_read_state_returns()
   {
      await MarkAsync("src/added.txt", HeadObjectId("src/added.txt"));

      var tour = await _fixture.Harness.CallAsync(On("read_state", _fixture.TourId));

      Assert.DoesNotContain("src/added.txt", tour.ToJsonString(), StringComparison.Ordinal);
   }

   [Fact]
   public async Task Concurrent_marks_on_different_files_are_all_stored()
   {
      string[] paths = ["bytes.txt", "logo.png", "page.html", "src/added.txt", "src/modified.txt", "src/renamed.txt"];

      await Task.WhenAll(paths.Select(path => MarkAsync(path, HeadObjectId(path))));

      Assert.Equal(paths, await ReviewedPathsAsync());
   }

   [Theory]
   [InlineData("mark_file_reviewed", """{ "path": "../etc/passwd", "headObjectId": null }""", 404, "fileNotFound", "path")]
   [InlineData("mark_file_reviewed", """{ "path": "/src/added.txt", "headObjectId": null }""", 404, "fileNotFound", "path")]
   [InlineData("clear_file_reviewed", """{ "path": "src/./added.txt" }""", 404, "fileNotFound", "path")]
   [InlineData("mark_file_reviewed", """{ "path": "src/added.txt", "headObjectId": "abc123" }""", 400, "invalidInput", "headObjectId")]
   [InlineData("mark_file_reviewed", """{ "path": "src/added.txt" }""", 400, "invalidInput", "headObjectId")]
   [InlineData("clear_file_reviewed", """{ "path": "src/added.txt", "headObjectId": null }""", 400, "invalidInput", "headObjectId")]
   public async Task Malformed_mark_is_refused_and_stores_nothing(string operation, string body, int status, string code, string? field)
   {
      var (actualStatus, error) = await SendAsync(On(operation, _fixture.TourId), body);

      Assert.Equal(status, actualStatus);
      Assert.Equal(code, (string)error["code"]!);
      Assert.Equal(field, (string?)error["field"]);
      Assert.Empty(_fixture.Harness.StoredReviewMarks(TourIdOf())!);
   }

   [Theory]
   [InlineData("mark_file_reviewed", """{ "path": "src/added.txt", "headObjectId": null }""")]
   [InlineData("clear_file_reviewed", """{ "path": "src/added.txt" }""")]
   public async Task Mark_on_an_unknown_tour_is_not_found(string operation, string body)
   {
      var (status, error) = await SendAsync(On(operation, new string('a', 16)), body);

      Assert.Equal(404, status);
      Assert.Equal("tourNotFound", (string)error["code"]!);
   }

   [Theory]
   [InlineData("mark_file_reviewed")]
   [InlineData("clear_file_reviewed")]
   public async Task Mark_without_a_token_is_refused(string operation)
   {
      using var response = await _fixture.Harness.SendAsync(_fixture.Harness.Request(On(operation, _fixture.TourId), json: """{ "path": "src/added.txt" }"""));

      Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
   }

   private string HeadObjectId(string path) => _fixture.Git.Git(_fixture.Git.OriginPath, "rev-parse", $"{_fixture.Head}:{path}").Trim();

   private TourId TourIdOf() => TourId.TryParse(_fixture.TourId, out var id) ? id : throw new InvalidOperationException("The fixture's tour ID does not parse.");

   private Task<JsonNode> MarkAsync(string path, string? headObjectId) =>
      _fixture.Harness.CallAsync(On("mark_file_reviewed", _fixture.TourId), new JsonObject { ["path"] = path, ["headObjectId"] = headObjectId }.ToJsonString());

   private Task<JsonNode> ClearAsync(string path) => _fixture.Harness.CallAsync(On("clear_file_reviewed", _fixture.TourId), new JsonObject { ["path"] = path }.ToJsonString());

   private async Task<string[]> ReviewedPathsAsync()
   {
      var listed = await _fixture.Harness.CallAsync(On("list_changed_files", _fixture.TourId));
      return [.. listed["reviewedPaths"]!.AsArray().Select(path => (string)path!)];
   }

   private async Task<(int Status, JsonNode Body)> SendAsync(OperationCall call, string body)
   {
      using var response = await _fixture.Harness.SendJsonAsync(call, body);
      var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
      return ((int)response.StatusCode, SuccessBody.OutputOrError(response, text));
   }
}
