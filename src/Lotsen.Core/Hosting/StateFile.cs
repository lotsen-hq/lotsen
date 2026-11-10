using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Lotsen.Json;

namespace Lotsen.Hosting;

/// <summary>
/// The running server's entry in the state file.
/// </summary>
/// <param name="Pid">The server's process ID.</param>
/// <param name="Address">The loopback IP address the server listens on, as text.</param>
/// <param name="Port">The TCP port the server listens on.</param>
/// <param name="Token">The server token: the bearer token for the server's API that the adapter and the CLI send.</param>
internal sealed record ServerState(int Pid, string Address, int Port, string Token)
{
   // A record prints every property, and the token must never reach a log or an assertion message.
   public override string ToString() => $"ServerState {{ Pid = {Pid}, Address = {Address}, Port = {Port} }}";
}

/// <summary>
/// Reads and writes the state file, through which <see cref="ServerDiscovery"/> finds the running server.
/// </summary>
internal static class StateFile
{
   /// <summary>Writes the state file readable by the user only (0600 on Unix), replacing any earlier one.</summary>
   /// <remarks>
   /// The write is atomic, so a reader never sees a partial file. See <see cref="UserOnlyStorage.WriteFileAtomically"/>.
   /// </remarks>
   public static void Write(DataDirectory dataDirectory, ServerState state)
   {
      dataDirectory.EnsureExists();
      UserOnlyStorage.WriteFileAtomically(dataDirectory.StateFilePath, stream => JsonSerializer.Serialize(stream, state, LotsenJsonContext.Default.ServerState));
   }

   /// <summary>Returns the server's entry, or null when there is no state file, it cannot be read, or it names an address or port that no server listens on.</summary>
   /// <remarks>A server listens only on a port of IPv4 loopback, so an entry with another address or a port out of range is no server's, and a client never calls it.</remarks>
   public static ServerState? TryRead(DataDirectory dataDirectory)
   {
      try
      {
         using var stream = new FileStream(dataDirectory.StateFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
         return JsonSerializer.Deserialize(stream, LotsenJsonContext.Default.ServerState) is { } state && IsLoopbackEndpoint(state) ? state : null;
      }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
      {
         return null;
      }
   }

   // A client puts the address text in a URL, and IPAddress.TryParse also accepts short forms such as 127.1, so only the dotted form that the server writes counts.
   private static bool IsLoopbackEndpoint(ServerState state) =>
      state.Port is >= 1 and <= IPEndPoint.MaxPort
      && IPAddress.TryParse(state.Address, out var address) && address.AddressFamily == AddressFamily.InterNetwork && IPAddress.IsLoopback(address) && address.ToString() == state.Address;
}
