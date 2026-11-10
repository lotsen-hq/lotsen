using Lotsen.Hosting;

namespace Lotsen.Tests;

/// <summary>Covers an agent session that opens during the grace period of a background server, and so keeps that server running, as separate processes.</summary>
/// <remarks>xunit runs the tests of one class one after another (https://xunit.net/docs/running-tests-in-parallel), so this slow test has a class of its own to run beside <see cref="BackgroundServerTests"/>.</remarks>
[Trait("Category", "Lifecycle")]
public sealed class BackgroundServerGracePeriodRaceTests : IDisposable
{
   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

   private readonly DataDirectory _dataDirectory = new(Path.Combine(Directory.CreateTempSubdirectory("lotsen-tests-").FullName, "data"));

   /// <summary>Race: the second adapter attaches while the grace period that the first one's exit started still runs.</summary>
   [Fact]
   public async Task Session_that_opens_during_the_grace_period_keeps_the_background_server_running()
   {
      var grace = TimeSpan.FromSeconds(5);
      using var first = AdapterProcess.Start(_dataDirectory.Path, AdapterProcess.WithGracePeriod(grace));
      await first.InitializeAsync();
      await first.ReachServerAsync(2);
      var server = StateFile.TryRead(_dataDirectory)!;
      first.CloseInput();
      Assert.True(await first.WaitForExitAsync(Timeout), $"The first adapter did not exit:\n{first.Output}");
      var firstExited = DateTime.UtcNow;

      using var second = AdapterProcess.Start(_dataDirectory.Path, AdapterProcess.WithGracePeriod(grace));
      await second.InitializeAsync();
      await second.ReachServerAsync(2);
      var attached = DateTime.UtcNow - firstExited;
      await Task.Delay(firstExited + grace + TimeSpan.FromSeconds(2) - DateTime.UtcNow, TestContext.Current.CancellationToken);

      Assert.True(attached < grace, $"The second adapter attached {attached.TotalSeconds:F1} s after the first one exited, after the grace period, so the test shows nothing.");
      Assert.True(BackgroundServerProcesses.IsRunning(server.Pid), "The background server exited although a session opened during its grace period.");
      Assert.Equal(server, StateFile.TryRead(_dataDirectory));
      second.CloseInput();
      await BackgroundServerProcesses.WaitForExitAsync(server.Pid, grace + Timeout, () => "The background server did not exit after the second adapter closed.");
   }

   public void Dispose() => BackgroundServerProcesses.CleanUp(_dataDirectory);
}
