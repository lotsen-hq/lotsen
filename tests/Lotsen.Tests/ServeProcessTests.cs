using System.Net;
using System.Net.Sockets;
using Lotsen.Hosting;

namespace Lotsen.Tests;

/// <summary>Covers <c>lotsen serve</c> and <c>lotsen open</c> as separate processes, as a user runs them.</summary>
public sealed class ServeProcessTests : IDisposable
{
   private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

   private readonly DataDirectory _dataDirectory = new(Path.Combine(Directory.CreateTempSubdirectory("lotsen-tests-").FullName, "data"));

   [Fact]
   [Trait("Category", "Security")]
   public async Task State_file_is_readable_by_the_user_only()
   {
      if (OperatingSystem.IsWindows())
      {
         Assert.Skip("Unix file modes do not apply on Windows.");
         return;
      }

      using var serve = LotsenProcess.Start(_dataDirectory.Path, null, "serve");

      await WaitForStateAsync(serve);

      Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_dataDirectory.StateFilePath));
      Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(_dataDirectory.Path));
   }

   [Fact]
   [Trait("Category", "Security")]
   public async Task Serve_records_its_loopback_address_and_never_prints_the_token()
   {
      using var serve = LotsenProcess.Start(_dataDirectory.Path, null, "serve");
      var state = await WaitForStateAsync(serve);

      using var client = new HttpClient();
      using var response = await client.GetAsync($"http://127.0.0.1:{state.Port}/", TestContext.Current.CancellationToken);
      await serve.StopAsync();
      await serve.WaitForExitAsync(StartTimeout);

      Assert.Equal(serve.Id, state.Pid);
      Assert.Equal("127.0.0.1", state.Address);
      Assert.NotEqual(0, state.Port);
      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
      Assert.Equal(0, serve.ExitCode);
      Assert.Contains($"http://127.0.0.1:{state.Port}", serve.Output);
      Assert.DoesNotContain(state.Token, serve.Output);
   }

   [Theory]
   [Trait("Category", "Security")]
   [InlineData("ASPNETCORE_", true)]
   [InlineData("DOTNET_", true)]
   [InlineData("ASPNETCORE_", false)]
   [InlineData("DOTNET_", false)]
   public async Task Hosting_urls_from_the_environment_do_not_move_the_listener(string prefix, bool preferHostingUrls)
   {
      // A lotsen that honors the URLS variable exits before it writes the state file: it cannot bind the held port, or its loopback-only check refuses the wildcard address.
      using var hostile = HeldPort.Listening();
      var environment = new Dictionary<string, string> { [$"{prefix}URLS"] = $"http://0.0.0.0:{hostile.Port}" };
      if (preferHostingUrls)
      {
         environment[$"{prefix}PREFERHOSTINGURLS"] = "true";
      }

      using var serve = LotsenProcess.Start(_dataDirectory.Path, environment, "serve");
      var state = await WaitForStateAsync(serve);

      Assert.Equal(serve.Id, state.Pid);
      Assert.True(await AcceptsConnectionAsync(state.Port));
   }

   [Fact]
   public async Task Serve_removes_the_state_file_on_ctrl_c()
   {
      Assert.SkipWhen(OperatingSystem.IsWindows(), "The test sends SIGINT, as a Ctrl-C does.");
      using var serve = LotsenProcess.Start(_dataDirectory.Path, null, "serve");
      await WaitForStateAsync(serve);

      serve.Interrupt();
      await serve.WaitForExitAsync(StartTimeout);

      Assert.Equal(0, serve.ExitCode);
      Assert.False(File.Exists(_dataDirectory.StateFilePath));
   }

   [Fact]
   public async Task Two_servers_get_different_ports_and_tokens()
   {
      var otherDirectory = new DataDirectory(Path.Combine(Path.GetDirectoryName(_dataDirectory.Path)!, "other"));
      using var first = LotsenProcess.Start(_dataDirectory.Path, null, "serve");
      using var second = LotsenProcess.Start(otherDirectory.Path, null, "serve");

      var firstState = await WaitForStateAsync(first, _dataDirectory);
      var secondState = await WaitForStateAsync(second, otherDirectory);

      Assert.NotEqual(firstState.Port, secondState.Port);
      Assert.NotEqual(firstState.Token, secondState.Token);
   }

   [Fact]
   [Trait("Category", "Security")]
   public async Task Open_prints_a_link_with_a_code_when_no_browser_can_be_launched()
   {
      using var serve = LotsenProcess.Start(_dataDirectory.Path, LotsenProcess.WithoutBrowser(), "serve");
      var state = await WaitForStateAsync(serve);

      var (exitCode, output) = await LotsenProcess.RunAsync(_dataDirectory.Path, null, "open");

      Assert.Equal(0, exitCode);
      Assert.Matches(@$"http://(127\.0\.0\.1|localhost):{state.Port}/#code=[A-Za-z0-9_-]+", output);
      Assert.DoesNotContain(state.Token, output);
      Assert.DoesNotContain(state.Token, serve.Output);
   }

   [Fact]
   [Trait("Category", "Cli")]
   public async Task Unknown_command_prints_usage_and_fails()
   {
      var (exitCode, output) = await LotsenProcess.RunAsync(_dataDirectory.Path, null, "frobnicate");

      Assert.Equal(2, exitCode);
      Assert.Contains("lotsen --help", output);
   }

   public void Dispose()
   {
      try
      {
         TestDirectories.Delete(Path.GetDirectoryName(_dataDirectory.Path)!, BackgroundServerProcesses.CleanupWait);
      }
      catch (IOException)
      {
      }
   }

   private Task<ServerState> WaitForStateAsync(LotsenProcess process) => WaitForStateAsync(process, _dataDirectory);

   private static async Task<bool> AcceptsConnectionAsync(int port)
   {
      using var client = new TcpClient();
      try
      {
         await client.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
         return true;
      }
      catch (SocketException)
      {
         return false;
      }
   }

   private static async Task<ServerState> WaitForStateAsync(LotsenProcess process, DataDirectory dataDirectory)
   {
      var deadline = DateTime.UtcNow + StartTimeout;
      while (DateTime.UtcNow < deadline)
      {
         Assert.False(process.HasExited, $"lotsen exited early:\n{process.Output}");
         if (StateFile.TryRead(dataDirectory) is { } state)
         {
            return state;
         }

         await Task.Delay(50, TestContext.Current.CancellationToken);
      }

      throw new TimeoutException($"lotsen wrote no state file:\n{process.Output}");
   }
}
