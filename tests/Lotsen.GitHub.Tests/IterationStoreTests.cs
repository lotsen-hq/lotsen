using Lotsen.Git;
using Lotsen.GitHub;
using Lotsen.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lotsen.Tests;

/// <summary>Covers how the iteration store numbers a GitHub pull request's iterations from the heads it is given, and how it keeps its records in the data directory.</summary>
[Trait("Category", "GitHub")]
public sealed class IterationStoreTests : IDisposable
{
   private static readonly GitHubPullRequest PullRequest = Parse("https://github.com/octo-org/hello-world/pull/42");
   private static readonly CommitId First = Commit('1');
   private static readonly CommitId Second = Commit('2');
   private static readonly CommitId Third = Commit('3');

   private readonly string _root = Directory.CreateTempSubdirectory("lotsen-tests-").FullName;
   private readonly DataDirectory _dataDirectory;

   public IterationStoreTests()
   {
      _dataDirectory = new DataDirectory(Path.Combine(_root, "data"));
   }

   [Fact]
   public void First_head_is_iteration_one()
   {
      Assert.Equal(1, NewStore().IterationNumber(PullRequest, First));
   }

   [Fact]
   public void Same_head_keeps_the_number()
   {
      var store = NewStore();
      store.IterationNumber(PullRequest, First);

      Assert.Equal(1, store.IterationNumber(PullRequest, First));
      Assert.Equal(1, NewStore().IterationNumber(PullRequest, First));
   }

   [Fact]
   public void New_head_raises_the_number_by_one()
   {
      NewStore().IterationNumber(PullRequest, First);

      Assert.Equal(2, NewStore().IterationNumber(PullRequest, Second));
      Assert.Equal(3, NewStore().IterationNumber(PullRequest, Third));
   }

   [Fact]
   public void Return_to_an_earlier_head_raises_the_number()
   {
      var store = NewStore();
      store.IterationNumber(PullRequest, First);
      store.IterationNumber(PullRequest, Second);

      Assert.Equal(3, store.IterationNumber(PullRequest, First));
   }

   [Fact]
   public void Each_pull_request_has_its_own_count()
   {
      var store = NewStore();
      var other = Parse("https://github.com/octo-org/hello-world/pull/43");
      var otherRepository = Parse("https://github.com/octo-org/other/pull/42");
      store.IterationNumber(PullRequest, First);
      store.IterationNumber(PullRequest, Second);

      Assert.Equal(1, store.IterationNumber(other, Second));
      Assert.Equal(1, store.IterationNumber(otherRepository, Second));
      Assert.Equal(2, store.IterationNumber(PullRequest, Second));
   }

   /// <summary>The owner and the repository of one pull request compare in any letter case, as <see cref="GitHubPullRequest.IsSame"/> does.</summary>
   [Fact]
   public void Pull_request_in_another_letter_case_shares_the_count()
   {
      var store = NewStore();
      store.IterationNumber(PullRequest, First);

      Assert.Equal(2, store.IterationNumber(Parse("https://github.com/Octo-Org/Hello-World/pull/42"), Second));
   }

   [Fact]
   public void Deleted_record_restarts_the_numbering_at_one()
   {
      var store = NewStore();
      store.IterationNumber(PullRequest, First);
      store.IterationNumber(PullRequest, Second);

      File.Delete(store.PathOf(PullRequest));

      Assert.Equal(1, store.IterationNumber(PullRequest, Second));
      Assert.Equal(2, store.IterationNumber(PullRequest, Third));
   }

   [Theory]
   [InlineData("")]
   [InlineData("{")]
   [InlineData("null")]
   [InlineData("""{"head":"not-a-commit","count":4}""")]
   [InlineData("""{"head":"2222222222222222222222222222222222222222","count":0}""")]
   [InlineData("""{"count":4}""")]
   [InlineData("""{"head":"2222222222222222222222222222222222222222","count":2147483647}""")]
   [InlineData("""{"head":"ABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCD","count":4}""")]
   public void Malformed_record_restarts_the_numbering_at_one(string content)
   {
      var store = NewStore();
      store.IterationNumber(PullRequest, First);
      File.WriteAllText(store.PathOf(PullRequest), content);

      Assert.Equal(1, store.IterationNumber(PullRequest, Second));
      Assert.Equal(2, NewStore().IterationNumber(PullRequest, Third));
   }

   /// <summary>The highest count that a new head can still raise.</summary>
   [Fact]
   public void Record_one_below_the_highest_count_is_raised()
   {
      var store = NewStore();
      store.IterationNumber(PullRequest, First);
      File.WriteAllText(store.PathOf(PullRequest), $$"""{"head":"{{First}}","count":{{int.MaxValue - 1}}}""");

      Assert.Equal(int.MaxValue - 1, store.IterationNumber(PullRequest, First));
      Assert.Equal(int.MaxValue, store.IterationNumber(PullRequest, Second));
   }

   /// <summary>A record that exists but cannot be opened may hold a count that a later call can still read, so the call must not replace it.</summary>
   [Theory]
   [InlineData('1')]
   [InlineData('2')]
   public void Record_that_cannot_be_opened_fails_the_call_and_stays_as_it_was(char headDigit)
   {
      if (OperatingSystem.IsWindows())
      {
         Assert.Skip("Unix file modes do not apply on Windows.");
         return;
      }

      var store = NewStore();
      store.IterationNumber(PullRequest, First);
      store.IterationNumber(PullRequest, Second);
      var record = store.PathOf(PullRequest);
      var before = File.ReadAllBytes(record);
      File.SetUnixFileMode(record, UnixFileMode.None);
      try
      {
         Assert.Throws<UnauthorizedAccessException>(() => store.IterationNumber(PullRequest, Commit(headDigit)));
      }
      finally
      {
         File.SetUnixFileMode(record, UnixFileMode.UserRead | UnixFileMode.UserWrite);
      }

      Assert.Equal(before, File.ReadAllBytes(record));
      Assert.Equal(3, store.IterationNumber(PullRequest, Third));
   }

   /// <summary>
   /// Each call has a store of its own, so only a gate that every store shares keeps the writes apart. Each writer has a thread of its own, so the barrier opens without a wait for the thread
   /// pool to grow.
   /// </summary>
   [Fact]
   public async Task Concurrent_new_heads_of_one_pull_request_each_get_their_own_number()
   {
      const int writers = 32;
      using var start = new Barrier(writers);
      var heads = Enumerable.Range(0, writers).Select(index => CommitId.Parse(index.ToString("x40", System.Globalization.CultureInfo.InvariantCulture))).ToList();

      var numbers = await Task.WhenAll(heads.Select(head => Task.Factory.StartNew(
         () =>
         {
            start.SignalAndWait(TestContext.Current.CancellationToken);
            return NewStore().IterationNumber(PullRequest, head);
         },
         TestContext.Current.CancellationToken,
         TaskCreationOptions.LongRunning,
         TaskScheduler.Default)));

      Assert.Equal(Enumerable.Range(1, writers), numbers.Order());
      Assert.Equal(writers + 1, NewStore().IterationNumber(PullRequest, First));
   }

   [Fact]
   public void Record_and_its_folders_are_readable_by_the_user_only()
   {
      if (OperatingSystem.IsWindows())
      {
         Assert.Skip("Unix file modes do not apply on Windows.");
         return;
      }

      var store = NewStore();
      store.IterationNumber(PullRequest, First);

      const UnixFileMode userOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
      var record = store.PathOf(PullRequest);
      var iterations = Path.GetDirectoryName(record)!;
      Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(record));
      Assert.Equal(userOnlyDirectory, File.GetUnixFileMode(iterations));
      Assert.Equal(userOnlyDirectory, File.GetUnixFileMode(Path.GetDirectoryName(iterations)!));
      Assert.Equal(userOnlyDirectory, File.GetUnixFileMode(_dataDirectory.Path));
   }

   [Fact]
   public void Record_lies_in_a_folder_of_its_own_under_the_data_directory_and_holds_only_the_head_and_the_count()
   {
      var store = NewStore();
      store.IterationNumber(PullRequest, First);
      store.IterationNumber(PullRequest, Second);

      var record = store.PathOf(PullRequest);
      Assert.Equal(Path.Combine(_dataDirectory.Path, "github", "iterations"), Path.GetDirectoryName(record));
      Assert.Equal($$"""{"head":"{{Second}}","count":2}""", File.ReadAllText(record));
      Assert.Equal([record], Directory.GetFiles(Path.GetDirectoryName(record)!));
   }

   public void Dispose() => TestDirectories.Delete(_root, TimeSpan.FromSeconds(5));

   private GitHubIterationStore NewStore() => new(_dataDirectory, NullLogger<GitHubIterationStore>.Instance);

   private static CommitId Commit(char digit) => CommitId.Parse(new string(digit, 40));

   private static GitHubPullRequest Parse(string url) => GitHubPullRequest.TryParse(url, out var pullRequest) ? pullRequest : throw new ArgumentException($"'{url}' is no pull request URL.");
}
