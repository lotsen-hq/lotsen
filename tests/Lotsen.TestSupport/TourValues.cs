using Lotsen.Git;
using Lotsen.Operations;
using Lotsen.Tours;

namespace Lotsen.Tests;

/// <summary>Builds tour values for the rule tables and the writer tests: a valid tour, plan, stop and content that a test changes in one place.</summary>
internal static class TourValues
{
   /// <summary>The time that every value built here holds.</summary>
   public static readonly DateTimeOffset Now = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

   /// <summary>The head commit of <see cref="Pinned"/>.</summary>
   public static readonly CommitId Head = CommitId.Parse(new string('b', 40));

   /// <summary>A pin of iteration 1, whose merge base is all <c>a</c> and whose head is <see cref="Head"/>.</summary>
   public static readonly TourPin.Pinned Pinned = new(CommitId.Parse(new string('a', 40)), Head, 1, new Uri("https://example.test/repo.git"));

   /// <summary>The ID of every tour built here.</summary>
   public static readonly TourId Id = TourId.TryParse("abcdefghijklmnop", out var id) ? id : throw new InvalidOperationException("The ID does not parse.");

   /// <summary>Returns a tour pinned to <see cref="Pinned"/> with the plan, and no threads or answers.</summary>
   public static Tour Tour(Plan? plan = null) =>
      new(Id, new PullRequestTarget("test", new Uri("https://example.test/pr/1")), Familiarity.Some, Now, Now, Pinned, plan, TourThreads.Empty, []);

   /// <summary>Returns a plan of one chapter <c>a</c> with these stops, or with one pending inspect stop <c>s1</c>.</summary>
   public static Plan Plan(params Stop[] stops) => new([new Chapter("a", "A")], stops.Length == 0 ? [Pending("s1")] : stops, []);

   /// <summary>Returns a stop without content in chapter <c>a</c>.</summary>
   public static Stop.Pending Pending(string id, StopKind kind = StopKind.Inspect, string chapter = "a", AnchorHint? hint = null) => new(id, $"Stop {id}", kind, chapter, hint);

   /// <summary>Returns a stop in chapter <c>a</c> at version 1 of the content, or of <see cref="Content"/> for its kind.</summary>
   public static Stop.Written Written(string id, StopKind kind = StopKind.Inspect, StopContent? content = null) => new(id, $"Stop {id}", kind, "a", content ?? Content(kind), 1, []);

   /// <summary>Returns content that follows the rules of the kind: a headline, a primary passage, a scenario for a risk and a check for a question.</summary>
   public static StopContent Content(StopKind kind = StopKind.Inspect, string checkId = "c1") => new(
      "The point.",
      kind == StopKind.Overview ? [] : [Passage()],
      Checks: kind == StopKind.Question ? [new StopCheck(checkId, "Fine?")] : null,
      Scenario: kind == StopKind.Risk ? "It breaks." : null);

   /// <summary>Returns a passage of lines 1 to 2 of <c>src/a.cs</c> at the head.</summary>
   public static StopPassage Passage(PassageRole role = PassageRole.Primary, int startLine = 1, int endLine = 2, string path = "src/a.cs") =>
      new(FileSide.Head, path, startLine, endLine, role, Head, "line 1\nline 2\n");
}
