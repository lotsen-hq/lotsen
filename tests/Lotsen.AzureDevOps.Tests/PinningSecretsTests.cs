using System.Text;
using System.Text.Json.Nodes;
using Lotsen.AzureDevOps;
using Lotsen.Credentials;
using Lotsen.Git;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>
/// Covers that the Azure DevOps token stays in memory through a whole pinning run in a server: a failed pin whose Azure DevOps error repeats the token, then a pin again that clones the repository.
/// </summary>
/// <remarks>
/// The token reaches Azure DevOps as a bearer header, and git through its environment, which sends it to an <see cref="AuthenticatingGitServer"/> that serves the fixture. It reaches nothing else.
/// </remarks>
[Trait("Category", "Secrets")]
public sealed class PinningSecretsTests : IDisposable
{
   private const string CreateBody = $$"""{ "prUrl": "{{FakeAzureDevOps.PrUrl}}", "familiarity": "some" }""";

   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

   private readonly GitFixture _fixture = new();
   private readonly FakeAzureCli _az = new();
   private readonly RecordingLoggerProvider _logs = new();
   private readonly string _token = "lotsen-test-token-" + Guid.NewGuid().ToString("N");

   [Fact]
   public async Task Token_stays_out_of_logs_files_responses_events_and_git_arguments()
   {
      var baseCommit = _fixture.Commit("Base", ("a.txt", "one\n"));
      _fixture.Checkout("feature", create: true);
      var head = _fixture.Commit("Change", ("a.txt", "two\n"));
      _az.SignedIn(_token, DateTimeOffset.UtcNow.AddHours(1));
      var git = new RecordingGit(_fixture.ScratchPath);
      await using var gitServer = await AuthenticatingGitServer.StartAsync(_fixture.OriginPath, $"Bearer {_token}");
      await using var azureDevOps = new FakeAzureDevOps();
      azureDevOps.Refuse(500, $"The token {_token} is not valid.");
      await using var harness = await ServerHarness.StartAsync(configure: options => options with
      {
         Platforms = [AzureDevOpsPlatform.Factory(new AzureDevOpsOptions { ServerOverride = azureDevOps.Server.BaseUrl }, new AzureCliOptions { Executable = _az.Executable }, accessTokens: null)],
         Git = git.Options,
         LoggerProvider = _logs,
      });
      await using var events = await EventStream.OpenAsync(harness);
      var responses = new List<string>();

      var id = (string)(await CallAsync(harness, responses, "create_tour", CreateBody, asAgent: true))["id"]!;
      await events.NextBesidesControlsAsync(Timeout);
      var failed = await events.NextBesidesControlsAsync(Timeout);
      azureDevOps.PullRequest(gitServer.RepositoryUrl, (head, baseCommit));
      await CallAsync(harness, responses, On("pin_tour", id), asAgent: true);
      await events.NextBesidesControlsAsync(Timeout);
      var pinned = await events.NextBesidesControlsAsync(Timeout);
      await CallAsync(harness, responses, On("read_state", id));
      await CallAsync(harness, responses, "list_tours");
      await harness.StopServerAsync();

      Assert.Equal("failed", (string)failed.Data["pin"]!["state"]!);
      Assert.Equal("pinned", (string)pinned.Data["pin"]!["state"]!);
      Assert.All(azureDevOps.Server.Requests, request => Assert.Contains($"Authorization: Bearer {_token}\r\n", request, StringComparison.Ordinal));
      Assert.True(git.RunsWithCredential > 0, "No git run received the token through its environment.");
      Assert.True(gitServer.AuthorizedRequests > 0);
      Assert.Contains("clone", git.Arguments, StringComparison.Ordinal);
      Assert.DoesNotContain(_token, git.Arguments, StringComparison.Ordinal);
      Assert.DoesNotContain(_token, git.Trace, StringComparison.Ordinal);
      Assert.All(_az.Runs, run => Assert.DoesNotContain(_token, run, StringComparison.Ordinal));
      Assert.All(responses, body => Assert.DoesNotContain(_token, body, StringComparison.Ordinal));
      Assert.True(events.Data.Count >= 4, $"The stream gave {events.Data.Count} events, fewer than the four that the test read.");
      Assert.All(events.Data, data => Assert.DoesNotContain(_token, data, StringComparison.Ordinal));
      Assert.NotEmpty(_logs.Lines);
      Assert.All(_logs.Lines, line => Assert.DoesNotContain(_token, line, StringComparison.Ordinal));
      var tokenBytes = Encoding.UTF8.GetBytes(_token);
      var files = Directory.EnumerateFiles(harness.DataDirectory.Path, "*", SearchOption.AllDirectories).ToList();
      Assert.Contains(files, file => file.Contains($"{Path.DirectorySeparatorChar}clones{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
      Assert.All(files, file => Assert.True(File.ReadAllBytes(file).AsSpan().IndexOf(tokenBytes) < 0, $"The token is in {file}."));
   }

   public void Dispose()
   {
      _az.Dispose();
      _fixture.Dispose();
   }

   // Calls as the UI, or as the harness's agent session, which create_tour and pin_tour need.
   private static async Task<JsonNode> CallAsync(ServerHarness harness, List<string> responses, OperationCall call, string? json = null, bool asAgent = false)
   {
      using var response = asAgent ? await harness.AgentSendJsonAsync(call, json) : await harness.SendJsonAsync(call, json);
      var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
      responses.Add(body);
      Assert.True(response.IsSuccessStatusCode, $"{call.Name} returned {(int)response.StatusCode}: {body}");
      return SuccessBody.Output(body);
   }
}
