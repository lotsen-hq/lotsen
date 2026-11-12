using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Operations;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>Covers the tour operations through the HTTP API: create tour, set plan, read state and list tours.</summary>
[Trait("Category", "Api")]
public sealed class TourApiTests : IAsyncLifetime
{
   private const string PrUrl = TestPlatform.PrUrl;

   private const string ValidPlan = """
      {
        "chapters": [{ "id": "orientation", "title": "Orientation" }, { "id": "pricing", "title": "Pricing" }],
        "stops": [
          { "title": "What this PR changes", "kind": "overview", "chapter": "orientation" },
          { "title": "How prices were computed", "kind": "context", "chapter": "orientation",
            "anchorHint": { "path": "src/Pricing/PriceCalculator.cs" } },
          { "kind": "inspect", "title": "The new discount rule", "chapter": "pricing",
            "anchorHint": { "path": "src/Pricing/Discounts.cs", "line": 40 } },
          { "title": "Rounding on stacked discounts", "kind": "risk", "chapter": "pricing" }
        ]
      }
      """;

   private const string GeneratedAndMechanicalMarks = """
      [
        { "path": "src/Api/Client.g.cs", "mark": "generated", "reason": "NSwag writes it from the OpenAPI document." },
        { "path": "src/Pricing/Discounts.cs", "mark": "mechanical", "reason": "Only the namespace changed." }
      ]
      """;

   private const string MarkedPlan = $$"""
      {
        "chapters": [{ "id": "pricing", "title": "Pricing" }],
        "stops": [{ "kind": "inspect", "title": "The new discount rule", "chapter": "pricing" }],
        "fileMarks": {{GeneratedAndMechanicalMarks}}
      }
      """;

   private ServerHarness _harness = null!;

   public async ValueTask InitializeAsync() => _harness = await ServerHarness.StartAsync();

   public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

   [Fact]
   public async Task Created_tour_with_a_plan_is_visible_through_read_state_and_list()
   {
      var created = await CreateTourAsync();
      var id = (string)created["id"]!;

      var planned = await _harness.AgentCallAsync(On("set_plan", id), ValidPlan);
      var read = await _harness.CallAsync(On("read_state", id));
      var list = await _harness.CallAsync("list_tours");

      Assert.Equal("pullRequest", (string)created["target"]!["kind"]!);
      Assert.Equal(TestPlatform.DefaultId, (string)created["target"]!["platform"]!);
      Assert.Equal(PrUrl, (string)created["target"]!["url"]!);
      Assert.Equal("some", (string)created["familiarity"]!);
      Assert.Null(created["plan"]);

      Assert.True(JsonNode.DeepEquals(planned, read), $"read_state differs from set_plan's result: {read}");
      var stops = read["plan"]!["stops"]!.AsArray();
      Assert.Equal(["What this PR changes", "How prices were computed", "The new discount rule", "Rounding on stacked discounts"], stops.Select(stop => (string)stop!["title"]!));
      Assert.Equal(["overview", "context", "inspect", "risk"], stops.Select(stop => (string)stop!["kind"]!));
      Assert.All(stops, stop => Assert.Equal("pending", (string)stop!["status"]!));
      Assert.Equal(4, stops.Select(stop => (string)stop!["id"]!).Distinct().Count());
      Assert.Equal(40, (int)stops[2]!["anchorHint"]!["line"]!);
      Assert.Equal(["orientation", "pricing"], read["plan"]!["chapters"]!.AsArray().Select(c => (string)c!["id"]!));

      var summary = Assert.Single(list["tours"]!.AsArray())!;
      Assert.Equal(id, (string)summary["id"]!);
      Assert.Equal(PrUrl, (string)summary["target"]!["url"]!);
      Assert.Equal(TestPlatform.PrLabel, (string)summary["label"]!);
      Assert.Equal("some", (string)summary["familiarity"]!);
      Assert.Equal(4, (int)summary["stopCount"]!);
      Assert.Equal((string)read["updatedAt"]!, (string)summary["updatedAt"]!);
   }

   [Fact]
   public async Task List_returns_tours_oldest_first()
   {
      var first = (string)(await CreateTourAsync())["id"]!;
      var second = (string)(await CreateTourAsync(familiarity: "new"))["id"]!;

      var list = await _harness.CallAsync("list_tours");

      Assert.Equal([first, second], list["tours"]!.AsArray().Select(tour => (string)tour!["id"]!));
      Assert.All(list["tours"]!.AsArray(), tour => Assert.Equal(0, (int)tour!["stopCount"]!));
   }

   [Fact]
   public async Task List_gives_each_tour_the_revision_of_its_last_write_response()
   {
      var once = (string)(await CreateTourAsync())["id"]!;
      var twice = (string)(await CreateTourAsync())["id"]!;
      var planned = await RevisionOfAsync(await _harness.AgentSendJsonAsync(On("set_plan", once), ValidPlan));
      await _harness.AgentCallAsync(On("set_plan", twice), ValidPlan);
      var replanned = await RevisionOfAsync(await _harness.AgentSendJsonAsync(On("set_plan", twice), ValidPlan));

      var tours = (await _harness.CallAsync("list_tours"))["tours"]!.AsArray();

      Assert.Equal([once, twice], tours.Select(tour => (string)tour!["id"]!));
      Assert.Equal([planned, replanned], tours.Select(tour => (long)tour!["revision"]!));
      Assert.NotEqual(planned, replanned);
   }

   /// <summary>The test platform normalizes its URLs by dropping the query, the fragment and a trailing slash.</summary>
   [Fact]
   public async Task Pull_request_url_is_stored_as_its_platform_normalizes_it()
   {
      var created = await CreateTourAsync($"{PrUrl}/?tab=files#top");

      Assert.Equal(PrUrl, (string)created["target"]!["url"]!);
   }

   [Fact]
   public async Task Pull_request_url_that_no_platform_recognizes_is_refused_with_every_platforms_accepted_forms()
   {
      var first = new TestPlatform("first", "first.test");
      var second = new TestPlatform("second", "second.test");
      await using var harness = await ServerHarness.StartAsync(configure: options => options with { Platforms = [_ => first, _ => second] });

      using var response = await harness.AgentSendJsonAsync("create_tour", """{ "prUrl": "https://third.test/shop/pull/42", "familiarity": "some" }""");

      var error = await ReadErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("prUrl", (string)error["field"]!);
      Assert.Contains(first.ExpectedForms, (string)error["reason"]!, StringComparison.Ordinal);
      Assert.Contains(second.ExpectedForms, (string)error["reason"]!, StringComparison.Ordinal);
   }

   [Fact]
   public async Task Pull_request_url_is_given_to_the_first_platform_that_recognizes_it()
   {
      var other = new TestPlatform("other", "other.test");
      var platform = new TestPlatform();
      await using var harness = await ServerHarness.StartAsync(configure: options => options with { Platforms = [_ => other, _ => platform] });

      var created = await harness.AgentCallAsync("create_tour", $$"""{ "prUrl": "{{PrUrl}}", "familiarity": "some" }""");

      Assert.Equal(TestPlatform.DefaultId, (string)created["target"]!["platform"]!);
   }

   /// <summary>The tour's platform is not registered on the second server, so no platform labels it.</summary>
   [Fact]
   public async Task Summary_of_a_tour_whose_platform_is_not_registered_is_labeled_with_its_url()
   {
      var id = (string)(await CreateTourAsync())["id"]!;
      await _harness.StopServerAsync();
      await using var harness = await ServerHarness.StartAsync(dataDirectory: _harness.DataDirectory, platform: new TestPlatform("other", "other.test"));

      var summary = Assert.Single((await harness.CallAsync("list_tours"))["tours"]!.AsArray())!;

      Assert.Equal(id, (string)summary["id"]!);
      Assert.Equal(PrUrl, (string)summary["label"]!);
   }

   [Fact]
   public async Task Replanning_keeps_the_stop_ids_it_is_given_and_assigns_new_ones()
   {
      var id = (string)(await CreateTourAsync())["id"]!;
      var first = await _harness.AgentCallAsync(On("set_plan", id), ValidPlan);
      var kept = (string)first["plan"]!["stops"]![2]!["id"]!;

      var second = await _harness.AgentCallAsync(On("set_plan", id), $$"""
         {
           "chapters": [{ "id": "pricing", "title": "Pricing" }],
           "stops": [
             { "id": "{{kept}}", "kind": "inspect", "title": "The discount rule", "chapter": "pricing" },
             { "kind": "inspect", "title": "A new stop", "chapter": "pricing" }
           ]
         }
         """);

      var stops = second["plan"]!["stops"]!.AsArray();
      Assert.Equal(kept, (string)stops[0]!["id"]!);
      Assert.Equal("The discount rule", (string)stops[0]!["title"]!);
      var oldIds = first["plan"]!["stops"]!.AsArray().Select(stop => (string)stop!["id"]!);
      Assert.DoesNotContain((string)stops[1]!["id"]!, oldIds);
   }

   /// <summary>The test platform's pins never end, so the marks are set while the tour is pinning and the server cannot know which files the pull request changes.</summary>
   [Fact]
   public async Task Plan_with_file_marks_is_stored_and_returned_while_the_tour_is_pinning()
   {
      var id = (string)(await CreateTourAsync())["id"]!;

      var planned = await _harness.AgentCallAsync(On("set_plan", id), MarkedPlan);
      var read = await _harness.CallAsync(On("read_state", id));

      Assert.Equal("pinning", (string)read["pin"]!["state"]!);
      Assert.True(JsonNode.DeepEquals(planned, read), $"read_state differs from set_plan's result: {read}");
      Assert.True(
         JsonNode.DeepEquals(JsonNode.Parse(GeneratedAndMechanicalMarks), read["plan"]!["fileMarks"]),
         $"The stored marks differ: {read["plan"]!["fileMarks"]}");
   }

   [Fact]
   public async Task Plan_without_file_marks_has_an_empty_list_of_them()
   {
      var id = (string)(await CreateTourAsync())["id"]!;

      var planned = await _harness.AgentCallAsync(On("set_plan", id), ValidPlan);

      Assert.Empty(planned["plan"]!["fileMarks"]!.AsArray());
   }

   [Fact]
   public async Task Plan_with_parts_is_stored_and_returned_by_read_state()
   {
      var id = (string)(await CreateTourAsync())["id"]!;
      var parted = ValidPlan
         .Replace("\"chapters\":", """ "parts": [{ "id": "why", "title": "Why" }, { "id": "how", "title": "How" }], "chapters": """, StringComparison.Ordinal)
         .Replace("\"title\": \"Orientation\" }", "\"title\": \"Orientation\", \"part\": \"why\" }", StringComparison.Ordinal)
         .Replace("\"title\": \"Pricing\" }", "\"title\": \"Pricing\", \"part\": \"how\" }", StringComparison.Ordinal);

      var planned = await _harness.AgentCallAsync(On("set_plan", id), parted);
      var read = await _harness.CallAsync(On("read_state", id));

      Assert.True(JsonNode.DeepEquals(planned, read), $"read_state differs from set_plan's result: {read}");
      Assert.True(
         JsonNode.DeepEquals(JsonNode.Parse("""[{ "id": "why", "title": "Why" }, { "id": "how", "title": "How" }]"""), read["plan"]!["parts"]),
         $"The stored parts differ: {read["plan"]!["parts"]}");
      Assert.Equal(["why", "how"], read["plan"]!["chapters"]!.AsArray().Select(chapter => (string)chapter!["part"]!));
   }

   [Theory]
   [InlineData("")]
   [InlineData(""" "parts": null, """)]
   [InlineData(""" "parts": [], """)]
   public async Task Plan_without_parts_leaves_them_out(string parts)
   {
      var id = (string)(await CreateTourAsync())["id"]!;

      var planned = await _harness.AgentCallAsync(On("set_plan", id), ValidPlan.Replace("\"stops\":", $"{parts}\"stops\":", StringComparison.Ordinal));

      Assert.False(planned["plan"]!.AsObject().ContainsKey("parts"));
      Assert.All(planned["plan"]!["chapters"]!.AsArray(), chapter => Assert.False(chapter!.AsObject().ContainsKey("part")));
   }

   [Fact]
   public async Task Chapter_in_a_part_that_the_plan_lacks_is_rejected_with_its_field()
   {
      var id = (string)(await CreateTourAsync())["id"]!;

      using var response = await _harness.AgentSendJsonAsync(On("set_plan", id), """
         { "parts": [{ "id": "p", "title": "P" }], "chapters": [{ "id": "a", "title": "A", "part": "q" }], "stops": [{ "kind": "inspect", "title": "S", "chapter": "a" }] }
         """);

      var error = await ReadErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("chapters[0].part", (string)error["field"]!);
   }

   /// <summary>Each replan starts from a tour whose plan has marks, so a replan that kept them would show.</summary>
   [Theory]
   [InlineData("", new string[0])]
   [InlineData(""" "fileMarks": null, """, new string[0])]
   [InlineData(""" "fileMarks": [], """, new string[0])]
   [InlineData(""" "fileMarks": [{ "path": "package-lock.json", "mark": "generated", "reason": "npm writes the lock file." }], """, new[] { "package-lock.json" })]
   public async Task Replan_replaces_the_file_marks_with_the_ones_it_gives(string fileMarks, string[] paths)
   {
      var id = (string)(await CreateTourAsync())["id"]!;
      await _harness.AgentCallAsync(On("set_plan", id), MarkedPlan);

      var replanned = await _harness.AgentCallAsync(On("set_plan", id), ValidPlan.Replace("\"stops\":", $"{fileMarks}\"stops\":", StringComparison.Ordinal));

      Assert.Equal(paths, replanned["plan"]!["fileMarks"]!.AsArray().Select(mark => (string)mark!["path"]!));
      Assert.True(JsonNode.DeepEquals(replanned, await _harness.CallAsync(On("read_state", id))));
   }

   [Fact]
   public async Task Rejected_file_mark_leaves_the_previous_plan_and_its_marks_in_place()
   {
      var id = (string)(await CreateTourAsync())["id"]!;
      var planned = await _harness.AgentCallAsync(On("set_plan", id), MarkedPlan);

      using var response = await _harness.AgentSendJsonAsync(On("set_plan", id), MarkedPlan.Replace("Only the namespace changed.", "", StringComparison.Ordinal));

      await ReadErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.True(JsonNode.DeepEquals(planned, await _harness.CallAsync(On("read_state", id))));
   }

   [Theory]
   [InlineData("""{ "path": "src/a.cs", "mark": "generated", "reason": "" }""", "fileMarks[0].reason")]
   [InlineData("""{ "path": "src/a.cs", "mark": "generated", "reason": "  " }""", "fileMarks[0].reason")]
   [InlineData("""{ "path": "src/a.cs", "mark": "generated", "reason": "Generated.\nDo not edit." }""", "fileMarks[0].reason")]
   [InlineData("""{ "path": "src/a.cs", "mark": "generated", "reason": "Generated.\r\nDo not edit." }""", "fileMarks[0].reason")]
   [InlineData("""{ "path": "src/a.cs", "mark": "generated", "reason": "Generated.\rDo not edit." }""", "fileMarks[0].reason")]
   [InlineData("""{ "path": "src/a.cs", "mark": "generated", "reason": "Generated.\u2028Do not edit." }""", "fileMarks[0].reason")]
   [InlineData("""{ "path": "src/a.cs", "mark": "generated", "reason": "Generated.\u0085Do not edit." }""", "fileMarks[0].reason")]
   [InlineData("""{ "path": "src/a.cs", "mark": "generated" }""", "reason")]
   [InlineData("""{ "path": "src/a.cs", "mark": "noise", "reason": "Noise." }""", "fileMarks[0].mark")]
   [InlineData("""{ "path": "src/a.cs", "mark": "Generated", "reason": "Generated." }""", "fileMarks[0].mark")]
   [InlineData("""{ "path": "src/a.cs", "reason": "Generated." }""", "mark")]
   [InlineData("""{ "path": "../a.cs", "mark": "mechanical", "reason": "Renamed." }""", "fileMarks[0].path")]
   [InlineData("""{ "path": "src\\a.cs", "mark": "mechanical", "reason": "Renamed." }""", "fileMarks[0].path")]
   [InlineData("""{ "path": "/src/a.cs", "mark": "mechanical", "reason": "Renamed." }""", "fileMarks[0].path")]
   [InlineData("""{ "path": "", "mark": "mechanical", "reason": "Renamed." }""", "fileMarks[0].path")]
   [InlineData("""{ "path": "src/a.cs", "mark": "mechanical", "reason": "Renamed.", "lines": 3 }""", "lines")]
   [InlineData("""null""", "fileMarks[0]")]
   [InlineData("""{ "path": "src/a.cs", "mark": "mechanical", "reason": "Renamed." }, { "path": "src/a.cs", "mark": "generated", "reason": "Generated." }""", "fileMarks[1].path")]
   [InlineData("""{ "path": "src/b.cs", "mark": "mechanical", "reason": "Renamed." }, { "path": "src/a.cs", "mark": "generated", "reason": "\t" }""", "fileMarks[1].reason")]
   public async Task Invalid_file_mark_is_rejected_with_its_field_and_nothing_is_stored(string marks, string field)
   {
      var created = await CreateTourAsync();
      var id = (string)created["id"]!;

      using var response = await _harness.AgentSendJsonAsync(On("set_plan", id), $$"""
         { "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "kind": "inspect", "title": "S", "chapter": "a" }], "fileMarks": [{{marks}}] }
         """);

      var error = await ReadErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Contains(field, (string)error["reason"]!);
      Assert.DoesNotContain("Lotsen.", (string)error["reason"]!);
      Assert.True(JsonNode.DeepEquals(created, await _harness.CallAsync(On("read_state", id))));
   }

   [Fact]
   public async Task Reason_of_the_longest_length_is_accepted_and_one_character_more_is_rejected()
   {
      var id = (string)(await CreateTourAsync())["id"]!;
      static string Plan(int length) => $$"""
         { "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "kind": "inspect", "title": "S", "chapter": "a" }],
           "fileMarks": [{ "path": "src/a.cs", "mark": "generated", "reason": "{{new string('x', length)}}" }] }
         """;

      var planned = await _harness.AgentCallAsync(On("set_plan", id), Plan(PlanDrafts.MaxReasonLength));
      using var response = await _harness.AgentSendJsonAsync(On("set_plan", id), Plan(PlanDrafts.MaxReasonLength + 1));

      Assert.Equal(PlanDrafts.MaxReasonLength, ((string)planned["plan"]!["fileMarks"]![0]!["reason"]!).Length);
      var error = await ReadErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("fileMarks[0].reason", (string)error["field"]!);
   }

   [Theory]
   [InlineData("""{ "prUrl": "http://platform.test/shop/pull/42", "familiarity": "some" }""", "prUrl")]
   [InlineData("""{ "prUrl": "https://dev.azure.com/contoso/shop/_git/web/pullrequest/42", "familiarity": "some" }""", "prUrl")]
   [InlineData("""{ "prUrl": "https://platform.test.evil.example/shop/pull/42", "familiarity": "some" }""", "prUrl")]
   [InlineData("""{ "prUrl": "https://platform.test/shop/pull/", "familiarity": "some" }""", "prUrl")]
   [InlineData("""{ "prUrl": "https://platform.test/shop/pull/4x2", "familiarity": "some" }""", "prUrl")]
   [InlineData("""{ "prUrl": "not a url", "familiarity": "some" }""", "prUrl")]
   [InlineData("""{ "prUrl": "https://platform.test/shop/pull/42", "familiarity": "expert" }""", "familiarity")]
   [InlineData("""{ "prUrl": "https://platform.test/shop/pull/42", "familiarity": "Some" }""", "familiarity")]
   [InlineData("""{ "prUrl": "https://platform.test/shop/pull/42", "familiarity": 1 }""", "familiarity")]
   [InlineData("""{ "prUrl": 42, "familiarity": "some" }""", "prUrl")]
   [InlineData("""{ "prUrl": null, "familiarity": "some" }""", "prUrl")]
   [InlineData("""{ "familiarity": "some" }""", "prUrl")]
   [InlineData("""{ "prUrl": "https://platform.test/shop/pull/42" }""", "familiarity")]
   [InlineData("""{ "prUrl": "https://platform.test/shop/pull/42", "familiarity": "some", "planFirst": "yes" }""", "planFirst")]
   [InlineData("""{ "prUrl": "https://platform.test/shop/pull/42", """, null)]
   [InlineData("""[]""", null)]
   [InlineData("", null)]
   public async Task Invalid_create_tour_input_is_rejected_with_its_reason_and_nothing_is_stored(string body, string? field)
   {
      using var response = await _harness.AgentSendJsonAsync("create_tour", body);

      var error = await ReadErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      if (field is not null)
      {
         Assert.Contains(field, (string)error["reason"]!);
      }

      Assert.DoesNotContain("Lotsen.", (string)error["reason"]!);

      Assert.Empty((await _harness.CallAsync("list_tours"))["tours"]!.AsArray());
      Assert.False(Directory.Exists(_harness.DataDirectory.ToursPath) && Directory.EnumerateFileSystemEntries(_harness.DataDirectory.ToursPath).Any());
   }

   [Theory]
   [InlineData("""{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "title": "S", "chapter": "a", "kind": "test" }] }""", "stops[0].kind")]
   [InlineData("""{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "title": "S", "chapter": "a", "kind": "Inspect" }] }""", "stops[0].kind")]
   [InlineData("""{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "title": "S", "chapter": "a" }] }""", "kind")]
   [InlineData("""{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "kind": "inspect", "title": "S", "chapter": "a" }, { "title": "O", "chapter": "a", "kind": "overview" }] }""", "stops[1].kind")]
   [InlineData("""{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "kind": "inspect", "title": "S", "chapter": "a", "anchorHint": { "path": "src/a.cs", "line": 0 } }] }""", "stops[0].anchorHint.line")]
   [InlineData("""{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "kind": "inspect", "title": "S", "chapter": "a", "anchorHint": { "path": "src/a.cs", "line": "3" } }] }""", "stops[0].anchorHint.line")]
   [InlineData("""{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "kind": "inspect", "title": "S", "chapter": "a", "anchorHint": { "path": "src\\a.cs" } }] }""", "stops[0].anchorHint.path")]
   [InlineData("""{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "kind": "inspect", "title": "S", "chapter": "a", "anchorHint": { "path": "../etc/passwd" } }] }""", "stops[0].anchorHint.path")]
   [InlineData("""{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "kind": "inspect", "title": "S", "chapter": "a", "anchorHint": { "path": "src/../../x.cs" } }] }""", "stops[0].anchorHint.path")]
   [InlineData("""{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "kind": "inspect", "title": "S", "chapter": "a", "anchorHint": { "path": "./src/a.cs" } }] }""", "stops[0].anchorHint.path")]
   [InlineData("""{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "kind": "inspect", "title": "S", "chapter": "a", "anchorHint": { "path": "src/./a.cs" } }] }""", "stops[0].anchorHint.path")]
   [InlineData("""{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "kind": "inspect", "title": "S", "chapter": "a", "anchorHint": { "path": "C:/Windows/x.cs" } }] }""", "stops[0].anchorHint.path")]
   [InlineData("""{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "kind": "inspect", "title": "S", "chapter": "a", "anchorHint": { "path": "c:x.cs" } }] }""", "stops[0].anchorHint.path")]
   [InlineData("""{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "kind": "inspect", "title": "S", "chapter": "a", "anchorHint": { "path": "/etc/passwd" } }] }""", "stops[0].anchorHint.path")]
   [InlineData("""{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "kind": "inspect", "title": "S", "chapter": "a", "anchorHint": { "path": "src//a.cs" } }] }""", "stops[0].anchorHint.path")]
   [InlineData("""{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "kind": "inspect", "title": "S", "chapter": "a", "anchorHint": { "path": "src/" } }] }""", "stops[0].anchorHint.path")]
   [InlineData("""{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "kind": "inspect", "title": "S", "chapter": "a", "anchorHint": { "path": ".." } }] }""", "stops[0].anchorHint.path")]
   [InlineData("""{ "chapters": [], "stops": [{ "kind": "inspect", "title": "S", "chapter": "a" }] }""", "chapters:")]
   [InlineData("""{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "kind": "inspect", "title": "S", "chapter": "a", "id": "nosuchstop" }] }""", "stops[0].id")]
   [InlineData("""{ "chapters": [{ "id": "a", "title": "A" }], "stops": [null] }""", "stops[0]")]
   [InlineData("""{ "chapters": [{ "id": "a", "title": "A" }] }""", "stops")]
   [InlineData("""{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "kind": "inspect", "title": "S", "chapter": "a", "scenario": "x" }] }""", "scenario")]
   [InlineData("""{ "tourId": "aaaaaaaaaaaaaaaa", "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "kind": "inspect", "title": "S", "chapter": "a" }] }""", "tourId")]
   public async Task Invalid_plan_is_rejected_with_its_reason_and_nothing_is_stored(string body, string field)
   {
      var created = await CreateTourAsync();
      var id = (string)created["id"]!;

      using var response = await _harness.AgentSendJsonAsync(On("set_plan", id), body);

      var error = await ReadErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Contains(field, (string)error["reason"]!);
      Assert.DoesNotContain("Lotsen.", (string)error["reason"]!);
      var read = await _harness.CallAsync(On("read_state", id));
      Assert.True(JsonNode.DeepEquals(created, read), $"The tour changed: {read}");
   }

   /// <summary>The plan's own chapter rule names the input field, because the plan keeps the input's order of chapters.</summary>
   [Fact]
   public async Task Error_holds_its_code_problem_field_and_the_reason_that_names_the_field()
   {
      var id = (string)(await CreateTourAsync())["id"]!;

      using var response = await _harness.AgentSendJsonAsync(
         On("set_plan", id), """{ "chapters": [{ "id": "a", "title": "A" }, { "id": "b", "title": "B" }], "stops": [{ "kind": "inspect", "title": "S", "chapter": "a" }] }""");

      var error = await ReadErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal(["code", "problem", "field", "reason"], error.AsObject().Select(property => property.Key));
      Assert.Equal(("chapters[1]", "chapter 'b' has no stops."), ((string)error["field"]!, (string)error["problem"]!));
      Assert.Equal("chapters[1]: chapter 'b' has no stops.", (string)error["reason"]!);
   }

   [Fact]
   public async Task Anchor_path_with_dotted_names_is_accepted()
   {
      var id = (string)(await CreateTourAsync())["id"]!;

      var planned = await _harness.AgentCallAsync(On("set_plan", id),
         """{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "kind": "inspect", "title": "S", "chapter": "a", "anchorHint": { "path": ".github/x..y/.config.yml" } }] }""");

      Assert.Equal(".github/x..y/.config.yml", (string)planned["plan"]!["stops"]![0]!["anchorHint"]!["path"]!);
   }

   [Fact]
   public async Task Rejected_replan_leaves_the_previous_plan_in_place()
   {
      var id = (string)(await CreateTourAsync())["id"]!;
      var planned = await _harness.AgentCallAsync(On("set_plan", id), ValidPlan);

      using var response = await _harness.AgentSendJsonAsync(On("set_plan", id),
         """{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "kind": "inspect", "title": "S", "chapter": "a", "id": "nosuchstop" }] }""");

      await ReadErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.True(JsonNode.DeepEquals(planned, await _harness.CallAsync(On("read_state", id))));
   }

   [Theory]
   [InlineData("read_state", "aaaaaaaaaaaaaaaa")]
   [InlineData("set_plan", "aaaaaaaaaaaaaaaa")]
   [InlineData("read_state", "not-a-tour-id")]
   [InlineData("set_plan", "not-a-tour-id")]
   [InlineData("read_state", "AAAAAAAAAAAAAAAA")]
   [InlineData("read_state", "aaaaaaaaaaaaaaaa\n")]
   [InlineData("set_plan", "")]
   public async Task Unknown_or_malformed_tour_is_not_found(string operation, string tourId)
   {
      await CreateTourAsync();

      using var response = await _harness.AgentSendJsonAsync(On(operation, tourId), operation == "set_plan" ? ValidPlan : null);

      var error = await ReadErrorAsync(response, HttpStatusCode.NotFound, "tourNotFound");
      Assert.Contains("tourId", (string)error["reason"]!);
      Assert.Single((await _harness.CallAsync("list_tours"))["tours"]!.AsArray());
   }

   [Fact]
   public async Task List_leaves_out_an_unreadable_tour_and_read_state_reports_it()
   {
      var readable = (string)(await CreateTourAsync())["id"]!;
      var unreadable = (string)(await CreateTourAsync())["id"]!;
      await File.WriteAllTextAsync(Path.Combine(_harness.DataDirectory.ToursPath, $"{unreadable}.json"), "{", TestContext.Current.CancellationToken);

      var list = await _harness.CallAsync("list_tours");
      using var read = await _harness.SendJsonAsync(On("read_state", unreadable));

      Assert.Equal(readable, (string)Assert.Single(list["tours"]!.AsArray())!["id"]!);
      var error = await ReadErrorAsync(read, HttpStatusCode.InternalServerError, "tourUnreadable");
      Assert.DoesNotContain(_harness.DataDirectory.Path, (string)error["reason"]!);
   }

   /// <summary>A relative target URL has no absolute form for a label, so the store refuses the tour, and the list leaves it out.</summary>
   [Fact]
   public async Task List_leaves_out_a_tour_whose_target_url_is_relative()
   {
      var readable = (string)(await CreateTourAsync())["id"]!;
      var unreadable = (string)(await CreateTourAsync())["id"]!;
      var path = Path.Combine(_harness.DataDirectory.ToursPath, $"{unreadable}.json");
      var stored = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
      await File.WriteAllTextAsync(path, stored.Replace($"\"url\":\"{PrUrl}\"", "\"url\":\"shop/pull/42\"", StringComparison.Ordinal), TestContext.Current.CancellationToken);
      Assert.NotEqual(stored, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));

      var list = await _harness.CallAsync("list_tours");

      Assert.Equal(readable, (string)Assert.Single(list["tours"]!.AsArray())!["id"]!);
   }

   [Theory]
   [InlineData("POST", "api/ops/list_tours")]
   [InlineData("POST", "api/ops/create_tour")]
   [InlineData("POST", "api/ops/read_state")]
   [InlineData("POST", "api/ops/set_plan")]
   [InlineData("POST", "api/ops/no_such_operation")]
   [InlineData("GET", "api/events")]
   [InlineData("GET", "api/operations")]
   public async Task Route_without_the_token_is_refused(string method, string path)
   {
      using var response = await _harness.SendAsync(_harness.Request(new HttpMethod(method), path));

      Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
   }

   private Task<JsonNode> CreateTourAsync(string prUrl = PrUrl, string familiarity = "some") =>
      _harness.AgentCallAsync("create_tour", $$"""{ "prUrl": "{{prUrl}}", "familiarity": "{{familiarity}}" }""");

   /// <summary>Returns the tour's revision that a successful response carries beside its output.</summary>
   private static async Task<long> RevisionOfAsync(HttpResponseMessage response)
   {
      using (response)
      {
         var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
         Assert.True(response.IsSuccessStatusCode, $"{response.RequestMessage?.RequestUri} returned {(int)response.StatusCode}: {body}");
         return (long)JsonNode.Parse(body)!["revision"]!;
      }
   }

   private static async Task<JsonNode> ReadErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
   {
      var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
      Assert.True(status == response.StatusCode, $"Expected {(int)status}, got {(int)response.StatusCode}: {body}");
      Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
      var error = JsonNode.Parse(body)!;
      Assert.Equal(code, (string)error["code"]!);
      Assert.False(string.IsNullOrWhiteSpace((string?)error["reason"]), $"The error has no reason: {body}");
      return error;
   }
}
