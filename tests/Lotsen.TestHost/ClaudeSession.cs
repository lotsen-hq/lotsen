using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Lotsen.Client;
using Lotsen.Hosting;
using Lotsen.Operations;
using Lotsen.Server;
using Lotsen.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace Lotsen.TestHost;

/// <summary>A real agent session on a world's server, opened and called through the adapter's own client, with its session events in a queue that a test reads.</summary>
internal sealed class ClaudeSession : IAsyncDisposable
{
   private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(10);

   private readonly ServerClient _client;
   private readonly AgentSessionStream _stream;
   private readonly bool _confirm;
   private readonly Channel<SessionEvent> _events = Channel.CreateUnbounded<SessionEvent>(new UnboundedChannelOptions { SingleReader = false });
   private readonly CancellationTokenSource _stopping = new();
   private readonly Task _pump;

   private ClaudeSession(string name, ServerClient client, AgentSessionStream stream, bool confirm)
   {
      Name = name;
      _client = client;
      _stream = stream;
      _confirm = confirm;
      _pump = Task.Run(PumpAsync);
   }

   /// <summary>The name that the host's control API gives the session.</summary>
   public string Name { get; }

   /// <summary>The session's ID on the server, which requests and replies name.</summary>
   public string SessionId => _stream.SessionId;

   public static async Task<ClaudeSession> OpenAsync(string name, DataDirectory dataDirectory, LotsenServer server, bool confirm, CancellationToken cancellationToken)
   {
      var client = new ServerClient(StateFile.TryRead(dataDirectory) ?? throw new InvalidOperationException("The world's server wrote no state file."));
      if (await client.OpenAgentSessionAsync(server.Services.GetRequiredService<VersionInfo>(), cancellationToken) is not WireAnswer<AgentSessionStream>.Output { Value: var stream })
      {
         client.Dispose();
         throw new InvalidOperationException("The server opened no agent session.");
      }

      return new ClaudeSession(name, client, stream, confirm);
   }

   /// <summary>Calls the operation as this session, and returns its output, or the reason of its refusal.</summary>
   public async Task<(JsonNode? Output, string? Refusal)> CallAsync(string operation, JsonObject input, CancellationToken cancellationToken)
   {
      using var document = JsonDocument.Parse(input.ToJsonString());
      var properties = document.RootElement.EnumerateObject().Select(property => KeyValuePair.Create(property.Name, property.Value.Clone())).ToList();
      return await _client.InvokeAsync(OperationCatalog.Get(operation), properties, _stream.Credential, cancellationToken) switch
      {
         WireAnswer<string>.Output output => (JsonNode.Parse(output.Value), null),
         WireAnswer<string>.Refused refused => (null, refused.Error.Reason),
         WireAnswer<string>.CredentialsRefused => (null, ServerClient.CredentialsRefusedReason),
         WireAnswer<string>.Unexpected unexpected => (null, unexpected.Description),
      };
   }

   /// <summary>Returns the session's next event of the kind, and drops the events of other kinds before it, or null when none comes within the timeout or the stream ends.</summary>
   /// <param name="kind">The event's type, such as <c>question</c>, or null for any kind.</param>
   public async Task<SessionEvent?> NextAsync(string? kind, TimeSpan timeout, CancellationToken cancellationToken)
   {
      using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      waiting.CancelAfter(timeout);
      try
      {
         while (await _events.Reader.WaitToReadAsync(waiting.Token))
         {
            while (_events.Reader.TryRead(out var next))
            {
               if (kind is null || next.Type == kind)
               {
                  return next;
               }
            }
         }
      }
      catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
      {
      }

      return null;
   }

   /// <summary>Completes once the server recorded the session's end and settled it, so a later read sees the released bindings.</summary>
   public async Task WhenSettledAsync(LotsenServer server)
   {
      if (server.Services.GetRequiredService<AgentSessions>().Find(SessionId) is { } onServer)
      {
         await onServer.WhenEnded.WaitAsync(SettleTimeout);
      }

      server.Services.GetRequiredService<TourWriter>().SettleEnds();
   }

   public async ValueTask DisposeAsync()
   {
      await _stopping.CancelAsync();
      _stream.Dispose();
      try
      {
         await _pump;
      }
      catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or IOException or HttpRequestException)
      {
      }

      _client.Dispose();
      _stopping.Dispose();
   }

   // Reads the stream into the queue. A session that confirms acks each probe before the probe reaches the queue.
   private async Task PumpAsync()
   {
      try
      {
         while (await _stream.NextAsync(_stopping.Token) is { } next)
         {
            if (_confirm && next.Type == SessionEvents.Probe && JsonNode.Parse(next.Data)?["nonce"] is { } nonce)
            {
               await CallAsync("ack", new JsonObject { ["nonce"] = nonce.DeepClone() }, _stopping.Token);
            }

            _events.Writer.TryWrite(next);
         }
      }
      catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or IOException or HttpRequestException)
      {
      }
      finally
      {
         _events.Writer.TryComplete();
      }
   }
}
