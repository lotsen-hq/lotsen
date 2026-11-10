using System.Text;
using System.Threading.Channels;
using Lotsen.Operations;
using Microsoft.AspNetCore.Http.Features;

namespace Lotsen.Server;

/// <summary>One event of a Server-Sent Events stream: its type name and its payload as JSON text.</summary>
internal interface IStreamEvent
{
   /// <summary>The event's type name, which the stream writes as <c>event:</c>.</summary>
   string Type { get; }

   /// <summary>The event's payload as JSON text, which the stream writes as one <c>data:</c> line.</summary>
   string Data { get; }

   /// <summary>Whether the stream writes <see cref="Data"/> as a comment line, which every reader skips, with no <c>event:</c> line.</summary>
   bool IsComment => false;
}

/// <summary>How an event stream shows that it is alive: the server's keep-alive interval, and how many intervals of silence a reader waits before it gives the connection up.</summary>
/// <remarks>Every stream announces it in its <see cref="HelloEvent"/>, so a reader takes its silence limit from the stream and holds no constant of its own.</remarks>
internal sealed record StreamLiveness
{
   /// <exception cref="ArgumentOutOfRangeException">
   /// The interval is not a whole number of milliseconds from 1, the factor is below 1, or the silence limit is longer than <see cref="int.MaxValue"/> milliseconds.
   /// </exception>
   public StreamLiveness(TimeSpan keepAliveInterval, int silenceFactor)
   {
      if (!Holds(keepAliveInterval, silenceFactor))
      {
         throw new ArgumentOutOfRangeException(nameof(keepAliveInterval), $"{keepAliveInterval} × {silenceFactor} is no liveness: the interval must be whole milliseconds from 1, the factor from 1, and the product at most {int.MaxValue} ms.");
      }

      KeepAliveInterval = keepAliveInterval;
      SilenceFactor = silenceFactor;
   }

   /// <summary>The liveness of a server that its options leave at the default.</summary>
   public static StreamLiveness Default { get; } = new(TimeSpan.FromSeconds(20), 3);

   /// <summary>How long a stream stays quiet before the server writes a keep-alive comment.</summary>
   public TimeSpan KeepAliveInterval { get; }

   /// <summary>How many keep-alive intervals of silence a reader waits before it gives the connection up.</summary>
   public int SilenceFactor { get; }

   /// <summary>How long a reader waits for the next line before it gives the connection up.</summary>
   public TimeSpan SilenceLimit => KeepAliveInterval.Multiply(SilenceFactor);

   /// <summary>The payload of the hello that announces this liveness.</summary>
   public HelloEvent Hello => new((int)KeepAliveInterval.TotalMilliseconds, SilenceFactor);

   /// <summary>Returns the liveness that a hello announces, or null when the hello announces none that a server can have.</summary>
   public static StreamLiveness? Of(HelloEvent hello) =>
      Holds(TimeSpan.FromMilliseconds(hello.KeepAliveMs), hello.SilenceFactor) ? new(TimeSpan.FromMilliseconds(hello.KeepAliveMs), hello.SilenceFactor) : null;

   // The hello announces the interval in whole milliseconds, and the UI times the silence limit with a browser timer, which waits at most int.MaxValue milliseconds
   // (https://developer.mozilla.org/en-US/docs/Web/API/Window/setTimeout#maximum_delay_value).
   private static bool Holds(TimeSpan interval, int factor) =>
      interval >= TimeSpan.FromMilliseconds(1) && interval.Ticks % TimeSpan.TicksPerMillisecond == 0 && factor >= 1 && (long)interval.TotalMilliseconds * factor <= int.MaxValue;
}

/// <summary>The payload of the <see cref="ServerEvents.Hello"/> event, which opens every event stream.</summary>
/// <param name="KeepAliveMs">The server's keep-alive interval, in milliseconds.</param>
/// <param name="SilenceFactor">How many keep-alive intervals of silence a reader waits before it gives the connection up.</param>
internal sealed record HelloEvent(int KeepAliveMs, int SilenceFactor);

/// <summary>Starts the server's event streams with the server's liveness and clock.</summary>
/// <remarks>
/// Each event is <c>event: &lt;type&gt;</c> plus one <c>data:</c> line of JSON. Each stream opens with its hello, because only <see cref="EventStreamWriter.OpenAsync"/> builds a writer.
/// </remarks>
internal sealed class ServerSentEvents
{
   private readonly StreamLiveness _liveness;
   private readonly TimeProvider _time;

   /// <param name="liveness">The liveness that every stream announces and keeps.</param>
   /// <param name="time">The clock that times the keep-alive.</param>
   public ServerSentEvents(StreamLiveness liveness, TimeProvider time)
   {
      _liveness = liveness;
      _time = time;
   }

   /// <summary>Starts the response as an unbuffered event stream, sends its hello, and returns the writer of the rest.</summary>
   public Task<EventStreamWriter> StartAsync(HttpContext context, CancellationToken cancellationToken)
   {
      context.Response.ContentType = "text/event-stream";
      context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
      return OpenAsync(context.Response.Body, cancellationToken);
   }

   /// <summary>Sends the hello on the body, and returns the writer of the rest of the stream.</summary>
   public Task<EventStreamWriter> OpenAsync(Stream body, CancellationToken cancellationToken) => EventStreamWriter.OpenAsync(body, _liveness, _time, cancellationToken);
}

/// <summary>Writes the events of one stream after its hello, and a keep-alive comment whenever the stream has been quiet for its keep-alive interval.</summary>
internal sealed class EventStreamWriter
{
   // A comment line, which every reader skips.
   private static readonly byte[] KeepAlive = ": keep-alive\n\n"u8.ToArray();

   private readonly Stream _body;
   private readonly StreamLiveness _liveness;
   private readonly TimeProvider _time;

   private EventStreamWriter(Stream body, StreamLiveness liveness, TimeProvider time)
   {
      _body = body;
      _liveness = liveness;
      _time = time;
   }

   /// <summary>Sends the hello that announces the liveness on the body, and returns the writer of the rest of the stream.</summary>
   public static async Task<EventStreamWriter> OpenAsync(Stream body, StreamLiveness liveness, TimeProvider time, CancellationToken cancellationToken)
   {
      var writer = new EventStreamWriter(body, liveness, time);
      await writer.WriteAsync(ServerEvent.Of(ServerEvents.Hello, liveness.Hello), cancellationToken);
      return writer;
   }

   /// <summary>Writes one event and sends it at once.</summary>
   public async Task WriteAsync(IStreamEvent streamEvent, CancellationToken cancellationToken)
   {
      await WriteFramedAsync(streamEvent, cancellationToken);
      await _body.FlushAsync(cancellationToken);
   }

   /// <summary>Writes each queued event until the queue completes or the token is canceled.</summary>
   public async Task PumpAsync<TEvent>(ChannelReader<TEvent> events, CancellationToken cancellationToken)
      where TEvent : IStreamEvent
   {
      try
      {
         while (true)
         {
            using var quiet = new CancellationTokenSource(_liveness.KeepAliveInterval, _time);
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, quiet.Token);
            bool open;
            try
            {
               open = await events.WaitToReadAsync(idle.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
               await _body.WriteAsync(KeepAlive, cancellationToken);
               await _body.FlushAsync(cancellationToken);
               continue;
            }

            if (!open)
            {
               return;
            }

            while (events.TryRead(out var streamEvent))
            {
               await WriteFramedAsync(streamEvent, cancellationToken);
            }

            await _body.FlushAsync(cancellationToken);
         }
      }
      catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
      {
      }
   }

   private ValueTask WriteFramedAsync(IStreamEvent streamEvent, CancellationToken cancellationToken) =>
      _body.WriteAsync(Encoding.UTF8.GetBytes(streamEvent.IsComment ? $": {streamEvent.Data}\n\n" : $"event: {streamEvent.Type}\ndata: {streamEvent.Data}\n\n"), cancellationToken);
}
