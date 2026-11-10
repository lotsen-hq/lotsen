using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Lotsen.Hosting;
using Lotsen.Operations;

namespace Lotsen.Tests;

/// <summary>Covers that the log file of a background server that an adapter started holds no credential, through a failed pin, a UI sign-in and a refused request.</summary>
[Trait("Category", "Secrets")]
public sealed class BackgroundServerSecretsTests : IDisposable
{
   private const string PrUrl = "https://dev.azure.com/contoso/shop/_git/web/pullrequest/42";

   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

   private readonly DataDirectory _dataDirectory = new(Path.Combine(Directory.CreateTempSubdirectory("lotsen-tests-").FullName, "data"));

   /// <summary>The server finds no Azure CLI on its PATH, so the pin fails and the log gets a warning.</summary>
   [Fact]
   public async Task Log_file_holds_no_server_token_ui_credential_or_one_time_code()
   {
      var environment = LotsenProcess.WithoutBrowser(AdapterProcess.WithGracePeriod(TimeSpan.FromSeconds(1)));
      environment["PATH"] = Path.Combine(_dataDirectory.Path, "no-tools");
      using var adapter = AdapterProcess.Start(_dataDirectory.Path, environment);
      await adapter.InitializeAsync();
      var created = await adapter.CallToolAsync(2, "create_tour", $$"""{ "prUrl": "{{PrUrl}}", "familiarity": "some" }""");
      var opened = await adapter.CallToolAsync(3, "open_ui");
      Assert.False(created.IsError || opened.IsError, $"{created.Text}\n{opened.Text}\n{adapter.Output}");
      var server = StateFile.TryRead(_dataDirectory)!;
      var url = (string)JsonNode.Parse(opened.Text)!["url"]!;
      var code = url[(url.IndexOf("#code=", StringComparison.Ordinal) + "#code=".Length)..];

      using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/") };
      var ui = await ExchangeAsync(http, code);
      var asUi = await ListToursAsync(http, ui);
      var wrongToken = await ListToursAsync(http, server.Token + "x");
      await Poll.UntilAsync(() => Log() is { } log && log.Contains("could not be pinned", StringComparison.Ordinal) ? log : null, Timeout, () => $"The log holds no pin failure:\n{Log()}");
      adapter.CloseInput();
      await BackgroundServerProcesses.WaitForExitAsync(server.Pid, Timeout, () => "The background server did not exit after its grace period.");

      Assert.Equal((HttpStatusCode.OK, HttpStatusCode.Unauthorized), (asUi, wrongToken));
      var log = Log()!;
      Assert.DoesNotContain(server.Token, log, StringComparison.Ordinal);
      Assert.DoesNotContain(ui, log, StringComparison.Ordinal);
      Assert.DoesNotContain(code, log, StringComparison.Ordinal);
      if (!OperatingSystem.IsWindows())
      {
         Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_dataDirectory.LogFilePath));
      }
   }

   public void Dispose() => BackgroundServerProcesses.CleanUp(_dataDirectory);

   private static async Task<string> ExchangeAsync(HttpClient http, string code)
   {
      using var content = new StringContent(new JsonObject { ["code"] = code }.ToJsonString(), Encoding.UTF8, "application/json");
      using var response = await http.PostAsync("api/ui-credential", content, TestContext.Current.CancellationToken);
      response.EnsureSuccessStatusCode();
      return (string)JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["credential"]!;
   }

   private static async Task<HttpStatusCode> ListToursAsync(HttpClient http, string credential)
   {
      using var request = OperationWire.Request(OperationCatalog.Get("list_tours"), "{}"u8.ToArray());
      request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
      using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
      return response.StatusCode;
   }

   private string? Log()
   {
      try
      {
         using var stream = new FileStream(_dataDirectory.LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
         using var reader = new StreamReader(stream, Encoding.UTF8);
         return reader.ReadToEnd();
      }
      catch (FileNotFoundException)
      {
         return null;
      }
   }
}
