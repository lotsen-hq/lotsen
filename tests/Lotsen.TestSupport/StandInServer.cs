using System.Net;
using System.Net.Sockets;
using System.Text;
using Lotsen.Operations;
using Lotsen.Server;

namespace Lotsen.Tests;

/// <summary>
/// A loopback stand-in for a lotsen server: it answers the version request without a token as lotsen does, opens an agent session for each request to open one, and answers every other
/// request with the test's answer to that call.
/// </summary>
/// <remarks>Each session's stream stays open until <see cref="EndSessions"/> or the disposal, so the adapter learns of a refused session only from the answer to a call.</remarks>
internal sealed class StandInServer : IAsyncDisposable
{
   private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
   private readonly CancellationTokenSource _stop = new();
   private readonly List<TcpClient> _clients = [];
   private readonly List<TcpClient> _sessions = [];
   private readonly List<string> _calls = [];
   private readonly Func<int, Task<HttpAnswer>> _answer;
   private readonly Task _accepting;
   private int _sessionsOpened;

   /// <param name="answer">Returns the answer to the call with this number, from 1, or null to answer every call with a bare 401, as the request guards refuse a server token.</param>
   public StandInServer(Func<int, Task<HttpAnswer>>? answer = null)
   {
      _answer = answer ?? (_ => Task.FromResult(new HttpAnswer(401)));
      _listener.Start();
      _accepting = AcceptAsync();
   }

   public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

   public int SessionsOpened => Volatile.Read(ref _sessionsOpened);

   /// <summary>The heads of the calls received so far, each as the raw text up to the blank line.</summary>
   public IReadOnlyList<string> Calls
   {
      get
      {
         lock (_calls)
         {
            return [.. _calls];
         }
      }
   }

   /// <summary>Returns the session credential of the session with this number, from 1, which a call as that session carries.</summary>
   public static string CredentialOf(int session) => $"session{session}.key";

   /// <summary>Closes the stream of every open session, as a server does that ends its sessions.</summary>
   public void EndSessions()
   {
      lock (_sessions)
      {
         _sessions.ForEach(session => session.Dispose());
         _sessions.Clear();
      }
   }

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

      lock (_clients)
      {
         _clients.ForEach(client => client.Dispose());
      }

      _stop.Dispose();
   }

   private async Task AcceptAsync()
   {
      while (!_stop.IsCancellationRequested)
      {
         var client = await _listener.AcceptTcpClientAsync(_stop.Token);
         lock (_clients)
         {
            _clients.Add(client);
         }

         _ = AnswerAsync(client);
      }
   }

   private async Task AnswerAsync(TcpClient client)
   {
      try
      {
         var stream = client.GetStream();
         var head = await ReadHeadAsync(stream);
         if (head.StartsWith("POST /api/agent-session ", StringComparison.Ordinal))
         {
            var number = Interlocked.Increment(ref _sessionsOpened);
            var data = $$"""{"sessionId":"session{{number}}","credential":"{{CredentialOf(number)}}"}""";
            var hello = ServerEvent.Of(ServerEvents.Hello, StreamLiveness.Default.Hello);
            var chunk = $"event: {hello.Type}\ndata: {hello.Data}\n\nevent: agentSession\ndata: {data}\n\n";
            var answer = $"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\n\r\n{Encoding.UTF8.GetByteCount(chunk):x}\r\n{chunk}\r\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(answer), _stop.Token);
            lock (_sessions)
            {
               _sessions.Add(client);
            }

            return;
         }

         if (head.StartsWith("GET /api/version ", StringComparison.Ordinal) && !head.Contains("Authorization:", StringComparison.OrdinalIgnoreCase))
         {
            await WriteAsync(client, stream, new HttpAnswer(401, RequestGuards.NeedsCredentialReason, "text/plain; charset=utf-8"));
            return;
         }

         int call;
         lock (_calls)
         {
            _calls.Add(head);
            call = _calls.Count;
         }

         await WriteAsync(client, stream, await _answer(call));
      }
      catch (Exception exception) when (exception is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
      {
      }
   }

   private async Task WriteAsync(TcpClient client, NetworkStream stream, HttpAnswer answer)
   {
      var body = Encoding.UTF8.GetBytes(answer.Body);
      var challenge = answer.Status == 401 ? "WWW-Authenticate: Bearer\r\n" : "";
      var head = $"HTTP/1.1 {answer.Status} Answer\r\nContent-Type: {answer.ContentType}\r\n{challenge}Content-Length: {body.Length}\r\nConnection: close\r\n\r\n";
      await stream.WriteAsync(Encoding.ASCII.GetBytes(head), _stop.Token);
      await stream.WriteAsync(body, _stop.Token);

      // A close with unread request bytes resets the connection, and the reset can discard the answer before the client reads it.
      client.Client.Shutdown(SocketShutdown.Send);
      var rest = new byte[4096];
      while (await stream.ReadAsync(rest, _stop.Token) > 0)
      {
      }

      client.Dispose();
   }

   private async Task<string> ReadHeadAsync(NetworkStream stream)
   {
      var head = new StringBuilder();
      var buffer = new byte[1];
      while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(buffer, _stop.Token) == 1)
      {
         head.Append((char)buffer[0]);
      }

      return head.ToString();
   }
}
