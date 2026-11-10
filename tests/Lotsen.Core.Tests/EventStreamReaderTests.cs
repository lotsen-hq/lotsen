using System.IO.Pipelines;
using System.Net;
using System.Text;
using Lotsen.Client;

namespace Lotsen.Tests;

/// <summary>Covers the one C# reader of an event stream: its hello and the silence limit that the hello announces, over a pipe on a manual clock.</summary>
[Trait("Category", "Adapter")]
public sealed class EventStreamReaderTests : IDisposable
{
   private const string Hello = "event: hello\ndata: {\"keepAliveMs\":50,\"silenceFactor\":3}\n\n";

   private static readonly TimeSpan SilenceLimit = TimeSpan.FromMilliseconds(150);
   private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

   private readonly ManualTimeProvider _clock = new();
   private readonly Pipe _pipe = new();
   private readonly WatchedBody _body;
   private readonly EventStreamReader _reader;

   public EventStreamReaderTests()
   {
      _body = new WatchedBody(_pipe.Reader.AsStream(), _clock);
      _reader = new EventStreamReader(_body, _clock);
   }

   [Fact]
   public async Task Reader_returns_the_events_after_the_hello()
   {
      await SendAsync(Hello + ": comment\n\nevent: probe\ndata: {\"nonce\":\"n\"}\n\n");

      Assert.Equal(new StreamEvent("probe", """{"nonce":"n"}"""), await NextAsync());
   }

   [Fact]
   public async Task Reader_gives_up_after_the_announced_silence_limit()
   {
      await SendAsync(Hello);
      var next = NextAsync();
      var armed = await SilenceDueAsync(_body, _clock.GetUtcNow() + SilenceLimit);

      _clock.Advance(SilenceLimit - TimeSpan.FromMilliseconds(1));
      Assert.NotNull(armed.Due);
      _clock.Advance(TimeSpan.FromMilliseconds(1));

      await Assert.ThrowsAsync<SilenceException>(() => next);
   }

   [Fact]
   public async Task Reader_gives_up_on_a_stream_that_sends_no_hello_within_the_hello_limit()
   {
      var next = NextAsync();
      var armed = await SilenceDueAsync(_body, _clock.GetUtcNow() + EventStreamReader.HelloLimit);

      _clock.Advance(EventStreamReader.HelloLimit - TimeSpan.FromMilliseconds(1));
      Assert.NotNull(armed.Due);
      _clock.Advance(TimeSpan.FromMilliseconds(1));

      await Assert.ThrowsAsync<SilenceException>(() => next);
   }

   /// <summary>The keep-alive comes 100 ms into the silence, so a limit timed from the hello would end the stream 100 ms before the one timed from the keep-alive.</summary>
   [Fact]
   public async Task Keep_alive_starts_the_silence_limit_again()
   {
      await SendAsync(Hello);
      var next = NextAsync();
      await SilenceDueAsync(_body, _clock.GetUtcNow() + SilenceLimit);
      _clock.Advance(TimeSpan.FromMilliseconds(100));

      await SendAsync(": keep-alive\n\n");
      var armed = await SilenceDueAsync(_body, _clock.GetUtcNow() + SilenceLimit);
      _clock.Advance(SilenceLimit - TimeSpan.FromMilliseconds(1));
      Assert.NotNull(armed.Due);
      await SendAsync("event: probe\ndata: {}\n\n");

      Assert.Equal(new StreamEvent("probe", "{}"), await next);
   }

   [Theory]
   [InlineData("event: probe\ndata: {}\n\n")]
   [InlineData("data: {\"keepAliveMs\":50,\"silenceFactor\":3}\n\n")]
   [InlineData("event: hello\ndata: {\"keepAliveMs\":0,\"silenceFactor\":3}\n\n")]
   [InlineData("event: hello\ndata: {\"keepAliveMs\":50,\"silenceFactor\":0}\n\n")]
   [InlineData("event: hello\ndata: {\"keepAliveMs\":50}\n\n")]
   [InlineData("event: hello\ndata: {\"keepAliveMs\":2147483647,\"silenceFactor\":2}\n\n")]
   [InlineData("event: hello\ndata: not json\n\n")]
   public async Task Stream_without_a_valid_hello_first_is_refused(string first)
   {
      await SendAsync(first + "event: probe\ndata: {}\n\n");

      await Assert.ThrowsAsync<InvalidDataException>(NextAsync);
   }

   [Fact]
   public async Task Agent_session_stream_ends_after_the_announced_silence_limit()
   {
      var pipe = new Pipe();
      await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(Hello + "event: agentSession\ndata: {\"sessionId\":\"s\",\"credential\":\"c\"}\n\n"), TestContext.Current.CancellationToken);
      var body = new WatchedBody(pipe.Reader.AsStream(), _clock);
      using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
      using var session = await AgentSessionStream.OpenAsync(response, _clock, TestContext.Current.CancellationToken);
      var next = session.NextAsync(TestContext.Current.CancellationToken);

      await SilenceDueAsync(body, _clock.GetUtcNow() + SilenceLimit);
      _clock.Advance(SilenceLimit);

      Assert.Equal("s", session.SessionId);
      Assert.Null(await next.WaitAsync(Bound, TestContext.Current.CancellationToken));
   }

   [Fact]
   public async Task Agent_session_open_fails_when_the_headers_come_and_no_hello_follows()
   {
      var pipe = new Pipe();
      var body = new WatchedBody(pipe.Reader.AsStream(), _clock);
      using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
      var open = AgentSessionStream.OpenAsync(response, _clock, TestContext.Current.CancellationToken);

      await SilenceDueAsync(body, _clock.GetUtcNow() + EventStreamReader.HelloLimit);
      _clock.Advance(EventStreamReader.HelloLimit);

      await Assert.ThrowsAsync<HttpRequestException>(() => open.WaitAsync(Bound, TestContext.Current.CancellationToken));
   }

   public void Dispose() => _reader.Dispose();

   private async Task SendAsync(string text)
   {
      await _pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(text), TestContext.Current.CancellationToken);
   }

   private Task<StreamEvent?> NextAsync() => _reader.NextAsync(TestContext.Current.CancellationToken).WaitAsync(Bound, TestContext.Current.CancellationToken);

   // The reader waits for the next line on its own thread, after the test wrote the last one.
   private static Task<ManualTimeProvider.ManualTimer> SilenceDueAsync(WatchedBody body, DateTimeOffset due) =>
      Poll.UntilAsync(() => body.WaitingOn is { } limit && limit.Due == due ? limit : null, Bound, () => $"The reader waits on no silence limit due at {due:O}, but on one due at {body.WaitingOn?.Due:O}.");

   /// <summary>A reader's body that tells which silence limit the reader waits on: the clock's newest timer when the current read of the body began.</summary>
   /// <remarks>
   /// The reader arms a limit for each line, also for a line that it already holds in its buffer, and disposes that limit once it returns the line. It reads the body only when it holds no
   /// complete line, so the newest timer at that moment is the limit it waits on.
   /// </remarks>
   private sealed class WatchedBody : Stream
   {
      private readonly Stream _inner;
      private readonly ManualTimeProvider _clock;
      private volatile ManualTimeProvider.ManualTimer? _waitingOn;

      public WatchedBody(Stream inner, ManualTimeProvider clock)
      {
         _inner = inner;
         _clock = clock;
      }

      /// <summary>The silence limit that the read of the body in progress waits under, or null while no read of the body is in progress.</summary>
      public ManualTimeProvider.ManualTimer? WaitingOn => _waitingOn;

      public override bool CanRead => true;

      public override bool CanSeek => false;

      public override bool CanWrite => false;

      public override long Length => throw new NotSupportedException();

      public override long Position
      {
         get => throw new NotSupportedException();
         set => throw new NotSupportedException();
      }

      public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
      {
         _waitingOn = _clock.LastTimer;
         try
         {
            return await _inner.ReadAsync(buffer, cancellationToken);
         }
         finally
         {
            _waitingOn = null;
         }
      }

      public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

      public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

      public override void Flush()
      {
      }

      public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

      public override void SetLength(long value) => throw new NotSupportedException();

      public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

      protected override void Dispose(bool disposing)
      {
         if (disposing)
         {
            _inner.Dispose();
         }

         base.Dispose(disposing);
      }
   }
}
