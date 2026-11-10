using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Lotsen.Tests;

/// <summary>
/// Covers how <see cref="HttpClient.Timeout"/> applies to a request sent with <see cref="HttpCompletionOption.ResponseHeadersRead"/>, against a Kestrel server in the test process.
/// <see cref="Client.ServerClient"/> relies on it to read an agent session's stream with the same client as its tool calls.
/// </summary>
[Trait("Category", "Adapter")]
public sealed class HttpClientTimeoutTests : IAsyncDisposable
{
   private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(1);
   private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

   private readonly CancellationTokenSource _stop = new();
   private readonly TaskCompletionSource _secondLine = new(TaskCreationOptions.RunContinuationsAsynchronously);
   private readonly TaskCompletionSource _end = new(TaskCreationOptions.RunContinuationsAsynchronously);
   private WebApplication? _app;

   /// <summary>
   /// The held request starts after the stream's and fails with the same timeout, so the stream's timeout has passed before the server writes the second line.
   /// </summary>
   [Fact]
   public async Task Response_headers_read_stream_outlives_the_client_timeout_that_a_held_request_fails_with()
   {
      using var client = new HttpClient { BaseAddress = await StartAsync(), Timeout = ClientTimeout };
      using var response = await client.GetAsync("stream", HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken).WaitAsync(Bound, TestContext.Current.CancellationToken);
      using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));
      Assert.Equal("first", await reader.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Bound, TestContext.Current.CancellationToken));

      var held = await Record.ExceptionAsync(() => client.GetAsync("held", HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken).WaitAsync(Bound, TestContext.Current.CancellationToken));

      Assert.IsType<TimeoutException>(Assert.IsType<TaskCanceledException>(held).InnerException);
      _secondLine.SetResult();
      Assert.Equal("second", await reader.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Bound, TestContext.Current.CancellationToken));
      _end.SetResult();
      Assert.Null(await reader.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Bound, TestContext.Current.CancellationToken));
   }

   public async ValueTask DisposeAsync()
   {
      await _stop.CancelAsync();
      if (_app is not null)
      {
         await _app.StopAsync(CancellationToken.None);
         await _app.DisposeAsync();
      }

      _stop.Dispose();
   }

   private static async Task WriteLineAsync(HttpContext context, string line, CancellationToken cancellationToken)
   {
      await context.Response.WriteAsync(line + "\n", cancellationToken);
      await context.Response.Body.FlushAsync(cancellationToken);
   }

   /// <summary>Starts a server whose <c>/stream</c> sends its headers and a first line at once, and whose <c>/held</c> never sends its headers.</summary>
   /// <returns>The URL of the server's root, ending in a slash.</returns>
   private async Task<Uri> StartAsync()
   {
      var builder = WebApplication.CreateSlimBuilder();
      builder.Logging.ClearProviders();
      builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
      _app = builder.Build();
      _app.MapGet("/stream", async context =>
      {
         using var request = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _stop.Token);
         context.Response.ContentType = "text/plain";
         await context.Response.StartAsync(request.Token);
         await WriteLineAsync(context, "first", request.Token);
         await _secondLine.Task.WaitAsync(request.Token);
         await WriteLineAsync(context, "second", request.Token);
         await _end.Task.WaitAsync(request.Token);
      });
      _app.MapGet("/held", async context =>
      {
         using var request = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _stop.Token);
         try
         {
            await Task.Delay(Timeout.Infinite, request.Token);
         }
         catch (OperationCanceledException)
         {
         }
      });

      await _app.StartAsync(TestContext.Current.CancellationToken);
      return new Uri(_app.Urls.Single().TrimEnd('/') + "/");
   }
}
