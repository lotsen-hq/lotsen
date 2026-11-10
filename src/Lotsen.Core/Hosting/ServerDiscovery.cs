using System.Net.Sockets;
using Lotsen.Client;

namespace Lotsen.Hosting;

/// <summary>What <see cref="ServerDiscovery.FindAsync(CancellationToken)"/> finds on a data directory: whether a server runs there, and how to reach it.</summary>
internal closed record ServerAnswer
{
   private ServerAnswer()
   {
   }

   /// <summary>No state file, and no holder of the claim lock.</summary>
   internal sealed record None : ServerAnswer;

   /// <summary>A holder of the claim lock that has not written its own state file yet.</summary>
   internal sealed record Starting : ServerAnswer;

   /// <summary>A server whose process runs and whose port answers as lotsen does.</summary>
   /// <param name="State">The server's state file entry.</param>
   internal sealed record Running(ServerState State) : ServerAnswer;

   /// <summary>A state file whose port has a listener that does not answer as lotsen does within the probe's timeout. No server holds the claim lock.</summary>
   /// <remarks>The state file is stale, unless a server with .NET's file locking off stalls behind a listener that does not answer.</remarks>
   /// <param name="State">The state file entry.</param>
   internal sealed record Foreign(ServerState State) : ServerAnswer;

   /// <summary>A stale state file whose process has exited, or whose port refuses the connection or fails it with no HTTP. No server holds the claim lock.</summary>
   /// <param name="State">The stale state file entry.</param>
   internal sealed record Stale(ServerState State) : ServerAnswer;

   /// <summary>The state file of the claim lock's holder, whose process runs but whose port does not answer as lotsen does, as when the server stops or runs another build.</summary>
   /// <param name="State">The holder's state file entry.</param>
   internal sealed record Unreachable(ServerState State) : ServerAnswer;
}

/// <summary>
/// Finds the server that runs on a data directory, from its state file, its process, its port and the claim lock, checked in that order.
/// </summary>
/// <remarks>
/// <para>
/// A server runs only when its process runs and its port answers the version request without the server token as lotsen does, so a process that reuses an exited server's ID does not
/// count. The probe sends no token, so a listener on the port of a stale state file never receives one.
/// </para>
/// <para>
/// A server holds the claim lock until it exits, but a server with .NET's file locking off holds no OS lock, so the claim lock decides only the answers for a server that does not answer.
/// </para>
/// </remarks>
internal sealed class ServerDiscovery
{
   private readonly DataDirectory _dataDirectory;
   private readonly Action? _lockProbed;

   /// <param name="dataDirectory">The data directory to find the server of.</param>
   /// <param name="lockProbed">Called while a probe of the claim lock holds it, or null. Tests start a server in that moment with it.</param>
   public ServerDiscovery(DataDirectory dataDirectory, Action? lockProbed = null)
   {
      _dataDirectory = dataDirectory;
      _lockProbed = lockProbed;
   }

   private enum Probe
   {
      Lotsen,
      Foreign,
      Silent,
   }

   /// <summary>Returns what runs on the data directory, with a probe of the server's port that waits at most <see cref="ServerClient.ProbeTimeout"/>.</summary>
   public Task<ServerAnswer> FindAsync(CancellationToken cancellationToken) => FindAsync(ServerClient.ProbeTimeout, cancellationToken);

   /// <summary>Returns what runs on the data directory.</summary>
   /// <param name="probeTimeout">How long the probe of the server's port waits for an answer. A port that answers within it has an HTTP server behind it.</param>
   /// <param name="cancellationToken">Cancels the discovery.</param>
   public async Task<ServerAnswer> FindAsync(TimeSpan probeTimeout, CancellationToken cancellationToken)
   {
      while (true)
      {
         if (StateFile.TryRead(_dataDirectory) is not { } state)
         {
            return IsClaimed() ? new ServerAnswer.Starting() : new ServerAnswer.None();
         }

         // A server deletes a crashed server's state file right after it takes the claim lock, so the holder of the lock is a server that is starting.
         if (!ProcessLiveness.IsAlive(state.Pid))
         {
            return IsClaimed() ? new ServerAnswer.Starting() : new ServerAnswer.Stale(state);
         }

         var probe = await ProbeAsync(state, probeTimeout, cancellationToken);
         if (probe == Probe.Lotsen)
         {
            return new ServerAnswer.Running(state);
         }

         if (!IsClaimed())
         {
            return probe == Probe.Foreign ? new ServerAnswer.Foreign(state) : new ServerAnswer.Stale(state);
         }

         // For the same reason, only a state file that did not change during the probe is the holder's. A new one gets the checks again.
         if (StateFile.TryRead(_dataDirectory) == state)
         {
            return new ServerAnswer.Unreachable(state);
         }
      }
   }

   private bool IsClaimed() => ClaimLock.IsHeld(_dataDirectory, _lockProbed);

   // A port that refuses or fails the connection, or answers with no HTTP, is silent, so only the caller's cancellation escapes. A connection that a stopping server closes at once can fail
   // with a bare SocketException, as the loop of ServerDiscoveryTests.Every_server_start_succeeds_while_a_loop_of_discoveries_runs pins.
   private static async Task<Probe> ProbeAsync(ServerState state, TimeSpan probeTimeout, CancellationToken cancellationToken)
   {
      using var client = new ServerClient(state);
      using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      probe.CancelAfter(probeTimeout);
      try
      {
         return await client.AnswersAsLotsenAsync(probe.Token) ? Probe.Lotsen : Probe.Foreign;
      }
      catch (Exception exception) when (exception is HttpRequestException or IOException or SocketException)
      {
         return Probe.Silent;
      }
      catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
      {
         // A server with .NET's file locking off holds no claim lock, so a stale answer to one that stalls would let a start run a second server beside it.
         return Probe.Foreign;
      }
   }
}
