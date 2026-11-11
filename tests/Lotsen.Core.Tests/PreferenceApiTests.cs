using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Operations;

namespace Lotsen.Tests;

/// <summary>Covers <c>read_preferences</c> and <c>write_preference</c> over HTTP: who may call them, the event that every page receives, and the preferences that a restart keeps.</summary>
[Trait("Category", "Preferences")]
public sealed class PreferenceApiTests : IAsyncLifetime
{
   private ServerHarness _harness = null!;

   public async ValueTask InitializeAsync() => _harness = await ServerHarness.StartAsync();

   public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

   private static string Body(string key, string? value) => new JsonObject { ["key"] = key, ["value"] = value }.ToJsonString();

   [Fact]
   public async Task Ui_writes_a_preference_and_reads_it_back()
   {
      var written = await _harness.CallAsync("write_preference", Body("lotsen.theme", "dark"));
      var read = await _harness.CallAsync("read_preferences");

      Assert.True(JsonNode.DeepEquals(new JsonObject { ["key"] = "lotsen.theme", ["value"] = "dark" }, written), $"The answer is {written}.");
      Assert.True(JsonNode.DeepEquals(new JsonObject { ["preferences"] = new JsonObject { ["lotsen.theme"] = "dark" } }, read), $"The answer is {read}.");
   }

   [Fact]
   public async Task Every_open_page_receives_preferenceChanged_for_a_write_and_for_a_delete()
   {
      await using var first = await EventStream.OpenAsync(_harness);
      await using var second = await EventStream.OpenAsync(_harness);

      await _harness.CallAsync("write_preference", Body("lotsen.theme", "dark"));
      await _harness.CallAsync("write_preference", Body("lotsen.theme", null));

      foreach (var page in new[] { first, second })
      {
         foreach (var expected in new[] { new JsonObject { ["key"] = "lotsen.theme", ["value"] = "dark" }, new JsonObject { ["key"] = "lotsen.theme", ["value"] = null } })
         {
            var next = await page.NextAsync(ThreadCalls.EventWait);
            Assert.Equal(ServerEvents.PreferenceChanged.Name, next.Type);
            Assert.True(JsonNode.DeepEquals(expected, next.Data), $"The event is {next.Data}.");
         }
      }
   }

   [Fact]
   public async Task A_server_started_again_on_the_data_directory_reads_the_preferences()
   {
      await _harness.CallAsync("write_preference", Body("lotsen.theme", "dark"));
      await _harness.StopServerAsync();

      await using var restarted = await ServerHarness.StartAsync(dataDirectory: _harness.DataDirectory);

      var read = await restarted.CallAsync("read_preferences");
      Assert.Equal("dark", (string?)read["preferences"]!["lotsen.theme"]);
   }

   [Fact]
   public async Task An_agent_session_can_neither_read_nor_write_a_preference()
   {
      using var read = await _harness.AgentSendJsonAsync("read_preferences");
      using var write = await _harness.AgentSendJsonAsync("write_preference", Body("lotsen.theme", "dark"));

      await ThreadCalls.ErrorAsync(read, HttpStatusCode.Forbidden, "callerNotAllowed");
      await ThreadCalls.ErrorAsync(write, HttpStatusCode.Forbidden, "callerNotAllowed");
      Assert.False(File.Exists(_harness.DataDirectory.PreferencesFilePath));
   }

   [Fact]
   public async Task Write_without_a_value_is_refused()
   {
      using var response = await _harness.SendJsonAsync("write_preference", """{ "key": "lotsen.theme" }""");

      await ThreadCalls.ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.False(File.Exists(_harness.DataDirectory.PreferencesFilePath));
   }
}
