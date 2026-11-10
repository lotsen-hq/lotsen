using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lotsen.Hosting;
using Lotsen.Json;
using Lotsen.Operations;
using Lotsen.Pinning;
using Lotsen.Server;
using Lotsen.Sessions;
using Lotsen.Tours;
using Microsoft.Extensions.DependencyInjection;

namespace Lotsen.Tests;

/// <summary>Runs a lotsen server in the test process on a real loopback socket, and sends requests to it with chosen headers.</summary>
internal sealed class ServerHarness : IAsyncDisposable
{
   private bool _stopped;
   private AgentSessionConnection? _agent;

   private ServerHarness(DataDirectory dataDirectory, LotsenServer server, ServerState state, RecordingBrowserLauncher launcher, VersionInfo version)
   {
      DataDirectory = dataDirectory;
      Server = server;
      State = state;
      Launcher = launcher;
      Version = version;
      // Without a drain, disposing an unread event stream closes its connection at once, so the server sees an agent session's drop without a wait.
      Client = new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false, MaxResponseDrainSize = 0 })
      {
         BaseAddress = new Uri($"http://127.0.0.1:{state.Port}/"),
      };
   }

   public DataDirectory DataDirectory { get; }

   public LotsenServer Server { get; }

   /// <summary>The server's entry in the state file, read the way a client discovers it.</summary>
   public ServerState State { get; }

   public RecordingBrowserLauncher Launcher { get; }

   /// <summary>The versions the server reports, which an agent session must present to open.</summary>
   public VersionInfo Version { get; }

   /// <summary>The server's registry of agent sessions.</summary>
   public AgentSessions Sessions => Server.Services.GetRequiredService<AgentSessions>();

   /// <summary>The server's tour writer.</summary>
   public TourWriter Writer => Server.Services.GetRequiredService<TourWriter>();

   public HttpClient Client { get; }

   public int Port => State.Port;

   /// <summary>Timings under which a nudged iteration check runs at once and asks the platform each time, and the poll checks no tour, so a test sees each check that its call nudges.</summary>
   public static IterationSchedule ImmediateIterations { get; } = new(TimeSpan.Zero, TimeSpan.Zero, IterationSchedule.Default.PollInterval, TimeSpan.Zero);

   /// <summary>The server's UI credential, read from its services, so a test gets it without the one-time code that <see cref="UiCredentialAsync"/> exchanges.</summary>
   public string UiCredential => Server.Services.GetRequiredService<ServerCredentials>().Ui.Value;

   /// <summary>The server's credentials.</summary>
   public ServerCredentials Credentials => Server.Services.GetRequiredService<ServerCredentials>();

   /// <summary>The UI as a caller, from <see cref="Caller.Identify"/> with <see cref="UiCredential"/>, for tests that call the operations directly.</summary>
   public Caller UiCaller => Passes.Ui(Credentials, Sessions);

   /// <summary>The open session as a caller, from <see cref="Caller.Identify"/> with its credentials, for tests that call the operations directly.</summary>
   public Caller CallerOf(AgentSession session) => Passes.Agent(Credentials, Sessions, session);

   /// <summary>Starts a server with one test platform, whose pins wait until the server stops unless the platform or <paramref name="configure"/> says otherwise.</summary>
   /// <param name="version">The versions the server reports, or null for those of the core library alone.</param>
   /// <param name="dataDirectory">The data directory to start in, such as the one of a stopped server, or null for a new one.</param>
   /// <param name="platform">The server's one platform, or null for a <see cref="TestPlatform"/> without a pull request.</param>
   /// <param name="configure">Returns the options to start with from the harness's options, or null to keep them.</param>
   public static async Task<ServerHarness> StartAsync(
      TimeProvider? timeProvider = null,
      bool browserLaunches = true,
      VersionInfo? version = null,
      DataDirectory? dataDirectory = null,
      Func<ServerOptions, ServerOptions>? configure = null,
      TestPlatform? platform = null)
   {
      dataDirectory ??= new DataDirectory(Directory.CreateTempSubdirectory("lotsen-tests-").FullName);
      var launcher = new RecordingBrowserLauncher(browserLaunches);
      version ??= VersionInfo.FromAssemblies(typeof(LotsenServer).Assembly);
      var options = new ServerOptions(dataDirectory, version)
      {
         TimeProvider = timeProvider ?? TimeProvider.System,
         BrowserLauncher = launcher,
         Iterations = ImmediateIterations,
         Platforms = [_ => platform ?? new TestPlatform()],
      };

      var server = await LotsenServer.StartAsync(configure?.Invoke(options) ?? options, TestContext.Current.CancellationToken);
      var state = StateFile.TryRead(dataDirectory) ?? throw new InvalidOperationException("The server started without writing its state file.");
      return new ServerHarness(dataDirectory, server, state, launcher, version);
   }

   public HttpRequestMessage Request(HttpMethod method, string path, bool withToken = false)
   {
      var request = new HttpRequestMessage(method, path);
      if (withToken)
      {
         request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", State.Token);
      }

      return request;
   }

   /// <summary>Returns the request of an operation call with the rest of its input when given, and with the server token when <paramref name="withToken"/>.</summary>
   public HttpRequestMessage Request(OperationCall call, bool withToken = false, string? json = null)
   {
      var request = call.Request(json);
      if (withToken)
      {
         request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", State.Token);
      }

      return request;
   }

   /// <summary>Returns a request with this bearer credential, such as the UI credential, and the session credential of an agent session when given.</summary>
   public static HttpRequestMessage Request(HttpMethod method, string path, string credential, string? sessionCredential = null)
   {
      var request = new HttpRequestMessage(method, path);
      request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
      if (sessionCredential is not null)
      {
         request.Headers.Add(AgentSessions.CredentialHeader, sessionCredential);
      }

      return request;
   }

   /// <summary>Returns the request of an operation call with this bearer credential, such as the UI credential, and the session credential of an agent session when given.</summary>
   /// <param name="call">The operation and the IDs of its input.</param>
   /// <param name="json">The rest of the input, as <see cref="OperationCall.InputOf"/> takes it.</param>
   public static HttpRequestMessage Request(OperationCall call, string credential, string? sessionCredential = null, string? json = null)
   {
      var request = call.Request(json);
      request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
      if (sessionCredential is not null)
      {
         request.Headers.Add(AgentSessions.CredentialHeader, sessionCredential);
      }

      return request;
   }

   public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) => Client.SendAsync(request, TestContext.Current.CancellationToken);

   /// <summary>Sends the call with the rest of its input, when given.</summary>
   public Task<HttpResponseMessage> SendAsync(PreparedCall call, string? json = null) => SendAsync(call.Request(json));

   /// <summary>Calls an operation as the UI, with the UI credential, and the rest of the input when given.</summary>
   public Task<HttpResponseMessage> SendJsonAsync(OperationCall call, string? json = null) => SendAsync(Request(call, UiCredential, json: json));

   /// <summary>Calls an operation as the UI, requires success and returns the response body.</summary>
   public async Task<JsonNode> CallAsync(OperationCall call, string? json = null) => await SuccessAsync(await SendJsonAsync(call, json));

   /// <summary>Calls an operation as the CLI, with the server token alone, and the rest of the input when given.</summary>
   public Task<HttpResponseMessage> CliSendJsonAsync(OperationCall call, string? json = null) => SendAsync(Request(call, State.Token, json: json));

   /// <summary>Creates a tour that no session holds, and returns it as <c>create_tour</c> answers.</summary>
   /// <remarks>
   /// Only an agent session creates a tour, so this runs <c>create_tour</c>'s code with the pass of a session that ends after its admission, as when a session ends while its create is in
   /// flight. The tour then gets no binding, so no binding event follows its <c>tourCreated</c>.
   /// </remarks>
   public JsonNode CreateUnboundTour(string json)
   {
      var session = Sessions.TryOpen() ?? throw new InvalidOperationException("The server opens no agent session.");
      var pass = Passes.Admit<Access.AnySession.Pass>("create_tour", CallerOf(session));
      session.End();
      var created = Server.Services.GetRequiredService<TourPins>().CreateTour(pass, JsonSerializer.Deserialize(json, LotsenJsonContext.Default.CreateTourInput)!);
      Assert.True(created.Error is null, created.Error?.Reason);
      return JsonSerializer.SerializeToNode(created.Value, LotsenJsonContext.Default.Tour)!;
   }

   /// <summary>Returns the harness's own agent session, which opens on first use and closes when the server stops.</summary>
   /// <remarks>A tour that this session creates is bound to it, so the session can write the tour's plan and stops.</remarks>
   public async Task<AgentSessionConnection> AgentAsync() => _agent ??= await AgentSessionConnection.OpenAsync(this);

   /// <summary>Returns <see cref="AgentAsync"/>'s session as a caller, from <see cref="Caller.Identify"/> with its credentials, for tests that call the operations directly.</summary>
   public async Task<Caller> AgentCallerAsync() =>
      Passes.Identify(Credentials, Sessions, State.Token, (await AgentAsync()).Credential);

   /// <summary>Calls an operation as the session of <see cref="AgentAsync"/>, with the rest of the input when given.</summary>
   public async Task<HttpResponseMessage> AgentSendJsonAsync(OperationCall call, string? json = null) => await SendAsync((await AgentAsync()).Request(this, call), json);

   /// <summary>Calls an operation as the session of <see cref="AgentAsync"/>, requires success and returns the response body.</summary>
   public async Task<JsonNode> AgentCallAsync(OperationCall call, string? json = null) => await SuccessAsync(await AgentSendJsonAsync(call, json));

   /// <summary>Closes a session's connection and completes once the server settled the session's end, so a later read sees the released bindings and what the release stored.</summary>
   /// <remarks><see cref="AgentSession.WhenEnded"/> tells that the end is recorded, and <see cref="TourWriter.SettleEnds"/> then settles it here, unless the server's sweep settled it first.</remarks>
   public async Task DropSessionAsync(AgentSessionConnection session)
   {
      var onServer = Sessions.Find(session.Id);
      await session.DisposeAsync();
      if (onServer is not null)
      {
         await Poll.EndedAsync(onServer, TimeSpan.FromSeconds(10), $"{session.Id} did not end after its connection dropped.");
      }

      Writer.SettleEnds();
   }

   /// <summary>Returns the binding of the tour as the tour list serves it, or null when no session holds the tour.</summary>
   public async Task<JsonNode?> BindingOfAsync(string tourId) =>
      (await CallAsync("list_tours"))["tours"]!.AsArray().Single(tour => (string)tour!["id"]! == tourId)!["binding"]?.DeepClone();

   /// <summary>Returns the ID of the session that holds the tour, as the tour list serves it, or null when none does.</summary>
   public async Task<string?> BoundSessionOfAsync(string tourId) => (string?)(await BindingOfAsync(tourId))?["sessionId"];

   /// <summary>Returns the stored tour, read past the writer, or null when no tour has the ID.</summary>
   public Tour? StoredTour(TourId id) => new FileTourStore(DataDirectory).Get(id);

   /// <summary>Returns the stored review marks, read past the writer, or null when no tour has the ID.</summary>
   public IReadOnlyList<ReviewMark>? StoredReviewMarks(TourId id) => new FileTourStore(DataDirectory).GetReviewMarks(id);

   /// <summary>Applies the change to the stored tour past the writer's checks, under the writer's lock, so no write of the server's runs at once.</summary>
   /// <returns>The stored tour, or null when no tour has the ID.</returns>
   public Tour? ChangeStored(TourId id, Func<Tour, Tour> change) => Writer.Exclusive(_ => new FileTourStore(DataDirectory).Change(id, change));

   /// <summary>Applies the change to the stored review marks past the writer, under the writer's lock.</summary>
   /// <returns>The stored marks, or null when no tour has the ID.</returns>
   public IReadOnlyList<ReviewMark>? ChangeStoredMarks(TourId id, Func<IReadOnlyList<ReviewMark>, IReadOnlyList<ReviewMark>> change) =>
      Writer.Exclusive(_ => new FileTourStore(DataDirectory).ChangeMarks(id, change));

   /// <summary>Completes when no background job of the server, a pin or an iteration check, runs.</summary>
   public Task PinsIdleAsync() => Server.Services.GetRequiredService<BackgroundJobs>().WhenIdleAsync();

   /// <summary>Stops the server, as Ctrl-C would, before the harness is disposed.</summary>
   public async Task StopServerAsync()
   {
      if (!_stopped)
      {
         _stopped = true;
         await Server.DisposeAsync();
         if (_agent is not null)
         {
            await _agent.DisposeAsync();
         }
      }
   }

   /// <summary>Asks the server to open the UI and returns the one-time code carried in the link it launched.</summary>
   public async Task<string> IssueCodeAsync()
   {
      using var response = await SendAsync(Request(OperationCatalog.OpenUi.Name, State.Token));
      response.EnsureSuccessStatusCode();
      var url = Launcher.LastUrl ?? throw new InvalidOperationException("The server launched no browser.");
      const string marker = "#code=";
      var index = url.IndexOf(marker, StringComparison.Ordinal);
      Assert.True(index >= 0, $"The launched URL carries no code: {url}");
      return url[(index + marker.Length)..];
   }

   public Task<HttpResponseMessage> ExchangeAsync(string code)
   {
      var request = Request(HttpMethod.Post, "api/ui-credential");
      request.Content = new StringContent(new JsonObject { ["code"] = code }.ToJsonString(), Encoding.UTF8, "application/json");
      return SendAsync(request);
   }

   /// <summary>Exchanges a new one-time code and returns the UI credential it yields.</summary>
   public async Task<string> UiCredentialAsync()
   {
      using var response = await ExchangeAsync(await IssueCodeAsync());
      response.EnsureSuccessStatusCode();
      return (string)JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["credential"]!;
   }

   /// <summary>Asks the server who the caller of a request with these credentials is, and returns the status and the answer's JSON, or null when the request was refused.</summary>
   public async Task<(System.Net.HttpStatusCode Status, JsonNode? Caller)> WhoAsync(string credential, string? sessionCredential = null)
   {
      using var response = await SendAsync(Request(HttpMethod.Get, "api/caller", credential, sessionCredential));
      var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
      return (response.StatusCode, response.IsSuccessStatusCode ? JsonNode.Parse(body) : null);
   }

   private static async Task<JsonNode> SuccessAsync(HttpResponseMessage response)
   {
      using (response)
      {
         var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
         Assert.True(response.IsSuccessStatusCode, $"{response.RequestMessage?.RequestUri} returned {(int)response.StatusCode}: {body}");
         return SuccessBody.Output(body);
      }
   }

   public async ValueTask DisposeAsync()
   {
      Client.Dispose();
      await StopServerAsync();
      try
      {
         TestDirectories.Delete(DataDirectory.Path, TimeSpan.FromSeconds(5));
      }
      catch (IOException)
      {
      }
   }
}

/// <summary>A browser launcher that records the URLs it is given instead of opening them.</summary>
internal sealed class RecordingBrowserLauncher : IBrowserLauncher
{
   private readonly List<string> _urls = [];
   private readonly bool _launches;

   public RecordingBrowserLauncher(bool launches)
   {
      _launches = launches;
   }

   public string? LastUrl
   {
      get
      {
         lock (_urls)
         {
            return _urls.Count == 0 ? null : _urls[^1];
         }
      }
   }

   public Task<bool> TryLaunchAsync(string url, CancellationToken cancellationToken)
   {
      lock (_urls)
      {
         _urls.Add(url);
      }

      return Task.FromResult(_launches);
   }
}
