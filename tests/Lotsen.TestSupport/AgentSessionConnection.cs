using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Lotsen.Client;
using Lotsen.Hosting;
using Lotsen.Json;
using Lotsen.Sessions;

namespace Lotsen.Tests;

/// <summary>One agent session opened through the adapter's own <see cref="ServerClient"/>, with the ID and the session credential from its opening event.</summary>
internal sealed class AgentSessionConnection : IAsyncDisposable
{
   private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(10);

   private readonly ServerClient _client;

   private AgentSessionConnection(ServerClient client, EventStream stream, string id, string credential)
   {
      _client = client;
      Stream = stream;
      Id = id;
      Credential = credential;
   }

   /// <summary>The session's event stream, after its opening event.</summary>
   public EventStream Stream { get; }

   public string Id { get; }

   public string Credential { get; }

   /// <summary>Opens a session with the server token and the server's own versions.</summary>
   public static async Task<AgentSessionConnection> OpenAsync(ServerHarness harness)
   {
      var client = new ServerClient(harness.State);
      try
      {
         var answer = await client.OpenAgentSessionAsync(harness.Version, TestContext.Current.CancellationToken).WaitAsync(OpenTimeout, TestContext.Current.CancellationToken);
         if (answer is not WireAnswer<AgentSessionStream>.Output { Value: var stream })
         {
            throw new InvalidOperationException($"The server opened no agent session: {answer}");
         }

         return new AgentSessionConnection(client, EventStream.Of(stream), stream.SessionId, stream.Credential);
      }
      catch
      {
         client.Dispose();
         throw;
      }
   }

   /// <summary>Returns the request that opens a session, with this bearer credential and these versions.</summary>
   public static HttpRequestMessage OpenRequest(ServerHarness harness, string credential, VersionInfo version)
   {
      var request = harness.Request(HttpMethod.Post, "api/agent-session");
      request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
      request.Content = new StringContent(
         JsonSerializer.Serialize(AgentSessionRequest.Of(version), LotsenJsonContext.Default.AgentSessionRequest), Encoding.UTF8, "application/json");
      return request;
   }

   /// <summary>Reads the session's next event, which must be a probe, and returns the probe's nonce.</summary>
   public async Task<string> ProbeAsync()
   {
      var probe = await Stream.NextAsync(OpenTimeout);
      Assert.Equal(SessionEvents.Probe, probe.Type);
      return (string)probe.Data["nonce"]!;
   }

   /// <summary>Returns a request with the server token and this session's credential.</summary>
   public HttpRequestMessage Request(ServerHarness harness, HttpMethod method, string path) => ServerHarness.Request(method, path, harness.State.Token, Credential);

   /// <summary>Returns the operation call as this session, with the server token and this session's credential.</summary>
   public PreparedCall Request(ServerHarness harness, OperationCall call) => new(call, harness.State.Token, Credential);

   /// <summary>Drops the connection, as an adapter that exits does.</summary>
   public async ValueTask DisposeAsync()
   {
      await Stream.DisposeAsync();
      _client.Dispose();
   }
}
