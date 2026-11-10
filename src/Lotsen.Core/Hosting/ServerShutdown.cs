using System.Diagnostics;
using Lotsen.Client;
using Lotsen.Server;

namespace Lotsen.Hosting;

/// <summary>
/// Shuts down the server that runs on a data directory, foreground or background, through its shutdown route, and waits until its process has exited.
/// </summary>
/// <remarks>
/// <para>
/// The route works the same on every OS, so no signal is needed. The server stops as on Ctrl-C: it removes its state file and releases the claim lock before its process exits.
/// </para>
/// <para>
/// The shutdown acts on what <see cref="ServerDiscovery"/> finds. It waits for the state file of a server that is starting. It sends the server token only to a server that answers as
/// lotsen or to the claim lock's holder, so a listener on the port of a stale state file never receives it. It leaves a stale state file in place, for the next server to delete once
/// it holds the claim lock.
/// </para>
/// </remarks>
internal static class ServerShutdown
{
   /// <summary>How long <see cref="StopAsync"/> waits for a server to write its state file, and then for its process to exit.</summary>
   /// <remarks>It is three times the server's <see cref="LotsenServer.ShutdownTimeout"/>, so a server whose stop waits that long for its requests in flight still exits within it.</remarks>
   public static readonly TimeSpan Timeout = LotsenServer.ShutdownTimeout * 3;

   private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

   /// <summary>Asks the server on the data directory to shut down, and waits until its process has exited.</summary>
   /// <param name="timeout">How long to wait in all, for a starting server's state file, its answer and its exit.</param>
   /// <returns>The process ID of the server that exited, or null when no server runs on the data directory.</returns>
   /// <exception cref="ServerShutdownException">The server refused, did not answer or did not exit within the timeout. The message names the server's process ID and what to do.</exception>
   public static async Task<int?> StopAsync(DataDirectory dataDirectory, TimeSpan timeout, CancellationToken cancellationToken)
   {
      var discovery = new ServerDiscovery(dataDirectory);
      var elapsed = Stopwatch.StartNew();
      while (true)
      {
         var (state, starting, answered) = await discovery.FindAsync(ProbeTimeout(timeout, elapsed), cancellationToken) switch
         {
            ServerAnswer.None or ServerAnswer.Stale or ServerAnswer.Foreign => ((ServerState?)null, false, false),
            ServerAnswer.Starting => (null, true, false),
            ServerAnswer.Running running => (running.State, false, true),
            ServerAnswer.Unreachable unreachable => (unreachable.State, false, false),
         };

         if (state is not null)
         {
            return await ShutdownAsync(discovery, state, answered, timeout, elapsed, cancellationToken);
         }

         if (!starting)
         {
            return null;
         }

         if (elapsed.Elapsed >= timeout)
         {
            throw new ServerShutdownException(
               $"A lotsen process holds the data directory {dataDirectory.Path}, but wrote no state file within {timeout.TotalSeconds:0} seconds. Run `lotsen stop` again once it has started.");
         }

         await Task.Delay(PollInterval, cancellationToken);
      }
   }

   // Returns null when the failed request shows that no server runs.
   private static async Task<int?> ShutdownAsync(ServerDiscovery discovery, ServerState state, bool answeredAsLotsen, TimeSpan timeout, Stopwatch elapsed, CancellationToken cancellationToken)
   {
      var pid = state.Pid;
      using (var client = new ServerClient(state))
      using (var answer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
      {
         answer.CancelAfter(Remaining(timeout, elapsed));
         try
         {
            await client.ShutdownAsync(answer.Token);
         }
         catch (HttpRequestException exception) when (exception.StatusCode is null)
         {
            // No HTTP server answers on the port, so a server that answered as lotsen has closed its listener and is stopping. Any other process is waited for only if the claim lock is
            // still held for this state file, because a lock probe of another process can make a free lock read as held, and the process may then be a program that reuses the ID.
            if (!answeredAsLotsen && !await IsStillClaimedAsync(discovery, state, timeout, elapsed, cancellationToken))
            {
               return null;
            }
         }
         catch (HttpRequestException exception)
         {
            throw new ServerShutdownException($"The lotsen server (PID {pid}) refused to stop with {exception.Message.TrimEnd('.')}. End that process to stop it.");
         }
         catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
         {
            throw new ServerShutdownException($"The lotsen server (PID {pid}) did not answer within {timeout.TotalSeconds:0} seconds. End that process to stop it.");
         }
      }

      if (await WaitForExitAsync(pid, timeout, elapsed, cancellationToken))
      {
         return pid;
      }

      throw new ServerShutdownException($"The lotsen server (PID {pid}) did not exit within {timeout.TotalSeconds:0} seconds of the request to stop. End that process to stop it.");
   }

   // A server that stops deletes its state file before it releases the claim lock, so a held lock without the state file is that server's.
   private static async Task<bool> IsStillClaimedAsync(ServerDiscovery discovery, ServerState state, TimeSpan timeout, Stopwatch elapsed, CancellationToken cancellationToken) =>
      await discovery.FindAsync(ProbeTimeout(timeout, elapsed), cancellationToken) switch
      {
         ServerAnswer.Running running => running.State == state,
         ServerAnswer.Unreachable unreachable => unreachable.State == state,
         ServerAnswer.Starting => true,
         ServerAnswer.None or ServerAnswer.Stale or ServerAnswer.Foreign => false,
      };

   private static TimeSpan Remaining(TimeSpan timeout, Stopwatch elapsed) => timeout > elapsed.Elapsed ? timeout - elapsed.Elapsed : TimeSpan.Zero;

   // A probe waits at most ServerClient.ProbeTimeout, so a listener that never answers does not use up the whole stop.
   private static TimeSpan ProbeTimeout(TimeSpan timeout, Stopwatch elapsed) => Remaining(timeout, elapsed) is var remaining && remaining < ServerClient.ProbeTimeout ? remaining : ServerClient.ProbeTimeout;

   // The server is no child of this process, so this process polls it by ID.
   private static async Task<bool> WaitForExitAsync(int pid, TimeSpan timeout, Stopwatch elapsed, CancellationToken cancellationToken)
   {
      while (ProcessLiveness.IsAlive(pid))
      {
         if (elapsed.Elapsed >= timeout)
         {
            return false;
         }

         await Task.Delay(PollInterval, cancellationToken);
      }

      return true;
   }
}

/// <summary>An error that keeps a server from shutting down. Its message tells the user what to do.</summary>
internal sealed class ServerShutdownException : Exception
{
   public ServerShutdownException(string message)
      : base(message)
   {
   }
}
