using Lotsen.Operations;
using Lotsen.Tours;

namespace Lotsen.Tests;

/// <summary>Builds hooks for <see cref="TourWriter.Hook"/> that run at the end of the git phase of the named operations and pass every other write.</summary>
internal static class WriterHooks
{
   /// <summary>Returns a hook that awaits <paramref name="run"/> after the git phase of each named operation.</summary>
   public static Func<WritePoint, ValueTask> AfterGit(Func<TourId, Task> run, params string[] operations) =>
      point => operations.Contains(point.Operation) ? new ValueTask(run(point.TourId)) : ValueTask.CompletedTask;

   /// <summary>Returns a hook that calls <paramref name="run"/> after the git phase of each named operation.</summary>
   public static Func<WritePoint, ValueTask> AfterGit(Action<TourId> run, params string[] operations) => AfterGit(id =>
   {
      run(id);
      return Task.CompletedTask;
   }, operations);
}
