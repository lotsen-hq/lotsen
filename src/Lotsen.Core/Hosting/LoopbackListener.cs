using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;

namespace Lotsen.Hosting;

/// <summary>Binds the socket that a server on 127.0.0.1 listens on, and has a host's Kestrel listen on it.</summary>
internal static class LoopbackListener
{
   /// <summary>Returns a socket bound to the port of 127.0.0.1, or, for no port, to one that the OS assigns and that <see cref="ServerPort.IsBlocked"/> does not refuse.</summary>
   /// <exception cref="SocketException">The port could not be bound, as when another socket holds it.</exception>
   public static Socket Bind(ServerPort? port) =>
      port is null ? Unblocked(() => BindTo(0), socket => ((IPEndPoint)socket.LocalEndPoint!).Port) : BindTo(port.Value);

   /// <summary>Has the host's Kestrel listen on the bound socket.</summary>
   public static void ListenOn(IWebHostBuilder webHost, Socket listener)
   {
      var endpoint = (IPEndPoint)listener.LocalEndPoint!;
      webHost.UseSockets(sockets => sockets.CreateBoundListenSocket = bound => endpoint.Equals(bound) ? listener : SocketTransportOptions.CreateDefaultBoundListenSocket(bound));
      webHost.ConfigureKestrel(kestrel => kestrel.Listen(endpoint));
   }

   /// <summary>Binds until a bind's port is not blocked, and returns that bind.</summary>
   /// <remarks>Each blocked bind stays open until the end, so the OS cannot assign its port again, and so the loop ends after at most one bind per blocked port.</remarks>
   internal static T Unblocked<T>(Func<T> bind, Func<T, int> portOf)
      where T : IDisposable
   {
      var refused = new List<T>();
      try
      {
         while (true)
         {
            var bound = bind();
            if (!ServerPort.IsBlocked(portOf(bound)))
            {
               return bound;
            }

            refused.Add(bound);
         }
      }
      finally
      {
         foreach (var bound in refused)
         {
            bound.Dispose();
         }
      }
   }

   private static Socket BindTo(int port) => SocketTransportOptions.CreateDefaultBoundListenSocket(new IPEndPoint(IPAddress.Loopback, port));
}
