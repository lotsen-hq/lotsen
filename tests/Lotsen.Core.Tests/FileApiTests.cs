using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Hosting;
using Lotsen.Operations;
using Lotsen.Tours;
using Microsoft.Extensions.DependencyInjection;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>
/// Covers how the file operations read a file across a rename, how they withhold the content of binary and large files, and how they refuse a request: without the token, for a tour that is not
/// pinned, for a path outside the repository or not on the requested side, and for a file above the most they return.
/// </summary>
[Trait("Category", "Api")]
public sealed class FileApiTests
{
   private const string CreateBody = $$"""{ "prUrl": "{{TestPlatform.PrUrl}}", "familiarity": "some" }""";

   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

   public static TheoryData<string> ExternalFailures => ExternalFailureKinds.Names;

   public static TheoryData<string, string> FileReads => new()
   {
      { "list_changed_files", "{}" },
      { "read_file_text", """{ "side": "head", "path": "src/added.txt" }""" },
      { "diff_file", """{ "path": "src/added.txt" }""" },
      { "list_directory", """{ "side": "head" }""" },
      { "list_directory", """{ "side": "base", "path": "src" }""" },
   };

   public static TheoryData<string, string> BinaryReads => new()
   {
      { "diff_file", """{ "path": "logo.png" }""" },
      { "diff_file", """{ "path": "logo.png", "large": "include" }""" },
      { "read_file_text", """{ "side": "head", "path": "logo.png" }""" },
      { "diff_file", """{ "path": "assets/new.bin", "oldPath": "assets/old.bin" }""" },
      { "read_file_text", """{ "side": "base", "path": "assets/old.bin" }""" },
   };

   [Theory]
   [MemberData(nameof(FileReads))]
   public async Task File_read_without_a_token_is_refused(string operation, string input)
   {
      await using var harness = await ServerHarness.StartAsync();
      var id = (string)harness.CreateUnboundTour(CreateBody)["id"]!;

      using var response = await harness.SendAsync(harness.Request(On(operation, id), json: input));

      Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
   }

   /// <summary>The harness's platform has no pull request, so the tour stays pinning.</summary>
   [Theory]
   [MemberData(nameof(FileReads))]
   public async Task File_read_of_a_pinning_tour_is_refused_with_its_pin(string operation, string input)
   {
      await using var harness = await ServerHarness.StartAsync();
      var id = (string)harness.CreateUnboundTour(CreateBody)["id"]!;

      var (status, error) = await GetAsync(harness, On(operation, id), input);

      Assert.Equal(409, status);
      Assert.Equal("tourNotPinned", (string)error["code"]!);
      Assert.Equal("tourId", (string)error["field"]!);
      Assert.Contains("pinning", (string)error["reason"]!, StringComparison.Ordinal);
      Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{ "state": "pinning" }"""), error["pin"]), error.ToJsonString());
   }

   [Theory]
   [MemberData(nameof(FileReads))]
   public async Task File_read_of_a_tour_whose_pin_failed_is_refused_with_its_pin_and_reason(string operation, string input)
   {
      var platform = new TestPlatform();
      platform.Fail("The pull request was not found. Check the URL.");
      await using var harness = await ServerHarness.StartAsync(platform: platform);
      var id = (string)harness.CreateUnboundTour(CreateBody)["id"]!;
      await harness.PinsIdleAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);
      var pin = (await harness.CallAsync(On("read_state", id)))["pin"]!;
      Assert.Equal("failed", (string)pin["state"]!);

      var (status, error) = await GetAsync(harness, On(operation, id), input);

      Assert.Equal(409, status);
      Assert.Equal("tourNotPinned", (string)error["code"]!);
      Assert.Contains((string)pin["reason"]!, (string)error["reason"]!, StringComparison.Ordinal);
      Assert.True(JsonNode.DeepEquals(pin, error["pin"]), error.ToJsonString());
   }

   [Theory]
   [MemberData(nameof(FileReads))]
   public async Task File_read_of_an_unknown_tour_is_not_found(string operation, string input)
   {
      await using var harness = await ServerHarness.StartAsync();

      var (status, error) = await GetAsync(harness, On(operation, new string('a', 16)), input);

      Assert.Equal(404, status);
      Assert.Equal("tourNotFound", (string)error["code"]!);
   }

   [Theory]
   [InlineData("read_file_text", """{ "side": "head", "path": "../etc/passwd" }""")]
   [InlineData("read_file_text", """{ "side": "head", "path": "src/../../etc/passwd" }""")]
   [InlineData("read_file_text", """{ "side": "head", "path": "../src/added.txt" }""")]
   [InlineData("read_file_text", """{ "side": "head", "path": "/etc/passwd" }""")]
   [InlineData("read_file_text", """{ "side": "head", "path": "src\\added.txt" }""")]
   [InlineData("read_file_text", """{ "side": "head", "path": "C:/Windows/win.ini" }""")]
   [InlineData("read_file_text", """{ "side": "head", "path": "./src/added.txt" }""")]
   [InlineData("read_file_text", """{ "side": "head", "path": "" }""")]
   [InlineData("diff_file", """{ "path": "../etc/passwd" }""")]
   [InlineData("diff_file", """{ "path": "src/./added.txt" }""")]
   [InlineData("read_file_text", """{ "side": "base", "path": "src/added.txt" }""")]
   [InlineData("read_file_text", """{ "side": "head", "path": "src/deleted.txt" }""")]
   [InlineData("read_file_text", """{ "side": "head", "path": "src" }""")]
   [InlineData("read_file_text", """{ "side": "head", "path": "no-such-file.txt" }""")]
   [InlineData("diff_file", """{ "path": "no-such-file.txt" }""")]
   [InlineData("diff_file", """{ "path": "src" }""")]
   [InlineData("list_directory", """{ "side": "head", "path": "../etc" }""")]
   [InlineData("list_directory", """{ "side": "head", "path": "src/../src" }""")]
   [InlineData("list_directory", """{ "side": "head", "path": "/src" }""")]
   [InlineData("list_directory", """{ "side": "head", "path": "src/" }""")]
   [InlineData("list_directory", """{ "side": "head", "path": "" }""")]
   [InlineData("list_directory", """{ "side": "head", "path": "src/added.txt" }""")]
   [InlineData("list_directory", """{ "side": "head", "path": "no-such-directory" }""")]
   [InlineData("list_directory", """{ "side": "base", "path": "data" }""")]
   public async Task Path_outside_the_repository_or_not_on_the_side_is_not_found(string operation, string input)
   {
      await using var fixture = await PinnedTourFixture.StartAsync();

      var (status, error) = await GetAsync(fixture.Harness, On(operation, fixture.TourId), input);

      Assert.Equal(404, status);
      Assert.Equal("fileNotFound", (string)error["code"]!);
      Assert.Equal("path", (string)error["field"]!);
   }

   [Fact]
   public async Task Directory_listing_of_a_pinned_tour_lists_the_root_at_the_head()
   {
      await using var fixture = await PinnedTourFixture.StartAsync();

      var listed = await fixture.Harness.CallAsync(On("list_directory", fixture.TourId), """{ "side": "head" }""");

      Assert.Equal(fixture.Head.Value, (string)listed["commit"]!);
      Assert.Contains(listed["entries"]!.AsArray(), entry => JsonNode.DeepEquals(JsonNode.Parse("""{ "path": "src", "type": "directory" }"""), entry));
   }

   [Theory]
   [InlineData("""{ "path": "src/renamed.txt", "oldPath": "../src/old-name.txt" }""", "oldPath")]
   [InlineData("""{ "path": "src/renamed.txt", "oldPath": "no-such-file.txt" }""", "oldPath")]
   [InlineData("""{ "path": "src/renamed.txt", "oldPath": "src/added.txt" }""", "oldPath")]
   [InlineData("""{ "path": "../src/renamed.txt", "oldPath": "src/old-name.txt" }""", "path")]
   [InlineData("""{ "path": "no-such-file.txt", "oldPath": "src/old-name.txt" }""", "path")]
   [InlineData("""{ "path": "src/deleted.txt", "oldPath": "src/old-name.txt" }""", "path")]
   public async Task Diff_across_a_rename_names_the_path_that_is_not_a_file_on_its_side(string input, string field)
   {
      await using var fixture = await PinnedTourFixture.StartAsync();

      var (status, error) = await GetAsync(fixture.Harness, On("diff_file", fixture.TourId), input);

      Assert.Equal(404, status);
      Assert.Equal("fileNotFound", (string)error["code"]!);
      Assert.Equal(field, (string)error["field"]!);
      Assert.StartsWith($"{field}: ", (string)error["reason"]!, StringComparison.Ordinal);
   }

   [Fact]
   public async Task Diff_of_a_renamed_file_with_its_old_path_covers_only_the_edited_line()
   {
      await using var fixture = await PinnedTourFixture.StartAsync();

      var diff = await fixture.Harness.CallAsync(On("diff_file", fixture.TourId), """{ "path": "src/renamed.txt", "oldPath": "src/old-name.txt" }""");

      var baseText = (string)diff["baseText"]!;
      var headText = (string)diff["headText"]!;
      Assert.Equal(PinnedTourFixture.RenamedBase, baseText);
      Assert.Equal(PinnedTourFixture.RenamedHead, headText);
      var change = Assert.Single(diff["changes"]!.AsArray());
      Assert.Equal("gamma\n", baseText[(int)change!["fromA"]!..(int)change["toA"]!]);
      Assert.Equal("GAMMA\n", headText[(int)change["fromB"]!..(int)change["toB"]!]);
   }

   /// <summary>The fixture's <c>src/line-endings.txt</c> changes only its line breaks, from LF to CRLF.</summary>
   [Fact]
   public async Task Diff_with_whitespace_ignored_has_no_changes_for_a_file_whose_only_change_is_its_line_endings()
   {
      await using var fixture = await PinnedTourFixture.StartAsync();
      var diff = On("diff_file", fixture.TourId);

      var compared = await fixture.Harness.CallAsync(diff, """{ "path": "src/line-endings.txt", "whitespace": "compare" }""");
      var ignored = await fixture.Harness.CallAsync(diff, """{ "path": "src/line-endings.txt", "whitespace": "ignore" }""");

      Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""[{ "fromA": 0, "toA": 8, "fromB": 0, "toB": 10 }]"""), compared["changes"]), compared["changes"]!.ToJsonString());
      Assert.Equal(("one\ntwo\n", "one\r\ntwo\r\n"), ((string)ignored["baseText"]!, (string)ignored["headText"]!));
      Assert.Empty(ignored["changes"]!.AsArray());
   }

   [Theory]
   [MemberData(nameof(FileReads))]
   public async Task File_read_answers_the_ui_without_the_tours_controls(string operation, string input)
   {
      await using var fixture = await PinnedTourFixture.StartAsync();

      using var response = await fixture.Harness.SendJsonAsync(On(operation, fixture.TourId), input);
      var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;

      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
      Assert.Equal(["output"], body.AsObject().Select(property => property.Key));
   }

   [Theory]
   [MemberData(nameof(BinaryReads))]
   public async Task Binary_file_text_and_diff_carry_no_bytes_of_its_content(string operation, string input)
   {
      await using var fixture = await PinnedTourFixture.StartAsync();

      using var response = await fixture.Harness.SendJsonAsync(On(operation, fixture.TourId), input);
      var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
      Assert.Equal("binary", (string)SuccessBody.Output(raw)["content"]!);
      Assert.DoesNotContain(PinnedTourFixture.BinaryMarker, raw, StringComparison.Ordinal);
      Assert.DoesNotContain("\\u0000", raw, StringComparison.Ordinal);
   }

   [Fact]
   public async Task Large_file_diff_comes_with_only_its_size_until_the_caller_includes_it()
   {
      await using var fixture = await PinnedTourFixture.StartAsync();
      var diff = On("diff_file", fixture.TourId);

      var (status, withheld) = await GetAsync(fixture.Harness, diff, """{ "path": "data/large.txt" }""");
      var included = await fixture.Harness.CallAsync(diff, """{ "path": "data/large.txt", "large": "include" }""");

      Assert.Equal(200, status);
      Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{ "content": "large", "size": 1048577 }"""), withheld), withheld.ToJsonString());
      Assert.Equal("text", (string)included["content"]!);
      Assert.Null(included["baseText"]);
      Assert.Equal(PinnedTourFixture.LargeText, (string)included["headText"]!);
   }

   [Fact]
   public async Task Large_file_text_comes_with_only_its_size_until_the_caller_includes_it()
   {
      await using var fixture = await PinnedTourFixture.StartAsync();
      var text = On("read_file_text", fixture.TourId);

      var (status, withheld) = await GetAsync(fixture.Harness, text, """{ "side": "head", "path": "data/large.txt" }""");
      var included = await fixture.Harness.CallAsync(text, """{ "side": "head", "path": "data/large.txt", "large": "include" }""");

      Assert.Equal(200, status);
      Assert.Equal(("large", 1048577L, "data/large.txt"), ((string)withheld["content"]!, (long)withheld["size"]!, (string)withheld["path"]!));
      Assert.Null(withheld["text"]);
      Assert.Equal("text", (string)included["content"]!);
      Assert.Equal(PinnedTourFixture.LargeText, (string)included["text"]!);
   }

   /// <summary><c>huge.txt</c> is one byte above the most that the file operations return.</summary>
   [Theory]
   [InlineData("diff_file", """{ "path": "huge.txt", "large": "include" }""")]
   [InlineData("read_file_text", """{ "side": "head", "path": "huge.txt", "large": "include" }""")]
   public async Task File_above_the_most_that_the_routes_return_is_refused_even_when_included(string operation, string input)
   {
      await using var fixture = await PinnedTourFixture.StartAsync(("huge.txt", new string('h', 16 * 1024 * 1024) + "!"));

      var (status, error) = await GetAsync(fixture.Harness, On(operation, fixture.TourId), input);

      Assert.Equal(400, status);
      Assert.Equal("invalidInput", (string)error["code"]!);
      Assert.Equal("large", (string)error["field"]!);
      Assert.Contains("16777217 bytes", (string)error["reason"]!, StringComparison.Ordinal);
   }

   /// <summary>The tour file is written by hand, because a pin never stores a URL that the clone cache refuses.</summary>
   [Theory]
   [MemberData(nameof(FileReads))]
   public async Task File_read_of_a_tour_pinned_to_a_url_that_lotsen_does_not_clone_is_refused(string operation, string input)
   {
      var dataDirectory = new DataDirectory(Directory.CreateTempSubdirectory("lotsen-tests-").FullName);
      var id = TourId.New();
      Directory.CreateDirectory(dataDirectory.ToursPath);
      File.WriteAllText(Path.Combine(dataDirectory.ToursPath, $"{id}.json"), $$"""
         {"schemaVersion":{{TourDocument.CurrentSchemaVersion}},"tour":{"id":"{{id}}","target":{"kind":"pullRequest","platform":"{{TestPlatform.DefaultId}}","url":"{{TestPlatform.PrUrl}}"},
         "familiarity":"some","createdAt":"2026-10-21T09:30:00+00:00","updatedAt":"2026-10-21T09:30:00+00:00",
         "pin":{"state":"pinned","mergeBase":"{{new string('a', 40)}}","head":"{{new string('b', 40)}}","iteration":1,
         "remoteUrl":"ssh://platform.test/shop"},"plan":null,"threads":[],"answers":[],"earlierVersions":[],"skippedStops":[]},"reviewMarks":[] }
         """);
      await using var harness = await ServerHarness.StartAsync(dataDirectory: dataDirectory);

      var (status, error) = await GetAsync(harness, On(operation, id.Value), input);

      Assert.Equal(502, status);
      Assert.Equal("repositoryUnavailable", (string)error["code"]!);
      Assert.Contains("ssh", (string)error["reason"]!, StringComparison.Ordinal);
   }

   /// <summary>
   /// The tour file is written by hand, pinned to an <see cref="AuthenticatingGitServer"/> that refuses the test platform's credential, so the read's sync of the clone is refused.
   /// </summary>
   [Fact]
   public async Task File_read_whose_remote_refuses_the_platforms_credential_is_refused_with_the_credentials_fix()
   {
      using var git = new GitFixture();
      var commit = git.Commit("Base", ("src/added.txt", "one\n"));
      await using var server = await AuthenticatingGitServer.StartAsync(git.OriginPath, "Bearer another-token");
      var dataDirectory = new DataDirectory(Directory.CreateTempSubdirectory("lotsen-tests-").FullName);
      var id = TourId.New();
      Directory.CreateDirectory(dataDirectory.ToursPath);
      File.WriteAllText(Path.Combine(dataDirectory.ToursPath, $"{id}.json"), $$"""
         {"schemaVersion":{{TourDocument.CurrentSchemaVersion}},"tour":{"id":"{{id}}","target":{"kind":"pullRequest","platform":"{{TestPlatform.DefaultId}}","url":"{{TestPlatform.PrUrl}}"},
         "familiarity":"some","createdAt":"2026-10-21T09:30:00+00:00","updatedAt":"2026-10-21T09:30:00+00:00",
         "pin":{"state":"pinned","mergeBase":"{{commit}}","head":"{{commit}}","iteration":1,
         "remoteUrl":"{{server.RepositoryUrl}}"},"plan":null,"threads":[],"answers":[],"earlierVersions":[],"skippedStops":[]},"reviewMarks":[] }
         """);
      await using var harness = await ServerHarness.StartAsync(dataDirectory: dataDirectory);

      var (status, error) = await GetAsync(harness, On("read_file_text", id.Value), """{ "side": "head", "path": "src/added.txt" }""");

      Assert.Equal(502, status);
      Assert.Equal("repositoryUnavailable", (string)error["code"]!);
      Assert.Equal(
         $"The repository of tour {id} could not be read. Git could not clone {server.RepositoryUrl}: the remote refused lotsen's credential. {TestGitCredential.Hint}",
         (string)error["problem"]!);
   }

   /// <summary>The test edits the stored tour's platform, because create_tour accepts only a URL that a registered platform recognizes.</summary>
   [Fact]
   public async Task File_read_of_a_tour_whose_platform_is_not_registered_is_refused()
   {
      await using var fixture = await PinnedTourFixture.StartAsync();
      var path = Path.Combine(fixture.Harness.DataDirectory.ToursPath, $"{fixture.TourId}.json");
      var stored = File.ReadAllText(path);
      File.WriteAllText(path, stored.Replace($"\"platform\":\"{TestPlatform.DefaultId}\"", "\"platform\":\"gone\"", StringComparison.Ordinal));
      Assert.NotEqual(stored, File.ReadAllText(path));

      var (status, error) = await GetAsync(fixture.Harness, On("read_file_text", fixture.TourId), """{ "side": "head", "path": "src/added.txt" }""");

      Assert.Equal(502, status);
      Assert.Equal("repositoryUnavailable", (string)error["code"]!);
      Assert.Contains("does not know the platform 'gone'", (string)error["reason"]!, StringComparison.Ordinal);
   }

   /// <summary>The query throws after the clone opened, so the row's exception is the only failure.</summary>
   [Theory]
   [MemberData(nameof(ExternalFailures))]
   public async Task Repository_query_that_fails_outside_the_process_is_refused_as_repository_unavailable_with_the_reason(string failure)
   {
      await using var fixture = await PinnedTourFixture.StartAsync();
      var tour = fixture.Harness.Writer.Read(ParsedId(fixture.TourId)).Value!;

      var result = await RepositoryQueryAsync(fixture, tour, ExternalFailureKinds.Of(failure, "The remote went away. Try again."));

      Assert.Equal(OperationErrorCode.RepositoryUnavailable, result.Error?.Code);
      Assert.Equal(HttpStatusCode.BadGateway, OperationWire.StatusOf(result.Error!.Code.Kind));
      Assert.Equal($"The repository of tour {tour.Id} could not be read. The remote went away. Try again.", result.Error.Reason);
   }

   [Fact]
   public async Task Repository_query_that_faults_in_lotsen_throws_its_exception()
   {
      await using var fixture = await PinnedTourFixture.StartAsync();
      var tour = fixture.Harness.Writer.Read(ParsedId(fixture.TourId)).Value!;

      await Assert.ThrowsAsync<InvalidOperationException>(() => RepositoryQueryAsync(fixture, tour, new InvalidOperationException("The query faulted.")));
   }

   [Theory]
   [InlineData("read_file_text", """{ "side": "left", "path": "src/added.txt" }""", "side")]
   [InlineData("read_file_text", """{ "side": "Head", "path": "src/added.txt" }""", "side")]
   [InlineData("read_file_text", """{ "path": "src/added.txt" }""", null)]
   [InlineData("read_file_text", """{ "side": "head" }""", null)]
   [InlineData("read_file_text", """{ "side": "head", "side": "base", "path": "src/added.txt" }""", "side")]
   [InlineData("read_file_text", """{ "side": "head", "path": "src/added.txt", "line": "3" }""", null)]
   [InlineData("diff_file", """{ "path": "src/added.txt", "tourId": "aaaaaaaaaaaaaaaa" }""", "tourId")]
   [InlineData("diff_file", """{ "path": "src/added.txt", "large": "yes" }""", "large")]
   [InlineData("read_file_text", """{ "side": "head", "path": "src/added.txt", "large": "Include" }""", "large")]
   [InlineData("diff_file", """{ "path": "src/added.txt", "whitespace": "yes" }""", "whitespace")]
   [InlineData("read_file_text", """{ "side": "head", "path": "src/added.txt", "whitespace": "ignore" }""", null)]
   [InlineData("list_directory", """{ "path": "src" }""", null)]
   [InlineData("list_directory", """{ "side": "left" }""", "side")]
   [InlineData("list_directory", """{ "side": "head", "large": "include" }""", null)]
   [InlineData("read_file_text", """{ "side": "head", "path": "src/added.txt", "large": true }""", "large")]
   [InlineData("diff_file", """{ "path": 5 }""", "path")]
   [InlineData("diff_file", """{ "path": null }""", "path")]
   public async Task Input_that_does_not_fit_the_schema_is_invalid_input(string operation, string input, string? field)
   {
      await using var harness = await ServerHarness.StartAsync();
      var id = (string)harness.CreateUnboundTour(CreateBody)["id"]!;

      var (status, error) = await GetAsync(harness, On(operation, id), input);

      Assert.Equal(400, status);
      Assert.Equal("invalidInput", (string)error["code"]!);
      if (field is not null)
      {
         Assert.Equal(field, (string)error["field"]!);
      }
   }

   private static TourId ParsedId(string text) => TourId.TryParse(text, out var id) ? id : throw new ArgumentException(text);

   private static Task<OperationResult<ChangedFileList>> RepositoryQueryAsync(PinnedTourFixture fixture, Tour tour, Exception thrown) =>
      fixture.Harness.Server.Services.GetRequiredService<FileOperations>()
         .OnFactsAsync<ChangedFileList>(tour, (TourPin.Pinned)tour.Pin, _ => Task.FromException<OperationResult<ChangedFileList>>(thrown));

   private static async Task<(int Status, JsonNode Body)> GetAsync(ServerHarness harness, OperationCall call, string input)
   {
      using var response = await harness.SendJsonAsync(call, input);
      var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
      return ((int)response.StatusCode, SuccessBody.OutputOrError(response, body));
   }
}
