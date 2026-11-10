using System.Net;
using System.Text;
using Lotsen.Hosting;
using Microsoft.Extensions.Logging;

namespace Lotsen.Tests;

/// <summary>Covers that the log file of a server keeps the credentials out, as the console does, at the most verbose level.</summary>
[Trait("Category", "Secrets")]
public sealed class ServerLogSecretsTests
{
   [Fact]
   public async Task Log_file_holds_no_server_token_ui_credential_one_time_code_or_session_credential()
   {
      await using var harness = await ServerHarness.StartAsync(configure: options => options with { LogsToFile = true, MinimumLogLevel = LogLevel.Trace });
      var agent = await harness.AgentAsync();
      var code = await harness.IssueCodeAsync();
      using (var exchange = await harness.ExchangeAsync(code))
      {
         exchange.EnsureSuccessStatusCode();
      }

      var ui = await harness.UiCredentialAsync();
      var asUi = await harness.WhoAsync(ui);
      var asAgent = await harness.WhoAsync(harness.State.Token, agent.Credential);
      var wrong = await harness.WhoAsync(harness.State.Token + "x");
      await harness.StopServerAsync();

      Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.Unauthorized), (asUi.Status, asAgent.Status, wrong.Status));
      var log = File.ReadAllText(harness.DataDirectory.LogFilePath, Encoding.UTF8);
      Assert.Contains("api/agent-session", log, StringComparison.Ordinal);
      Assert.DoesNotContain(harness.State.Token, log, StringComparison.Ordinal);
      Assert.DoesNotContain(ui, log, StringComparison.Ordinal);
      Assert.DoesNotContain(code, log, StringComparison.Ordinal);
      Assert.DoesNotContain(agent.Credential, log, StringComparison.Ordinal);
   }

   [Fact]
   public async Task Log_file_is_user_only_and_starts_empty_at_each_start()
   {
      if (OperatingSystem.IsWindows())
      {
         Assert.Skip("Unix file modes do not apply on Windows.");
         return;
      }

      var dataDirectory = new DataDirectory(Directory.CreateTempSubdirectory("lotsen-tests-").FullName);
      File.WriteAllText(dataDirectory.LogFilePath, "a line of an earlier run\n");
      File.SetUnixFileMode(dataDirectory.LogFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

      await using var harness = await ServerHarness.StartAsync(dataDirectory: dataDirectory, configure: options => options with { LogsToFile = true });
      await harness.StopServerAsync();

      Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(dataDirectory.LogFilePath));
      Assert.DoesNotContain("earlier run", File.ReadAllText(dataDirectory.LogFilePath), StringComparison.Ordinal);
   }
}
