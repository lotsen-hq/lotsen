using System.Net;
using Lotsen.Hosting;
using Lotsen.Sessions;

namespace Lotsen.Tests;

/// <summary>Covers the idle exit of a background server: the registry of agent sessions decides it, under the lock that every session open takes.</summary>
[Trait("Category", "Lifecycle")]
public sealed class IdleExitTests
{
   private static readonly TimeSpan Grace = TimeSpan.FromSeconds(60);

   private readonly ManualTimeProvider _clock = new();
   private readonly AgentSessions _sessions = new();
   private int _exits;

   /// <summary>The grace period starts with the idle exit, so a server that no adapter reaches exits too.</summary>
   [Fact]
   public void Registry_without_a_session_exits_once_after_the_grace_period()
   {
      ExitWhenIdle();

      _clock.Advance(Grace - TimeSpan.FromSeconds(1));
      var early = _exits;
      _clock.Advance(TimeSpan.FromSeconds(1));
      _clock.Advance(Grace);

      Assert.Equal(0, early);
      Assert.Equal(1, _exits);
      Assert.Null(_sessions.TryOpen());
   }

   [Fact]
   public void Session_that_opens_during_the_grace_period_cancels_the_exit()
   {
      ExitWhenIdle();
      _clock.Advance(Grace - TimeSpan.FromSeconds(1));

      var session = _sessions.TryOpen();
      _clock.Advance(Grace * 2);

      Assert.NotNull(session);
      Assert.Equal(0, _exits);
   }

   /// <summary>The grace period counts from the drop of the last session, not from the start.</summary>
   [Fact]
   public void Drop_of_the_last_session_starts_a_new_grace_period()
   {
      ExitWhenIdle();
      var session = _sessions.TryOpen()!;
      _clock.Advance(Grace * 2);

      session.End();
      _clock.Advance(Grace - TimeSpan.FromSeconds(1));
      var early = _exits;
      _clock.Advance(TimeSpan.FromSeconds(1));

      Assert.Equal(0, early);
      Assert.Equal(1, _exits);
   }

   /// <summary>Race: the last of two sessions drops while a third attaches, so the count never stays at zero.</summary>
   [Fact]
   public void Drop_of_one_session_while_another_attaches_keeps_the_server_running()
   {
      ExitWhenIdle();
      var first = _sessions.TryOpen()!;
      var second = _sessions.TryOpen()!;

      first.End();
      var third = _sessions.TryOpen();
      second.End();
      _clock.Advance(Grace * 2);

      Assert.NotNull(third);
      Assert.Equal(0, _exits);
   }

   /// <summary>Race: the timer's callback is already running when a session attaches, as when the session opens at the moment the grace period ends.</summary>
   [Fact]
   public void Timer_that_fires_after_a_session_attached_does_not_exit()
   {
      ExitWhenIdle();
      var timer = _clock.LastTimer;

      var session = _sessions.TryOpen();
      timer.FireAlthoughDisposed();

      Assert.NotNull(session);
      Assert.Equal(0, _exits);
      Assert.NotNull(_sessions.TryOpen());
   }

   /// <summary>Race: a timer from an earlier idle spell fires after a session opened and dropped again, so only the newest grace period counts.</summary>
   [Fact]
   public void Timer_of_an_earlier_grace_period_does_not_exit()
   {
      ExitWhenIdle();
      var earlier = _clock.LastTimer;
      _sessions.TryOpen()!.End();

      earlier.FireAlthoughDisposed();

      Assert.Equal(0, _exits);
   }

   /// <summary>Once the exit is decided, the server stops, so a session that opens then would end at once.</summary>
   [Fact]
   public void Session_open_after_the_exit_was_decided_is_refused()
   {
      ExitWhenIdle();
      _clock.Advance(Grace);

      var late = _sessions.TryOpen();
      _clock.Advance(Grace * 2);

      Assert.Null(late);
      Assert.Empty(_sessions.Current);
      Assert.Equal(1, _exits);
   }

   [Fact]
   public void Registry_without_an_idle_exit_never_exits()
   {
      var session = _sessions.TryOpen()!;

      session.End();
      _clock.Advance(Grace * 10);

      Assert.Equal(0, _exits);
      Assert.NotNull(_sessions.TryOpen());
   }

   /// <summary>The host stops while two sessions are open, so their ends would otherwise arm the timer of the grace period again.</summary>
   [Fact]
   public void Shutdown_with_two_open_sessions_leaves_no_timer_armed_and_runs_no_idle_exit()
   {
      ExitWhenIdle();
      var first = _sessions.TryOpen()!;
      var second = _sessions.TryOpen()!;

      _sessions.BeginShutdown();
      first.End();
      second.End();
      var armed = _clock.Timers.Count(timer => timer.Due is not null);
      _clock.Advance(Grace * 2);

      Assert.Equal(0, armed);
      Assert.Equal(0, _exits);
      Assert.True(first.IsEnded && second.IsEnded);
   }

   private void ExitWhenIdle() => _sessions.ExitWhenIdle(Grace, _clock, () => _exits++);
}

/// <summary>Covers how a server with a grace period ends: it stops by itself, its state file goes, and a session that opens as it exits is refused.</summary>
[Trait("Category", "Lifecycle")]
public sealed class IdleServerTests
{
   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

   [Fact]
   public async Task Server_with_a_grace_period_stops_by_itself_and_removes_its_state_file()
   {
      await using var harness = await ServerHarness.StartAsync(configure: options => options with { IdleGracePeriod = TimeSpan.FromMilliseconds(200) });

      var shutdown = harness.Server.WaitForShutdownAsync(TestContext.Current.CancellationToken);
      var finished = await Task.WhenAny(shutdown, Task.Delay(Timeout, TestContext.Current.CancellationToken));
      await harness.StopServerAsync();

      Assert.True(finished == shutdown, "The server did not stop once its grace period ended.");
      Assert.False(File.Exists(harness.DataDirectory.StateFilePath));
   }

   [Fact]
   public async Task Server_without_a_grace_period_keeps_running_with_no_session()
   {
      await using var harness = await ServerHarness.StartAsync();

      var shutdown = harness.Server.WaitForShutdownAsync(TestContext.Current.CancellationToken);
      await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

      Assert.False(shutdown.IsCompleted);
      Assert.True(File.Exists(harness.DataDirectory.StateFilePath));
   }

   [Fact]
   public async Task Session_open_once_the_exit_is_decided_is_refused_with_503()
   {
      var clock = new ManualTimeProvider();
      await using var harness = await ServerHarness.StartAsync(timeProvider: clock, configure: options => options with { IdleGracePeriod = TimeSpan.FromMinutes(1) });

      clock.Advance(TimeSpan.FromMinutes(1));
      using var response = await harness.SendAsync(AgentSessionConnection.OpenRequest(harness, harness.State.Token, harness.Version));

      Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
      Assert.Empty(harness.Sessions.Current);
   }
}
