using System.Text.Json;
using Lotsen.Json;
using Lotsen.Operations;
using Lotsen.Server;

namespace Lotsen.Client;

/// <summary>One event that an event stream carried, after its hello.</summary>
/// <param name="Type">The event's type, or <c>message</c> when the stream named none.</param>
/// <param name="Data">The event's <c>data:</c> lines, joined with line feeds.</param>
internal sealed record StreamEvent(string Type, string Data);

/// <summary>A failure of an event stream that stayed silent past its silence limit, so the connection is gone without a close.</summary>
internal sealed class SilenceException : IOException
{
   public SilenceException(TimeSpan limit)
      : base($"The event stream stayed silent for {limit.TotalSeconds:0.###} s, its silence limit.")
   {
   }
}

/// <summary>Reads an event stream as the server writes it: its hello, then each event, with every comment skipped.</summary>
/// <remarks>
/// The silence limit comes from the hello, as <see cref="StreamLiveness.SilenceLimit"/>, and every line, a keep-alive comment included, starts it again. Until the hello arrives, the limit is
/// <see cref="HelloLimit"/>.
/// </remarks>
internal sealed class EventStreamReader : IDisposable
{
   private readonly StreamReader _reader;
   private readonly TimeProvider _time;
   private StreamLiveness? _liveness;

   /// <summary>The silence limit before the hello: that of a server whose options leave the liveness at the default.</summary>
   /// <remarks>A server sends the hello with the headers, so a stream that stays silent this long after them is broken.</remarks>
   public static TimeSpan HelloLimit => StreamLiveness.Default.SilenceLimit;

   /// <param name="body">The stream's body, which the reader disposes.</param>
   /// <param name="time">The clock that times the silence limit.</param>
   public EventStreamReader(Stream body, TimeProvider time)
   {
      _reader = new StreamReader(body);
      _time = time;
   }

   /// <summary>Returns the next event after the hello.</summary>
   /// <returns>The event, or null when the stream ends.</returns>
   /// <exception cref="SilenceException">No line came within the announced silence limit, or within <see cref="HelloLimit"/> before the hello.</exception>
   /// <exception cref="InvalidDataException">The stream's first event is not a hello with a liveness that a server can have.</exception>
   public async Task<StreamEvent?> NextAsync(CancellationToken cancellationToken)
   {
      while (await ReadEventAsync(cancellationToken) is { } next)
      {
         if (_liveness is not null)
         {
            return next;
         }

         _liveness = LivenessOf(next) ?? throw new InvalidDataException($"The event stream sent {next.Type} before its hello.");
      }

      return null;
   }

   public void Dispose() => _reader.Dispose();

   private static StreamLiveness? LivenessOf(StreamEvent first)
   {
      if (first.Type != ServerEvents.Hello.Name)
      {
         return null;
      }

      try
      {
         return JsonSerializer.Deserialize(first.Data, LotsenJsonContext.Default.HelloEvent) is { } hello ? StreamLiveness.Of(hello) : null;
      }
      catch (JsonException)
      {
         return null;
      }
   }

   private async Task<StreamEvent?> ReadEventAsync(CancellationToken cancellationToken)
   {
      string? type = null;
      var data = new List<string>();
      while (await ReadLineAsync(cancellationToken) is { } line)
      {
         if (line.Length == 0)
         {
            if (data.Count > 0)
            {
               return new StreamEvent(type ?? "message", string.Join('\n', data));
            }

            type = null;
            continue;
         }

         if (line.StartsWith("event:", StringComparison.Ordinal))
         {
            type = line["event:".Length..].Trim();
         }
         else if (line.StartsWith("data:", StringComparison.Ordinal))
         {
            data.Add(line["data:".Length..].TrimStart());
         }
      }

      return null;
   }

   private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
   {
      var limit = _liveness?.SilenceLimit ?? HelloLimit;
      using var silence = new CancellationTokenSource(limit, _time);
      using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, silence.Token);
      try
      {
         return await _reader.ReadLineAsync(wait.Token);
      }
      catch (OperationCanceledException) when (silence.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
      {
         throw new SilenceException(limit);
      }
   }
}
