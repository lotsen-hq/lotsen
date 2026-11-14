using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Lotsen.Tests;

/// <summary>An answer of <see cref="RecordingHttpServer"/>: a status code, a body and its content type, JSON in UTF-8 unless the test names another, and a <c>Location</c> header when not null.</summary>
internal sealed record HttpAnswer(int Status, string Body = "", string ContentType = "application/json; charset=utf-8", string? Location = null);

/// <summary>A loopback HTTP server that records the head of every request and answers each, or holds every answer until <see cref="Release"/>.</summary>
/// <remarks>
/// It stands in for a git host that git cannot clone from, so tests can see what git sends over HTTP, and for the Azure DevOps REST API.
/// </remarks>
internal sealed class RecordingHttpServer : IAsyncDisposable
{
   private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
   private readonly CancellationTokenSource _stop = new();
   private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
   private readonly TaskCompletionSource _firstRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
   private readonly List<string> _requests = [];
   private readonly Func<string, HttpAnswer> _answer;
   private readonly Task _accepting;

   /// <param name="hold">Whether every answer waits for <see cref="Release"/>.</param>
   /// <param name="answer">Returns the answer to a request target, such as <c>/a/b?c=d</c>. Without it, every answer is 404.</param>
   public RecordingHttpServer(bool hold = false, Func<string, HttpAnswer>? answer = null)
   {
      _answer = answer ?? (_ => new HttpAnswer(404));
      if (!hold)
      {
         _released.SetResult();
      }

      _listener.Start();
      _accepting = AcceptAsync();
   }

   /// <summary>The URL of this server's root, ending in a slash.</summary>
   public Uri BaseUrl => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");

   /// <summary>The URL of a repository on this server.</summary>
   public Uri RepositoryUrl(string name) => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/{name}");

   /// <summary>The heads of the requests received so far, each as the raw text up to the blank line.</summary>
   public IReadOnlyList<string> Requests
   {
      get
      {
         lock (_requests)
         {
            return [.. _requests];
         }
      }
   }

   /// <summary>Completes when the first request has arrived.</summary>
   public Task FirstRequest => _firstRequest.Task;

   /// <summary>Lets held and later requests receive their answers.</summary>
   public void Release() => _released.TrySetResult();

   public async ValueTask DisposeAsync()
   {
      Release();
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
         var client = await _listener.AcceptTcpClientAsync(_stop.Token);
         _ = AnswerAsync(client);
      }
   }

   private async Task AnswerAsync(TcpClient client)
   {
      using (client)
      {
         try
         {
            var stream = client.GetStream();
            var head = await ReadHeadAsync(stream);

            // A connection that closes before its head ends sent no request, so it gets no record and no answer.
            if (!head.EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
               return;
            }

            lock (_requests)
            {
               _requests.Add(head);
            }

            _firstRequest.TrySetResult();
            await _released.Task.WaitAsync(_stop.Token);
            var answer = _answer(head.Split(' ', 3) is [_, var target, ..] ? target : "");
            var body = Encoding.UTF8.GetBytes(answer.Body);
            var location = answer.Location is null ? "" : $"Location: {answer.Location}\r\n";
            var answerHead = $"HTTP/1.1 {answer.Status} Answer\r\nContent-Type: {answer.ContentType}\r\nContent-Length: {body.Length}\r\n{location}Connection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(answerHead), _stop.Token);
            await stream.WriteAsync(body, _stop.Token);

            // A close with unread request bytes resets the connection, and on Windows the reset discards the answer before the client reads it.
            client.Client.Shutdown(SocketShutdown.Send);
            await DrainAsync(stream);
         }
         catch (Exception exception) when (exception is IOException or OperationCanceledException or SocketException)
         {
         }
      }
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

   // Reads until the client closes its side.
   private async Task DrainAsync(NetworkStream stream)
   {
      var buffer = new byte[4096];
      while (await stream.ReadAsync(buffer, _stop.Token) > 0)
      {
      }
   }
}
