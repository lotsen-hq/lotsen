using System.Net;
using Lotsen.Hosting;
using Lotsen.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>Covers <c>POST /api/shutdown</c>, the route through which <c>lotsen stop</c> stops a server: who may call it, and that the server then stops as on Ctrl-C.</summary>
public sealed class ShutdownRouteTests : IAsyncLifetime
{
   private const string Route = "api/shutdown";

   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

   private ServerHarness _harness = null!;

   public async ValueTask InitializeAsync() => _harness = await ServerHarness.StartAsync();

   public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

   [Fact]
   [Trait("Category", "Security")]
   public async Task Shutdown_without_a_credential_is_refused_and_the_server_keeps_running()
   {
      using var response = await _harness.SendAsync(_harness.Request(HttpMethod.Post, Route));

      Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
      Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
      await AssertStillRunningAsync();
   }

   [Fact]
   [Trait("Category", "Security")]
   public async Task Shutdown_with_the_ui_credential_is_refused_and_the_server_keeps_running()
   {
      var credential = await _harness.UiCredentialAsync();

      using var response = await _harness.SendAsync(ServerHarness.Request(HttpMethod.Post, Route, credential));

      Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
      Assert.Contains("lotsen stop", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
      await AssertStillRunningAsync();
   }

   /// <summary>The server is a release build, so its reader may have only the plugin's npx and no lotsen on PATH.</summary>
   [Fact]
   public async Task Ui_refusal_of_a_release_build_names_lotsen_stop_and_the_npx_form_of_its_release()
   {
      await using var release = await ServerHarness.StartAsync(version: new VersionInfo("0.2.0+e23502b (8549f42b)", VersionInfo.CurrentProtocol));

      using var response = await release.SendAsync(ServerHarness.Request(HttpMethod.Post, Route, await release.UiCredentialAsync()));
      var reason = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

      Assert.Contains("`lotsen stop` in a terminal, or `npx -y lotsen@0.2.0 stop` where lotsen is not on PATH", reason, StringComparison.Ordinal);
   }

   [Fact]
   [Trait("Category", "Security")]
   public async Task Shutdown_as_an_agent_session_is_refused_and_the_server_keeps_running()
   {
      await using var connection = await AgentSessionConnection.OpenAsync(_harness);

      using var response = await _harness.SendAsync(connection.Request(_harness, HttpMethod.Post, Route));

      Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
      await AssertStillRunningAsync();
   }

   [Fact]
   [Trait("Category", "Lifecycle")]
   public async Task Shutdown_with_the_server_token_answers_before_the_server_stops_and_then_stops_it()
   {
      var stopped = _harness.Server.WaitForShutdownAsync(TestContext.Current.CancellationToken);

      using var response = await _harness.SendAsync(_harness.Request(HttpMethod.Post, Route, withToken: true));

      Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
      await stopped.WaitAsync(Timeout, TestContext.Current.CancellationToken);
   }

   [Fact]
   [Trait("Category", "Lifecycle")]
   public void Server_stops_within_a_timeout_shorter_than_the_wait_of_lotsen_stop()
   {
      var hostTimeout = _harness.Server.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout;

      Assert.Equal(LotsenServer.ShutdownTimeout, hostTimeout);
      Assert.True(hostTimeout < ServerShutdown.Timeout, $"The host waits {hostTimeout} to stop, and lotsen stop waits {ServerShutdown.Timeout}.");
   }

   /// <summary>Race: the shutdown arrives while a pin waits on its platform and an agent session holds its stream open.</summary>
   [Fact]
   [Trait("Category", "Lifecycle")]
   public async Task Shutdown_while_a_pin_runs_and_a_session_is_open_stops_the_server_and_the_next_start_pins_the_tour_again()
   {
      var platform = new TestPlatform();
      await using var first = await ServerHarness.StartAsync(platform: platform);
      var id = (string)(await first.AgentCallAsync("create_tour", $$"""{ "prUrl": "{{TestPlatform.PrUrl}}", "familiarity": "some" }"""))["id"]!;
      await platform.FirstResolve.WaitAsync(Timeout, TestContext.Current.CancellationToken);

      using var response = await first.SendAsync(first.Request(HttpMethod.Post, Route, withToken: true));
      await first.Server.WaitForShutdownAsync(TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken);
      await first.StopServerAsync().WaitAsync(Timeout, TestContext.Current.CancellationToken);

      Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
      Assert.False(File.Exists(first.DataDirectory.StateFilePath), "The stopped server left its state file.");
      using (var claim = ClaimLock.TryTake(first.DataDirectory))
      {
         Assert.NotNull(claim);
      }

      var next = new TestPlatform();
      await using var second = await ServerHarness.StartAsync(dataDirectory: first.DataDirectory, platform: next);
      await next.FirstResolve.WaitAsync(Timeout, TestContext.Current.CancellationToken);
      Assert.Equal("pinning", (string)(await second.CallAsync(On("read_state", id)))["pin"]!["state"]!);
   }

   private async Task AssertStillRunningAsync()
   {
      await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
      Assert.False(_harness.Server.WaitForShutdownAsync(TestContext.Current.CancellationToken).IsCompleted, "The server stopped.");
      using var tours = await _harness.SendJsonAsync("list_tours");
      Assert.Equal(HttpStatusCode.OK, tours.StatusCode);
   }
}
