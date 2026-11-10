using System.IO.Pipelines;
using System.Threading.Channels;
using Lotsen.Server;
using Lotsen.Sessions;

namespace Lotsen.Tests;

/// <summary>Covers the writer of every event stream: its hello and its keep-alive, over a pipe on a manual clock.</summary>
[Trait("Category", "Api")]
public sealed class ServerSentEventsTests : IAsyncDisposable
{
   private static readonly StreamLiveness Liveness = new(TimeSpan.FromMilliseconds(50), 3);
   private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

   private readonly ManualTimeProvider _clock = new();
   private readonly Pipe _pipe = new();
   private readonly StreamReader _wire;
   private readonly Channel<SessionEvent> _events = Channel.CreateUnbounded<SessionEvent>();
   private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
   private Task _pump = Task.CompletedTask;

   public ServerSentEventsTests()
   {
      _wire = new StreamReader(_pipe.Reader.AsStream());
   }

   [Fact]
   public async Task Stream_opens_with_a_hello_that_announces_the_liveness()
   {
      await StartAsync();

      Assert.Equal(["event: hello", """data: {"keepAliveMs":50,"silenceFactor":3}""", ""], await ReadLinesAsync(3));
   }

   [Fact]
   public async Task Keep_alive_follows_the_interval_of_quiet()
   {
      await StartAsync();
      await ReadLinesAsync(3);
      var armed = await KeepAliveDueAsync(_clock.GetUtcNow() + Liveness.KeepAliveInterval);

      _clock.Advance(Liveness.KeepAliveInterval - TimeSpan.FromMilliseconds(1));
      Assert.NotNull(armed.Due);
      _clock.Advance(TimeSpan.FromMilliseconds(1));

      Assert.Equal([": keep-alive", ""], await ReadLinesAsync(2));
   }

   /// <summary>The event comes 30 ms into the quiet, so a keep-alive timed from the stream's start would come 30 ms before the one timed from the event.</summary>
   [Fact]
   public async Task Event_starts_the_interval_of_quiet_again()
   {
      await StartAsync();
      await ReadLinesAsync(3);
      await KeepAliveDueAsync(_clock.GetUtcNow() + Liveness.KeepAliveInterval);
      _clock.Advance(TimeSpan.FromMilliseconds(30));

      await _events.Writer.WriteAsync(new SessionEvent("probe", """{"nonce":"n"}"""), TestContext.Current.CancellationToken);
      Assert.Equal(["event: probe", """data: {"nonce":"n"}""", ""], await ReadLinesAsync(3));
      await KeepAliveDueAsync(_clock.GetUtcNow() + Liveness.KeepAliveInterval);
      _clock.Advance(Liveness.KeepAliveInterval);

      Assert.Equal([": keep-alive", ""], await ReadLinesAsync(2));
   }

   public async ValueTask DisposeAsync()
   {
      await _stop.CancelAsync();
      await _pump.WaitAsync(Bound, TestContext.Current.CancellationToken);
      _wire.Dispose();
      _stop.Dispose();
   }

   private async Task StartAsync()
   {
      var writer = await new ServerSentEvents(Liveness, _clock).OpenAsync(_pipe.Writer.AsStream(), _stop.Token);
      _pump = writer.PumpAsync(_events.Reader, _stop.Token);
   }

   private async Task<string[]> ReadLinesAsync(int count)
   {
      var lines = new string[count];
      for (var i = 0; i < count; i++)
      {
         lines[i] = await _wire.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Bound, TestContext.Current.CancellationToken) ?? "<end>";
      }

      return lines;
   }

   // The pump arms its timer once it waits for the next event, which happens on its own thread after the write that the test read.
   private Task<ManualTimeProvider.ManualTimer> KeepAliveDueAsync(DateTimeOffset due) =>
      Poll.UntilAsync(() => _clock.Timers.FirstOrDefault(timer => timer.Due == due), Bound, () => $"No keep-alive timer is due at {due:O}: {string.Join(", ", _clock.Timers.Select(timer => timer.Due))}.");
}
