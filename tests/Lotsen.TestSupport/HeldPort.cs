using System.Net;
using System.Net.Sockets;

namespace Lotsen.Tests;

/// <summary>A loopback port that a test keeps bound while it runs, so no bind to port 0 elsewhere in the run receives it.</summary>
internal sealed class HeldPort : IDisposable
{
   private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

   private HeldPort(bool listen)
   {
      _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
      if (listen)
      {
         _socket.Listen();
      }
   }

   /// <summary>The port number.</summary>
   public int Port => ((IPEndPoint)_socket.LocalEndPoint!).Port;

   /// <summary>Returns a held port that nothing listens on, so a connection to it is refused.</summary>
   public static HeldPort Refusing() => new(listen: false);

   /// <summary>Returns a held port with a listener that never accepts, so no other socket can listen on it.</summary>
   public static HeldPort Listening() => new(listen: true);

   /// <summary>A loopback port that only a privileged process may bind on Unix.</summary>
   public const int Privileged = 1023;

   /// <summary>Returns why this process cannot bind the loopback port, or null when it can.</summary>
   public static SocketError? BindRefusal(int port)
   {
      using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
      try
      {
         socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
         return null;
      }
      catch (SocketException exception)
      {
         return exception.SocketErrorCode;
      }
   }

   public void Dispose() => _socket.Dispose();
}
