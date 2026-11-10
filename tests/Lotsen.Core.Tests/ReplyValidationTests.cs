using System.Net;
using System.Text.Json.Nodes;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>Covers how a reply checks its resolution and its prose, and every <c>ref:</c> link in it against the tour's pins, before it stores anything. The files come from <see cref="PinnedTourFixture"/>.</summary>
[Trait("Category", "Validation")]
public sealed class ReplyValidationTests : IClassFixture<QuestionTourFixture>
{
   private readonly QuestionTourFixture _fixture;

   public ReplyValidationTests(QuestionTourFixture fixture)
   {
      _fixture = fixture;
   }

   public static TheoryData<string, string?> Rejected => new()
   {
      { Reply("done", "See [the file](ref:head/src/nothing.txt#L1-1)."), "text" },
      { Reply("done", "See [the lines](ref:head/src/modified.txt#L4-9)."), "text" },
      { Reply("needsAnswer", "Is [this](ref:base/src/added.txt#L1-1) right?"), "text" },
      { Reply("done", "See [the logo](ref:head/logo.png#L1-1)."), "text" },
      { Reply("done", "See [the lines](ref:left/src/modified.txt#L1-1)."), "text" },
      { Reply("done", "See [the lines](ref:head/src/modified.txt#L2)."), "text" },
      { Reply("declined", "Not [this](ref:head/src/nothing.txt#L1-1)."), "text" },
      { Reply("done", string.Concat(Enumerable.Repeat("> ", 40)) + "deep"), "text" },
      { """{ "resolution": "done" }""", "text" },
      { Reply("done", " \\n "), "text" },
      { Reply("needsAnswer", null), "text" },
      { Reply("pending", "Yes."), "resolution" },
      { Reply("unanswered", "Yes."), "resolution" },
   };

   [Theory]
   [MemberData(nameof(Rejected))]
   public async Task Invalid_reply_is_refused_with_its_reason_and_stores_nothing(string body, string? field)
   {
      var questionId = await AskAsync();
      var before = await _fixture.ThreadsAsync();

      using var response = await ReplyAsync(questionId, body);

      var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
      Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"Expected 400, got {(int)response.StatusCode}: {text}");
      var error = JsonNode.Parse(text)!;
      Assert.Equal(("invalidInput", field), ((string)error["code"]!, (string?)error["field"]));
      Assert.DoesNotContain("Lotsen.", (string)error["reason"]!, StringComparison.Ordinal);
      Assert.True(JsonNode.DeepEquals(before, await _fixture.ThreadsAsync()), "The refused reply changed the threads.");
   }

   [Fact]
   public async Task Link_to_a_file_that_is_not_at_its_side_is_refused_with_the_link_and_the_reason()
   {
      var questionId = await AskAsync();

      using var response = await ReplyAsync(questionId, Reply("done", "See [the file](ref:base/src/added.txt#L1-1)."));

      var reason = (string)JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["reason"]!;
      Assert.Equal(
         $"text: the link 'ref:base/src/added.txt#L1-1' cannot be followed. 'src/added.txt' is not a file at the base of the tour, commit {_fixture.Tour.MergeBase}. "
         + "A file that the change adds exists only at the head, and a file that it deletes only at the base.",
         reason);
   }

   [Fact]
   public async Task Malformed_link_is_refused_with_the_link()
   {
      var questionId = await AskAsync();

      using var response = await ReplyAsync(questionId, Reply("done", "See [the lines](ref:head/src/modified.txt#L2)."));

      var reason = (string)JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["reason"]!;
      Assert.StartsWith("text: the link 'ref:head/src/modified.txt#L2' is not a well-formed code reference.", reason, StringComparison.Ordinal);
   }

   /// <summary>Like a stop's prose, a reply may link to code that the change leaves alone, on either side.</summary>
   [Fact]
   public async Task Links_to_changed_and_unchanged_code_on_either_side_are_accepted()
   {
      var questionId = await AskAsync();
      const string answer = "Compare [this](ref:base/src/same.txt#L1-1) with [that](ref:head/src/modified.txt#L2-2).";

      using var response = await ReplyAsync(questionId, Reply("done", answer));

      var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
      Assert.True(response.IsSuccessStatusCode, $"The reply returned {(int)response.StatusCode}: {body}");
      Assert.Equal(answer, (string)SuccessBody.Output(body)["messages"]![1]!["text"]!);
   }

   /// <summary>A link has no commits to be checked at until the tour is pinned, and a reply without links needs none.</summary>
   [Fact]
   public async Task Link_in_a_reply_to_a_tour_that_is_not_pinned_is_refused_with_the_pin()
   {
      await using var harness = await ServerHarness.StartAsync();
      var id = (string)(await harness.AgentCallAsync("create_tour", $$"""{ "prUrl": "{{TestPlatform.PrUrl}}", "familiarity": "some" }"""))["id"]!;
      var ask = new PreparedCall(On("ask_question", id), await harness.UiCredentialAsync());
      using var asked = await harness.SendAsync(ask, """{ "text": "Why?", "anchor": { "kind": "tour" } }""");
      var questionId = (string)SuccessBody.Output(await asked.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))["messages"]![0]!["id"]!;

      using var linked = await harness.AgentSendJsonAsync(On("reply", id, questionId: questionId), Reply("done", "See [this](ref:head/src/modified.txt#L2-2)."));
      var stored = (await harness.CallAsync(On("read_state", id)))["threads"]![0]!["messages"]!.AsArray().Count;
      var plain = await harness.AgentCallAsync(On("reply", id, questionId: questionId), Reply("done", "Because of the change."));

      var error = JsonNode.Parse(await linked.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
      Assert.Equal((HttpStatusCode.Conflict, "tourNotPinned"), (linked.StatusCode, (string)error["code"]!));
      Assert.Equal("pinning", (string)error["pin"]!["state"]!);
      Assert.Equal(1, stored);
      Assert.Equal("done", (string)plain["messages"]![0]!["requests"]![0]!["state"]!);
   }

   private async Task<string> AskAsync() => (string)(await _fixture.AskThreadAsync("""{ "text": "Why?", "anchor": { "kind": "tour" } }"""))["messages"]![0]!["id"]!;

   private Task<HttpResponseMessage> ReplyAsync(string questionId, string body) =>
      _fixture.Tour.Harness.AgentSendJsonAsync(On("reply", _fixture.Tour.TourId, questionId: questionId), body);

   private static string Reply(string resolution, string? text) =>
      text is null ? $$"""{ "resolution": "{{resolution}}" }""" : $$"""{ "resolution": "{{resolution}}", "text": "{{text}}" }""";
}
