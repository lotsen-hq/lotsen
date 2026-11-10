using Lotsen.Cli;
using Lotsen.Hosting;
using Lotsen.Platforms;
using Lotsen.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lotsen.Tests;

/// <summary>Covers the settings that a server starts with and the endpoint it knows from the start.</summary>
[Trait("Category", "Lifecycle")]
public sealed class ServerStartTests
{
   [Fact]
   public async Task Endpoint_holds_the_port_that_the_state_file_names()
   {
      await using var harness = await ServerHarness.StartAsync();

      Assert.Equal(harness.Port, harness.Server.Services.GetRequiredService<ServerEndpoint>().Port);
   }

   /// <summary>Each command's own settings are the console's log level for <c>lotsen serve</c>, and the grace period and the log file for a background server.</summary>
   [Fact]
   public void Serve_and_a_background_server_start_with_the_same_options_apart_from_their_own()
   {
      var dataDirectory = new DataDirectory(Path.Combine(Path.GetTempPath(), "lotsen-tests-unused"));
      var version = VersionInfo.FromAssemblies(typeof(LotsenServer).Assembly);
      IReadOnlyList<Func<IServiceProvider, IPullRequestPlatform>> platforms = [_ => new TestPlatform()];
      Assert.True(ServerPort.TryParse("8123", out var port));
      Assert.True(FactCacheSize.TryParse("8", out var factCacheSize));

      var serve = Commands.ServeOptions(dataDirectory, version, platforms, LogLevel.Debug, port, factCacheSize);
      var background = WithVariables(port.ToString(), factCacheSize.ToString(), () => Commands.TryBackgroundOptions(dataDirectory, version, platforms, out var options, out _) ? options : null);

      Assert.NotNull(background);
      Assert.Equal((LogLevel.Debug, (TimeSpan?)null, false), (serve.MinimumLogLevel, serve.IdleGracePeriod, serve.LogsToFile));
      Assert.Equal((LogLevel.Warning, BackgroundServer.GracePeriodFromEnvironment(), true), (background.MinimumLogLevel, background.IdleGracePeriod, background.LogsToFile));
      Assert.Same(port, serve.Port);
      Assert.Same(factCacheSize, serve.FactCacheSize);
      Assert.Equal((8123, 8), (background.Port?.Value, background.FactCacheSize.Mebibytes));
      Assert.Same(platforms, serve.Platforms);
      Assert.IsType<SystemBrowserLauncher>(serve.BrowserLauncher);
      Assert.IsType<SystemBrowserLauncher>(background.BrowserLauncher);
      Assert.Equal(
         serve with { MinimumLogLevel = background.MinimumLogLevel, IdleGracePeriod = background.IdleGracePeriod, LogsToFile = background.LogsToFile, BrowserLauncher = background.BrowserLauncher },
         background with { Port = serve.Port, FactCacheSize = serve.FactCacheSize });
   }

   [Fact]
   public void Background_server_without_its_variables_starts_with_a_port_that_the_os_assigns_and_64_mib()
   {
      var dataDirectory = new DataDirectory(Path.Combine(Path.GetTempPath(), "lotsen-tests-unused"));
      var version = VersionInfo.FromAssemblies(typeof(LotsenServer).Assembly);

      var background = WithVariables(null, "", () => Commands.TryBackgroundOptions(dataDirectory, version, [], out var options, out _) ? options : null);

      Assert.NotNull(background);
      Assert.Equal(((int?)null, 64), (background.Port?.Value, background.FactCacheSize.Mebibytes));
   }

   [Fact]
   public void Background_server_with_a_fact_cache_variable_that_is_not_a_size_does_not_start_and_names_the_variable()
   {
      var dataDirectory = new DataDirectory(Path.Combine(Path.GetTempPath(), "lotsen-tests-unused"));
      var version = VersionInfo.FromAssemblies(typeof(LotsenServer).Assembly);
      string? error = null;

      var started = WithVariables(null, "abc", () => Commands.TryBackgroundOptions(dataDirectory, version, [], out _, out error));

      Assert.False(started);
      Assert.Contains($"{FactCacheSize.Variable} is 'abc'", error);
   }

   [Fact]
   public void Background_server_with_a_port_variable_that_browsers_refuse_does_not_start_and_says_why()
   {
      var dataDirectory = new DataDirectory(Path.Combine(Path.GetTempPath(), "lotsen-tests-unused"));
      var version = VersionInfo.FromAssemblies(typeof(LotsenServer).Assembly);
      string? error = null;

      var started = WithVariables("6000", null, () => Commands.TryBackgroundOptions(dataDirectory, version, [], out _, out error));

      Assert.False(started);
      Assert.Contains($"{ServerPort.Variable} is '6000', a port that browsers refuse to open", error);
   }

   /// <summary>A variable that is no positive whole number of milliseconds that fits an <see cref="int"/> leaves the 10-second default in force.</summary>
   [Theory]
   [InlineData(null, "within 10 seconds")]
   [InlineData("", "within 10 seconds")]
   [InlineData("0", "within 10 seconds")]
   [InlineData("abc", "within 10 seconds")]
   [InlineData("-2000", "within 10 seconds")]
   [InlineData("+2000", "within 10 seconds")]
   [InlineData("99999999999", "within 10 seconds")]
   [InlineData("2000", "within 2 seconds")]
   [InlineData("500", "within 0.5 seconds")]
   public void Start_timeout_message_names_the_start_timeout_that_the_variable_sets_or_the_default(string? milliseconds, string expected) =>
      Assert.Contains(expected, BackgroundServer.StartTimeoutMessage(new DataDirectory("/data"), BackgroundServer.StartTimeoutFrom(milliseconds), LotsenCommand.Installed), StringComparison.Ordinal);

   // The tests of this class run one at a time, and no other test in this process reads these variables.
   private static T WithVariables<T>(string? port, string? factCacheSize, Func<T> read)
   {
      var saved = (Environment.GetEnvironmentVariable(ServerPort.Variable), Environment.GetEnvironmentVariable(FactCacheSize.Variable));
      Environment.SetEnvironmentVariable(ServerPort.Variable, port);
      Environment.SetEnvironmentVariable(FactCacheSize.Variable, factCacheSize);
      try
      {
         return read();
      }
      finally
      {
         Environment.SetEnvironmentVariable(ServerPort.Variable, saved.Item1);
         Environment.SetEnvironmentVariable(FactCacheSize.Variable, saved.Item2);
      }
   }
}
