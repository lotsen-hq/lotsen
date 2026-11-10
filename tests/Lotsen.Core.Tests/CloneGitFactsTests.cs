using Lotsen.Git;
using Lotsen.Hosting;
using Lotsen.Operations;
using Lotsen.Pinning;
using Lotsen.Tours;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lotsen.Tests;

/// <summary>Covers how <see cref="CloneGitFacts"/> reads git facts from a clone: when a scope runs git, how it brings the commits into the clone, its memo, and its clone use.</summary>
[Trait("Category", "Remap")]
public sealed class CloneGitFactsTests : IDisposable
{
   private readonly GitFixture _fixture = new();
   private readonly CommitId _base;
   private readonly CommitId _head;

   public CloneGitFactsTests()
   {
      byte[] binary = [0x89, 0x50, 0x00, 0x01, 0xFF];
      _base = _fixture.Commit("Base", ("a.txt", "one\ntwo\nthree\n"), ("same.txt", "same\n"));
      _fixture.Checkout("feature", create: true);
      File.WriteAllBytes(Path.Combine(_fixture.OriginPath, "logo.png"), binary);
      _head = _fixture.Commit("Change", ("a.txt", "one\nTWO\nthree\n"), ("added.txt", "new\n"));
      _fixture.Checkout("main");
   }

   public void Dispose() => _fixture.Dispose();

   [Fact]
   public async Task Second_scope_on_facts_that_the_memo_holds_runs_no_git()
   {
      var git = new RecordingGit(_fixture.ScratchPath);
      var facts = new CloneGitFacts(Cache(git), NullLogger<CloneGitFacts>.Instance, FactCacheSize.Default);
      FactKey[] keys = [new FactKey.ChangedPaths(_base, _head), new FactKey.Lines(_head, "a.txt"), new FactKey.Hunks(_base, "a.txt", _head, "a.txt")];
      await using (var first = facts.Open(_fixture.Remote(), [_base, _head], FetchPolicy.Sync))
      {
         await first.ReadAsync(keys, TestContext.Current.CancellationToken);
      }

      var runs = git.Arguments;
      await using var second = facts.Open(_fixture.Remote(), [_base, _head], FetchPolicy.Sync);
      await second.ReadAsync(keys, TestContext.Current.CancellationToken);

      Assert.Equal(runs, git.Arguments);
      Assert.True(second.Known.TryGet(new FactKey.Lines(_head, "a.txt"), out var lines));
      Assert.Equal("TWO", lines!.Line(2));
   }

   [Fact]
   public async Task Scope_runs_no_git_until_its_first_read_and_brings_the_commits_in_once()
   {
      var git = new RecordingGit(_fixture.ScratchPath);
      var facts = new CloneGitFacts(Cache(git), NullLogger<CloneGitFacts>.Instance, FactCacheSize.Default);

      await using var scope = facts.Open(_fixture.Remote(), [_base, _head], FetchPolicy.Sync);
      var beforeRead = git.Arguments;
      await scope.ReadAsync([new FactKey.MergeBaseOf(_head, _base)], TestContext.Current.CancellationToken);
      await scope.ReadAsync([new FactKey.ChangedPaths(_base, _head)], TestContext.Current.CancellationToken);

      Assert.Equal("", beforeRead);
      Assert.Single(Runs(git, "clone"));
      Assert.Empty(Runs(git, "fetch"));
      Assert.True(scope.Known.TryGet(new FactKey.MergeBaseOf(_head, _base), out var mergeBase));
      Assert.Equal(_base, mergeBase);
   }

   /// <summary>A branch that no commit of the scope names appears on the remote after the clone, so only a sync of the remote's branches brings it in.</summary>
   [Theory]
   [InlineData(nameof(FetchPolicy.Sync), 1)]
   [InlineData(nameof(FetchPolicy.WhenMissing), 0)]
   public async Task Scope_syncs_the_clone_or_opens_it_without_a_fetch_as_its_policy_says(string policy, int fetches)
   {
      var git = new RecordingGit(_fixture.ScratchPath);
      var cache = Cache(git);
      (await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, TestContext.Current.CancellationToken)).Dispose();
      _fixture.Checkout("unrelated", create: true);
      _fixture.Commit("Unrelated", ("unrelated.txt", "x\n"));
      _fixture.Checkout("main");
      var before = Runs(git, "fetch").Count;

      await using var scope = new CloneGitFacts(cache, NullLogger<CloneGitFacts>.Instance, FactCacheSize.Default).Open(_fixture.Remote(), [_base, _head], Enum.Parse<FetchPolicy>(policy));
      await scope.ReadAsync([new FactKey.Blob(_head, "a.txt")], TestContext.Current.CancellationToken);

      Assert.Equal(fetches, Runs(git, "fetch").Count - before);
   }

   [Fact]
   public async Task Scope_keeps_its_clone_through_a_prune_until_it_is_disposed()
   {
      var cache = Cache();
      var scope = new CloneGitFacts(cache, NullLogger<CloneGitFacts>.Instance, FactCacheSize.Default).Open(_fixture.Remote(), [_base, _head], FetchPolicy.Sync);
      await scope.ReadAsync([new FactKey.Blob(_head, "a.txt")], TestContext.Current.CancellationToken);

      var whileOpen = await cache.PruneAsync(() => [], null, TestContext.Current.CancellationToken);
      await scope.DisposeAsync();
      var afterDispose = await cache.PruneAsync(() => [], null, TestContext.Current.CancellationToken);

      Assert.Equal((0, 1), (whileOpen, afterDispose));
   }

   /// <summary>A scope that has read nothing holds the clone too, as an iteration update on a memo hit relies on until it stores.</summary>
   [Fact]
   public async Task Scope_without_a_read_keeps_the_clone_of_its_remote_through_a_prune()
   {
      var cache = Cache();
      (await cache.OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, TestContext.Current.CancellationToken)).Dispose();
      await using var scope = new CloneGitFacts(cache, NullLogger<CloneGitFacts>.Instance, FactCacheSize.Default).Open(_fixture.Remote(), [_base, _head], FetchPolicy.WhenMissing);

      Assert.Equal(0, await cache.PruneAsync(() => [], null, TestContext.Current.CancellationToken));
      Assert.Single(_fixture.Clones());
   }

   [Fact]
   public async Task Pin_diff_fact_holds_the_regions_of_a_changed_text_file_and_marks_a_binary_and_an_unchanged_file()
   {
      await using var scope = new CloneGitFacts(Cache(), NullLogger<CloneGitFacts>.Instance, FactCacheSize.Default).Open(_fixture.Remote(), [_base, _head], FetchPolicy.Sync);
      FactKey.PinDiff[] keys = [Diff(FileSide.Head, "a.txt"), Diff(FileSide.Base, "a.txt"), Diff(FileSide.Head, "added.txt"), Diff(FileSide.Head, "logo.png"), Diff(FileSide.Head, "same.txt")];

      await scope.ReadAsync(keys, TestContext.Current.CancellationToken);

      var touches = keys.Select(key => scope.Known.TryGet(key, out var touch) ? touch : null).ToList();
      Assert.Equal((false, true, false), (touches[0]!.Touches(1, 1), touches[0]!.Touches(2, 2), touches[0]!.Touches(3, 3)));
      Assert.True(touches[1]!.Touches(2, 2));
      Assert.True(touches[2]!.Touches(1, 1));
      Assert.IsType<DiffTouch.Throughout>(touches[3]);
      Assert.IsType<DiffTouch.Unchanged>(touches[4]);
   }

   /// <summary>The memo holds one merge base, so the first scope's read of a second one evicts the merge base that the second scope found in the memo.</summary>
   [Fact]
   public async Task Scope_keeps_a_fact_that_it_found_in_the_memo_after_the_memo_evicts_it()
   {
      var facts = new CloneGitFacts(Cache(), NullLogger<CloneGitFacts>.Instance, budget: 64);
      var (kept, evicting) = (new FactKey.MergeBaseOf(_head, _base), new FactKey.MergeBaseOf(_base, _head));
      await using var first = facts.Open(_fixture.Remote(), [_base, _head], FetchPolicy.Sync);
      await first.ReadAsync([kept], TestContext.Current.CancellationToken);
      await using var second = facts.Open(_fixture.Remote(), [_base, _head], FetchPolicy.Sync);
      await second.ReadAsync([kept], TestContext.Current.CancellationToken);

      await first.ReadAsync([evicting], TestContext.Current.CancellationToken);

      Assert.False(facts.Known.TryGet(kept, out _));
      Assert.True(second.Known.TryGet(kept, out var mergeBase));
      Assert.Equal(_base, mergeBase);
   }

   /// <summary>A diff read of the pin downloads its changed files, so the warm after it runs no git, and a second scope learns of the download from the facts.</summary>
   [Fact]
   public async Task Scope_downloads_the_changed_files_of_a_pin_once_and_the_facts_record_the_download()
   {
      var git = new RecordingGit(_fixture.ScratchPath);
      var facts = new CloneGitFacts(Cache(git), NullLogger<CloneGitFacts>.Instance, FactCacheSize.Default);
      await using var scope = facts.Open(_fixture.Remote(), [_base, _head], FetchPolicy.Sync);
      var before = facts.Downloaded(_fixture.Remote().Url, _base, _head);

      await scope.ReadAsync([Diff(FileSide.Head, "a.txt")], TestContext.Current.CancellationToken);
      var afterRead = git.Arguments;
      await scope.WarmAsync(_base, _head, TestContext.Current.CancellationToken);

      Assert.Equal(afterRead, git.Arguments);
      Assert.Equal((false, true), (before, facts.Downloaded(_fixture.Remote().Url, _base, _head)));
      Assert.False(facts.Downloaded(_fixture.Remote().Url, _head, _base));
   }

   /// <summary>A scope downloads the pin's changed files, a prune removes the clone once the scope ends, and the next scope clones the remote again without the files.</summary>
   [Fact]
   public async Task Download_record_ends_when_a_prune_removes_the_clone()
   {
      var cache = Cache();
      var facts = new CloneGitFacts(cache, NullLogger<CloneGitFacts>.Instance, FactCacheSize.Default);
      await using (var first = facts.Open(_fixture.Remote(), [_base, _head], FetchPolicy.Sync))
      {
         await first.ReadAsync([Diff(FileSide.Head, "a.txt")], TestContext.Current.CancellationToken);
      }

      var beforePrune = facts.Downloaded(_fixture.Remote().Url, _base, _head);
      Assert.Equal(1, await cache.PruneAsync(() => [], null, TestContext.Current.CancellationToken));
      await using var second = facts.Open(_fixture.Remote(), [_base, _head], FetchPolicy.Sync);
      await second.ReadAsync([new FactKey.MergeBaseOf(_head, _base)], TestContext.Current.CancellationToken);

      Assert.Equal((true, false), (beforePrune, facts.Downloaded(_fixture.Remote().Url, _base, _head)));
   }

   /// <summary>The git double fails the first diff-tree of the scope alone, which is the download of the pin's changed files when the read downloads them before it lists them.</summary>
   [Fact]
   public async Task Pin_diff_read_downloads_the_changed_files_of_its_pin_first_and_logs_a_failed_download_without_failing()
   {
      var failed = 0;
      var git = GitDouble.Options(run => run.Subcommand == "diff-tree" && Interlocked.Exchange(ref failed, 1) == 0 ? Task.FromResult(GitRun.Exit(128, "fatal: the download broke\n")) : run.GitAsync());
      var logs = new RecordingLoggerProvider();
      var cache = _fixture.NewCache(git);
      await using var scope = new CloneGitFacts(cache, logs.CreateLogger<CloneGitFacts>(), FactCacheSize.Default).Open(_fixture.Remote(), [_base, _head], FetchPolicy.Sync);

      await scope.ReadAsync([Diff(FileSide.Head, "a.txt")], TestContext.Current.CancellationToken);

      Assert.True(scope.Known.TryGet(Diff(FileSide.Head, "a.txt"), out var touch));
      Assert.True(touch!.Touches(2, 2));
      Assert.Contains(logs.Entries, entry => entry.Level == LogLevel.Warning && entry.Line.Contains("the download broke", StringComparison.Ordinal));
   }

   [Fact]
   public void Memo_evicts_the_least_recently_used_facts_past_its_budget_and_keeps_none_larger_than_it()
   {
      var memo = new FactMemo(budget: 3 * 64);
      FactKey[] keys = [new FactKey.MergeBaseOf(_head, _base), new FactKey.MergeBaseOf(_base, _head), new FactKey.MergeBaseOf(_head, _head)];
      memo.Add(keys[0], _base);
      memo.Add(keys[1], _base);
      memo.Add(keys[2], _base);
      Assert.True(memo.TryFind(keys[0], out _));

      memo.Add(new FactKey.MergeBaseOf(_base, _base), _base);
      memo.Add(new FactKey.Lines(_head, "a.txt"), new FileLines(new string('x', 1000)));

      Assert.Equal(3, memo.Count);
      Assert.True(memo.TryFind(keys[0], out _));
      Assert.False(memo.TryFind(keys[1], out _));
      Assert.False(memo.TryFind(new FactKey.Lines(_head, "a.txt"), out _));
   }

   /// <summary>The lines of one line estimate 64 bytes, plus two for each character and four for the line, so 500,000 characters estimate 1,000,068 bytes, under 1 MiB, and 600,000 estimate 1,200,068.</summary>
   [Fact]
   public void Memo_keeps_facts_up_to_the_configured_fact_cache_size()
   {
      Assert.True(FactCacheSize.TryParse("1", out var size));
      var facts = new CloneGitFacts(Cache(), NullLogger<CloneGitFacts>.Instance, size);
      var memo = Assert.IsType<FactMemo>(facts.Known);
      var (fits, tooLarge) = (new FactKey.Lines(_head, "fits.txt"), new FactKey.Lines(_head, "too-large.txt"));

      memo.Add(fits, new FileLines(new string('x', 500_000)));
      memo.Add(tooLarge, new FileLines(new string('x', 600_000)));

      Assert.True(memo.TryFind(fits, out _));
      Assert.False(memo.TryFind(tooLarge, out _));
   }

   private static List<string> Runs(RecordingGit git, string subcommand) => [.. git.Arguments.Split('\n').Where(line => line.Split(' ').Contains(subcommand))];

   private FactKey.PinDiff Diff(FileSide side, string path) => new(_base, _head, side, path);

   private CloneCache Cache(RecordingGit? git = null) => _fixture.NewCache(git?.Options);
}
