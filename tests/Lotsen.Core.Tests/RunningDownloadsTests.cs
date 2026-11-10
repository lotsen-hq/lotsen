using Lotsen.Operations;

namespace Lotsen.Tests;

/// <summary>Covers how a request for files joins the downloads in progress, and how a download leaves the table when it ends, whichever way it ends.</summary>
[Trait("Category", "Git")]
public sealed class RunningDownloadsTests
{
   private readonly RunningDownloads<string> _downloads = new();

   [Fact]
   public void Claim_with_no_download_in_progress_starts_one_of_each_distinct_key()
   {
      var (running, started) = _downloads.Claim(["a", "b", "a"]);

      Assert.Empty(running);
      Assert.Equal(["a", "b"], started!.Keys);
   }

   [Fact]
   public void Claim_of_keys_that_a_download_in_progress_covers_joins_it_and_starts_none()
   {
      var first = _downloads.Claim(["a", "b"]).Started!;

      var (running, started) = _downloads.Claim(["b", "a"]);

      Assert.Equal([first], running);
      Assert.Null(started);
   }

   [Fact]
   public void Claim_that_overlaps_downloads_in_progress_joins_each_and_starts_one_for_the_rest()
   {
      var first = _downloads.Claim(["a"]).Started!;
      var second = _downloads.Claim(["b"]).Started!;

      var (running, started) = _downloads.Claim(["a", "b", "c"]);

      Assert.Equal(new HashSet<RunningDownloads<string>.Download> { first, second }, [.. running]);
      Assert.Equal(["c"], started!.Keys);
   }

   [Fact]
   public async Task Claim_after_a_download_ended_starts_another()
   {
      var first = _downloads.Claim(["a"]).Started!;
      first.End();

      var (running, started) = _downloads.Claim(["a"]);

      Assert.Empty(running);
      Assert.Equal(["a"], started!.Keys);
      Assert.Null(await first.Ended);
   }

   [Fact]
   public async Task Canceled_download_leaves_the_table_and_its_waiters_see_the_cancellation()
   {
      var first = _downloads.Claim(["a"]).Started!;
      var joined = Assert.Single(_downloads.Claim(["a"]).Running);

      first.Cancel();

      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunningDownloads<string>.WaitAsync([joined], TestContext.Current.CancellationToken));
      Assert.NotNull(_downloads.Claim(["a"]).Started);
   }

   /// <summary>The failure reaches every waiter as the exception that the download threw.</summary>
   [Fact]
   public async Task Failure_of_a_download_reaches_a_waiter_that_joined_it()
   {
      var first = _downloads.Claim(["a"]).Started!;
      var joined = _downloads.Claim(["a", "b"]);
      var waiting = RunningDownloads<string>.WaitAsync([.. joined.Running], TestContext.Current.CancellationToken);

      first.End(new InvalidOperationException("broken"));
      joined.Started!.End();

      var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => waiting);
      Assert.Equal("broken", thrown.Message);
      Assert.Empty(_downloads.Claim(["a", "b"]).Running);
   }

   [Fact]
   public async Task Ended_download_stays_as_it_ended_when_it_is_canceled_after()
   {
      var first = _downloads.Claim(["a"]).Started!;

      first.End();
      first.Cancel();

      Assert.Null(await first.Ended);
   }

   /// <summary>A download that ended once ends again on its way out, as a job's cleanup does, after another download took its key.</summary>
   [Fact]
   public void Second_end_of_a_download_leaves_the_download_that_took_its_key()
   {
      var first = _downloads.Claim(["a"]).Started!;
      first.End();
      var second = _downloads.Claim(["a"]).Started!;

      first.Cancel();

      Assert.Equal([second], _downloads.Claim(["a"]).Running);
   }

   [Fact]
   public async Task Wait_stops_at_the_token_while_the_download_goes_on()
   {
      var first = _downloads.Claim(["a"]).Started!;
      using var canceled = new CancellationTokenSource();
      await canceled.CancelAsync();

      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunningDownloads<string>.WaitAsync([first], canceled.Token));

      Assert.False(first.Ended.IsCompleted);
      Assert.Equal([first], _downloads.Claim(["a"]).Running);
   }
}
