using System.Net.Sockets;
using Lotsen.Hosting;
using Lotsen.Server;

namespace Lotsen.Tests;

/// <summary>Covers the claim lock and the configured port of a server in the test process.</summary>
[Trait("Category", "Lifecycle")]
public sealed class ClaimLockTests : IDisposable
{
   private readonly DataDirectory _dataDirectory = new(Directory.CreateTempSubdirectory("lotsen-tests-").FullName);

   [Fact]
   public void Lock_refuses_a_second_holder_until_the_first_releases_it()
   {
      using (var first = ClaimLock.TryTake(_dataDirectory))
      {
         Assert.NotNull(first);
         Assert.Null(ClaimLock.TryTake(_dataDirectory));
      }

      using var again = ClaimLock.TryTake(_dataDirectory);
      Assert.NotNull(again);
   }

   [Fact]
   public async Task Second_server_on_the_same_data_directory_throws_naming_the_first_ones_pid()
   {
      await using var first = await ServerHarness.StartAsync(dataDirectory: _dataDirectory);

      var refused = await Assert.ThrowsAsync<ServerAlreadyRunningException>(() => ServerHarness.StartAsync(dataDirectory: _dataDirectory));

      Assert.Equal(Environment.ProcessId, refused.Pid);
      Assert.Equal(first.State, StateFile.TryRead(_dataDirectory));
   }

   [Fact]
   public async Task Stopped_server_releases_the_claim_lock()
   {
      var first = await ServerHarness.StartAsync(dataDirectory: _dataDirectory);
      await first.StopServerAsync();

      using var claim = ClaimLock.TryTake(_dataDirectory);

      Assert.NotNull(claim);
      Assert.False(File.Exists(_dataDirectory.StateFilePath));
   }

   /// <summary>A state file that no running server holds the claim lock for is a crashed server's.</summary>
   [Fact]
   public async Task Start_replaces_a_stale_state_file()
   {
      StateFile.Write(_dataDirectory, new ServerState(Environment.ProcessId, "127.0.0.1", 1, "stale"));

      await using var harness = await ServerHarness.StartAsync(dataDirectory: _dataDirectory);

      Assert.NotEqual(1, harness.State.Port);
   }

   [Fact]
   public async Task Port_in_use_throws_naming_the_port_and_releases_the_claim_lock()
   {
      using var held = HeldPort.Listening();
      Assert.True(ServerPort.TryParse(held.Port.ToString(), out var port));

      var refused = await Assert.ThrowsAsync<PortUnavailableException>(() => ServerHarness.StartAsync(dataDirectory: _dataDirectory, configure: options => options with { Port = port }));

      Assert.Equal(held.Port, refused.Port.Value);
      Assert.Contains($"Port {held.Port} of 127.0.0.1 is in use", refused.Message);
      Assert.False(File.Exists(_dataDirectory.StateFilePath));
      using var claim = ClaimLock.TryTake(_dataDirectory);
      Assert.NotNull(claim);
   }

   /// <summary>The OS refuses an unprivileged bind of a privileged port with a reason other than a port in use.</summary>
   [Fact]
   public async Task Port_that_the_os_refuses_throws_naming_the_port_and_releases_the_claim_lock()
   {
      Assert.SkipUnless(HeldPort.BindRefusal(HeldPort.Privileged) == SocketError.AccessDenied, $"This process may bind port {HeldPort.Privileged}, or another socket holds it.");
      Assert.True(ServerPort.TryParse(HeldPort.Privileged.ToString(), out var port));

      var refused = await Assert.ThrowsAsync<PortUnavailableException>(() => ServerHarness.StartAsync(dataDirectory: _dataDirectory, configure: options => options with { Port = port }));

      Assert.Contains($"Port {HeldPort.Privileged} of 127.0.0.1 cannot be used", refused.Message);
      Assert.False(File.Exists(_dataDirectory.StateFilePath));
      using var claim = ClaimLock.TryTake(_dataDirectory);
      Assert.NotNull(claim);
   }

   [Theory]
   [InlineData("2", 2)]
   [InlineData("65535", 65535)]
   [InlineData("08080", 8080)]
   public void Port_parses_a_number_from_1_to_65535_that_browsers_allow(string text, int expected)
   {
      Assert.True(ServerPort.TryParse(text, out var port));
      Assert.Equal(expected, port.Value);
   }

   [Theory]
   [InlineData("0")]
   [InlineData("65536")]
   [InlineData("-1")]
   [InlineData("+1")]
   [InlineData(" 1")]
   [InlineData("1 ")]
   [InlineData("1.0")]
   [InlineData("")]
   [InlineData("99999999999999999999")]
   public void Port_refuses_text_that_is_not_a_port_number(string text)
   {
      Assert.False(ServerPort.TryParse(text, out _));
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
}
