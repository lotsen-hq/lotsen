using System.Text.Json;
using Lotsen.Hosting;
using Lotsen.Json;
using Lotsen.Server;

namespace Lotsen.Tests;

/// <summary>Covers how <see cref="ServerShutdown"/> acts on each answer of <see cref="ServerDiscovery"/> and waits for the server to exit, against stand-ins for servers that misbehave.</summary>
[Trait("Category", "Lifecycle")]
public sealed class ServerShutdownTests : IDisposable
{
   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

   // A first line longer than any quoted reason, and a second line that a reason never holds.
   private static readonly string ForeignPage = $"<!DOCTYPE HTML>{new string('x', 300)}\n<html><head><title>Error response</title></head></html>";

   private readonly DataDirectory _dataDirectory = new(Directory.CreateTempSubdirectory("lotsen-tests-").FullName);

   [Fact]
   public async Task Shutdown_without_a_data_directory_finds_no_server_and_creates_nothing()
   {
      var missing = new DataDirectory(Path.Combine(_dataDirectory.Path, "missing"));

      var stopped = await ServerShutdown.StopAsync(missing, Timeout, TestContext.Current.CancellationToken);

      Assert.Null(stopped);
      Assert.False(Directory.Exists(missing.Path), "The shutdown created the data directory.");
   }

   /// <summary>The state file names the live test process, as when a crashed server's PID was reused, and a port that refuses connections.</summary>
   [Fact]
   public async Task Shutdown_with_a_state_file_whose_port_refuses_and_a_free_claim_lock_finds_no_server()
   {
      LeaveFreeClaimLock();
      using var stalePort = HeldPort.Refusing();
      StateFile.Write(_dataDirectory, new ServerState(Environment.ProcessId, "127.0.0.1", stalePort.Port, "stale-token"));

      var stopped = await ServerShutdown.StopAsync(_dataDirectory, Timeout, TestContext.Current.CancellationToken);

      Assert.Null(stopped);
   }

   /// <summary>The state file names the live test process and the port of another HTTP server, as when a crashed server's PID and port were both reused.</summary>
   [Fact]
   public async Task Shutdown_with_a_state_file_naming_another_http_server_and_a_free_claim_lock_finds_no_server_and_sends_it_no_token()
   {
      LeaveFreeClaimLock();
      await using var server = new RecordingHttpServer(answer: _ => new HttpAnswer(200, ForeignPage));
      StateFile.Write(_dataDirectory, new ServerState(Environment.ProcessId, "127.0.0.1", server.BaseUrl.Port, "stale-token"));

      var stopped = await ServerShutdown.StopAsync(_dataDirectory, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken);

      Assert.Null(stopped);
      Assert.DoesNotContain(server.Requests, request => request.Contains("Authorization:", StringComparison.OrdinalIgnoreCase));
      Assert.DoesNotContain(server.Requests, request => request.StartsWith("POST /api/shutdown ", StringComparison.Ordinal));
   }

   /// <summary>
   /// The claim lock is free, and the listener accepts the connection and never answers, so the probe's deadline ends the wait. The stop's whole budget outlasts the test's wait, so a
   /// probe that waited for the budget would fail the test.
   /// </summary>
   [Fact]
   public async Task Free_claim_lock_with_a_listener_that_never_answers_finds_no_server_once_the_probe_times_out()
   {
      LeaveFreeClaimLock();
      await using var server = new RecordingHttpServer(hold: true);
      StateFile.Write(_dataDirectory, new ServerState(Environment.ProcessId, "127.0.0.1", server.BaseUrl.Port, "stale-token"));

      var stopped = await ServerShutdown.StopAsync(_dataDirectory, ServerShutdown.Timeout, TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken);

      Assert.Null(stopped);
      Assert.DoesNotContain(server.Requests, request => request.Contains("Authorization:", StringComparison.OrdinalIgnoreCase));
   }

   /// <summary>The test holds the claim lock, and the listener accepts the connection and never answers, so the stop budget ends the wait.</summary>
   [Fact]
   public async Task Claim_lock_held_with_a_listener_that_never_answers_fails_the_shutdown_naming_its_pid()
   {
      using var claim = HoldClaimLock();
      await using var server = new RecordingHttpServer(hold: true);
      StateFile.Write(_dataDirectory, new ServerState(Environment.ProcessId, "127.0.0.1", server.BaseUrl.Port, "token"));

      var failure = await Record.ExceptionAsync(() => ServerShutdown.StopAsync(_dataDirectory, TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken));

      Assert.Contains($"PID {Environment.ProcessId}", Assert.IsType<ServerShutdownException>(failure).Message);
      Assert.Contains("did not answer", failure.Message);
   }

   /// <summary>
   /// The claim lock is free, as for a server that runs with .NET's file locking off, and the stand-in answers the version request as lotsen does: with the refusal of a request without the server
   /// token, or with lotsen's versions. The process that the state file names, the test process, never exits.
   /// </summary>
   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public async Task Shutdown_with_a_free_claim_lock_and_a_listener_that_answers_as_lotsen_sends_the_shutdown(bool versionsWithoutToken)
   {
      LeaveFreeClaimLock();
      var versions = new HttpAnswer(200, JsonSerializer.Serialize(VersionInfo.FromAssemblies(typeof(ServerShutdown).Assembly), LotsenJsonContext.Default.VersionInfo));
      await using var server = new RecordingHttpServer(answer: target => target switch
      {
         "/api/version" => versionsWithoutToken ? versions : new HttpAnswer(401, RequestGuards.NeedsCredentialReason),
         "/api/shutdown" => new HttpAnswer(202),
         _ => new HttpAnswer(404),
      });
      StateFile.Write(_dataDirectory, new ServerState(Environment.ProcessId, "127.0.0.1", server.BaseUrl.Port, "token"));

      var failure = await Record.ExceptionAsync(() => ServerShutdown.StopAsync(_dataDirectory, TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken));

      Assert.Contains("did not exit", Assert.IsType<ServerShutdownException>(failure).Message);
      Assert.Contains(server.Requests, request => request.StartsWith("GET /api/version ", StringComparison.Ordinal) && !request.Contains("Authorization:", StringComparison.OrdinalIgnoreCase));
      Assert.Contains(server.Requests, request => request.StartsWith("POST /api/shutdown ", StringComparison.Ordinal) && request.Contains("Authorization: Bearer token", StringComparison.Ordinal));
   }

   /// <summary>The test holds the claim lock, as a server that has closed its listener and still stops does, and the process that the state file names, the test process, never exits.</summary>
   [Fact]
   public async Task Claim_lock_held_with_a_state_file_whose_port_refuses_waits_for_the_server_to_exit()
   {
      using var claim = HoldClaimLock();
      using var closedPort = HeldPort.Refusing();
      StateFile.Write(_dataDirectory, new ServerState(Environment.ProcessId, "127.0.0.1", closedPort.Port, "token"));

      // Windows fails a connect to a refusing port only after its retries, and the shutdown makes two: the probe and the request.
      var timeout = OperatingSystem.IsWindows() ? TimeSpan.FromSeconds(6) : TimeSpan.FromMilliseconds(500);
      var failure = await Record.ExceptionAsync(() => ServerShutdown.StopAsync(_dataDirectory, timeout, TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken));

      Assert.Contains($"PID {Environment.ProcessId}", Assert.IsType<ServerShutdownException>(failure).Message);
      Assert.Contains("did not exit", failure.Message);
   }

   /// <summary>
   /// The test holds the claim lock through the first discovery, as a lock probe of another process can make a free lock read as held, and releases it once the shutdown request arrives.
   /// The listener resets every connection, and the process that the state file names, the test process, never exits.
   /// </summary>
   [Fact]
   public async Task Claim_lock_released_before_a_failed_shutdown_request_finds_no_server()
   {
      using var claim = HoldClaimLock();
      await using var listener = new NoHttpListener(NoHttpPeer.Resets, Task.CompletedTask, received: request => OnShutdownRequest(request, claim.Dispose));
      StateFile.Write(_dataDirectory, new ServerState(Environment.ProcessId, "127.0.0.1", listener.Port, "token"));

      var stopped = await ServerShutdown.StopAsync(_dataDirectory, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken);

      Assert.Null(stopped);
   }

   /// <summary>A server takes the claim lock over and writes its own state file once the shutdown request arrives, so the state file no longer names the process that never exits.</summary>
   [Fact]
   public async Task State_file_replaced_before_a_failed_shutdown_request_finds_no_server()
   {
      using var claim = HoldClaimLock();
      using var newPort = HeldPort.Refusing();
      await using var listener = new NoHttpListener(
         NoHttpPeer.Resets, Task.CompletedTask, received: request => OnShutdownRequest(request, () => StateFile.Write(_dataDirectory, new ServerState(Environment.ProcessId, "127.0.0.1", newPort.Port, "new"))));
      StateFile.Write(_dataDirectory, new ServerState(Environment.ProcessId, "127.0.0.1", listener.Port, "token"));

      var stopped = await ServerShutdown.StopAsync(_dataDirectory, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken);

      Assert.Null(stopped);
   }

   /// <summary>The test keeps the claim lock and deletes the state file once the shutdown request arrives, as a server does while it stops after it closed its listener.</summary>
   [Fact]
   public async Task State_file_deleted_under_a_held_claim_lock_before_a_failed_shutdown_request_waits_for_the_server_to_exit()
   {
      using var claim = HoldClaimLock();
      await using var listener = new NoHttpListener(NoHttpPeer.Resets, Task.CompletedTask, received: request => OnShutdownRequest(request, () => File.Delete(_dataDirectory.StateFilePath)));
      StateFile.Write(_dataDirectory, new ServerState(Environment.ProcessId, "127.0.0.1", listener.Port, "token"));

      var failure = await Record.ExceptionAsync(() => ServerShutdown.StopAsync(_dataDirectory, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken));

      Assert.Contains($"PID {Environment.ProcessId}", Assert.IsType<ServerShutdownException>(failure).Message);
      Assert.Contains("did not exit", failure.Message);
   }

   /// <summary>
   /// The claim lock is free, as for a server that runs with .NET's file locking off, and the listener answers the first version request as lotsen does, then resets every connection, as
   /// that server does once it stops. The process that the state file names, the test process, never exits.
   /// </summary>
   [Fact]
   public async Task Server_that_answered_as_lotsen_and_fails_the_shutdown_request_is_waited_for()
   {
      LeaveFreeClaimLock();
      await using var listener = new NoHttpListener(NoHttpPeer.Resets, Task.CompletedTask, lotsenProbes: 1);
      StateFile.Write(_dataDirectory, new ServerState(Environment.ProcessId, "127.0.0.1", listener.Port, "token"));

      var failure = await Record.ExceptionAsync(() => ServerShutdown.StopAsync(_dataDirectory, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken));

      Assert.Contains($"PID {Environment.ProcessId}", Assert.IsType<ServerShutdownException>(failure).Message);
      Assert.Contains("did not exit", failure.Message);
   }

   /// <summary>The stand-in accepts the shutdown, and the process that the state file names, the test process, never exits.</summary>
   [Fact]
   public async Task Server_that_does_not_exit_in_time_fails_the_shutdown_naming_its_pid()
   {
      using var claim = HoldClaimLock();
      await using var server = new RecordingHttpServer(answer: target => new HttpAnswer(target == "/api/shutdown" ? 202 : 404));
      StateFile.Write(_dataDirectory, new ServerState(Environment.ProcessId, "127.0.0.1", server.BaseUrl.Port, "token"));

      var failure = await Record.ExceptionAsync(() => ServerShutdown.StopAsync(_dataDirectory, TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken));

      Assert.Contains($"PID {Environment.ProcessId}", Assert.IsType<ServerShutdownException>(failure).Message);
      Assert.Contains("did not exit", failure.Message);
      Assert.Contains(server.Requests, request => request.StartsWith("POST /api/shutdown ", StringComparison.Ordinal) && request.Contains("Authorization: Bearer token", StringComparison.Ordinal));
   }

   /// <summary>The stand-in refuses with a one-line reason, as lotsen's request guards do.</summary>
   [Fact]
   public async Task Server_that_refuses_the_shutdown_fails_it_quoting_its_reason()
   {
      using var claim = HoldClaimLock();
      await using var server = new RecordingHttpServer(answer: _ => new HttpAnswer(403, "The UI cannot stop the lotsen server."));
      StateFile.Write(_dataDirectory, new ServerState(Environment.ProcessId, "127.0.0.1", server.BaseUrl.Port, "token"));

      var failure = await Record.ExceptionAsync(() => ServerShutdown.StopAsync(_dataDirectory, Timeout, TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken));

      Assert.Contains("403", Assert.IsType<ServerShutdownException>(failure).Message);
      Assert.Contains("The UI cannot stop the lotsen server.", failure.Message);
      Assert.Contains($"PID {Environment.ProcessId}", failure.Message);
   }

   /// <summary>The stand-in answers the shutdown with 404 and a long page, as a server without the route would.</summary>
   [Fact]
   public async Task Server_that_refuses_the_shutdown_with_a_page_fails_it_naming_the_status_without_the_page()
   {
      using var claim = HoldClaimLock();
      await using var server = new RecordingHttpServer(answer: _ => new HttpAnswer(404, ForeignPage));
      StateFile.Write(_dataDirectory, new ServerState(Environment.ProcessId, "127.0.0.1", server.BaseUrl.Port, "token"));

      var failure = await Record.ExceptionAsync(() => ServerShutdown.StopAsync(_dataDirectory, Timeout, TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken));

      Assert.Contains("404", Assert.IsType<ServerShutdownException>(failure).Message);
      Assert.Contains($"PID {Environment.ProcessId}", failure.Message);
      Assert.DoesNotContain("<title>", failure.Message);
      Assert.DoesNotContain(new string('x', 300), failure.Message);
   }

   /// <summary>The test holds the claim lock and writes no state file, as a server between its lock and its state file does, until the timeout passes.</summary>
   [Fact]
   public async Task Claim_lock_held_without_a_state_file_past_the_timeout_fails_the_shutdown_naming_the_data_directory()
   {
      using var claim = ClaimLock.TryTake(_dataDirectory)!;

      var failure = await Record.ExceptionAsync(() => ServerShutdown.StopAsync(_dataDirectory, TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken));

      Assert.Contains(_dataDirectory.Path, Assert.IsType<ServerShutdownException>(failure).Message);
   }

   /// <summary>The test releases the claim lock while the shutdown waits for a state file, as a server that fails its start does.</summary>
   [Fact]
   public async Task Claim_lock_released_while_the_shutdown_waits_finds_no_server()
   {
      var claim = ClaimLock.TryTake(_dataDirectory)!;
      var shutdown = ServerShutdown.StopAsync(_dataDirectory, Timeout, TestContext.Current.CancellationToken);
      await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
      var waited = !shutdown.IsCompleted;

      claim.Dispose();
      var stopped = await shutdown.WaitAsync(Timeout, TestContext.Current.CancellationToken);

      Assert.True(waited, "The shutdown did not wait for the holder of the claim lock.");
      Assert.Null(stopped);
   }

   public void Dispose()
   {
      try
      {
         Directory.Delete(_dataDirectory.Path, recursive: true);
      }
      catch (IOException)
      {
      }
   }

   private ClaimLock HoldClaimLock() => ClaimLock.TryTake(_dataDirectory) ?? throw new InvalidOperationException("The claim lock is held.");

   // The lock file exists, so the shutdown probes the lock.
   private void LeaveFreeClaimLock() => HoldClaimLock().Dispose();

   private static void OnShutdownRequest(string request, Action action)
   {
      if (request.StartsWith("POST /api/shutdown ", StringComparison.Ordinal))
      {
         action();
      }
   }
}
