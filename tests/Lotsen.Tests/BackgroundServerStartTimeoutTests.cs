using Lotsen.Hosting;

namespace Lotsen.Tests;

/// <summary>Covers the tool call that fails when the background server that the adapter starts does not answer in time, as separate processes.</summary>
/// <remarks>xunit runs the tests of one class one after another (https://xunit.net/docs/running-tests-in-parallel), so this slow test has a class of its own to run beside <see cref="BackgroundServerTests"/>.</remarks>
[Trait("Category", "Lifecycle")]
public sealed class BackgroundServerStartTimeoutTests : IDisposable
{
   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

   private readonly DataDirectory _dataDirectory = new(Path.Combine(Directory.CreateTempSubdirectory("lotsen-tests-").FullName, "data"));

   /// <summary>
   /// The test holds the claim lock, so every background server that the adapter starts exits at once and none answers. The adapter's start timeout is 2 seconds, so the start at launch and the
   /// call's start each time out soon.
   /// </summary>
   [Fact]
   public async Task Start_that_does_not_answer_in_time_fails_the_tool_call_naming_the_log()
   {
      using var claim = ClaimLock.TryTake(_dataDirectory)!;
      var startTimeout = TimeSpan.FromSeconds(2);
      using var adapter = AdapterProcess.Start(_dataDirectory.Path, AdapterProcess.WithStartTimeout(startTimeout));
      await adapter.InitializeAsync();

      // The adapter starts a server unasked at launch. Wait until that start times out, so the call waits for its own start alone.
      var timedOut = BackgroundServer.StartTimeoutMessage(_dataDirectory, startTimeout, LotsenCommand.Installed);
      await Poll.UntilAsync(() => adapter.Errors.Contains(timedOut, StringComparison.Ordinal) ? "" : null, startTimeout + Timeout, () => $"The start at launch did not time out:\n{adapter.Output}");

      var result = await adapter.CallToolAsync(2, "open_ui");

      Assert.True(result.IsError);
      Assert.Contains("within 2 seconds", result.Text);
      Assert.Contains(_dataDirectory.LogFilePath, result.Text);
   }

   public void Dispose() => BackgroundServerProcesses.CleanUp(_dataDirectory);
}
