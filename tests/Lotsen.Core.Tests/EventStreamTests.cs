using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Operations;
using Lotsen.Server;
using Microsoft.Extensions.DependencyInjection;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>Covers the event stream on <c>/api/events</c>, read over HTTP with the bearer header as the UI reads it.</summary>
[Trait("Category", "Api")]
public sealed class EventStreamTests : IAsyncLifetime
{
   private const string CreateBody = $$"""{ "prUrl": "{{TestPlatform.PrUrl}}", "familiarity": "new" }""";

   private const string PlanBody = """
      { "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "title": "S", "chapter": "a", "kind": "risk" }],
        "fileMarks": [{ "path": "src/a.g.cs", "mark": "generated", "reason": "The build writes it." }] }
      """;

   private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(10);

   private readonly RecordingLoggerProvider _serverLog = new();

   private ServerHarness _harness = null!;

   public async ValueTask InitializeAsync() => _harness = await ServerHarness.StartAsync(configure: options => options with { LoggerProvider = _serverLog });

   public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

   [Fact]
   public async Task Created_tour_and_set_plan_arrive_on_every_connected_stream()
   {
      await using var first = await EventStream.OpenAsync(_harness);
      await using var second = await EventStream.OpenAsync(_harness);

      var tour = await _harness.AgentCallAsync("create_tour", CreateBody);
      var id = (string)tour["id"]!;
      var planned = await _harness.AgentCallAsync(On("set_plan", id), PlanBody);

      foreach (var stream in new[] { first, second })
      {
         var created = await stream.NextAsync(EventTimeout);
         Assert.Equal("tourCreated", created.Type);
         Assert.Equal(id, (string)created.Data["id"]!);
         Assert.Equal("new", (string)created.Data["familiarity"]!);
         Assert.Equal((string)tour["target"]!["url"]!, (string)created.Data["target"]!["url"]!);
         Assert.Equal(TestPlatform.PrLabel, (string)created.Data["label"]!);

         var plan = await stream.NextAsync(EventTimeout);
         Assert.Equal("planSet", plan.Type);
         Assert.Equal(id, (string)plan.Data["tourId"]!);
         Assert.Equal((string)planned["updatedAt"]!, (string)plan.Data["updatedAt"]!);
         Assert.True(JsonNode.DeepEquals(planned["plan"], plan.Data["plan"]), $"The event's plan differs: {plan.Data}");
         Assert.Equal("src/a.g.cs", (string)plan.Data["plan"]!["fileMarks"]![0]!["path"]!);
      }
   }

   [Fact]
   public async Task Rejected_operation_publishes_nothing()
   {
      // The unknown stop ID passes binding and plan rules, so the operation itself rejects it inside the store update.
      const string rejectedPlan = """{ "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "id": "nosuchstop", "title": "S", "chapter": "a", "kind": "risk" }] }""";
      await using var stream = await EventStream.OpenAsync(_harness);

      using var rejectedTour = await _harness.AgentSendJsonAsync("create_tour", """{ "prUrl": "x", "familiarity": "new" }""");
      var tour = await _harness.AgentCallAsync("create_tour", CreateBody);
      using var rejectedFirst = await _harness.AgentSendJsonAsync(On("set_plan", (string)tour["id"]!), rejectedPlan);
      var planned = await _harness.AgentCallAsync(On("set_plan", (string)tour["id"]!), PlanBody);
      using var rejectedLater = await _harness.AgentSendJsonAsync(On("set_plan", (string)tour["id"]!), rejectedPlan);
      var marker = await _harness.AgentCallAsync("create_tour", CreateBody);

      Assert.Equal(HttpStatusCode.BadRequest, rejectedTour.StatusCode);
      Assert.All([rejectedFirst, rejectedLater], response => Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode));
      var created = await stream.NextBesidesControlsAsync(EventTimeout);
      Assert.Equal(("tourCreated", (string)tour["id"]!), (created.Type, (string)created.Data["id"]!));
      var planSet = await stream.NextBesidesControlsAsync(EventTimeout);
      Assert.Equal("planSet", planSet.Type);
      Assert.True(JsonNode.DeepEquals(planned["plan"], planSet.Data["plan"]), $"The event's plan differs: {planSet.Data}");
      var next = await stream.NextBesidesControlsAsync(EventTimeout);
      Assert.Equal(("tourCreated", (string)marker["id"]!), (next.Type, (string)next.Data["id"]!));
   }

   [Fact]
   public async Task Stream_ends_when_the_server_stops()
   {
      await using var stream = await EventStream.OpenAsync(_harness);
      var stopwatch = Stopwatch.StartNew();

      await _harness.StopServerAsync().WaitAsync(EventTimeout, TestContext.Current.CancellationToken);
      var read = stream.ReadToEndAsync(EventTimeout);

      // Kestrel closes an aborted connection with a reset and logs the abort's reason on the line with "sending RST":
      // https://github.com/dotnet/aspnetcore/blob/b24508ef43a38a8d1914feb0dc6a82fac80a5da3/src/Servers/Kestrel/Transport.Sockets/src/Internal/SocketConnection.cs#L385-L391
      var failure = await Record.ExceptionAsync(() => read);
      Assert.True(failure is null, $"Reading the stream failed: {failure}\nThe server's log:\n{string.Join('\n', _serverLog.Lines)}");
      Assert.True(await read, "The stream did not end after the server stopped.");
      Assert.True(stopwatch.Elapsed < EventTimeout, $"Stopping took {stopwatch.Elapsed}.");
   }

   [Fact]
   public async Task Stream_is_an_uncached_event_stream()
   {
      await using var stream = await EventStream.OpenAsync(_harness);

      Assert.Equal("text/event-stream", stream.Response.Content.Headers.ContentType?.MediaType);
      Assert.Equal("no-store", stream.Response.Headers.CacheControl?.ToString());
   }

   /// <summary>The server's <c>TimeProvider</c> stays still, as a test host's world holds its clock, and only the liveness clock moves.</summary>
   [Fact]
   public async Task Keep_alive_follows_the_liveness_clock_while_the_tour_clock_stays_still()
   {
      var liveness = new StreamLiveness(TimeSpan.FromMilliseconds(50), 3);
      var tourClock = new ManualTimeProvider();
      var livenessClock = new ManualTimeProvider();
      await using var harness = await ServerHarness.StartAsync(tourClock, configure: options => options with { Liveness = liveness, LivenessClock = livenessClock });
      var request = ServerHarness.Request(HttpMethod.Get, "api/events", await harness.UiCredentialAsync());
      using var response = await harness.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
      using var wire = new StreamReader(await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));
      for (var line = 0; line < 3; line++)
      {
         await wire.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(EventTimeout, TestContext.Current.CancellationToken);
      }

      var due = livenessClock.GetUtcNow() + liveness.KeepAliveInterval;
      await Poll.UntilAsync(() => livenessClock.Timers.FirstOrDefault(timer => timer.Due == due), EventTimeout, () => $"No keep-alive timer is due at {due:O} on the liveness clock.");
      livenessClock.Advance(liveness.KeepAliveInterval);

      Assert.Equal(": keep-alive", await wire.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(EventTimeout, TestContext.Current.CancellationToken));
   }

   [Fact]
   public async Task Comment_reaches_the_stream_as_a_comment_line_after_the_events_published_before_it()
   {
      var request = ServerHarness.Request(HttpMethod.Get, "api/events", await _harness.UiCredentialAsync());
      using var response = await _harness.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
      using var wire = new StreamReader(await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));
      var broadcaster = _harness.Server.Services.GetRequiredService<EventBroadcaster>();

      broadcaster.Publish(ServerEvents.ForTourDeleted(new DeletedTour(Lotsen.Tours.TourId.New())));
      broadcaster.Publish(ServerEvent.Comment("marker 1"));

      var lines = new List<string>();
      for (var line = 0; line < 8; line++)
      {
         lines.Add((await wire.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(EventTimeout, TestContext.Current.CancellationToken))!);
      }

      Assert.Equal("event: hello", lines[0]);
      Assert.Equal("event: tourDeleted", lines[3]);
      Assert.Equal([": marker 1", ""], lines[6..]);
   }

   [Theory]
   [InlineData("a\nb")]
   [InlineData("a\rb")]
   public void Comment_refuses_a_line_break(string text) => Assert.Throws<ArgumentException>(() => ServerEvent.Comment(text));

   [Fact]
   public async Task Subscriber_that_falls_behind_is_disconnected_without_blocking_the_publisher()
   {
      var broadcaster = new EventBroadcaster();
      using var slow = broadcaster.Subscribe();
      using var fast = broadcaster.Subscribe();
      var fastReceived = 0;
      var deleted = ServerEvents.ForTourDeleted(new DeletedTour(Lotsen.Tours.TourId.New()));

      for (var i = 0; i < EventBroadcaster.SubscriberCapacity + 10; i++)
      {
         broadcaster.Publish(deleted);
         while (fast.Reader.TryRead(out _))
         {
            fastReceived++;
         }
      }

      var slowReceived = 0;
      await foreach (var _ in slow.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
      {
         slowReceived++;
      }

      Assert.Equal(EventBroadcaster.SubscriberCapacity + 10, fastReceived);
      Assert.Equal(EventBroadcaster.SubscriberCapacity, slowReceived);
      Assert.False(fast.Reader.Completion.IsCompleted);
   }
}
