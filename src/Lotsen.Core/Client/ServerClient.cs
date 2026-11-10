using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Lotsen.Hosting;
using Lotsen.Json;
using Lotsen.Operations;
using Lotsen.Server;
using Lotsen.Sessions;
using Lotsen.Tours;

namespace Lotsen.Client;

/// <summary>
/// Calls the API of the lotsen server that runs for this user, as <see cref="ServerDiscovery"/> finds it.
/// </summary>
internal sealed class ServerClient : IDisposable
{
   /// <summary>The most characters of a server's reason that a refusal of <see cref="ShutdownAsync"/>, or a <see cref="WireAnswer{T}.Unexpected"/>, quotes.</summary>
   public const int MaxReasonLength = 200;

   /// <summary>The most bytes of an answer that <see cref="AnswersAsLotsenAsync"/> reads, more than any lotsen answer to the version request holds.</summary>
   public const int MaxProbeBodyLength = 4096;

   /// <summary>The message of the <see cref="HttpRequestException"/> of a request whose credentials the server refused.</summary>
   public const string CredentialsRefusedReason = "The lotsen server refused the credentials.";

   /// <summary>How long a caller of <see cref="AnswersAsLotsenAsync"/> waits for its answer.</summary>
   /// <remarks>A lotsen server refuses the probe in its request guards, before any route runs, so it answers well within this.</remarks>
   public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

   /// <summary>How long a request waits for the server's whole answer, or for the headers of an agent session's stream, unless the client was given another timeout.</summary>
   public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

   private readonly HttpClient _http;

   /// <param name="state">The state file entry of the server to call, as a <see cref="ServerAnswer"/> holds it.</param>
   /// <param name="requestTimeout">How long each request waits, or null for <see cref="RequestTimeout"/>.</param>
   public ServerClient(ServerState state, TimeSpan? requestTimeout = null)
   {
      State = state;

      // An agent session's stream ends unread, and without a drain its disposal closes the connection at once, as Disposing_the_session_stream_ends_the_session_at_once pins.
      var handler = new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false, MaxResponseDrainSize = 0 };
      _http = new HttpClient(handler) { BaseAddress = new Uri($"http://{state.Address}:{state.Port}/"), Timeout = requestTimeout ?? RequestTimeout };
      _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", state.Token);
   }

   /// <summary>The state file entry of the server this client calls.</summary>
   public ServerState State { get; }

   /// <summary>Returns the server's versions, which also shows that it answers.</summary>
   /// <exception cref="HttpRequestException">The server cannot be reached or refused the request.</exception>
   public async Task<VersionInfo> GetVersionAsync(CancellationToken cancellationToken)
   {
      using var response = await _http.GetAsync("api/version", cancellationToken);
      if (!response.IsSuccessStatusCode)
      {
         throw new HttpRequestException($"The lotsen server answered the version request with {(int)response.StatusCode} {response.ReasonPhrase}.", inner: null, response.StatusCode);
      }

      return await response.Content.ReadFromJsonAsync(LotsenJsonContext.Default.VersionInfo, cancellationToken) ?? throw new HttpRequestException("The server returned no versions.");
   }

   /// <summary>Asks the server to open the UI in the user's browser.</summary>
   /// <exception cref="HttpRequestException">The server cannot be reached or refused the request.</exception>
   public Task<OpenUiResult> OpenUiAsync(CancellationToken cancellationToken) => CallAsync(OperationCatalog.OpenUi, new OpenUiInput(), cancellationToken);

   /// <summary>Asks the server for a fresh link to the UI, and launches no browser.</summary>
   /// <exception cref="HttpRequestException">The server cannot be reached or refused the request.</exception>
   public Task<UiLinkResult> CreateUiLinkAsync(CancellationToken cancellationToken) => CallAsync(OperationCatalog.CreateUiLink, new NoInput(), cancellationToken);

   /// <summary>Returns whether the listener on the server's port answers the version request as a lotsen server does, asked without the server token.</summary>
   /// <remarks>
   /// The version route needs a credential, so a lotsen server answers 401 with <see cref="RequestGuards.NeedsCredentialReason"/>. A listener on the port of a stale state file then never
   /// receives the token. The check reads at most <see cref="MaxProbeBodyLength"/> bytes and compares them without a decode, so no charset or length of a foreign answer makes it fail.
   /// </remarks>
   /// <exception cref="HttpRequestException">No listener answers on the port, or the connection fails during the answer.</exception>
   public async Task<bool> AnswersAsLotsenAsync(CancellationToken cancellationToken)
   {
      using var handler = new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false };
      using var anonymous = new HttpClient(handler) { BaseAddress = _http.BaseAddress, Timeout = System.Threading.Timeout.InfiniteTimeSpan };
      using var response = await anonymous.GetAsync("api/version", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
      var body = await ReadBoundedAsync(response.Content, cancellationToken);
      if (response.StatusCode == HttpStatusCode.Unauthorized)
      {
         return body.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(RequestGuards.NeedsCredentialReason));
      }

      try
      {
         return response.IsSuccessStatusCode && JsonSerializer.Deserialize(body, LotsenJsonContext.Default.VersionInfo) is not null;
      }
      catch (JsonException)
      {
         return false;
      }
   }

   /// <summary>Asks the server to shut down, and returns once it has accepted, before its process exits.</summary>
   /// <exception cref="HttpRequestException">
   /// The server cannot be reached, or refused the request. A refusal carries its status and the first line of the server's reason as the message, cut at <see cref="MaxReasonLength"/> characters.
   /// </exception>
   public async Task ShutdownAsync(CancellationToken cancellationToken)
   {
      using var response = await _http.PostAsync("api/shutdown", content: null, cancellationToken);
      if (!response.IsSuccessStatusCode)
      {
         var reason = FirstLine(await response.Content.ReadAsStringAsync(cancellationToken));
         throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}{(reason.Length > 0 ? $": {reason}" : "")}", inner: null, response.StatusCode);
      }
   }

   /// <summary>Opens an agent session with the adapter's versions, and returns once the server has registered it.</summary>
   /// <returns>
   /// The session's open stream, or the server's refusal, such as <see cref="OperationErrorCode.BuildMismatch"/>. A server without the route is an older build, so its 404 reads as a build mismatch.
   /// </returns>
   /// <exception cref="HttpRequestException">The server cannot be reached, or its stream ended or failed before its opening event.</exception>
   public async Task<WireAnswer<AgentSessionStream>> OpenAgentSessionAsync(VersionInfo version, CancellationToken cancellationToken)
   {
      using var request = new HttpRequestMessage(HttpMethod.Post, "api/agent-session")
      {
         Content = JsonContent.Create(AgentSessionRequest.Of(version), LotsenJsonContext.Default.AgentSessionRequest),
      };
      // The open stream relies on the client's timeout ending once the headers arrive, as HttpClientTimeoutTests pins.
      HttpResponseMessage? response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

      try
      {
         if (response.StatusCode == HttpStatusCode.NotFound)
         {
            return new WireAnswer<AgentSessionStream>.Refused(new OperationError(OperationErrorCode.BuildMismatch, VersionInfo.MismatchReason(null, version)));
         }

         if (!response.IsSuccessStatusCode)
         {
            return RefusalOf<AgentSessionStream>(response, await response.Content.ReadAsStringAsync(cancellationToken), "the agent session");
         }

         var stream = await AgentSessionStream.OpenAsync(response, TimeProvider.System, cancellationToken);
         response = null;
         return new WireAnswer<AgentSessionStream>.Output(stream);
      }
      finally
      {
         response?.Dispose();
      }
   }

   /// <summary>Calls an operation with its whole input, as <see cref="OperationWire"/> sends it.</summary>
   /// <param name="operation">The operation to call.</param>
   /// <param name="input">The input properties by name, as an agent tool passes them.</param>
   /// <param name="cancellationToken">Cancels the call.</param>
   /// <returns>The operation's output as JSON text, or the server's answer that refused it.</returns>
   /// <exception cref="HttpRequestException">The server cannot be reached.</exception>
   /// <exception cref="TimeoutException">The server did not answer within the client's request timeout, and it may still run the operation.</exception>
   public Task<WireAnswer<string>> InvokeAsync(Operation operation, IEnumerable<KeyValuePair<string, JsonElement>> input, CancellationToken cancellationToken) =>
      InvokeAsync(operation, input, sessionCredential: null, cancellationToken);

   /// <summary>Calls an operation as <see cref="InvokeAsync(Operation, IEnumerable{KeyValuePair{string, JsonElement}}, CancellationToken)"/> does, as the agent session with this credential.</summary>
   /// <param name="operation">The operation to call.</param>
   /// <param name="input">The input properties by name, as an agent tool passes them.</param>
   /// <param name="sessionCredential">The agent session's credential, or null to call with the server token alone.</param>
   /// <param name="cancellationToken">Cancels the call.</param>
   /// <returns>The operation's output as JSON text, or the server's answer that refused it, such as <see cref="OperationErrorCode.SessionUnknown"/> for a session that has ended.</returns>
   /// <exception cref="HttpRequestException">The server cannot be reached.</exception>
   /// <exception cref="TimeoutException">The server did not answer within the client's request timeout, and it may still run the operation.</exception>
   /// <exception cref="OperationCanceledException">The token was canceled, also when the timeout came at the same time.</exception>
   public async Task<WireAnswer<string>> InvokeAsync(Operation operation, IEnumerable<KeyValuePair<string, JsonElement>> input, string? sessionCredential, CancellationToken cancellationToken)
   {
      using var request = OperationWire.Request(operation, WriteObject(input));
      if (sessionCredential is not null)
      {
         request.Headers.Add(AgentSessions.CredentialHeader, sessionCredential);
      }

      try
      {
         return await SendAsync(operation, request, cancellationToken);
      }
      catch (TaskCanceledException canceled) when (canceled.InnerException is TimeoutException && !cancellationToken.IsCancellationRequested)
      {
         // HttpClient reports its own timeout as a cancellation whose inner exception is a TimeoutException, as HttpClientTimeoutTests pins.
         throw new TimeoutException($"The lotsen server did not answer {operation.Name} within the request timeout.", canceled);
      }
   }

   private async Task<TOutput> CallAsync<TInput, TOutput>(Operation<TInput, TOutput> operation, TInput input, CancellationToken cancellationToken)
      where TInput : class
      where TOutput : class
   {
      using var request = OperationWire.Request(operation, input);
      return await SendAsync(operation, request, cancellationToken) switch
      {
         WireAnswer<string>.Output output => JsonSerializer.Deserialize(output.Value, operation.TypedOutput) ?? throw new HttpRequestException("The server returned no result."),
         WireAnswer<string>.Refused refused => throw new HttpRequestException(refused.Error.Reason),
         WireAnswer<string>.CredentialsRefused => throw new HttpRequestException(CredentialsRefusedReason, inner: null, HttpStatusCode.Unauthorized),
         WireAnswer<string>.Unexpected unexpected => throw new HttpRequestException(unexpected.Description),
      };
   }

   private async Task<WireAnswer<string>> SendAsync(Operation operation, HttpRequestMessage request, CancellationToken cancellationToken)
   {
      using var response = await _http.SendAsync(request, cancellationToken);
      var body = await response.Content.ReadAsStringAsync(cancellationToken);
      return response.IsSuccessStatusCode ? new WireAnswer<string>.Output(OperationWire.OutputOf(body).GetRawText()) : RefusalOf<string>(response, body, operation.Name);
   }

   public void Dispose() => _http.Dispose();

   // A refusal counts as lotsen's only when its body is an OperationError whose code's kind has the response's status. The guards answer a server token that they do not know with a bare
   // 401, and any other answer reaches the caller as unexpected, with the first line of its body.
   private static WireAnswer<T> RefusalOf<T>(HttpResponseMessage response, string body, string request)
   {
      if (TryReadError(body) is { } error && OperationWire.StatusOf(error.Code.Kind) == response.StatusCode)
      {
         return new WireAnswer<T>.Refused(error);
      }

      return response.StatusCode == HttpStatusCode.Unauthorized
         ? new WireAnswer<T>.CredentialsRefused()
         : new WireAnswer<T>.Unexpected($"The lotsen server answered {request} with {(int)response.StatusCode} {response.ReasonPhrase}: {FirstLine(body)}");
   }

   // A body longer than the limit reads as its first bytes, which match no lotsen answer.
   private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
   {
      await using var stream = await content.ReadAsStreamAsync(cancellationToken);
      var buffer = new byte[MaxProbeBodyLength + 1];
      var length = 0;
      while (length < buffer.Length && await stream.ReadAsync(buffer.AsMemory(length), cancellationToken) is > 0 and var read)
      {
         length += read;
      }

      return buffer[..length];
   }

   // A server other than lotsen can answer with a whole page, which a terminal message must not repeat.
   private static string FirstLine(string body)
   {
      var line = body.AsSpan().TrimStart();
      if (line.IndexOfAny('\r', '\n') is >= 0 and var end)
      {
         line = line[..end];
      }

      line = line.TrimEnd();
      return line.Length > MaxReasonLength ? $"{line[..MaxReasonLength]}…" : line.ToString();
   }

   private static byte[] WriteObject(IEnumerable<KeyValuePair<string, JsonElement>> properties)
   {
      using var buffer = new MemoryStream();
      using (var writer = new Utf8JsonWriter(buffer))
      {
         writer.WriteStartObject();
         foreach (var (name, value) in properties)
         {
            writer.WritePropertyName(name);
            value.WriteTo(writer);
         }

         writer.WriteEndObject();
      }

      return buffer.ToArray();
   }

   private static OperationError? TryReadError(string body)
   {
      try
      {
         return JsonSerializer.Deserialize(body, LotsenJsonContext.Default.OperationError);
      }
      catch (JsonException)
      {
         return null;
      }
   }
}
