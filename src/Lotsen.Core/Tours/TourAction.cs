using System.Text.Json.Serialization;

namespace Lotsen.Tours;

/// <summary>What the reviewer asked the bound session to do to the tour, as an asked thread stores it. The <c>kind</c> property in JSON names the derived type.</summary>
/// <remarks>
/// A new thread gets one only from <see cref="ActionRules.Admit"/>, which builds it from an <see cref="ActionRequest"/>. The rules of each kind are the columns of <see cref="ActionRules"/>.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Explain), ExplainKind)]
[JsonDerivedType(typeof(Revise), ReviseKind)]
[JsonDerivedType(typeof(Replan), ReplanKind)]
[JsonDerivedType(typeof(Reorder), ReorderKind)]
[JsonDerivedType(typeof(Summarize), SummarizeKind)]
[JsonDerivedType(typeof(Dive), DiveKind)]
[JsonDerivedType(typeof(FollowUp.DraftWorkItems), DraftWorkItemsKind)]
[JsonDerivedType(typeof(FollowUp.WalkOpenQuestions), WalkOpenQuestionsKind)]
[JsonDerivedType(typeof(FollowUp.DraftComments), DraftCommentsKind)]
internal closed record TourAction
{
   public const string ExplainKind = "explain";
   public const string ReviseKind = "revise";
   public const string ReplanKind = "replan";
   public const string ReorderKind = "reorder";
   public const string SummarizeKind = "summarize";
   public const string DiveKind = "dive";
   public const string DraftWorkItemsKind = "draftWorkItems";
   public const string WalkOpenQuestionsKind = "walkOpenQuestions";
   public const string DraftCommentsKind = "draftComments";

   private TourAction()
   {
   }

   /// <summary>The kind's name in JSON, which the question event names as its action.</summary>
   [JsonIgnore]
   public abstract string Kind { get; }

   /// <summary>A request to explain a stop, or text in a stop, more deeply than the stop does.</summary>
   /// <param name="StopVersion">The stop's version when the request was stored, on a whole-stop anchor, or null on a stop text anchor, which holds its own version. JSON leaves it out when null.</param>
   [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
   internal sealed record Explain([property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? StopVersion = null) : TourAction
   {
      [JsonIgnore]
      public override string Kind => ExplainKind;
   }

   /// <summary>A request to revise a stop as the reviewer's words say.</summary>
   /// <param name="StopVersion">The stop's version when the request was stored, on a whole-stop anchor, or null on a stop text anchor, which holds its own version. JSON leaves it out when null.</param>
   [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
   internal sealed record Revise([property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? StopVersion = null) : TourAction
   {
      [JsonIgnore]
      public override string Kind => ReviseKind;
   }

   /// <summary>A request to change the plan as the reviewer's words say.</summary>
   [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
   internal sealed record Replan : TourAction
   {
      [JsonIgnore]
      public override string Kind => ReplanKind;
   }

   /// <summary>A request to put the plan's stops in the reviewer's proposed order, with the reviewer's optional words.</summary>
   /// <param name="StopIds">The IDs of every stop of the plan when the request was stored, each once, in the order that the reviewer proposes.</param>
   /// <remarks>
   /// The proposal is a request, because only Claude writes the plan: Claude may keep a stop where it depends on another, and gives the chapters to the new order. So an order that splits a
   /// chapter is not refused.
   /// </remarks>
   [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
   internal sealed record Reorder(IReadOnlyList<string> StopIds) : TourAction
   {
      [JsonIgnore]
      public override string Kind => ReorderKind;
   }

   /// <summary>A request to write a synthesis of the review with <c>write_synthesis</c>, with the reviewer's optional words.</summary>
   /// <remarks>A cancel or a drop reaches no session, so Claude may still write the synthesis of a canceled or unanswered request.</remarks>
   [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
   internal sealed record Summarize : TourAction
   {
      [JsonIgnore]
      public override string Kind => SummarizeKind;
   }

   /// <summary>A request to create a dive from the anchor's stop, or from the whole tour, and write it as a tour of its purpose, with the reviewer's words.</summary>
   /// <param name="Purpose">What the dive is for.</param>
   /// <param name="Familiarity">The familiarity level of the dive.</param>
   [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
   internal sealed record Dive(TourPurpose Purpose, Familiarity Familiarity) : TourAction
   {
      [JsonIgnore]
      public override string Kind => DiveKind;
   }

   /// <summary>
   /// A request from the summary page that hands Claude one group of the review summary, with the reviewer's optional words, as the summary that an agent session reads held the group when the
   /// server stored the request: the sent answers of a type and the sent threads of that type.
   /// </summary>
   /// <remarks>
   /// A re-send carries the stored items, so a later answer or a later change of a thread's type does not change what Claude was asked about. lotsen creates no work item and posts no comment:
   /// Claude does, with its own tools, after the reviewer approves in the terminal. Each list of threads is null when the group held none, an empty list included, so an action without one keeps
   /// the JSON that a build without these lists reads.
   /// </remarks>
   internal closed record FollowUp : TourAction
   {
      private FollowUp()
      {
      }

      /// <summary>A request that Claude propose work items for the reviewer's decisions, and create each with its own tools once the reviewer approves it in the terminal.</summary>
      /// <param name="Decisions">The summary's decisions, in its order.</param>
      /// <param name="DecisionThreads">The summary's sent threads of type decision, in its order.</param>
      [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
      internal sealed record DraftWorkItems(IReadOnlyList<FollowUpAnswer> Decisions, IReadOnlyList<FollowUpThread>? DecisionThreads = null) : FollowUp
      {
         [JsonIgnore]
         public override string Kind => DraftWorkItemsKind;

         /// <summary>The summary's sent threads of type decision, in its order, or null for none. JSON leaves it out when null.</summary>
         [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
         public IReadOnlyList<FollowUpThread>? DecisionThreads { get; } = FollowUpThread.NoneAsNull(DecisionThreads);
      }

      /// <summary>
      /// A request that Claude walk the reviewer through the open questions in the UI, with what would help each decision and a recommendation, for the reviewer to settle there: in a new
      /// thread for an open question answer, and in its own thread for an open question thread.
      /// </summary>
      /// <param name="OpenQuestions">The summary's open questions, in its order.</param>
      /// <param name="OpenQuestionThreads">The summary's sent threads of type open question, in its order.</param>
      [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
      internal sealed record WalkOpenQuestions(IReadOnlyList<FollowUpAnswer> OpenQuestions, IReadOnlyList<FollowUpThread>? OpenQuestionThreads = null) : FollowUp
      {
         [JsonIgnore]
         public override string Kind => WalkOpenQuestionsKind;

         /// <summary>The summary's sent threads of type open question, in its order, or null for none. JSON leaves it out when null.</summary>
         [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
         public IReadOnlyList<FollowUpThread>? OpenQuestionThreads { get; } = FollowUpThread.NoneAsNull(OpenQuestionThreads);
      }

      /// <summary>
      /// A request that Claude draft a comment for the author for each of the reviewer's concerns that needs one, the risk stops included, and return the drafts as text. Claude posts one only with
      /// its own tools, once the reviewer approves it in the terminal.
      /// </summary>
      /// <param name="Concerns">The summary's concerns, in its order.</param>
      /// <param name="Risks">The risk stops that <see cref="ActionRules"/> hands to Claude from the summary, in plan order.</param>
      /// <param name="ConcernThreads">The summary's sent threads of type concern, in its order.</param>
      [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
      internal sealed record DraftComments(IReadOnlyList<FollowUpAnswer> Concerns, IReadOnlyList<FollowUpRisk> Risks, IReadOnlyList<FollowUpThread>? ConcernThreads = null) : FollowUp
      {
         [JsonIgnore]
         public override string Kind => DraftCommentsKind;

         /// <summary>The summary's sent threads of type concern, in its order, or null for none. JSON leaves it out when null.</summary>
         [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
         public IReadOnlyList<FollowUpThread>? ConcernThreads { get; } = FollowUpThread.NoneAsNull(ConcernThreads);
      }
   }
}
