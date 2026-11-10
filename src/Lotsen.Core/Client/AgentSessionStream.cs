using System.Text.Json;
using Lotsen.Json;
using Lotsen.Sessions;

namespace Lotsen.Client;

/// <summary>The open event stream of one agent session, from the adapter's side, with the session's ID and credential from its first event.</summary>
/// <remarks>Disposing the stream closes the connection, which ends the session on the server.</remarks>
internal sealed class AgentSessionStream : IDisposable
{
   private readonly HttpResponseMessage _response;
   private readonly EventStreamReader _reader;

   private AgentSessionStream(HttpResponseMessage response, EventStreamReader reader, AgentSessionOpened opened)
   {
      _response = response;
      _reader = reader;
      SessionId = opened.SessionId;
      Credential = opened.Credential;
   }

   public string SessionId { get; }

   /// <summary>The session credential, which each call of the session carries.</summary>
   public string Credential { get; }

   /// <summary>Reads the opening event of a response that the server accepted.</summary>
   /// <param name="response">The server's answer, which the stream disposes.</param>
   /// <param name="time">The clock that times the stream's silence limit, before its hello and after it.</param>
   /// <param name="cancellationToken">Cancels the read of the opening event.</param>
   /// <exception cref="HttpRequestException">The stream ended or failed before its opening event, or the event is malformed.</exception>
   public static async Task<AgentSessionStream> OpenAsync(HttpResponseMessage response, TimeProvider time, CancellationToken cancellationToken)
   {
      var reader = new EventStreamReader(await response.Content.ReadAsStreamAsync(cancellationToken), time);
      try
      {
         var first = await ReadEventAsync(reader, cancellationToken);
         AgentSessionOpened? opened = null;
         if (first?.Type == AgentSessions.OpenedEvent)
         {
            try
            {
               opened = JsonSerializer.Deserialize(first.Data, LotsenJsonContext.Default.AgentSessionOpened);
            }
            catch (JsonException)
            {
            }
         }

         return new AgentSessionStream(response, reader, opened ?? throw new HttpRequestException("The lotsen server opened no agent session."));
      }
      catch
      {
         reader.Dispose();
         throw;
      }
   }

   /// <summary>Returns the next event, or null when the stream ends, fails or stays silent past its announced limit.</summary>
   public async Task<SessionEvent?> NextAsync(CancellationToken cancellationToken) => await ReadEventAsync(_reader, cancellationToken) is { } next ? new SessionEvent(next.Type, next.Data) : null;

   public void Dispose()
   {
      _reader.Dispose();
      _response.Dispose();
   }

   private static async Task<StreamEvent?> ReadEventAsync(EventStreamReader reader, CancellationToken cancellationToken)
   {
      try
      {
         return await reader.NextAsync(cancellationToken);
      }
      catch (Exception exception) when (exception is IOException or InvalidDataException or HttpRequestException || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
      {
         return null;
      }
   }
}
