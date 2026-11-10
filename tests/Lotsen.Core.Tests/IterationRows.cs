using Lotsen.Git;
using Lotsen.Operations;
using Lotsen.Pinning;
using Lotsen.Platforms;
using Lotsen.Tours;

namespace Lotsen.Tests;

/// <summary>Builds the rows of the pure core's tests: a tour on <see cref="Pin"/>, git facts about made-up commits, and platform answers.</summary>
/// <remarks>
/// The tour is pinned to merge base <see cref="Base"/> and head <see cref="Head"/>. The new iteration keeps the merge base and moves the head to <see cref="NewHead"/>, as <see cref="NewPin"/>
/// holds it, unless a row names <see cref="NewBase"/>.
/// </remarks>
internal static class IterationRows
{
   public static readonly DateTimeOffset Now = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

   public static readonly CommitId Base = Commit('a');
   public static readonly CommitId Head = Commit('b');
   public static readonly CommitId NewBase = Commit('c');
   public static readonly CommitId NewHead = Commit('d');
   public static readonly CommitId Target = Commit('e');

   public static readonly Uri Remote = new("https://platform.test/shop.git");
   public static readonly PullRequestTarget PullRequest = new("test", new Uri("https://platform.test/shop/pull/42"));

   public static readonly TourPin.Pinned Pin = new(Base, Head, 1, Remote);
   public static readonly TourPin.Pinned NewPin = new(Base, NewHead, 2, Remote);

   public static readonly TourId Id = TourId.TryParse("iterationrowsabc", out var id) ? id : throw new InvalidOperationException("The ID does not parse.");

   /// <summary>Returns lines <c>line &lt;first&gt;</c> to <c>line &lt;last&gt;</c>, each ending in a line feed, with each given line replaced.</summary>
   public static string Text(int first, int last, params (int Line, string Replacement)[] replacements) =>
      string.Concat(Enumerable.Range(first, last - first + 1).Select(i => (replacements.FirstOrDefault(r => r.Line == i).Replacement ?? $"line {i}") + "\n"));

   public static FileLines Lines(string text) => new(text);

   public static StopPassage Passage(string path, int startLine, int endLine, PassageRole role = PassageRole.Primary, FileSide side = FileSide.Head, CommitId? commit = null, string? text = null) =>
      new(side, path, startLine, endLine, role, commit ?? (side == FileSide.Head ? Head : Base), text ?? new FileLines(Text(1, 20)).Text(startLine, endLine));

   public static Stop.Written Stop(string id, StopKind kind, params StopPassage[] passages) => new(id, $"Stop {id}", kind, "c1", new StopContent($"Stop {id}.", passages), 1, []);

   /// <summary>Returns the content with the prose in one field: <c>pinnedBy</c>, <c>scenario</c>, or <c>checks[c1].recommendation</c>, which sets the one check c1.</summary>
   public static StopContent WithProse(this StopContent content, string field, string prose) => field switch
   {
      "pinnedBy" => content with { PinnedBy = prose },
      "scenario" => content with { Scenario = prose },
      "checks[c1].recommendation" => content with { Checks = [new StopCheck("c1", "Fine?", prose)] },
      _ => throw new ArgumentException($"No row sets the field '{field}'.", nameof(field)),
   };

   public static Tour TourOf(IReadOnlyList<Stop> stops, IReadOnlyList<QuestionThread>? threads = null, IReadOnlyList<FileMark>? marks = null) =>
      new(Id, PullRequest, Familiarity.Some, Now, Now, Pin, new Plan([new Chapter("c1", "Chapter")], stops, marks ?? []), ThreadFixtures.Threads([.. threads ?? []]), []);

   public static PlatformAnswer.Resolved Answer(CommitId source, CommitId? target = null, int iteration = 2, PullRequestState state = PullRequestState.Active) =>
      new(new ResolvedPullRequest(Remote, source, target ?? Target, iteration, state), Now);

   /// <summary>Returns facts in which the new head modifies each path in <paramref name="modified"/> and changes nothing else, the merge base of the new head is <see cref="Base"/>, and every
   /// head-side passage of a change stop touches the new pin's diff.</summary>
   public static FactSetBuilder Facts(params string[] modified) => new FactSetBuilder()
      .Add(new FactKey.MergeBaseOf(NewHead, Target), Base)
      .Add(new FactKey.ChangedPaths(Head, NewHead), [.. modified.Select(path => new ChangedPath(path, FileChangeKind.Modified))]);

   /// <summary>Adds the hunks of the path from the head to the new head, and the path's text at the new head.</summary>
   public static FactSetBuilder Changed(this FactSetBuilder facts, string path, string newText, params LineHunk[] hunks) => facts
      .Add(new FactKey.Hunks(Head, path, NewHead, path), hunks)
      .Add(new FactKey.Lines(NewHead, path), new FileLines(newText));

   /// <summary>Adds that the new pin's diff changes the path at the side throughout, so every passage there touches it.</summary>
   public static FactSetBuilder InDiff(this FactSetBuilder facts, string path, FileSide side = FileSide.Head) =>
      facts.Add(new FactKey.PinDiff(Base, NewHead, side, path), new DiffTouch.Throughout());

   /// <summary>Adds that the new pin's diff does not change the path at the side.</summary>
   public static FactSetBuilder OffDiff(this FactSetBuilder facts, string path, FileSide side = FileSide.Head) =>
      facts.Add(new FactKey.PinDiff(Base, NewHead, side, path), new DiffTouch.Unchanged());

   /// <summary>Returns the value of a pass that must decide.</summary>
   public static T Decided<T>(Decision<T> decision) => Assert.IsType<Decision<T>.Decided>(decision).Value;

   /// <summary>Returns the keys that a pass that must not decide lacked.</summary>
   public static IReadOnlySet<FactKey> Missing<T>(Decision<T> decision) => Assert.IsType<Decision<T>.NeedsFacts>(decision).Missing;

   private static CommitId Commit(char digit) => CommitId.Parse(new string(digit, 40));
}
