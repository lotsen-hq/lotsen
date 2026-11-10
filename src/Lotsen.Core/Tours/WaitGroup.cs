namespace Lotsen.Tours;

/// <summary>A group of threads whose waiting holds back the threads of the group, as <see cref="ActionRules.WaitsAlone"/> and <see cref="DecisionRules.WaitsAlone"/> give it to a thread.</summary>
/// <remarks>
/// The table of <see cref="TourThreads"/> refuses each command that would make a thread of a group wait while a thread that holds back that group waits. So at most one thread of a group waits
/// at a time, except in <see cref="Plan"/>, because no waiting thread holds back a follow-up there or a re-send of one.
/// </remarks>
internal enum WaitGroup
{
   /// <summary>The summarize tour actions, because each synthesis replaces the last one.</summary>
   Summary,

   /// <summary>The draftWorkItems follow-up actions.</summary>
   WorkItems,

   /// <summary>The walkOpenQuestions follow-up actions.</summary>
   OpenQuestions,

   /// <summary>The draftComments follow-up actions.</summary>
   Comments,

   /// <summary>The familiarity changes and the plan approval, because Claude writes stops at the level that each of them sets.</summary>
   Level,

   /// <summary>The iteration updates, because Claude revises each stale stop against the pin that its update set.</summary>
   Iteration,

   /// <summary>The replans and the reorders, because each asks Claude to change the plan that the other changes.</summary>
   /// <remarks>
   /// A waiting thread of <see cref="Level"/> or <see cref="Iteration"/> holds back a new one and a re-send of one too, as <see cref="WaitGroups.HoldsBack"/> says. No waiting thread holds back a
   /// follow-up in their threads or a re-send of that follow-up, as <see cref="TourThreads.FollowUpGroupOf"/> says.
   /// </remarks>
   Plan,
}

/// <summary>Which waiting threads hold back a thread of a wait group.</summary>
internal static class WaitGroups
{
   /// <summary>Returns whether a waiting thread of <paramref name="waiting"/> holds back a thread of <paramref name="group"/>.</summary>
   /// <remarks>
   /// A thread holds back the threads of its own group. A familiarity change, the plan approval and an iteration update also hold back a replan or reorder, because Claude writes the
   /// stops of the plan for each of them.
   /// </remarks>
   public static bool HoldsBack(this WaitGroup waiting, WaitGroup group) => waiting == group || (group == WaitGroup.Plan && waiting is WaitGroup.Level or WaitGroup.Iteration);
}
