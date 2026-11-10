using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Lotsen.Tours;

/// <summary>The tour action that <c>request_tour_action</c> asks for. The <c>kind</c> property in JSON names the derived type, by the kind names of <see cref="TourAction"/>.</summary>
/// <remarks>
/// It names no reviewer decision and carries no follow-up items, because the server fills those from the tour, and only explain and revise name a stop. JSON refuses any other member, so
/// a request that gives a follow-up action's items fails to read. <see cref="ActionRules.Admit"/> turns it into the stored <see cref="TourAction"/> with its anchor.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Explain), TourAction.ExplainKind)]
[JsonDerivedType(typeof(Revise), TourAction.ReviseKind)]
[JsonDerivedType(typeof(Replan), TourAction.ReplanKind)]
[JsonDerivedType(typeof(Reorder), TourAction.ReorderKind)]
[JsonDerivedType(typeof(Summarize), TourAction.SummarizeKind)]
[JsonDerivedType(typeof(FollowUp.DraftWorkItems), TourAction.DraftWorkItemsKind)]
[JsonDerivedType(typeof(FollowUp.WalkOpenQuestions), TourAction.WalkOpenQuestionsKind)]
[JsonDerivedType(typeof(FollowUp.DraftComments), TourAction.DraftCommentsKind)]
internal closed record ActionRequest
{
   private const string TargetDescription = "The stop, or the text selected in a prose field of a stop, that the action is about.";

   private ActionRequest()
   {
   }

   [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
   internal sealed record Explain([property: Description(TargetDescription)] StopTarget Target) : ActionRequest;

   [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
   internal sealed record Revise([property: Description(TargetDescription)] StopTarget Target) : ActionRequest;

   [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
   internal sealed record Replan : ActionRequest;

   [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
   internal sealed record Reorder(
      [property: Description("The IDs of every stop of the plan, each once, in the order that the reviewer proposes.")]
      IReadOnlyList<string> StopIds) : ActionRequest;

   [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
   internal sealed record Summarize : ActionRequest;

   /// <summary>A follow-up action, whose items the server collects from the review summary.</summary>
   internal closed record FollowUp : ActionRequest
   {
      private FollowUp()
      {
      }

      [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
      internal sealed record DraftWorkItems : FollowUp;

      [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
      internal sealed record WalkOpenQuestions : FollowUp;

      [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
      internal sealed record DraftComments : FollowUp;
   }
}

/// <summary>The stop or stop text that an explain or revise request is about, and the stop or stop text anchor of <c>ask_question</c>. The <c>kind</c> property in JSON names the derived type.</summary>
/// <remarks><see cref="StopTargets.Resolve"/> turns one into its <see cref="ThreadAnchor"/>.</remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(WholeStop), "stop")]
[JsonDerivedType(typeof(StopText), "stopText")]
internal closed record StopTarget
{
   private StopTarget()
   {
   }

   [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
   internal sealed record WholeStop(
      [property: Description("The ID of the stop.")]
      string StopId,
      [property: Description("The stop's version that the reviewer viewed. A version that is not the stop's current one is refused. Omit it for the current version.")]
      int? Version = null) : StopTarget;

   [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
   internal sealed record StopText(
      [property: Description("The ID of the stop whose text the reviewer selected.")]
      string StopId,
      [property: Description(ProseField.SchemaDescription)]
      string Field,
      [property: Description("The selected text.")]
      string Text,
      [property: Description("The stop's version that the text was selected in, as the stop's version in the tour. A version that is not the stop's current one is refused. Omit it for the current version.")]
      int? Version = null) : StopTarget;
}
