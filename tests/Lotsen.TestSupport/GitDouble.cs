using System.Diagnostics;
using System.Text;
using Lotsen.Git;
using Lotsen.Processes;

namespace Lotsen.Tests;

/// <summary>Builds git options that hand each run of git to a test's step in the test's own process, so a test sees, holds or fails a run without a process in git's place.</summary>
internal static class GitDouble
{
   /// <summary>Returns options whose every git run ends with the step's outcome. The step runs git, if at all, with <see cref="GitRun.GitAsync"/>.</summary>
   public static GitOptions Options(Func<GitRun, Task<ProcessOutcome>> step) => new() { Run = (command, cancellationToken) => step(new GitRun(command, cancellationToken)) };
}

/// <summary>One run of git as a <see cref="GitDouble"/> step sees it: the command, and the time and the cancellation that the run shares with git.</summary>
/// <remarks>A hold or a delay counts against the command's timeout, so a step that outlasts the timeout or the cancellation ends as <see cref="ProcessRunner"/> ends a process then.</remarks>
internal sealed class GitRun
{
   private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

   private readonly Stopwatch _elapsed = Stopwatch.StartNew();

   public GitRun(ProcessCommand command, CancellationToken cancellationToken)
   {
      Command = command;
      CancellationToken = cancellationToken;
   }

   public ProcessCommand Command { get; }

   public CancellationToken CancellationToken { get; }

   /// <summary>The git subcommand, which is the first argument, or an empty string for a run without arguments.</summary>
   public string Subcommand => Command.Arguments.Count > 0 ? Command.Arguments[0] : "";

   private TimeSpan Left => Command.Timeout - _elapsed.Elapsed;

   /// <summary>Returns the outcome of a git that exited with the code, the output and the error, without a run of git.</summary>
   public static ProcessOutcome Exit(int code, string error = "", string output = "") => new ProcessOutcome.Exited(code, Encoding.UTF8.GetBytes(output), error);

   /// <summary>Runs git with the run's command, or with the given one, within the time that the run has left.</summary>
   public Task<ProcessOutcome> GitAsync(ProcessCommand? command = null) =>
      Left <= TimeSpan.Zero ? Task.FromResult<ProcessOutcome>(new ProcessOutcome.TimedOut()) : ProcessRunner.RunAsync((command ?? Command) with { Timeout = Left }, CancellationToken);

   /// <summary>Waits until <paramref name="released"/> returns true.</summary>
   /// <returns>Null once released, or the outcome of a run whose timeout or cancellation came first.</returns>
   public async Task<ProcessOutcome?> HoldAsync(Func<bool> released)
   {
      while (!released())
      {
         var left = Left;
         if (left <= TimeSpan.Zero)
         {
            return new ProcessOutcome.TimedOut();
         }

         try
         {
            await Task.Delay(left < PollInterval ? left : PollInterval, CancellationToken);
         }
         catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
         {
            return new ProcessOutcome.Canceled();
         }
      }

      return null;
   }

   /// <summary>Waits for the length, as a <c>sleep</c> before git would.</summary>
   /// <returns>Null after the length, or the outcome of a run whose timeout or cancellation came first.</returns>
   public Task<ProcessOutcome?> DelayAsync(TimeSpan length)
   {
      var end = _elapsed.Elapsed + length;
      return HoldAsync(() => _elapsed.Elapsed >= end);
   }
}
