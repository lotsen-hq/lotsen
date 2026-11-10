using System.Text;
using Lotsen.Git;
using Lotsen.Processes;

namespace Lotsen.Tests;

/// <summary>
/// A <see cref="GitDouble"/> that records each git run's arguments and whether its environment carries a credential answer, and makes git trace every command it runs, so the argument lists of
/// git's own child processes are recorded too.
/// </summary>
internal sealed class RecordingGit
{
   private readonly Lock _lock = new();
   private readonly StringBuilder _arguments = new();
   private int _runsWithCredential;

   public RecordingGit(string directory)
   {
      TraceLog = Path.Combine(directory, "trace.log");
      Options = GitDouble.Options(RunAsync);
   }

   /// <summary>The git options whose runs this records.</summary>
   public GitOptions Options { get; }

   /// <summary>The argument list of every git run so far, one per line, its arguments separated by spaces.</summary>
   public string Arguments
   {
      get
      {
         lock (_lock)
         {
            return _arguments.ToString();
         }
      }
   }

   /// <summary>Git's trace of every command it ran, with the argument lists of its child processes.</summary>
   public string Trace => File.Exists(TraceLog) ? File.ReadAllText(TraceLog) : "";

   /// <summary>The number of fetches that git ran to download file contents that the clone lacked, as <see cref="ContentFetchesIn"/> counts them.</summary>
   public int ContentFetches => ContentFetchesIn(Trace);

   /// <summary>The number of runs whose environment passed git's credential helper an answer.</summary>
   public int RunsWithCredential
   {
      get
      {
         lock (_lock)
         {
            return _runsWithCredential;
         }
      }
   }

   private string TraceLog { get; }

   /// <summary>Returns the number of fetches of file contents that a trace of git holds.</summary>
   /// <remarks>Git starts such a fetch with <c>--stdin</c>, and the clone cache's own fetches never pass it.</remarks>
   public static int ContentFetchesIn(string trace) =>
      trace.Split('\n').Count(line => line.Contains("built-in: git fetch ", StringComparison.Ordinal) && line.Contains(" --stdin", StringComparison.Ordinal));

   // GitRunner passes the helper's answer in a GIT_CONFIG_VALUE_n variable whose first line is the authtype capability.
   private static bool CarriesCredentialAnswer(ProcessCommand command) =>
      command.Environment.Any(variable => variable.Key.StartsWith("GIT_CONFIG_VALUE_", StringComparison.Ordinal) && variable.Value?.Split('\n')[0] == "capability[]=authtype");

   private Task<ProcessOutcome> RunAsync(GitRun run)
   {
      lock (_lock)
      {
         _arguments.Append(string.Join(' ', run.Command.Arguments)).Append('\n');
         if (CarriesCredentialAnswer(run.Command))
         {
            _runsWithCredential++;
         }
      }

      return run.GitAsync(run.Command with { Environment = new Dictionary<string, string?>(run.Command.Environment) { ["GIT_TRACE"] = TraceLog } });
   }
}
