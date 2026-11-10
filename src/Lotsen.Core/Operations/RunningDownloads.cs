using System.Runtime.ExceptionServices;

namespace Lotsen.Operations;

/// <summary>The downloads in progress, each with the keys of the files that it downloads, so a request for a file that one of them downloads waits for it instead of starting another.</summary>
/// <remarks>A download leaves the table when it ends, whichever way it ends, so a later request for its files starts a new one.</remarks>
internal sealed class RunningDownloads<TKey>
   where TKey : notnull
{
   private readonly Lock _lock = new();
   private readonly Dictionary<TKey, Download> _running;

   public RunningDownloads()
   {
      _running = [];
   }

   /// <summary>Returns the downloads in progress that cover some of the keys, and a new download of the keys that none of them covers.</summary>
   /// <returns>
   /// The downloads in progress, and the new download, or null when those cover every key. The caller runs the new download and ends it with <see cref="Download.End"/> or
   /// <see cref="Download.Cancel"/>.
   /// </returns>
   public (IReadOnlyCollection<Download> Running, Download? Started) Claim(IEnumerable<TKey> keys)
   {
      lock (_lock)
      {
         var running = new HashSet<Download>();
         var rest = new List<TKey>();
         foreach (var key in keys.Distinct())
         {
            if (_running.TryGetValue(key, out var download))
            {
               running.Add(download);
            }
            else
            {
               rest.Add(key);
            }
         }

         if (rest.Count == 0)
         {
            return (running, null);
         }

         var started = new Download(this, rest);
         foreach (var key in rest)
         {
            _running[key] = started;
         }

         return (running, started);
      }
   }

   /// <summary>Waits until every download ends, then rethrows the first failure among them.</summary>
   /// <param name="downloads">The downloads.</param>
   /// <param name="cancellationToken">Stops the wait, but not the downloads.</param>
   /// <exception cref="OperationCanceledException">The token was canceled, or a download was canceled.</exception>
   public static async Task WaitAsync(IEnumerable<Download> downloads, CancellationToken cancellationToken)
   {
      var failures = await Task.WhenAll(downloads.Select(download => download.Ended)).WaitAsync(cancellationToken);
      failures.FirstOrDefault(failure => failure is not null)?.Throw();
   }

   // Removes only the keys that still name the download, because another download takes a key once this one left the table.
   private void Remove(Download download)
   {
      lock (_lock)
      {
         foreach (var key in download.Keys)
         {
            if (_running.TryGetValue(key, out var current) && current == download)
            {
               _running.Remove(key);
            }
         }
      }
   }

   /// <summary>One download of the files that its keys name.</summary>
   internal sealed class Download
   {
      private readonly RunningDownloads<TKey> _table;
      private readonly TaskCompletionSource<ExceptionDispatchInfo?> _outcome;

      internal Download(RunningDownloads<TKey> table, IReadOnlyList<TKey> keys)
      {
         _table = table;
         _outcome = new TaskCompletionSource<ExceptionDispatchInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
         Keys = keys;
      }

      /// <summary>The keys of the files that the download fetches, each once.</summary>
      public IReadOnlyList<TKey> Keys { get; }

      /// <summary>Completes when the download ends: with null, with the failure that <see cref="WaitAsync"/> rethrows, or canceled.</summary>
      /// <remarks>It never faults, so a waiter that stopped waiting leaves no unobserved task exception behind.</remarks>
      public Task<ExceptionDispatchInfo?> Ended => _outcome.Task;

      /// <summary>Takes the download out of the table, then ends it with the failure, or with none. A download that ended already keeps its end.</summary>
      public void End(Exception? failure = null)
      {
         _table.Remove(this);
         _outcome.TrySetResult(failure is null ? null : ExceptionDispatchInfo.Capture(failure));
      }

      /// <summary>Takes the download out of the table, then ends it canceled. A download that ended already keeps its end.</summary>
      public void Cancel()
      {
         _table.Remove(this);
         _outcome.TrySetCanceled();
      }
   }
}
