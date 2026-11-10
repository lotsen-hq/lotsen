using System.Text.Json;
using System.Text.Json.Nodes;
using Lotsen.Client;
using Lotsen.Hosting;
using Lotsen.Operations;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>Covers the changed files, file texts and file diffs that the API reads from a pinned tour's commits.</summary>
[Trait("Category", "Git")]
public sealed class FileContentTests : IAsyncLifetime
{
   private PinnedTourFixture _fixture = null!;

   public async ValueTask InitializeAsync() => _fixture = await PinnedTourFixture.StartAsync();

   public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

   private OperationCall Text => On("read_file_text", _fixture.TourId);

   private OperationCall Directory => On("list_directory", _fixture.TourId);

   private OperationCall Diff => On("diff_file", _fixture.TourId);

   [Fact]
   public async Task Changed_files_lists_each_file_between_the_merge_base_and_the_head_with_its_kind_and_marks()
   {
      var listed = await _fixture.Harness.CallAsync(On("list_changed_files", _fixture.TourId));
      foreach (var file in listed["files"]!.AsArray())
      {
         var path = (string)file!["path"]!;
         var expected = (string)file["kind"]! == "deleted" ? null : _fixture.Git.Git(_fixture.Git.OriginPath, "rev-parse", $"{_fixture.Head}:{path}").Trim();
         Assert.Equal(expected, (string?)file["headObjectId"]);
         file.AsObject().Remove("headObjectId");
      }

      Assert.Equal(_fixture.MergeBase.Value, (string)listed["mergeBase"]!);
      Assert.Equal(_fixture.Head.Value, (string)listed["head"]!);
      Assert.True(
         JsonNode.DeepEquals(
            JsonNode.Parse("""
               [
                  { "path": "assets/new.bin", "kind": "renamed", "oldPath": "assets/old.bin", "binary": true },
                  { "path": "bytes.txt", "kind": "added" },
                  { "path": "data/large.txt", "kind": "added", "large": { "size": 1048577 } },
                  { "path": "logo.png", "kind": "added", "binary": true },
                  { "path": "page.html", "kind": "added" },
                  { "path": "src/added.txt", "kind": "added" },
                  { "path": "src/deleted.txt", "kind": "deleted" },
                  { "path": "src/line-endings.txt", "kind": "modified", "lineEndingsOnly": true },
                  { "path": "src/modified.txt", "kind": "modified" },
                  { "path": "src/renamed.txt", "kind": "renamed", "oldPath": "src/old-name.txt" }
               ]
               """),
            listed["files"]),
         listed["files"]!.ToJsonString());
      Assert.Empty(listed["reviewedPaths"]!.AsArray());
   }

   [Theory]
   [InlineData("base", "src/modified.txt", PinnedTourFixture.ModifiedBase)]
   [InlineData("head", "src/modified.txt", PinnedTourFixture.ModifiedHead)]
   [InlineData("head", "src/added.txt", "new\n")]
   [InlineData("base", "src/deleted.txt", "gone\n")]
   [InlineData("base", "src/same.txt", "same\n")]
   public async Task File_text_is_the_text_at_the_pinned_commit_of_the_side(string side, string path, string expected)
   {
      var read = await _fixture.Harness.CallAsync(Text, $$"""{ "side": "{{side}}", "path": "{{path}}" }""");

      Assert.Equal(expected, (string)read["text"]!);
      Assert.Equal(path, (string)read["path"]!);
      Assert.Equal(side, (string)read["side"]!);
      Assert.Equal((side == "base" ? _fixture.MergeBase : _fixture.Head).Value, (string)read["commit"]!);
   }

   [Fact]
   public async Task Directory_listing_names_every_entry_at_the_side_with_unchanged_files_among_them()
   {
      var root = await _fixture.Harness.CallAsync(Directory, """{ "side": "head" }""");
      var srcAtHead = await _fixture.Harness.CallAsync(Directory, """{ "side": "head", "path": "src" }""");
      var srcAtBase = await _fixture.Harness.CallAsync(Directory, """{ "side": "base", "path": "src" }""");

      Assert.Equal((null, "head", _fixture.Head.Value), ((string?)root["path"], (string)root["side"]!, (string)root["commit"]!));
      Assert.Equal(
         ["assets directory", "bytes.txt file", "data directory", "logo.png file", "page.html file", "src directory"],
         root["entries"]!.AsArray().Select(entry => $"{entry!["path"]} {entry["type"]}"));
      Assert.Equal(("src", "base", _fixture.MergeBase.Value), ((string)srcAtBase["path"]!, (string)srcAtBase["side"]!, (string)srcAtBase["commit"]!));
      Assert.Equal(
         ["src/added.txt", "src/line-endings.txt", "src/modified.txt", "src/renamed.txt", "src/same.txt"],
         srcAtHead["entries"]!.AsArray().Select(entry => (string)entry!["path"]!));
      Assert.Equal(
         ["src/deleted.txt", "src/line-endings.txt", "src/modified.txt", "src/old-name.txt", "src/same.txt"],
         srcAtBase["entries"]!.AsArray().Select(entry => (string)entry!["path"]!));
   }

   [Fact]
   public async Task File_text_comes_as_json_even_when_the_file_is_html()
   {
      var (status, contentType, body) = await _fixture.GetAsync(Text, """{ "side": "head", "path": "page.html" }""");

      Assert.Equal(200, status);
      Assert.Equal("application/json", contentType);
      Assert.Equal(PinnedTourFixture.HtmlPage, (string)body["text"]!);

      using var response = await _fixture.Harness.SendJsonAsync(Text, """{ "side": "head", "path": "page.html" }""");
      var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
      Assert.DoesNotContain("<script>", raw, StringComparison.Ordinal);
   }

   /// <summary>The fixture's <c>bytes.txt</c> holds the bytes <c>66 FF FE 0A</c>, where FF and FE are not UTF-8.</summary>
   [Fact]
   public async Task File_text_that_is_not_valid_utf8_comes_with_each_invalid_byte_replaced()
   {
      var read = await _fixture.Harness.CallAsync(Text, """{ "side": "head", "path": "bytes.txt" }""");

      Assert.Equal("f��\n", (string)read["text"]!);
   }

   [Fact]
   public async Task Diff_of_a_modified_file_matches_gits_hunks()
   {
      var diff = await _fixture.Harness.CallAsync(Diff, """{ "path": "src/modified.txt" }""");

      var baseText = (string)diff["baseText"]!;
      var headText = (string)diff["headText"]!;
      Assert.Equal(PinnedTourFixture.ModifiedBase, baseText);
      Assert.Equal(PinnedTourFixture.ModifiedHead, headText);
      var hunks = GitHunks("src/modified.txt");
      var changes = diff["changes"]!.AsArray();
      Assert.Equal(2, hunks.Count);
      Assert.Equal(hunks.Count, changes.Count);
      foreach (var (change, (removed, added)) in changes.Zip(hunks))
      {
         int fromA = (int)change!["fromA"]!, toA = (int)change["toA"]!, fromB = (int)change["fromB"]!, toB = (int)change["toB"]!;
         Assert.Equal(removed, baseText[fromA..toA]);
         Assert.Equal(added, headText[fromB..toB]);
      }
   }

   [Fact]
   public async Task Diff_of_an_added_file_has_no_base_text_and_one_change_over_the_head_text()
   {
      var diff = await _fixture.Harness.CallAsync(Diff, """{ "path": "src/added.txt" }""");

      Assert.Null(diff["baseText"]);
      Assert.Equal("new\n", (string)diff["headText"]!);
      Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""[{ "fromA": 0, "toA": 0, "fromB": 0, "toB": 4 }]"""), diff["changes"]), diff["changes"]!.ToJsonString());
   }

   [Fact]
   public async Task Pinned_tour_reads_its_files_after_a_restart_even_when_its_clone_is_gone()
   {
      await _fixture.Harness.StopServerAsync();
      DirectoryTree.Delete(_fixture.Harness.DataDirectory.ClonesPath);
      await _fixture.RestartServerAsync();

      var read = await _fixture.Harness.CallAsync(Text, """{ "side": "base", "path": "src/deleted.txt" }""");

      Assert.Equal("gone\n", (string)read["text"]!);
   }

   // Each hunk of git's own zero-context diff, as the text of its removed lines and the text of its added lines.
   private List<(string Removed, string Added)> GitHunks(string path)
   {
      var patch = _fixture.Git.Git(_fixture.Git.OriginPath, "diff", "--unified=0", "--no-color", _fixture.MergeBase.Value, _fixture.Head.Value, "--", path);
      var hunks = new List<(string Removed, string Added)>();
      foreach (var line in patch.Split('\n'))
      {
         if (line.StartsWith("@@ ", StringComparison.Ordinal))
         {
            hunks.Add(("", ""));
         }
         else if (hunks.Count > 0 && line.StartsWith('-'))
         {
            hunks[^1] = (hunks[^1].Removed + line[1..] + "\n", hunks[^1].Added);
         }
         else if (hunks.Count > 0 && line.StartsWith('+'))
         {
            hunks[^1] = (hunks[^1].Removed, hunks[^1].Added + line[1..] + "\n");
         }
      }

      return hunks;
   }
}
