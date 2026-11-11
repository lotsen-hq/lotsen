using System.Text.Json.Nodes;
using Lotsen.Git;
using Lotsen.Operations;
using Lotsen.Platforms;
using Lotsen.Prose;
using Lotsen.Tours;

namespace Lotsen.Tests;

/// <summary>Covers the JSON that <see cref="ProseHtml"/> returns for the UI: each stop's content keeps its Markdown and gains the HTML of every prose field.</summary>
[Trait("Category", "Markdown")]
public sealed class ProseHtmlTests
{
   private static readonly StopPassage Passage = new(FileSide.Head, "a.txt", 1, 1, PassageRole.Primary, CommitId.Parse(new string('a', 40)), "a");

   [Fact]
   public void Each_check_gets_the_html_of_its_question_and_recommendation_in_order_and_its_suggestions_stay_text()
   {
      StopCheck[] checks = [new("c1", "", "*r*", [new SuggestedAnswer("*s*", AnswerType.Concern)]), new("c2", "*a*")];

      var node = ProseHtml.Of(Ready(new StopContent("H", [Passage], Checks: checks)), MentionLinks.None);

      var html = node["content"]!["html"]!["checks"]!.AsArray();
      Assert.Equal([("", "<p><em>r</em></p>\n"), ("<p><em>a</em></p>\n", null)], html.Select(check => ((string)check!["question"]!, (string?)check["recommendation"])));
      Assert.Equal("*s*", (string)node["content"]!["checks"]![0]!["suggestions"]![0]!["text"]!);
   }

   /// <summary>The fields nest one level past the limit, as only a tour file edited by hand can hold them.</summary>
   [Fact]
   public void Field_that_does_not_render_becomes_its_escaped_text()
   {
      var tooDeep = string.Concat(Enumerable.Repeat("> ", ProseRenderer.MaxDepth + 1)) + "<x>";

      var html = ProseHtml.Of(Ready(new StopContent(tooDeep, [Passage], Scenario: tooDeep)), MentionLinks.None)["content"]!["html"]!;

      Assert.Equal(ProseRenderer.AsText(tooDeep), (string)html["headline"]!);
      Assert.Equal(ProseRenderer.AsText(tooDeep), (string)html["scenario"]!);
      Assert.DoesNotContain("<x>", html.ToJsonString(), StringComparison.Ordinal);
   }

   [Fact]
   public void Mention_in_a_tours_stop_links_to_the_url_that_the_tours_platform_builds()
   {
      var tour = TourValues.Tour(TourValues.Plan(Ready(new StopContent("Fixes [#1234](workitem:1234).", [Passage])))) with
      {
         Target = new PullRequestTarget(TestPlatform.DefaultId, new Uri(TestPlatform.PrUrl)),
      };
      var platforms = new PullRequestPlatforms([new TestPlatform()]);

      var node = ProseHtml.Of(tour, platforms.MentionsOf(tour.Target));

      Assert.Contains($"<a href=\"https://{TestPlatform.DefaultHost}/shop/workitems/1234\"", (string)node["plan"]!["stops"]![0]!["content"]!["html"]!["headline"]!, StringComparison.Ordinal);
   }

   [Fact]
   public void Stop_without_content_and_absent_fields_get_no_html()
   {
      var pending = new Stop.Pending("s1", "Pending", StopKind.Inspect, "c");
      var plan = new Plan([new Chapter("c", "C")], [pending, Ready(new StopContent("H", [Passage]))], []);

      var node = ProseHtml.Of(plan, MentionLinks.None);

      Assert.Null(node["stops"]![0]!["content"]);
      Assert.Equal(["headline"], node["stops"]![1]!["content"]!["html"]!.AsObject().Select(property => property.Key));
   }

   /// <summary>IDNA refuses the host, which <see cref="Uri"/> accepts, so the stored prose must not fail a read.</summary>
   [Fact]
   public void Stored_https_link_whose_host_idna_refuses_renders_as_text()
   {
      var html = ProseHtml.Of(Ready(new StopContent("[x](https://exa\u200Dmple.com/)", [Passage])), MentionLinks.None)["content"]!["html"]!;

      Assert.Equal("<p>x</p>\n", (string)html["headline"]!);
   }

   /// <summary>A new thread of these outputs holds no reply yet, so each output here holds a thread that does, as a later output of the same type could.</summary>
   [Fact]
   public void Each_output_that_holds_a_thread_renders_the_threads_prose_as_html_in_its_operations_view()
   {
      var now = DateTimeOffset.UnixEpoch;
      var thread = ThreadFixtures.Parse(ThreadFixtures.Thread("asked", "t1", new ThreadAnchor.WholeTour(), QuestionThreadState.Open,
      [
         ThreadFixtures.Question("q1", "Why?", null, now, ("r1", RequestState.Done, "s1")),
         ThreadFixtures.Reply("a1", "r1", "s1", "*Because.*", now),
      ]));
      var tourId = TourId.New();
      var pin = new TourPin.Pinned(CommitId.Parse(new string('a', 40)), CommitId.Parse(new string('b', 40)), 2, new Uri("https://platform.test/shop"));
      (string Operation, object Output, Func<JsonNode, JsonNode> Thread)[] outputs =
      [
         ("change_familiarity", new ChangedFamiliarity(tourId, now, Familiarity.New, thread), node => node["thread"]!),
         ("approve_plan", new ApprovedPlan(tourId, now, PlanApproval.Given, Familiarity.Some, [], thread), node => node["thread"]!),
         ("send_notes", new SentNotes(tourId, now, [thread]), node => node["threads"]![0]!),
         ("update_iteration", new UpdatedIteration(tourId, now, pin, ["s1"], thread), node => node["thread"]!),
      ];

      foreach (var (operation, output, threadOf) in outputs)
      {
         var node = OperationCatalog.Get(operation).Prose.Render(output, MentionLinks.None)!;

         Assert.Equal("<p><em>Because.</em></p>\n", (string?)threadOf(node)["messages"]![1]!["html"]);
         Assert.Equal("*Because.*", (string?)threadOf(node)["messages"]![1]!["text"]);
      }
   }

   [Fact]
   public void Each_reply_and_agent_message_with_text_gets_its_html_and_a_question_and_a_reply_without_text_get_none()
   {
      var now = DateTimeOffset.UnixEpoch;
      var thread = ThreadFixtures.Parse(ThreadFixtures.Thread("asked", "t1", new ThreadAnchor.WholeTour(), QuestionThreadState.Open,
      [
         ThreadFixtures.Question("q1", "*Why?*", null, now, ("r1", RequestState.Declined, "s1")),
         ThreadFixtures.Reply("a1", "r1", "s1", null, now),
         ThreadFixtures.Question("q2", "*And?*", null, now, ("r2", RequestState.Done, "s1")),
         ThreadFixtures.Reply("a2", "r2", "s1", "*Because.*", now),
         ThreadFixtures.AgentMessage("m1", "s1", "*Also.*", now),
      ]));

      var node = ProseHtml.Of(thread, MentionLinks.None);

      var messages = node["messages"]!.AsArray();
      Assert.Equal([false, false, false, true, true], messages.Select(message => message!.AsObject().ContainsKey("html")));
      Assert.Equal(("*Because.*", "<p><em>Because.</em></p>\n"), ((string)messages[3]!["text"]!, (string)messages[3]!["html"]!));
      Assert.Equal("<p><em>Also.</em></p>\n", (string)messages[4]!["html"]!);
   }

   [Fact]
   public void Summary_answers_get_the_html_of_their_question_and_risks_of_their_scenario_and_the_answer_text_stays_text()
   {
      var tour = SummaryTours.Apply(SummaryTours.Empty());

      var node = ProseHtml.Of(ReviewSummary.Of(tour, tour), MentionLinks.None);

      var answers = node["answers"]!;
      Assert.Equal(("Is the cap right?", "<p>Is the cap right?</p>\n"), ((string)answers["decisions"]![0]!["question"]!, (string)answers["decisions"]![0]!["questionHtml"]!));
      Assert.Equal("<p>Is the old rule kept?</p>\n", (string)answers["outdated"]![0]!["questionHtml"]!);
      Assert.False(answers["concerns"]!.AsArray().Single(answer => (string)answer!["checkId"]! == "c9")!.AsObject().ContainsKey("questionHtml"));
      Assert.Equal(["stopId", "stopTitle", "checkId", "question", "text", "type", "state", "questionHtml"], answers["openQuestions"]![0]!.AsObject().Select(property => property.Key));

      var risks = node["risks"]!.AsArray();
      Assert.Equal(["<p>Two discounts round twice.</p>\n", null], risks.Select(risk => (string?)risk!["scenarioHtml"]));
   }

   [Fact]
   public void Mention_in_a_summarys_synthesis_links_to_the_url_that_the_tours_platform_builds()
   {
      var tour = SummaryTours.Apply(SummaryTours.Empty());
      var summary = ReviewSummary.Of(tour, tour) with { Synthesis = new SummarySynthesis("Follows [!42](pullrequest:42).", TourValues.Now, TourValues.Pinned, PredatesSummary: false) };
      var mentions = new PullRequestPlatforms([new TestPlatform()]).MentionsOf(new PullRequestTarget(TestPlatform.DefaultId, new Uri(TestPlatform.PrUrl)));

      var node = ProseHtml.Of(summary, mentions);

      Assert.Contains($"<a href=\"https://{TestPlatform.DefaultHost}/shop/pull/42\"", (string)node["synthesis"]!["html"]!, StringComparison.Ordinal);
   }

   private static Stop.Written Ready(StopContent content) => new("s2", "Ready", StopKind.Inspect, "c", content, 1, []);
}
