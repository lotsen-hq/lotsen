using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Client;
using Lotsen.Operations;

namespace Lotsen.Tests;

/// <summary>One open event stream, read through the adapter's <see cref="EventStreamReader"/>, or an agent session's stream as the adapter's own client reads it.</summary>
internal sealed class EventStream : IAsyncDisposable
{
   private readonly HttpResponseMessage? _response;
   private readonly EventStreamReader? _reader;
   private readonly AgentSessionStream? _session;
   private readonly List<string> _data = [];

   private EventStream(HttpResponseMessage response, EventStreamReader reader)
   {
      _response = response;
      _reader = reader;
   }

   private EventStream(AgentSessionStream session)
   {
      _session = session;
   }

   /// <summary>The response of a stream that the test opened over HTTP itself.</summary>
   /// <exception cref="InvalidOperationException">The stream is an agent session's.</exception>
   public HttpResponseMessage Response => _response ?? throw new InvalidOperationException("An agent session's stream has no response of its own.");

   /// <summary>The text of every event read so far, as the server sent it.</summary>
   public IReadOnlyList<string> Data => _data;

   /// <summary>Opens the event stream with a UI credential from a new one-time code, as the UI does.</summary>
   public static async Task<EventStream> OpenAsync(ServerHarness harness) => await OpenAsync(harness, ServerHarness.Request(HttpMethod.Get, "api/events", await harness.UiCredentialAsync()));

   /// <summary>Sends a request whose answer is an event stream, and requires that the server accepts it.</summary>
   public static async Task<EventStream> OpenAsync(ServerHarness harness, HttpRequestMessage request)
   {
      var response = await harness.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
      if (response.StatusCode != HttpStatusCode.OK)
      {
         var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
         response.Dispose();
         Assert.Fail($"{request.Method} {request.RequestUri} returned {(int)response.StatusCode}: {body}");
      }

      return new EventStream(response, new EventStreamReader(await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken), TimeProvider.System));
   }

   /// <summary>Returns the stream of an agent session that <see cref="ServerClient.OpenAgentSessionAsync"/> opened, after its opening event, and disposes it with this stream.</summary>
   public static EventStream Of(AgentSessionStream session) => new(session);

   public async Task<(string Type, JsonNode Data)> NextAsync(TimeSpan timeout)
   {
      using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
      cts.CancelAfter(timeout);
      var next = _session is null ? await _reader!.NextAsync(cts.Token) : await _session.NextAsync(cts.Token) is { } sessionEvent ? new StreamEvent(sessionEvent.Type, sessionEvent.Data) : null;
      if (next is null)
      {
         throw new InvalidOperationException("The stream ended before the next event.");
      }

      _data.Add(next.Data);
      return (next.Type, JsonNode.Parse(next.Data)!);
   }

   /// <summary>Returns the next event of a kind other than <see cref="ServerEvents.ControlsChanged"/>, for a test about the other events that a write publishes.</summary>
   public async Task<(string Type, JsonNode Data)> NextBesidesControlsAsync(TimeSpan timeout)
   {
      while (true)
      {
         var next = await NextAsync(timeout);
         if (next.Type != ServerEvents.ControlsChanged.Name)
         {
            return next;
         }
      }
   }

   /// <summary>Returns a copy of a tour-scoped event's payload without its revision, to compare with an operation's output, and requires that the payload has one.</summary>
   public static JsonNode WithoutRevision(JsonNode payload)
   {
      var copy = payload.DeepClone().AsObject();
      Assert.True(copy.Remove(ServerEvent.RevisionProperty), $"The event carries no revision: {payload.ToJsonString()}");
      return copy;
   }

   /// <summary>Returns a copy of a thread that an event serves to the UI without its <c>status</c>, to compare with an output read without prose, and requires that the thread has one.</summary>
   public static JsonNode WithoutStatus(JsonNode thread)
   {
      var copy = thread.DeepClone().AsObject();
      Assert.True(copy.Remove("status"), $"The thread carries no status: {thread.ToJsonString()}");
      return copy;
   }

   /// <summary>Reads until the server closes the stream, and returns false when it stays open past the timeout.</summary>
   public async Task<bool> ReadToEndAsync(TimeSpan timeout)
   {
      using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
      cts.CancelAfter(timeout);
      try
      {
         if (_session is not null)
         {
            while (await _session.NextAsync(cts.Token) is not null)
            {
            }

            return true;
         }

         while (await _reader!.NextAsync(cts.Token) is not null)
         {
         }

         return true;
      }
      catch (OperationCanceledException) when (cts.IsCancellationRequested)
      {
         return false;
      }
   }

   public ValueTask DisposeAsync()
   {
      _session?.Dispose();
      _reader?.Dispose();
      _response?.Dispose();
      return ValueTask.CompletedTask;
   }
}
