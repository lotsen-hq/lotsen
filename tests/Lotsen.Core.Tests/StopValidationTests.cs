using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lotsen.Git;
using Lotsen.Json;
using Lotsen.Operations;
using Lotsen.Pinning;
using Lotsen.Prose;
using Lotsen.Tours;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>A pinned tour that every test of <see cref="StopValidationTests"/> shares. Each test sets its own plan first.</summary>
public sealed class StopTourFixture : IAsyncLifetime
{
   /// <summary>A file that the head adds, with a UTF-8 byte order mark and CRLF line breaks.</summary>
   public const string BomFile = "﻿using System;\r\nclass C { }\r\n";

   internal PinnedTourFixture Tour { get; private set; } = null!;

   public async ValueTask InitializeAsync() => Tour = await PinnedTourFixture.StartAsync(("src/bom.cs", BomFile));

   public async ValueTask DisposeAsync() => await Tour.DisposeAsync();
}

/// <summary>
/// Covers how <c>put_stop</c> checks a stop before it stores it: the fields, the kind rules, the plan's rules after a change of title, kind or chapter, and every passage against the tour's pinned
/// commits. The files come from <see cref="PinnedTourFixture"/>.
/// </summary>
[Trait("Category", "Validation")]
public sealed class StopValidationTests : IClassFixture<StopTourFixture>
{
   // The stops by index: 0 overview, 1 context, 2 inspect, 3 risk, 4 question.
   private const string Plan = """
      {
        "chapters": [{ "id": "start", "title": "Start" }, { "id": "code", "title": "Code" }],
        "stops": [
          { "title": "What changes", "kind": "overview", "chapter": "start" },
          { "title": "Background", "kind": "context", "chapter": "start", "anchorHint": { "path": "src/same.txt" } },
          { "title": "The modified file", "kind": "inspect", "chapter": "code", "anchorHint": { "path": "src/modified.txt", "line": 2 } },
          { "title": "A rename", "kind": "risk", "chapter": "code" },
          { "title": "Keep the rename?", "kind": "question", "chapter": "code" }
        ]
      }
      """;

   private const int Inspect = 2;

   private readonly PinnedTourFixture _tour;

   public StopValidationTests(StopTourFixture fixture)
   {
      _tour = fixture.Tour;
   }

   public static TheoryData<int, string, string> Rejected => new()
   {
      { Inspect, Body(Passage("head", "src/modified.txt", 4, 9)), "passages[0].endLine" },
      { Inspect, Body(Passage("head", "src/modified.txt", 9, 9)), "passages[0].startLine" },
      { Inspect, Body(Passage("head", "src/modified.txt", 0, 1)), "passages[0].startLine" },
      { Inspect, Body(Passage("head", "src/modified.txt", 2, 1)), "passages[0].endLine" },
      { Inspect, Body(Passage("base", "src/added.txt", 1, 1)), "passages[0].path" },
      { Inspect, Body(Passage("head", "src/deleted.txt", 1, 1)), "passages[0].path" },
      { Inspect, Body(Passage("head", "src/nothing.txt", 1, 1)), "passages[0].path" },
      { Inspect, Body(Passage("head", "src", 1, 1)), "passages[0].path" },
      { Inspect, Body(Passage("head", "../src/modified.txt", 1, 1)), "passages[0].path" },
      { Inspect, Body(Passage("head", "logo.png", 1, 1)), "passages[0].path" },
      { Inspect, Body(Passage("head", "src/modified.txt", 3, 4)), "passages[0]" },
      { Inspect, Body(Passage("head", "src/same.txt", 1, 1)), "passages[0]" },
      { Inspect, Body(Passage("head", "src/renamed.txt", 1, 2)), "passages[0]" },
      { 3, Body(Passage("head", "src/same.txt", 1, 1), extra: """ "scenario": "It breaks." """), "passages[0]" },
      { 4, Body(Passage("base", "src/same.txt", 1, 1), extra: """ "checks": [{ "question": "Keep it?" }] """), "passages[0]" },
      { Inspect, Body(Passage("head", "src/modified.txt", 2, 2, expected: "two")), "passages[0].expectedFirstLine" },
      { Inspect, Body(Passage("head", "src/modified.txt", 1, 2) + ", " + Passage("head", "src/modified.txt", 5, 5)), "passages" },
      { Inspect, Body(Passage("head", "src/modified.txt", 1, 2, role: "related")), "passages" },
      { Inspect, Body(Passage("head", "src/modified.txt", 1, 2) + ", " + Passage("base", "src/same.txt", 1, 2, role: "related")), "passages[1].endLine" },
      { Inspect, Body(Passage("head", "src/modified.txt", 1, 9) + ", " + Passage("head", "src/same.txt", 1, 1, role: "related") + ", " + Passage("head", "src/nothing.txt", 1, 1, role: "related")), "passages[0].endLine" },
      { Inspect, Body(Passage("head", "src/modified.txt", 1, 2, role: "main")), "passages[0].role" },
      { Inspect, Body(Passage("left", "src/modified.txt", 1, 2)), "passages[0].side" },
      { Inspect, Body("null"), "passages[0]" },
      { Inspect, """{ "headline": "A headline." }""", "passages" },
      { Inspect, """{ "headline": "A headline.", "passages": [] }""", "passages" },
      { Inspect, Body(Passage("head", "src/modified.txt", 1, 2), headline: " "), "headline" },
      { Inspect, $$"""{ "passages": [{{Passage("head", "src/modified.txt", 1, 2)}}] }""", "headline" },
      { Inspect, Body(Passage("head", "src/modified.txt", 1, 2), extra: """ "kind": "overview" """), "kind" },
      { Inspect, Body(Passage("head", "src/modified.txt", 1, 2), extra: """ "kind": "test" """), "kind" },
      { Inspect, Body(Passage("head", "src/modified.txt", 1, 2), extra: """ "kind": "risk" """), "scenario" },
      { Inspect, Body(Passage("head", "src/modified.txt", 1, 2), extra: """ "title": " " """), "title" },
      { Inspect, Body(Passage("head", "src/modified.txt", 1, 2), extra: """ "chapter": "nowhere" """), "chapter" },
      { 3, Body(Passage("head", "src/renamed.txt", 3, 3), extra: """ "chapter": "start", "scenario": "It breaks." """), "chapter" },
      { Inspect, Body(Passage("head", "src/modified.txt", 1, 2), extra: """ "placement": "middle" """), "placement" },
      { Inspect, Body(Passage("head", "src/modified.txt", 1, 2), extra: """ "anchorHint": { "path": "src/modified.txt" } """), "anchorHint" },
      { Inspect, Body(Passage("head", "src/modified.txt", 1, 2), extra: """ "checks": [null] """), "checks[0]" },
      { 3, Body(Passage("head", "src/renamed.txt", 3, 3)), "scenario" },
      { 3, Body(Passage("head", "src/renamed.txt", 3, 3), extra: """ "scenario": "  " """), "scenario" },
      { 4, Body(Passage("head", "src/renamed.txt", 3, 3)), "checks" },
      { 4, Body(Passage("head", "src/renamed.txt", 3, 3), extra: """ "checks": [] """), "checks" },
      { 1, """{ "headline": "A headline." }""", "passages" },
      { 0, Body(Passage("head", "src/modified.txt", 1, 2), extra: """ "chapter": "code" """), "chapter" },
   };

   [Theory]
   [MemberData(nameof(Rejected))]
   public async Task Invalid_stop_is_rejected_with_its_reason_and_nothing_is_stored(int stop, string body, string field)
   {
      var ids = await SetPlanAsync();
      var before = await ReadStateAsync();

      using var response = await PutAsync(ids[stop], body);

      var error = await ReadErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      var reason = (string)error["reason"]!;
      Assert.DoesNotContain("Lotsen.", reason, StringComparison.Ordinal);
      if (error["field"] is { } named)
      {
         Assert.Equal(field, (string)named!);
         Assert.StartsWith(field + ": ", reason, StringComparison.Ordinal);
      }
      else
      {
         Assert.Contains(field, reason, StringComparison.Ordinal);
      }

      await AssertUnchangedAsync(before);
   }

   [Fact]
   public async Task Unknown_stop_id_is_not_found_and_nothing_is_stored()
   {
      await SetPlanAsync();
      var before = await ReadStateAsync();

      using var response = await PutAsync("nosuchstop", Body(Passage("head", "src/modified.txt", 1, 2)));

      var error = await ReadErrorAsync(response, HttpStatusCode.NotFound, "stopNotFound");
      Assert.Equal("stopId", (string)error["field"]!);
      Assert.Contains("'nosuchstop'", (string)error["reason"]!, StringComparison.Ordinal);
      Assert.Contains("set_plan", (string)error["reason"]!, StringComparison.Ordinal);
      await AssertUnchangedAsync(before);
   }

   /// <summary>A chapter move that empties a chapter, or that leaves a later stop out of chapter order, breaks a rule outside the stop itself, so the refusal names the chapter field.</summary>
   [Theory]
   [InlineData("""[{ "id": "a", "title": "A" }, { "id": "b", "title": "B" }]""", "chapter 'b' has no stops")]
   [InlineData("""[{ "id": "a", "title": "A" }, { "id": "b", "title": "B" }, { "id": "c", "title": "C" }]""", "not 'c'")]
   public async Task Chapter_change_that_breaks_a_rule_elsewhere_in_the_plan_is_rejected_on_the_chapter_field(string chapters, string problem)
   {
      var stops = chapters.Contains("\"c\"", StringComparison.Ordinal)
         ? """[{ "title": "S", "kind": "context", "chapter": "a" }, { "title": "T", "kind": "context", "chapter": "b" }, { "title": "U", "kind": "context", "chapter": "c" }]"""
         : """[{ "title": "S", "kind": "context", "chapter": "a" }, { "title": "T", "kind": "context", "chapter": "b" }]""";
      var ids = await SetPlanAsync($$"""{ "chapters": {{chapters}}, "stops": {{stops}} }""");
      var before = await ReadStateAsync();

      using var response = await PutAsync(ids[1], Body(Passage("head", "src/same.txt", 1, 1), extra: """ "chapter": "a" """));

      var error = await ReadErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("chapter", (string)error["field"]!);
      Assert.StartsWith("chapter: moving the stop to chapter 'a' breaks a rule of the plan.", (string)error["reason"]!, StringComparison.Ordinal);
      Assert.Contains(problem, (string)error["reason"]!, StringComparison.Ordinal);
      await AssertUnchangedAsync(before);
   }

   /// <summary>A plan that set_plan replaces while the passages are checked is checked again before the stop is stored.</summary>
   [Theory]
   [InlineData("""{ "tourId": "{tour}", "chapters": [{ "id": "code", "title": "Code" }], "stops": [{ "title": "Other", "kind": "inspect", "chapter": "code" }] }""", 404, "stopNotFound")]
   [InlineData("""{ "tourId": "{tour}", "chapters": [{ "id": "code", "title": "Code" }], "stops": [{ "id": "{kept}", "title": "Now context", "kind": "context", "chapter": "code" }] }""", 400, "invalidInput")]
   public async Task Plan_replaced_during_the_passage_checks_is_checked_again(string replacement, int status, string code)
   {
      var ids = await SetPlanAsync();
      var writer = _tour.Harness.Server.Services.GetRequiredService<TourWriter>();
      var plans = _tour.Harness.Server.Services.GetRequiredService<PlanOperations>();
      var pass = Passes.Admit<Access.BoundSession.Pass>("set_plan", await _tour.Harness.AgentCallerAsync());
      JsonNode? replaced = null;
      writer.Hook = WriterHooks.AfterGit(_ =>
      {
         var json = replacement.Replace("{tour}", _tour.TourId, StringComparison.Ordinal).Replace("{kept}", ids[Inspect], StringComparison.Ordinal);
         var plan = JsonSerializer.Deserialize(json, LotsenJsonContext.Default.SetPlanInput)!;
         replaced = JsonSerializer.SerializeToNode(plans.SetPlan(pass, plan).Value, LotsenJsonContext.Default.Tour);
         return Task.CompletedTask;
      }, "put_stop");
      try
      {
         using var response = await PutAsync(ids[Inspect], Body(Passage("head", "src/modified.txt", 1, 2)));

         var error = await ReadErrorAsync(response, (HttpStatusCode)status, code);
         Assert.Equal("stopId", (string)error["field"]!);
         Assert.NotNull(replaced);
         await AssertUnchangedAsync(replaced);
      }
      finally
      {
         writer.Hook = null;
      }
   }

   [Fact]
   public async Task Valid_stop_is_stored_ready_with_the_text_of_each_passage_in_place_of_its_anchor_hint()
   {
      var ids = await SetPlanAsync();
      var before = await ReadStateAsync();
      var body = $$"""
         {
           "headline": "Line two becomes a digit.",
           "how": "It replaces the word.",
           "why": "Digits sort first.",
           "designChoices": "A digit, not a numeral.",
           "pinnedBy": "No test.",
           "checks": [{ "question": "Is a digit right here?" }],
           "tags": ["naming"],
           "placement": "inline",
           "passages": [
             {{Passage("head", "src/modified.txt", 1, 2, expected: "one", label: "the change")}},
             {{Passage("base", "src/same.txt", 1, 1, role: "related", label: "unchanged")}},
             {{Passage("base", "src/old-name.txt", 2, 3, role: "related", expected: "  beta ")}}
           ]
         }
         """;

      var stored = await CallPutAsync(ids[Inspect], body);

      Assert.Equal("ready", (string)stored["status"]!);
      Assert.Null(stored["anchorHint"]);
      Assert.Equal(("The modified file", "inspect", "code"), ((string)stored["title"]!, (string)stored["kind"]!, (string)stored["chapter"]!));
      var content = stored["content"]!;
      Assert.Equal("Line two becomes a digit.", (string)content["headline"]!);
      Assert.Equal(("It replaces the word.", "Digits sort first.", "A digit, not a numeral.", "No test."),
         ((string)content["how"]!, (string)content["why"]!, (string)content["designChoices"]!, (string)content["pinnedBy"]!));
      Assert.Equal(["Is a digit right here?"], content["checks"]!.AsArray().Select(check => (string)check!["question"]!));
      Assert.Equal(["naming"], content["tags"]!.AsArray().Select(tag => (string)tag!));
      Assert.Equal("inline", (string)content["placement"]!);
      var passages = content["passages"]!.AsArray();
      Assert.Equal(3, passages.Count);
      AssertPassage(passages[0]!, "head", "src/modified.txt", 1, 2, "primary", _tour.Head.Value, "one\n2", "the change");
      AssertPassage(passages[1]!, "base", "src/same.txt", 1, 1, "related", _tour.MergeBase.Value, "same", "unchanged");
      AssertPassage(passages[2]!, "base", "src/old-name.txt", 2, 3, "related", _tour.MergeBase.Value, "beta\ngamma", label: null);
      Assert.Null(passages[0]!["expectedFirstLine"]);

      var read = await ReadStateAsync();
      Assert.True(JsonNode.DeepEquals(stored, read["plan"]!["stops"]![Inspect]), $"read_state differs from put_stop's result: {read}");
      foreach (var other in new[] { 0, 1, 3, 4 })
      {
         Assert.True(JsonNode.DeepEquals(before["plan"]!["stops"]![other], read["plan"]!["stops"]![other]), $"Stop {other} changed: {read}");
      }
   }

   [Fact]
   public async Task Empty_optional_fields_are_dropped()
   {
      var ids = await SetPlanAsync();
      var body = $$"""
         {
           "headline": "A headline.", "how": "", "why": "  ", "designChoices": "\n", "pinnedBy": "", "scenario": "",
           "tags": ["", "  "],
           "passages": [{{Passage("head", "src/modified.txt", 2, 2, label: " ")}}]
         }
         """;

      var stored = await CallPutAsync(ids[Inspect], body);

      var content = stored["content"]!.AsObject();
      Assert.Equal(["headline", "passages"], content.Select(property => property.Key));
      Assert.Equal(["side", "path", "startLine", "endLine", "role", "commit", "text"], content["passages"]![0]!.AsObject().Select(property => property.Key));
   }

   public static TheoryData<int, string, string, string> RejectedLinks => new()
   {
      { Inspect, Prose("how", "See [the loop](ref:head/src/modified.txt#L5-3)."), "how", "ref:head/src/modified.txt#L5-3" },
      { Inspect, Prose("why", "See <ref:left/src/modified.txt#L1-2>."), "why", "ref:left/src/modified.txt#L1-2" },
      { Inspect, Prose("designChoices", "[x](ref:head/src/nothing.txt#L1-1)"), "designChoices", "ref:head/src/nothing.txt#L1-1" },
      { Inspect, Prose("designChoices", "[x](ref:head/src/no%20such.txt#L1-1)"), "designChoices", "ref:head/src/no%20such.txt#L1-1" },
      { Inspect, Prose("pinnedBy", "[the test](ref:base/src/added.txt#L1-1)"), "pinnedBy", "ref:base/src/added.txt#L1-1" },
      { Inspect, Prose("how", "[gone](ref:head/src/deleted.txt#L1-1)"), "how", "ref:head/src/deleted.txt#L1-1" },
      { Inspect, Prose("how", "[a directory](ref:head/src#L1-1)"), "how", "ref:head/src#L1-1" },
      { Inspect, Prose("how", "[a](ref:head/src/modified.txt#L1-2) and [b](ref:head/src/modified.txt#L1-6)"), "how", "ref:head/src/modified.txt#L1-6" },
      { Inspect, Checks("Fine?", "Is [this](ref:head/src/modified.txt#L5-6) right?"), "checks[1].question", "ref:head/src/modified.txt#L5-6" },
      { 3, Prose("scenario", "[x](ref:head/src/renamed.txt#L0-1)"), "scenario", "ref:head/src/renamed.txt#L0-1" },
      { Inspect, Prose("headline", "[logo](ref:head/logo.png#L1-1)"), "headline", "ref:head/logo.png#L1-1" },
      { Inspect, Prose("headline", "[see <ref:head/src/nothing.txt#L1-2>](https://example.com/)"), "headline", "ref:head/src/nothing.txt#L1-2" },
   };

   [Theory]
   [MemberData(nameof(RejectedLinks))]
   public async Task Stop_with_a_ref_link_that_cannot_be_read_or_followed_is_rejected_with_its_field_and_link(int stop, string prose, string field, string link)
   {
      var ids = await SetPlanAsync();
      var before = await ReadStateAsync();
      var headline = prose.Contains("\"headline\"", StringComparison.Ordinal) ? null : "A headline.";
      var passage = stop == 3 ? Passage("head", "src/renamed.txt", 3, 3) : Passage("head", "src/modified.txt", 2, 2);

      using var response = await PutAsync(ids[stop], $$"""{ {{(headline is null ? "" : $"\"headline\": \"{headline}\", ")}}"passages": [{{passage}}], {{prose}} }""");

      var error = await ReadErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal(field, (string)error["field"]!);
      Assert.StartsWith(field + ": ", (string)error["reason"]!, StringComparison.Ordinal);
      Assert.Contains($"'{link}'", (string)error["reason"]!, StringComparison.Ordinal);
      await AssertUnchangedAsync(before);
   }

   /// <summary>The first case is past the renderer's own limit, and the second past Markdig's parse limit.</summary>
   [Theory]
   [InlineData(ProseRenderer.MaxDepth + 1, "> ")]
   [InlineData(100_000, ">")]
   public async Task Stop_with_prose_that_nests_too_deep_is_rejected_on_its_field(int depth, string open)
   {
      var ids = await SetPlanAsync();
      var before = await ReadStateAsync();

      using var response = await PutAsync(ids[Inspect], Body(Passage("head", "src/modified.txt", 2, 2), extra: Prose("how", string.Concat(Enumerable.Repeat(open, depth)) + "x")));

      var error = await ReadErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal("how", (string)error["field"]!);
      Assert.Contains($"{ProseRenderer.MaxDepth} levels", (string)error["reason"]!, StringComparison.Ordinal);
      await AssertUnchangedAsync(before);
   }

   /// <summary>
   /// The links point at unchanged code on both sides, at a file that only the base has, and at one passage many times. A <c>ref:</c> link in inline code or in a code block is text, so its target
   /// is never checked.
   /// </summary>
   [Fact]
   public async Task Ref_links_to_unchanged_code_on_either_side_are_accepted_and_the_prose_is_stored_as_written()
   {
      var ids = await SetPlanAsync();
      var headline = "Line two [changes](ref:head/src/modified.txt#L2-2).";
      var how = "It reads [the unchanged file](ref:base/src/same.txt#L1-1), [its head](ref:head/src/same.txt#L1-1) and [the old name](ref:base/src/old-name.txt#L1-5).";
      var why = "Not links: `[x](ref:head/src/nothing.txt#L1-1)`\n\n```\n[y](ref:head/src/nothing.txt#L9-9)\n```\n\n    [z](ref:base/nothing#L1-1)\n";
      var pinnedBy = string.Concat(Enumerable.Repeat("[test](ref:head/src/modified.txt#L1-5) ", 300));
      var check = "Is [the base side](ref:base/src/modified.txt#L1-4) right?";
      var body = $$"""
         {
           "headline": {{Json(headline)}}, "how": {{Json(how)}}, "why": {{Json(why)}}, "pinnedBy": {{Json(pinnedBy)}}, "checks": [{ "question": {{Json(check)}} }],
           "passages": [{{Passage("head", "src/modified.txt", 2, 2)}}]
         }
         """;

      var stored = await CallPutAsync(ids[Inspect], body);

      var content = stored["content"]!;
      Assert.Equal((headline, how, why, pinnedBy), ((string)content["headline"]!, (string)content["how"]!, (string)content["why"]!, (string)content["pinnedBy"]!));
      Assert.Equal([check], content["checks"]!.AsArray().Select(item => (string)item!["question"]!));
      Assert.Null(content["html"]);
      Assert.True(JsonNode.DeepEquals(stored, (await ReadStateAsync())["plan"]!["stops"]![Inspect]));
   }

   /// <summary>IDNA refuses the link's host, which <see cref="Uri"/> accepts.</summary>
   [Fact]
   public async Task Https_link_whose_host_idna_refuses_is_accepted_and_the_prose_is_stored_as_written()
   {
      var ids = await SetPlanAsync();
      const string Headline = "See [the docs](https://exa\u200Dmple.com/).";

      var stored = await CallPutAsync(ids[Inspect], Body(Passage("head", "src/modified.txt", 2, 2), headline: Headline));

      Assert.Equal(Headline, (string)stored["content"]!["headline"]!);
      Assert.Equal("ready", (string)stored["status"]!);
   }

   [Fact]
   public async Task Overview_without_passages_and_context_on_unchanged_code_are_stored()
   {
      var ids = await SetPlanAsync();

      var overview = await CallPutAsync(ids[0], """{ "headline": "The change at a glance.", "placement": "side" }""");
      var context = await CallPutAsync(ids[1], Body(Passage("head", "src/same.txt", 1, 1, expected: "same")));

      Assert.Equal("ready", (string)overview["status"]!);
      Assert.Empty(overview["content"]!["passages"]!.AsArray());
      Assert.Equal("side", (string)overview["content"]!["placement"]!);
      Assert.Equal("same", (string)context["content"]!["passages"]![0]!["text"]!);
   }

   /// <summary>The base side of a renamed file is its old path, and a deleted file exists only at the base.</summary>
   [Fact]
   public async Task Primary_passage_that_touches_the_diff_on_either_side_is_stored()
   {
      var ids = await SetPlanAsync();

      var risk = await CallPutAsync(ids[3], Body(Passage("head", "src/renamed.txt", 3, 3, expected: "GAMMA"), extra: """ "scenario": "Callers still read gamma." """));
      var question = await CallPutAsync(ids[4], Body(Passage("base", "src/old-name.txt", 2, 3), extra: """ "checks": [{ "question": "Keep the old name?" }] """));
      var inspect = await CallPutAsync(ids[Inspect], Body(Passage("base", "src/deleted.txt", 1, 1, expected: "gone")));

      Assert.Equal("Callers still read gamma.", (string)risk["content"]!["scenario"]!);
      Assert.Equal("GAMMA", (string)risk["content"]!["passages"]![0]!["text"]!);
      Assert.Equal("beta\ngamma", (string)question["content"]!["passages"]![0]!["text"]!);
      Assert.Equal(["Keep the old name?"], question["content"]!["checks"]!.AsArray().Select(check => (string)check!["question"]!));
      Assert.Equal("gone", (string)inspect["content"]!["passages"]![0]!["text"]!);
   }

   /// <summary>
   /// The head turns the LF line breaks of <c>src/line-endings.txt</c> into CRLF. Lines end at LF, so a CRLF file has as many lines as its LF twin, and the comparison with the expected text
   /// leaves out the carriage return. The stored text keeps the file's line breaks between its lines and drops the break after the last one.
   /// </summary>
   [Fact]
   public async Task Crlf_lines_count_as_lines_and_keep_their_breaks_in_the_stored_text()
   {
      var ids = await SetPlanAsync();
      var before = await ReadStateAsync();
      using var pastTheEnd = await PutAsync(ids[Inspect], Body(Passage("head", "src/line-endings.txt", 2, 3)));
      await ReadErrorAsync(pastTheEnd, HttpStatusCode.BadRequest, "invalidInput");
      await AssertUnchangedAsync(before);

      var stored = await CallPutAsync(ids[Inspect], Body(
         Passage("head", "src/line-endings.txt", 1, 2, expected: "one") + ", "
         + Passage("base", "src/line-endings.txt", 1, 2, role: "related", expected: "one") + ", "
         + Passage("head", "src/line-endings.txt", 2, 2, role: "related", expected: "two") + ", "
         + Passage("head", "src/bom.cs", 1, 2, role: "related", expected: "using System;")));

      var passages = stored["content"]!["passages"]!.AsArray();
      Assert.Equal("one\r\ntwo", (string)passages[0]!["text"]!);
      Assert.Equal("one\ntwo", (string)passages[1]!["text"]!);
      Assert.Equal("two", (string)passages[2]!["text"]!);
      Assert.Equal("using System;\r\nclass C { }", (string)passages[3]!["text"]!);
   }

   /// <summary><c>data/large.txt</c> is a large file whose last line, <c>!</c>, has no line break after it.</summary>
   [Fact]
   public async Task Last_line_without_a_line_break_is_a_line_of_a_large_file()
   {
      var ids = await SetPlanAsync();

      var stored = await CallPutAsync(ids[Inspect], Body(Passage("head", "data/large.txt", 65536, 65537, expected: "0123456789abcde")));
      using var pastTheEnd = await PutAsync(ids[Inspect], Body(Passage("head", "data/large.txt", 65538, 65538)));

      Assert.Equal("0123456789abcde\n!", (string)stored["content"]!["passages"]![0]!["text"]!);
      var error = await ReadErrorAsync(pastTheEnd, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Contains("65537 lines", (string)error["reason"]!, StringComparison.Ordinal);
   }

   [Fact]
   public async Task Writing_a_ready_stop_again_replaces_its_content()
   {
      var ids = await SetPlanAsync();
      await CallPutAsync(ids[Inspect], Body(Passage("head", "src/modified.txt", 1, 2), extra: """ "how": "First." """));

      var second = await CallPutAsync(ids[Inspect], Body(Passage("head", "src/modified.txt", 5, 5, expected: "five"), headline: "Second."));

      Assert.Equal("Second.", (string)second["content"]!["headline"]!);
      Assert.Null(second["content"]!["how"]);
      Assert.Equal("five", (string)Assert.Single(second["content"]!["passages"]!.AsArray())!["text"]!);
      Assert.True(JsonNode.DeepEquals(second, (await _tour.Harness.AgentCallAsync(On("read_state", _tour.TourId)))["plan"]!["stops"]![Inspect]));
   }

   [Fact]
   public async Task Title_kind_and_chapter_change_with_the_content_when_the_plan_still_follows_its_rules()
   {
      var ids = await SetPlanAsync();

      var stored = await CallPutAsync(ids[Inspect], Body(
         Passage("head", "src/modified.txt", 1, 2),
         extra: """ "title": "A digit", "kind": "risk", "chapter": "start", "scenario": "Sorting changes." """));

      Assert.Equal(("A digit", "risk", "start"), ((string)stored["title"]!, (string)stored["kind"]!, (string)stored["chapter"]!));
      var stops = (await ReadStateAsync())["plan"]!["stops"]!.AsArray();
      Assert.Equal(ids, stops.Select(stop => (string)stop!["id"]!));
      Assert.Equal(["start", "start", "start", "code", "code"], stops.Select(stop => (string)stop!["chapter"]!));
   }

   /// <summary>The replan keeps every stop of <see cref="Plan"/>. The inspect stop is ready, and the context stop is pending with an anchor hint.</summary>
   [Theory]
   [InlineData("Background", "context", "start", "The modified file", "inspect", "code")]
   [InlineData("Background", "context", "start", "A new title", "inspect", "start")]
   [InlineData("Background", "inspect", "start", "The modified file", "inspect", "code")]
   public async Task Replanning_keeps_a_ready_stops_content_when_its_kind_stays(string pendingTitle, string pendingKind, string pendingChapter, string readyTitle, string readyKind, string readyChapter)
   {
      var ids = await SetPlanAsync();
      var stored = await CallPutAsync(ids[Inspect], Body(Passage("head", "src/modified.txt", 1, 2)));

      var replanned = await _tour.Harness.AgentCallAsync(On("set_plan", _tour.TourId), Replan(ids, (pendingTitle, pendingKind, pendingChapter), (readyTitle, readyKind, readyChapter)));

      var stops = replanned["plan"]!["stops"]!.AsArray();
      Assert.Equal(ids, stops.Select(stop => (string)stop!["id"]!));
      var ready = stops[Inspect]!;
      Assert.Equal(("ready", readyTitle, readyChapter), ((string)ready["status"]!, (string)ready["title"]!, (string)ready["chapter"]!));
      Assert.True(JsonNode.DeepEquals(stored["content"], ready["content"]), $"The content changed: {ready}");
      var pending = stops[1]!;
      Assert.Equal(("pending", pendingKind), ((string)pending["status"]!, (string)pending["kind"]!));
      Assert.Null(pending["content"]);
   }

   [Theory]
   [InlineData("risk")]
   [InlineData("context")]
   public async Task Replanning_that_changes_a_ready_stops_kind_is_rejected_and_nothing_is_stored(string kind)
   {
      var ids = await SetPlanAsync();
      await CallPutAsync(ids[Inspect], Body(Passage("head", "src/modified.txt", 1, 2)));
      var before = await ReadStateAsync();

      using var response = await _tour.Harness.AgentSendJsonAsync(
         On("set_plan", _tour.TourId), Replan(ids, ("Background", "context", "start"), ("The modified file", kind, "code")));

      var error = await ReadErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal($"stops[{Inspect}].kind", (string)error["field"]!);
      Assert.Contains("put_stop", (string)error["reason"]!, StringComparison.Ordinal);
      await AssertUnchangedAsync(before);
   }

   /// <summary>A ready stop's passages say where it points, so set_plan stores no anchor hint for it. A pending stop keeps the hint the plan gives it.</summary>
   [Fact]
   public async Task Replanning_stores_no_anchor_hint_for_a_ready_stop()
   {
      var ids = await SetPlanAsync();
      await CallPutAsync(ids[Inspect], Body(Passage("head", "src/modified.txt", 1, 2)));

      var replanned = await _tour.Harness.AgentCallAsync(
         On("set_plan", _tour.TourId), Replan(ids, ("Background", "context", "start"), ("The modified file", "inspect", "code"), hint: """{ "path": "src/modified.txt", "line": 3 }"""));

      var stops = replanned["plan"]!["stops"]!.AsArray();
      Assert.Null(stops[Inspect]!["anchorHint"]);
      Assert.Equal("ready", (string)stops[Inspect]!["status"]!);
      Assert.Equal("src/modified.txt", (string)stops[1]!["anchorHint"]!["path"]!);
   }

   // Keeps every stop of Plan by its ID, with the given title, kind and chapter for the context stop and the inspect stop. Both get the hint when one is given.
   private static string Replan(string[] ids, (string Title, string Kind, string Chapter) context, (string Title, string Kind, string Chapter) inspect, string hint = """{ "path": "src/same.txt" }""") => $$"""
      {
        "chapters": [{ "id": "start", "title": "Start" }, { "id": "code", "title": "Code" }],
        "stops": [
          { "id": "{{ids[0]}}", "title": "What changes", "kind": "overview", "chapter": "start" },
          { "id": "{{ids[1]}}", "title": "{{context.Title}}", "kind": "{{context.Kind}}", "chapter": "{{context.Chapter}}", "anchorHint": {{hint}} },
          { "id": "{{ids[2]}}", "title": "{{inspect.Title}}", "kind": "{{inspect.Kind}}", "chapter": "{{inspect.Chapter}}", "anchorHint": {{hint}} },
          { "id": "{{ids[3]}}", "title": "A rename", "kind": "risk", "chapter": "code" },
          { "id": "{{ids[4]}}", "title": "Keep the rename?", "kind": "question", "chapter": "code" }
        ]
      }
      """;

   private static string Passage(string side, string path, int start, int end, string role = "primary", string? expected = null, string? label = null)
   {
      var passage = new JsonObject { ["side"] = side, ["path"] = path, ["startLine"] = start, ["endLine"] = end, ["role"] = role };
      if (label is not null)
      {
         passage["label"] = label;
      }

      if (expected is not null)
      {
         passage["expectedFirstLine"] = expected;
      }

      return passage.ToJsonString();
   }

   private static string Body(string passages, string headline = "A headline.", string? extra = null) =>
      $$"""{ "headline": {{JsonValue.Create(headline).ToJsonString()}}, "passages": [{{passages}}]{{(extra is null ? "" : ", " + extra)}} }""";

   private static string Json(string text) => JsonValue.Create(text).ToJsonString();

   private static string Prose(string field, string markdown) => $"\"{field}\": {Json(markdown)}";

   private static string Checks(params string[] questions) => $"\"checks\": [{string.Join(", ", questions.Select(question => $"{{ \"question\": {Json(question)} }}"))}]";

   private static void AssertPassage(JsonNode passage, string side, string path, int start, int end, string role, string commit, string text, string? label)
   {
      Assert.Equal((side, path, start, end, role), ((string)passage["side"]!, (string)passage["path"]!, (int)passage["startLine"]!, (int)passage["endLine"]!, (string)passage["role"]!));
      Assert.Equal(commit, (string)passage["commit"]!);
      Assert.Equal(text, (string)passage["text"]!);
      Assert.Equal(label, (string?)passage["label"]);
   }

   private async Task<string[]> SetPlanAsync(string plan = Plan)
   {
      var tour = await _tour.Harness.AgentCallAsync(On("set_plan", _tour.TourId), plan);
      return [.. tour["plan"]!["stops"]!.AsArray().Select(stop => (string)stop!["id"]!)];
   }

   private Task<JsonNode> ReadStateAsync() => _tour.Harness.CallAsync(On("read_state", _tour.TourId));

   private Task<HttpResponseMessage> PutAsync(string stopId, string body) => _tour.Harness.AgentSendJsonAsync(On("put_stop", _tour.TourId, stopId: stopId), body);

   private Task<JsonNode> CallPutAsync(string stopId, string body) => _tour.Harness.AgentCallAsync(On("put_stop", _tour.TourId, stopId: stopId), body);

   private async Task AssertUnchangedAsync(JsonNode before)
   {
      var after = await ReadStateAsync();
      Assert.True(JsonNode.DeepEquals(before, after), $"The tour changed: {after}");
   }

   private static async Task<JsonNode> ReadErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
   {
      var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
      Assert.True(status == response.StatusCode, $"Expected {(int)status}, got {(int)response.StatusCode}: {body}");
      var error = JsonNode.Parse(body)!;
      Assert.Equal(code, (string)error["code"]!);
      return error;
   }
}

/// <summary>Covers how <see cref="StopPassages"/> reads the files that passages and code references name, through a <see cref="RecordingGit"/> that records each run.</summary>
[Trait("Category", "Validation")]
public sealed class StopReferenceTests : IDisposable
{
   private readonly GitFixture _fixture = new();

   /// <summary>The merge base holds the three files of the related passages and the link, so the pin downloaded none of them. The primary passage's diff check runs a lookup of its own.</summary>
   [Fact]
   public async Task Put_stop_reads_the_files_of_its_passages_and_ref_links_with_one_lookup()
   {
      var git = new RecordingGit(_fixture.ScratchPath);
      await using var tour = await PinnedTourFixture.StartAsync(git.Options, [("lib/one.txt", "one\n"), ("lib/two.txt", "two\n"), ("lib/three.txt", "three\n")]);
      const string plan = """{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "title": "S", "kind": "inspect", "chapter": "a" }] }""";
      var stopId = (string)(await tour.Harness.AgentCallAsync(On("set_plan", tour.TourId), plan))["plan"]!["stops"]![0]!["id"]!;
      const string body = """
         {
           "headline": "Line two becomes a digit.",
           "how": "It reads [the third file](ref:head/lib/three.txt#L1-1).",
           "passages": [
             { "side": "head", "path": "src/modified.txt", "startLine": 2, "endLine": 2, "role": "primary" },
             { "side": "head", "path": "lib/one.txt", "startLine": 1, "endLine": 1, "role": "related" },
             { "side": "head", "path": "lib/two.txt", "startLine": 1, "endLine": 1, "role": "related" }
           ]
         }
         """;
      var runsBefore = Runs(git).Count;

      var stored = await tour.Harness.AgentCallAsync(On("put_stop", tour.TourId, stopId: stopId), body);

      Assert.Equal(["2", "one", "two"], stored["content"]!["passages"]!.AsArray().Select(passage => (string)passage!["text"]!));
      var runs = Runs(git).Skip(runsBefore).ToList();
      Assert.Equal(2, runs.Count(run => run == "cat-file -Z --batch"));
      Assert.DoesNotContain(runs, run => run.StartsWith("cat-file blob ", StringComparison.Ordinal));
   }

   [Fact]
   public async Task File_that_many_references_name_is_read_once()
   {
      var mergeBase = _fixture.Commit("Base", ("a.txt", "one\ntwo\n"));
      var head = _fixture.Commit("Head", ("a.txt", "one\n2\n"));
      var git = new RecordingGit(_fixture.ScratchPath);
      var cache = _fixture.NewCache(git.Options);
      using var repository = await cache.OpenAsync(_fixture.Remote(), [mergeBase, head], FetchPolicy.WhenMissing, TestContext.Current.CancellationToken);
      var runsBefore = Runs(git).Count;
      var prose = ProseRenderer.Render(string.Concat(Enumerable.Repeat("[a](ref:head/a.txt#L1-2) [b](ref:base/a.txt#L2-2) ", 100)));

      await using var facts = new CloneGitFacts(cache, NullLogger<CloneGitFacts>.Instance, budget: 1 << 20).Open(_fixture.Remote(), [mergeBase, head], FetchPolicy.WhenMissing);

      var error = await new StopPassages(facts, new TourPin.Pinned(mergeBase, head, 1, _fixture.Url))
         .CheckReferencesAsync([.. prose.References.Select(reference => new ProseReference("how", reference))], TestContext.Current.CancellationToken);

      Assert.Null(error);
      var runs = Runs(git).Skip(runsBefore).ToList();
      Assert.Equal(1, runs.Count(run => run == "cat-file -Z --batch"));

      // The new clone lacks both blobs, so the lookup checks twice: once finding the blobs missing, once downloading them.
      Assert.Equal(2, runs.Count(run => run == "cat-file -Z --batch-check"));
      Assert.Equal(3, runs.Count);
   }

   public void Dispose() => _fixture.Dispose();

   private static List<string> Runs(RecordingGit git) => [.. git.Arguments.Split('\n', StringSplitOptions.RemoveEmptyEntries)];
}
