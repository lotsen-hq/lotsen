using System.Net;
using System.Net.Sockets;
using System.Text;
using Lotsen.Server;

namespace Lotsen.Tests;

/// <summary>How the process on the port of a stale state file answers each connection, in a way that is no HTTP.</summary>
public enum NoHttpPeer
{
   /// <summary>A reset of the connection once the request's first bytes arrive, so the request fails while it is sent or while its answer is read.</summary>
   Resets,

   /// <summary>An end of the peer's side of the connection without bytes, after which the request fails because the answer ended.</summary>
   Ends,

   /// <summary>Bytes that are no HTTP status line, then an end of the peer's side of the connection.</summary>
   SendsNoHttp,
}

/// <summary>A loopback listener that answers each connection as its <see cref="NoHttpPeer"/> says.</summary>
internal sealed class NoHttpListener : IAsyncDisposable
{
   private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
   private readonly CancellationTokenSource _stop = new();
   private readonly NoHttpPeer _peer;
   private readonly Task _answer;
   private readonly Task _accepting;
   private readonly Action<string>? _received;
   private int _lotsenProbes;

   /// <param name="peer">How the listener answers each connection.</param>
   /// <param name="answer">Completes when the listener may answer the requests it received.</param>
   /// <param name="lotsenProbes">How many of the first version requests without the server token the listener answers as a lotsen server does, before it answers no HTTP.</param>
   /// <param name="received">Called with the first bytes of each request as ASCII text, before the listener answers it, or null.</param>
   public NoHttpListener(NoHttpPeer peer, Task answer, int lotsenProbes = 0, Action<string>? received = null)
   {
      _peer = peer;
      _answer = answer;
      _lotsenProbes = lotsenProbes;
      _received = received;
      _listener.Start();
      _accepting = AcceptAsync();
   }

   public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

   public async ValueTask DisposeAsync()
   {
      await _stop.CancelAsync();
      _listener.Stop();
      try
      {
         await _accepting;
      }
      catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
      {
      }

      _stop.Dispose();
   }

   private async Task AcceptAsync()
   {
      while (!_stop.IsCancellationRequested)
      {
         using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
         try
         {
            await AnswerAsync(client.Client);
         }
         catch (Exception exception) when (exception is IOException or SocketException)
         {
         }
      }
   }

   private async Task AnswerAsync(Socket socket)
   {
      var buffer = new byte[4096];
      var request = Encoding.ASCII.GetString(buffer, 0, await socket.ReceiveAsync(buffer, _stop.Token));
      _received?.Invoke(request);
      if (_lotsenProbes > 0 && request.StartsWith("GET /api/version ", StringComparison.Ordinal))
      {
         _lotsenProbes--;
         var body = Encoding.UTF8.GetBytes(RequestGuards.NeedsCredentialReason);
         await socket.SendAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 401 Unauthorized\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
         await socket.SendAsync(body, _stop.Token);
         socket.Shutdown(SocketShutdown.Send);
         while (await socket.ReceiveAsync(buffer, _stop.Token) > 0)
         {
         }

         return;
      }

      await _answer.WaitAsync(_stop.Token);
      if (_peer == NoHttpPeer.Resets)
      {
         socket.LingerState = new LingerOption(enable: true, seconds: 0);
         return;
      }

      if (_peer == NoHttpPeer.SendsNoHttp)
      {
         await socket.SendAsync("SSH-2.0-OpenSSH\r\n"u8.ToArray(), _stop.Token);
      }

      // The peer reads until the client closes, because a close with unread bytes would reset the connection before the client reads the end.
      socket.Shutdown(SocketShutdown.Send);
      while (await socket.ReceiveAsync(buffer, _stop.Token) > 0)
      {
      }
   }
}
