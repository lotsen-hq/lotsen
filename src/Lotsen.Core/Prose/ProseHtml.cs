using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Lotsen.Json;
using Lotsen.Operations;
using Lotsen.Tours;

namespace Lotsen.Prose;

/// <summary>
/// Returns the JSON that the UI receives for a tour, a plan, a stop, a thread, an output that holds threads or a review summary: the stored form with the HTML of its prose. An <c>html</c> property
/// goes in each content that holds a <see cref="StopHtml"/>, a written stop's earlier versions included, and in each agent message or reply with text. Each thread also carries its
/// status. <see cref="Of(ReviewSummary)"/> names the HTML
/// properties of a review summary.
/// </summary>
/// <remarks>
/// The store keeps only the Markdown that Claude wrote, so the HTML always comes from the current <see cref="ProseRenderer"/>. A field that does not render, as in a tour file edited by hand,
/// becomes its escaped text, so no read or event fails because of stored prose.
/// </remarks>
internal static class ProseHtml
{
   private const string HtmlProperty = "html";
   private const string StatusProperty = "status";

   private static readonly string PlanProperty = JsonNamingPolicy.CamelCase.ConvertName(nameof(Tour.Plan));
   private static readonly string StopsProperty = JsonNamingPolicy.CamelCase.ConvertName(nameof(Plan.Stops));
   private static readonly string ContentProperty = JsonNamingPolicy.CamelCase.ConvertName(nameof(Stop.Written.Content));
   private static readonly string EarlierProperty = JsonNamingPolicy.CamelCase.ConvertName(nameof(Stop.Written.Earlier));
   private static readonly string ThreadsProperty = JsonNamingPolicy.CamelCase.ConvertName(nameof(Tour.Threads));
   private static readonly string MessagesProperty = JsonNamingPolicy.CamelCase.ConvertName(nameof(QuestionThread.Sent.Messages));
   private static readonly string ThreadProperty = JsonNamingPolicy.CamelCase.ConvertName(nameof(FetchedQuestion.Thread));
   private static readonly string StopProperty = JsonNamingPolicy.CamelCase.ConvertName(nameof(FetchedQuestion.Stop));
   private static readonly string AnswersProperty = JsonNamingPolicy.CamelCase.ConvertName(nameof(ReviewSummary.Answers));
   private static readonly string RisksProperty = JsonNamingPolicy.CamelCase.ConvertName(nameof(ReviewSummary.Risks));
   private static readonly string QuestionHtmlProperty = JsonNamingPolicy.CamelCase.ConvertName(nameof(SummaryAnswer.Question)) + "Html";
   private static readonly string ScenarioHtmlProperty = JsonNamingPolicy.CamelCase.ConvertName(nameof(SummaryRisk.Scenario)) + "Html";
   private static readonly string SynthesisProperty = JsonNamingPolicy.CamelCase.ConvertName(nameof(ReviewSummary.Synthesis));

   /// <summary>The properties that the methods here add to the JSON of each type, which <see cref="UiContract"/> adds to the schemas of what the UI receives.</summary>
   public static IReadOnlyList<AddedProperty> Added { get; } =
   [
      new(typeof(StopContent), HtmlProperty, LotsenJsonContext.Default.StopHtml, Always: true),
      new(typeof(ThreadMessage.Question.Reply), HtmlProperty, LotsenJsonContext.Default.String, Always: false),
      new(typeof(ThreadMessage.AgentMessage), HtmlProperty, LotsenJsonContext.Default.String, Always: true),
      new(typeof(SummaryAnswer), QuestionHtmlProperty, LotsenJsonContext.Default.String, Always: false),
      new(typeof(SummaryRisk), ScenarioHtmlProperty, LotsenJsonContext.Default.String, Always: false),
      new(typeof(SummarySynthesis), HtmlProperty, LotsenJsonContext.Default.String, Always: true),
      new(typeof(QuestionThread.Note), StatusProperty, LotsenJsonContext.Default.ThreadStatus, Always: true),
      new(typeof(QuestionThread.Sent.Asked), StatusProperty, LotsenJsonContext.Default.ThreadStatus, Always: true),
      new(typeof(QuestionThread.Sent.Decided), StatusProperty, LotsenJsonContext.Default.ThreadStatus, Always: true),
      new(typeof(QuestionThread.Sent.Started), StatusProperty, LotsenJsonContext.Default.ThreadStatus, Always: true),
   ];

   /// <summary>Returns the JSON of the tour, whose stops with content and agent messages and replies with text carry their HTML.</summary>
   public static JsonNode Of(Tour tour)
   {
      var node = JsonSerializer.SerializeToNode(tour, LotsenJsonContext.Default.Tour)!;
      if (tour.Plan is { } plan)
      {
         node[PlanProperty] = Of(plan);
      }

      node[ThreadsProperty] = new JsonArray([.. tour.Threads.Select(Of)]);
      return node;
   }

   /// <summary>Returns the JSON of the thread, whose agent messages and replies with text carry their HTML, with the thread's <see cref="ThreadStatus"/> as <c>status</c>.</summary>
   public static JsonNode Of(QuestionThread thread)
   {
      var node = JsonSerializer.SerializeToNode(thread, LotsenJsonContext.Default.QuestionThread)!;
      node[StatusProperty] = JsonSerializer.SerializeToNode(ThreadStatus.Of(thread), LotsenJsonContext.Default.ThreadStatus);
      if (thread is not QuestionThread.Sent sent)
      {
         return node;
      }

      var messages = node[MessagesProperty]!.AsArray();
      for (var i = 0; i < sent.Messages.Count; i++)
      {
         var text = sent.Messages[i] switch
         {
            ThreadMessage.Question.Reply reply => reply.Text,
            ThreadMessage.AgentMessage message => message.Text,
            ThreadMessage.Question => null,
         };
         if (text is not null)
         {
            messages[i]![HtmlProperty] = Html(text);
         }
      }

      return node;
   }

   /// <summary>Returns the JSON of the event payload, whose plan carries its HTML.</summary>
   public static JsonNode Of(PlanSetEvent planSet)
   {
      var node = JsonSerializer.SerializeToNode(planSet, LotsenJsonContext.Default.PlanSetEvent)!;
      node[PlanProperty] = Of(planSet.Plan);
      return node;
   }

   /// <summary>Returns the JSON of the event payload, whose stop carries its HTML.</summary>
   public static JsonNode Of(StopPutEvent stopPut)
   {
      var node = JsonSerializer.SerializeToNode(stopPut, LotsenJsonContext.Default.StopPutEvent)!;
      node[StopProperty] = Of(stopPut.Stop);
      return node;
   }

   /// <summary>Returns the JSON of the event payload, whose stop and threads carry their HTML.</summary>
   public static JsonNode Of(StopRevisedEvent revised)
   {
      var node = JsonSerializer.SerializeToNode(revised, LotsenJsonContext.Default.StopRevisedEvent)!;
      node[StopProperty] = Of(revised.Stop);
      node[ThreadsProperty] = new JsonArray([.. revised.Threads.Select(Of)]);
      return node;
   }

   /// <summary>Returns the JSON of the event payload, whose thread carries its HTML.</summary>
   public static JsonNode Of(ThreadCreatedEvent created)
   {
      var node = JsonSerializer.SerializeToNode(created, LotsenJsonContext.Default.ThreadCreatedEvent)!;
      node[ThreadProperty] = Of(created.Thread);
      return node;
   }

   /// <summary>Returns the JSON of the event payload, whose thread carries its HTML.</summary>
   public static JsonNode Of(ThreadUpdatedEvent updated)
   {
      var node = JsonSerializer.SerializeToNode(updated, LotsenJsonContext.Default.ThreadUpdatedEvent)!;
      node[ThreadProperty] = Of(updated.Thread);
      return node;
   }

   /// <summary>Returns the JSON of the updated thread, whose thread carries its HTML.</summary>
   public static JsonNode Of(UpdatedThread updated)
   {
      var node = JsonSerializer.SerializeToNode(updated, LotsenJsonContext.Default.UpdatedThread)!;
      node[ThreadProperty] = Of(updated.Thread);
      return node;
   }

   /// <summary>Returns the JSON of the sent notes, whose threads carry their HTML.</summary>
   public static JsonNode Of(SentNotes sent)
   {
      var node = JsonSerializer.SerializeToNode(sent, LotsenJsonContext.Default.SentNotes)!;
      node[ThreadsProperty] = new JsonArray([.. sent.Threads.Select(Of)]);
      return node;
   }

   /// <summary>Returns the JSON of the familiarity change, whose thread carries its HTML.</summary>
   public static JsonNode Of(ChangedFamiliarity changed)
   {
      var node = JsonSerializer.SerializeToNode(changed, LotsenJsonContext.Default.ChangedFamiliarity)!;
      node[ThreadProperty] = Of(changed.Thread);
      return node;
   }

   /// <summary>Returns the JSON of the plan approval, whose thread carries its HTML.</summary>
   public static JsonNode Of(ApprovedPlan approved)
   {
      var node = JsonSerializer.SerializeToNode(approved, LotsenJsonContext.Default.ApprovedPlan)!;
      node[ThreadProperty] = Of(approved.Thread);
      return node;
   }

   /// <summary>Returns the JSON of the iteration update, whose thread, when the update sent one, carries its HTML.</summary>
   public static JsonNode Of(UpdatedIteration updated)
   {
      var node = JsonSerializer.SerializeToNode(updated, LotsenJsonContext.Default.UpdatedIteration)!;
      if (updated.Thread is { } thread)
      {
         node[ThreadProperty] = Of(thread);
      }

      return node;
   }

   /// <summary>Returns the JSON of the fetched question, whose thread and stop carry their HTML.</summary>
   public static JsonNode Of(FetchedQuestion fetched)
   {
      var node = JsonSerializer.SerializeToNode(fetched, LotsenJsonContext.Default.FetchedQuestion)!;
      node[ThreadProperty] = Of(fetched.Thread);
      if (fetched.Stop is { } stop)
      {
         node[StopProperty] = Of(stop);
      }

      return node;
   }

   /// <summary>
   /// Returns the JSON of the review summary, in which each answer with a question carries the question's HTML as <c>questionHtml</c>, each risk with a scenario carries the scenario's HTML
   /// as <c>scenarioHtml</c>, and the synthesis carries its HTML as <c>html</c>. An answer's text is the reviewer's plain text, so it gets none.
   /// </summary>
   public static JsonNode Of(ReviewSummary summary)
   {
      var node = JsonSerializer.SerializeToNode(summary, LotsenJsonContext.Default.ReviewSummary)!;
      var groups = node[AnswersProperty]!;
      (string Name, IReadOnlyList<SummaryAnswer> Answers)[] answers =
      [
         (nameof(SummaryAnswers.Decisions), summary.Answers.Decisions),
         (nameof(SummaryAnswers.Concerns), summary.Answers.Concerns),
         (nameof(SummaryAnswers.OpenQuestions), summary.Answers.OpenQuestions),
         (nameof(SummaryAnswers.Outdated), summary.Answers.Outdated),
      ];
      foreach (var (name, entries) in answers)
      {
         AddHtml(groups[JsonNamingPolicy.CamelCase.ConvertName(name)]!.AsArray(), entries, answer => answer.Question, QuestionHtmlProperty);
      }

      AddHtml(node[RisksProperty]!.AsArray(), summary.Risks, risk => risk.Scenario, ScenarioHtmlProperty);
      if (summary.Synthesis is { } synthesis)
      {
         node[SynthesisProperty]![HtmlProperty] = Html(synthesis.Text);
      }

      return node;
   }

   private static void AddHtml<T>(JsonArray nodes, IReadOnlyList<T> items, Func<T, string?> prose, string property)
   {
      for (var i = 0; i < items.Count; i++)
      {
         if (prose(items[i]) is { } text)
         {
            nodes[i]![property] = Html(text);
         }
      }
   }

   /// <summary>Returns the JSON of the plan, whose stops with content carry their HTML.</summary>
   public static JsonNode Of(Plan plan)
   {
      var node = JsonSerializer.SerializeToNode(plan, LotsenJsonContext.Default.Plan)!;
      node[StopsProperty] = new JsonArray([.. plan.Stops.Select(Of)]);
      return node;
   }

   /// <summary>Returns the JSON of the stop, whose content and earlier versions carry their HTML when it is written.</summary>
   public static JsonNode Of(Stop stop)
   {
      var node = JsonSerializer.SerializeToNode(stop, LotsenJsonContext.Default.Stop)!;
      if (stop is Stop.Written written)
      {
         node[ContentProperty]![HtmlProperty] = Html(written.Content);
         var earlier = node[EarlierProperty]!.AsArray();
         for (var i = 0; i < written.Earlier.Count; i++)
         {
            earlier[i]![ContentProperty]![HtmlProperty] = Html(written.Earlier[i].Content);
         }
      }

      return node;
   }

   private static JsonNode? Html(StopContent content) => JsonSerializer.SerializeToNode(Render(content), LotsenJsonContext.Default.StopHtml);

   // StopProseTests checks this HTML for each prose field that its full content sets.
   private static StopHtml Render(StopContent content)
   {
      var rendered = StopProse.Map(content, (_, prose) => Html(prose));
      return new(
         rendered.Headline,
         rendered.How,
         rendered.Why,
         rendered.DesignChoices,
         rendered.PinnedBy,
         rendered.Checks?.Select(check => new CheckHtml(check.Question, check.Recommendation)).ToList(),
         rendered.Scenario);
   }

   private static string Html(string prose)
   {
      try
      {
         return ProseRenderer.Render(prose).Html;
      }
      catch (ProseTooDeepException)
      {
         return ProseRenderer.AsText(prose);
      }
   }
}

/// <summary>A property that <see cref="ProseHtml"/> adds to the JSON of a type, beside the type's own properties.</summary>
/// <param name="Owner">The type whose JSON gets the property.</param>
/// <param name="Name">The property's name.</param>
/// <param name="Value">The JSON type of the property's value.</param>
/// <param name="Always">Whether every JSON of the owner gets it, or only one whose prose field holds text.</param>
internal sealed record AddedProperty(Type Owner, string Name, JsonTypeInfo Value, bool Always);
