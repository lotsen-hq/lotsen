using Lotsen.Sessions;

namespace Lotsen.Tests;

/// <summary>Waits for a condition that another process or thread makes true.</summary>
internal static class Poll
{
   private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(25);

   /// <summary>Returns the first value that the probe returns other than null, and fails the test when none comes within the timeout.</summary>
   public static async Task<T> UntilAsync<T>(Func<T?> probe, TimeSpan timeout, Func<string> failure)
      where T : class
   {
      var deadline = DateTime.UtcNow + timeout;
      while (true)
      {
         if (probe() is { } value)
         {
            return value;
         }

         Assert.True(DateTime.UtcNow < deadline, failure());
         await Task.Delay(Interval, TestContext.Current.CancellationToken);
      }
   }

   /// <summary>Completes once the session's end is recorded, as <see cref="AgentSession.WhenEnded"/> tells, and fails the test when it is not within the timeout.</summary>
   public static async Task EndedAsync(AgentSession session, TimeSpan timeout, string failure)
   {
      var finished = await Task.WhenAny(session.WhenEnded, Task.Delay(timeout, TestContext.Current.CancellationToken));
      Assert.True(finished == session.WhenEnded, failure);
   }
}
