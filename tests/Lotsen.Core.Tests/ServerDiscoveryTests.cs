using System.Diagnostics;
using System.Text.Json;
using Lotsen.Hosting;
using Lotsen.Json;
using Lotsen.Server;

namespace Lotsen.Tests;

/// <summary>Covers how <see cref="ServerDiscovery"/> answers whether a server runs on a data directory, one answer at a time, against a real server and stand-ins.</summary>
[Trait("Category", "Lifecycle")]
public sealed class ServerDiscoveryTests : IDisposable
{
   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

   private readonly DataDirectory _dataDirectory = new(Directory.CreateTempSubdirectory("lotsen-tests-").FullName);

   [Fact]
   public async Task No_state_file_with_a_free_claim_lock_answers_none()
   {
      LeaveFreeClaimLock();

      var answer = await FindAsync();

      Assert.IsType<ServerAnswer.None>(answer);
   }

   [Fact]
   public async Task Missing_data_directory_answers_none_and_creates_nothing()
   {
      var missing = new DataDirectory(Path.Combine(_dataDirectory.Path, "missing"));

      var answer = await new ServerDiscovery(missing).FindAsync(TestContext.Current.CancellationToken);

      Assert.IsType<ServerAnswer.None>(answer);
      Assert.False(Directory.Exists(missing.Path), "The discovery created the data directory.");
   }

   /// <summary>The test holds the claim lock and writes no state file, as a server between its lock and its state file does.</summary>
   [Fact]
   public async Task Claim_lock_held_without_a_state_file_answers_starting()
   {
      using var claim = HoldClaimLock();

      var answer = await FindAsync();

      Assert.IsType<ServerAnswer.Starting>(answer);
   }

   [Fact]
   public async Task State_file_of_an_exited_process_answers_stale()
   {
      LeaveFreeClaimLock();
      using var port = HeldPort.Refusing();
      var state = new ServerState(ExitedProcessId(), "127.0.0.1", port.Port, "stale-token");
      StateFile.Write(_dataDirectory, state);

      var answer = await FindAsync();

      Assert.Equal(state, Assert.IsType<ServerAnswer.Stale>(answer).State);
   }

   /// <summary>A server that took the claim lock deletes a crashed server's state file next, so the lock's holder is a server that is starting.</summary>
   [Fact]
   public async Task State_file_of_an_exited_process_under_a_held_claim_lock_answers_starting()
   {
      using var claim = HoldClaimLock();
      using var port = HeldPort.Refusing();
      StateFile.Write(_dataDirectory, new ServerState(ExitedProcessId(), "127.0.0.1", port.Port, "stale-token"));

      var answer = await FindAsync();

      Assert.IsType<ServerAnswer.Starting>(answer);
   }

   /// <summary>The state file names the live test process and a port that no URL can hold, which <see cref="StateFile.TryRead"/> reads as no state file.</summary>
   [Fact]
   public async Task State_file_with_a_port_out_of_range_answers_none()
   {
      LeaveFreeClaimLock();
      StateFile.Write(_dataDirectory, new ServerState(Environment.ProcessId, "127.0.0.1", 70000, "stale-token"));

      var answer = await FindAsync();

      Assert.IsType<ServerAnswer.None>(answer);
   }

   /// <summary>The state file names the live test process, as when a crashed server's PID was reused, and a port that refuses connections.</summary>
   [Fact]
   public async Task State_file_of_a_reused_pid_whose_port_refuses_answers_stale()
   {
      LeaveFreeClaimLock();
      using var port = HeldPort.Refusing();
      var state = new ServerState(Environment.ProcessId, "127.0.0.1", port.Port, "stale-token");
      StateFile.Write(_dataDirectory, state);

      var answer = await FindAsync();

      Assert.Equal(state, Assert.IsType<ServerAnswer.Stale>(answer).State);
   }

   /// <summary>The state file names the live test process and a port where another process answers each connection with no HTTP.</summary>
   [Theory]
   [InlineData(NoHttpPeer.Resets)]
   [InlineData(NoHttpPeer.Ends)]
   [InlineData(NoHttpPeer.SendsNoHttp)]
   public async Task State_file_of_a_reused_pid_whose_port_answers_no_http_answers_stale(NoHttpPeer peer)
   {
      LeaveFreeClaimLock();
      await using var listener = new NoHttpListener(peer, Task.CompletedTask);
      StateFile.Write(_dataDirectory, new ServerState(Environment.ProcessId, "127.0.0.1", listener.Port, "stale-token"));

      var answer = await FindAsync();

      Assert.IsType<ServerAnswer.Stale>(answer);
   }

   /// <summary>
   /// The listener on the state file's port accepts the connection and never answers, so only the probe's deadline ends the wait. It may be a server with .NET's file locking off that
   /// stalls, so the answer is no stale state file that a start may replace.
   /// </summary>
   [Fact]
   public async Task Listener_that_never_answers_answers_foreign_once_the_probe_times_out()
   {
      LeaveFreeClaimLock();
      await using var server = new RecordingHttpServer(hold: true);
      StateFile.Write(_dataDirectory, new ServerState(Environment.ProcessId, "127.0.0.1", server.BaseUrl.Port, "stale-token"));
      var probeTimeout = TimeSpan.FromMilliseconds(300);

      var elapsed = Stopwatch.StartNew();
      var answer = await new ServerDiscovery(_dataDirectory).FindAsync(probeTimeout, TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken);

      Assert.IsType<ServerAnswer.Foreign>(answer);
      Assert.True(elapsed.Elapsed < Timeout / 2, $"The discovery took {elapsed.Elapsed.TotalSeconds:F1} s.");
      Assert.DoesNotContain(server.Requests, request => request.Contains("Authorization:", StringComparison.OrdinalIgnoreCase));
   }

   /// <summary>
   /// The state file names the live test process and the port of another HTTP server, as when a crashed server's PID and port were both reused. The answers stand for a server that accepts
   /// the request, one that asks for a credential in its own words, one that refuses it, and servers that know no such route or method, in UTF-8 or in a charset that .NET does not decode by default.
   /// </summary>
   [Theory]
   [InlineData(200, "text/html; charset=utf-8")]
   [InlineData(401, "text/html; charset=utf-8")]
   [InlineData(403, "text/html; charset=utf-8")]
   [InlineData(404, "text/html; charset=utf-8")]
   [InlineData(501, "text/html; charset=utf-8")]
   [InlineData(200, "text/html; charset=windows-1252")]
   [InlineData(401, "text/html; charset=windows-1252")]
   public async Task Listener_that_answers_not_as_lotsen_answers_foreign_and_receives_no_token(int status, string contentType)
   {
      LeaveFreeClaimLock();
      await using var server = new RecordingHttpServer(answer: _ => new HttpAnswer(status, $"<!DOCTYPE HTML>{new string('x', 300)}", contentType));
      var state = new ServerState(Environment.ProcessId, "127.0.0.1", server.BaseUrl.Port, "stale-token");
      StateFile.Write(_dataDirectory, state);

      var answer = await FindAsync();

      Assert.Equal(state, Assert.IsType<ServerAnswer.Foreign>(answer).State);
      Assert.DoesNotContain(server.Requests, request => request.Contains("Authorization:", StringComparison.OrdinalIgnoreCase));
   }

   [Fact]
   public async Task Running_server_answers_running_with_its_state_file_entry()
   {
      await using var harness = await ServerHarness.StartAsync(dataDirectory: _dataDirectory);

      var answer = await FindAsync();

      Assert.Equal(harness.State, Assert.IsType<ServerAnswer.Running>(answer).State);
   }

   /// <summary>
   /// The claim lock is free, as for a server that runs with .NET's file locking off, and the stand-in answers the version request as lotsen does: with the refusal of a request without the
   /// server token, or with lotsen's versions.
   /// </summary>
   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public async Task Listener_that_answers_as_lotsen_with_a_free_claim_lock_answers_running(bool versionsWithoutToken)
   {
      LeaveFreeClaimLock();
      var versions = JsonSerializer.Serialize(VersionInfo.FromAssemblies(typeof(ServerDiscovery).Assembly), LotsenJsonContext.Default.VersionInfo);
      await using var server = new RecordingHttpServer(answer: _ => versionsWithoutToken ? new HttpAnswer(200, versions) : new HttpAnswer(401, RequestGuards.NeedsCredentialReason));
      var state = new ServerState(Environment.ProcessId, "127.0.0.1", server.BaseUrl.Port, "token");
      StateFile.Write(_dataDirectory, state);

      var answer = await FindAsync();

      Assert.Equal(state, Assert.IsType<ServerAnswer.Running>(answer).State);
      Assert.DoesNotContain(server.Requests, request => request.Contains("Authorization:", StringComparison.OrdinalIgnoreCase));
   }

   /// <summary>The test holds the claim lock, as a server does while it stops after it closed its listener and before it deletes its state file.</summary>
   [Fact]
   public async Task Claim_lock_held_with_a_port_that_refuses_answers_unreachable()
   {
      using var claim = HoldClaimLock();
      using var port = HeldPort.Refusing();
      var state = new ServerState(Environment.ProcessId, "127.0.0.1", port.Port, "token");
      StateFile.Write(_dataDirectory, state);

      var answer = await FindAsync();

      Assert.Equal(state, Assert.IsType<ServerAnswer.Unreachable>(answer).State);
   }

   /// <summary>The test holds the claim lock, so the state file is the holder's, though its port does not answer the version request as this build of lotsen does.</summary>
   [Fact]
   public async Task Claim_lock_held_with_a_listener_that_answers_not_as_lotsen_answers_unreachable()
   {
      using var claim = HoldClaimLock();
      await using var server = new RecordingHttpServer(answer: _ => new HttpAnswer(404, "<html></html>", "text/html"));
      var state = new ServerState(Environment.ProcessId, "127.0.0.1", server.BaseUrl.Port, "token");
      StateFile.Write(_dataDirectory, state);

      var answer = await FindAsync();

      Assert.Equal(state, Assert.IsType<ServerAnswer.Unreachable>(answer).State);
   }

   /// <summary>
   /// Race: a server takes the claim lock and writes its own state file while the probe of a stale state file's foreign listener waits for its answer. The stand-in plays that server
   /// before it answers, and the new state file names a port that refuses connections.
   /// </summary>
   [Fact]
   public async Task State_file_that_a_new_holder_writes_during_the_probe_is_the_one_answered()
   {
      LeaveFreeClaimLock();
      using var newPort = HeldPort.Refusing();
      var replaced = new ServerState(Environment.ProcessId, "127.0.0.1", newPort.Port, "token");
      ClaimLock? claim = null;
      await using var foreign = new RecordingHttpServer(answer: _ =>
      {
         claim ??= HoldClaimLock();
         StateFile.Write(_dataDirectory, replaced);
         return new HttpAnswer(200, "<html></html>", "text/html");
      });
      StateFile.Write(_dataDirectory, new ServerState(Environment.ProcessId, "127.0.0.1", foreign.BaseUrl.Port, "stale-token"));
      try
      {
         var answer = await FindAsync();

         Assert.Equal(replaced, Assert.IsType<ServerAnswer.Unreachable>(answer).State);
         Assert.DoesNotContain(foreign.Requests, request => request.Contains("Authorization:", StringComparison.OrdinalIgnoreCase));
      }
      finally
      {
         claim?.Dispose();
      }
   }

   /// <summary>
   /// Race: a server starts while the discovery's lock probe holds the claim lock. The probe's hook starts the server in that moment and returns once the server's first try of the lock
   /// has failed, so the probe releases the lock only after the collision.
   /// </summary>
   [Fact]
   public async Task Server_that_starts_while_the_lock_probe_holds_the_claim_lock_still_starts()
   {
      LeaveFreeClaimLock();
      var refused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      Task<ServerHarness>? start = null;
      var discovery = new ServerDiscovery(_dataDirectory, lockProbed: () =>
      {
         if (start is null)
         {
            start = ServerHarness.StartAsync(dataDirectory: _dataDirectory, configure: options => options with { ClaimRefused = () => refused.TrySetResult() });
            refused.Task.Wait(Timeout, TestContext.Current.CancellationToken);
         }
      });

      var during = await discovery.FindAsync(TestContext.Current.CancellationToken);
      Assert.NotNull(start);
      await using var server = await start.WaitAsync(Timeout, TestContext.Current.CancellationToken);
      var after = await discovery.FindAsync(TestContext.Current.CancellationToken);

      Assert.True(refused.Task.IsCompleted, "The server's start never found the claim lock taken, so the test shows no collision.");
      Assert.IsType<ServerAnswer.None>(during);
      Assert.Equal(server.State, Assert.IsType<ServerAnswer.Running>(after).State);
   }

   /// <summary>Race: a loop of discoveries runs on the data directory while servers start and stop on it one after another, so lock probes and starts collide at times that the run decides.</summary>
   [Fact]
   public async Task Every_server_start_succeeds_while_a_loop_of_discoveries_runs()
   {
      LeaveFreeClaimLock();
      using var stop = new CancellationTokenSource();
      var discovery = new ServerDiscovery(_dataDirectory);
      var firstProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      var probing = new ServerDiscovery(_dataDirectory, lockProbed: () => firstProbe.TrySetResult());
      var loop = Task.Run(async () =>
      {
         while (!stop.IsCancellationRequested)
         {
            try
            {
               await probing.FindAsync(stop.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
         }
      }, TestContext.Current.CancellationToken);

      try
      {
         // The loop probes the claim lock only while no server answers, so the rounds wait for its first probe. A loop that starts late could otherwise miss every gap between rounds.
         await firstProbe.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

         // A harness deletes its data directory when it stops, so the rounds start servers of their own.
         var options = new ServerOptions(_dataDirectory, VersionInfo.FromAssemblies(typeof(LotsenServer).Assembly));
         for (var round = 0; round < 20; round++)
         {
            await using var server = await LotsenServer.StartAsync(options, TestContext.Current.CancellationToken);
            Assert.IsType<ServerAnswer.Running>(await discovery.FindAsync(TestContext.Current.CancellationToken));
         }
      }
      finally
      {
         await stop.CancelAsync();
         await loop.WaitAsync(Timeout, TestContext.Current.CancellationToken);
      }
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

   private Task<ServerAnswer> FindAsync() =>
      new ServerDiscovery(_dataDirectory).FindAsync(TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken);

   private ClaimLock HoldClaimLock() => ClaimLock.TryTake(_dataDirectory) ?? throw new InvalidOperationException("The claim lock is held.");

   // The lock file exists, so the discovery probes the lock.
   private void LeaveFreeClaimLock() => HoldClaimLock().Dispose();

   private static int ExitedProcessId()
   {
      using var process = Process.Start(new ProcessStartInfo("dotnet", "--version") { RedirectStandardOutput = true })!;
      process.WaitForExit();
      return process.Id;
   }
}
