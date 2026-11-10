using System.Net;
using System.Text.Json;

namespace Lotsen.Tests;

/// <summary>Covers the exchange of a one-time code for the UI credential.</summary>
[Trait("Category", "Security")]
public sealed class SessionExchangeTests
{
   private const string TourJson = """{ "prUrl": "https://platform.test/shop/pull/42", "familiarity": "some" }""";

   [Fact]
   public async Task Code_is_exchanged_once_then_refused()
   {
      await using var harness = await ServerHarness.StartAsync();
      var code = await harness.IssueCodeAsync();

      using var first = await harness.ExchangeAsync(code);
      using var second = await harness.ExchangeAsync(code);

      Assert.Equal(HttpStatusCode.OK, first.StatusCode);
      var credential = await ReadCredentialAsync(first);
      Assert.False(string.IsNullOrEmpty(credential));
      Assert.NotEqual(harness.State.Token, credential);
      Assert.Equal("no-store", first.Headers.CacheControl?.ToString());
      Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
      var refusal = await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
      Assert.DoesNotContain(harness.State.Token, refusal);
      Assert.DoesNotContain(credential, refusal);
   }

   [Fact]
   public async Task Exchanged_credential_authorizes_api_requests_and_the_event_stream()
   {
      await using var harness = await ServerHarness.StartAsync();
      using var exchange = await harness.ExchangeAsync(await harness.IssueCodeAsync());
      var credential = await ReadCredentialAsync(exchange);

      using var tours = await harness.SendAsync(new PreparedCall("list_tours", credential));
      await using var events = await EventStream.OpenAsync(harness, ServerHarness.Request(HttpMethod.Get, "api/events", credential));

      Assert.Equal(HttpStatusCode.OK, tours.StatusCode);
   }

   [Fact]
   public async Task Every_exchange_yields_the_same_ui_credential_until_the_server_restarts()
   {
      await using var first = await ServerHarness.StartAsync();
      var before = await first.UiCredentialAsync();
      var again = await first.UiCredentialAsync();
      await first.StopServerAsync();
      await using var second = await ServerHarness.StartAsync(dataDirectory: first.DataDirectory);

      var after = await second.UiCredentialAsync();
      var stale = await second.WhoAsync(before);

      Assert.Equal(before, again);
      Assert.NotEqual(before, after);
      Assert.Equal(HttpStatusCode.Unauthorized, stale.Status);
   }

   [Fact]
   public async Task Expired_code_is_refused()
   {
      var clock = new ManualTimeProvider();
      await using var harness = await ServerHarness.StartAsync(clock);
      var code = await harness.IssueCodeAsync();

      clock.Advance(TimeSpan.FromMinutes(10));
      using var response = await harness.ExchangeAsync(code);

      Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
   }

   [Fact]
   public async Task Each_launch_gets_its_own_code_and_each_code_works_once()
   {
      await using var harness = await ServerHarness.StartAsync();
      var first = await harness.IssueCodeAsync();
      var second = await harness.IssueCodeAsync();

      using var secondExchange = await harness.ExchangeAsync(second);
      using var firstExchange = await harness.ExchangeAsync(first);

      Assert.NotEqual(first, second);
      Assert.Equal(HttpStatusCode.OK, secondExchange.StatusCode);
      Assert.Equal(HttpStatusCode.OK, firstExchange.StatusCode);
   }

   [Theory]
   [InlineData("""{"code":"not-a-code"}""")]
   [InlineData("""{"code":""}""")]
   [InlineData("""{}""")]
   [InlineData("""{"code":42}""")]
   [InlineData("""not json""")]
   public async Task Unknown_or_malformed_code_is_refused(string body)
   {
      await using var harness = await ServerHarness.StartAsync();
      await harness.IssueCodeAsync();

      var request = harness.Request(HttpMethod.Post, "api/ui-credential");
      request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
      using var response = await harness.SendAsync(request);

      Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest, $"Expected 401 or 400, got {(int)response.StatusCode}.");
      Assert.DoesNotContain(harness.State.Token, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
   }

   [Fact]
   public async Task Launched_link_carries_the_code_in_the_fragment_and_never_the_token()
   {
      await using var harness = await ServerHarness.StartAsync();
      await harness.IssueCodeAsync();

      var url = new Uri(harness.Launcher.LastUrl!);

      Assert.Equal("/", url.AbsolutePath);
      Assert.Equal("", url.Query);
      Assert.StartsWith("#code=", url.Fragment);
      Assert.DoesNotContain(harness.State.Token, url.OriginalString);
   }

   [Fact]
   public async Task Open_ui_reports_when_no_browser_could_be_launched()
   {
      await using var harness = await ServerHarness.StartAsync(browserLaunches: false);

      using var response = await harness.SendAsync(ServerHarness.Request("open_ui", harness.State.Token));
      var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
      var body = SuccessBody.Output(text);
      Assert.False((bool)body["launched"]!);
      Assert.Equal(harness.Launcher.LastUrl, (string?)body["url"]);
   }

   [Theory]
   [InlineData("agent")]
   [InlineData("cli")]
   public async Task Launched_open_ui_returns_no_link(string caller)
   {
      await using var harness = await ServerHarness.StartAsync();
      await using var connection = caller == "agent" ? await AgentSessionConnection.OpenAsync(harness) : null;

      using var response = await harness.SendAsync(connection?.Request(harness, "open_ui") ?? new PreparedCall("open_ui", harness.State.Token));
      var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
      var body = SuccessBody.Output(text).AsObject();
      Assert.True((bool)body["launched"]!);
      Assert.False(body.ContainsKey("url"), text);
      Assert.DoesNotContain("#code=", text);
      Assert.DoesNotContain(CodeOf(harness.Launcher.LastUrl!), text);
   }

   [Theory]
   [InlineData("agent")]
   [InlineData("cli")]
   public async Task Open_ui_with_a_tour_id_launches_a_fragment_with_the_code_and_the_tour(string caller)
   {
      await using var harness = await ServerHarness.StartAsync();
      await using var connection = caller == "agent" ? await AgentSessionConnection.OpenAsync(harness) : null;
      var tourId = (string)harness.CreateUnboundTour(TourJson)["id"]!;
      var call = OperationCall.On("open_ui", tourId);

      using var response = await harness.SendAsync(connection?.Request(harness, call) ?? new PreparedCall(call, harness.State.Token));

      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
      Assert.Matches($"^#code=[^&]+&tour={tourId}$", new Uri(harness.Launcher.LastUrl!).Fragment);
   }

   [Fact]
   public async Task Open_ui_without_a_tour_id_launches_a_fragment_with_only_the_code()
   {
      await using var harness = await ServerHarness.StartAsync();

      using var response = await harness.SendAsync(new PreparedCall("open_ui", harness.State.Token));

      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
      Assert.Matches("^#code=[^&]+$", new Uri(harness.Launcher.LastUrl!).Fragment);
   }

   [Theory]
   [InlineData("aaaaaaaaaaaaaaaa")]
   [InlineData("not-a-tour-id")]
   public async Task Open_ui_for_a_tour_that_does_not_exist_is_refused_and_launches_nothing(string tourId)
   {
      await using var harness = await ServerHarness.StartAsync();

      using var response = await harness.SendAsync(new PreparedCall(OperationCall.On("open_ui", tourId), harness.State.Token));

      var error = await ThreadCalls.ErrorAsync(response, HttpStatusCode.NotFound, "tourNotFound");
      Assert.Contains(tourId, (string?)error["problem"]);
      Assert.Null(harness.Launcher.LastUrl);
   }

   [Fact]
   public async Task Link_of_a_launch_for_an_agent_session_still_exchanges_once()
   {
      await using var harness = await ServerHarness.StartAsync();
      await using var connection = await AgentSessionConnection.OpenAsync(harness);
      using var opened = await harness.SendAsync(connection.Request(harness, "open_ui"));
      opened.EnsureSuccessStatusCode();
      var code = CodeOf(harness.Launcher.LastUrl!);

      using var first = await harness.ExchangeAsync(code);
      using var second = await harness.ExchangeAsync(code);

      Assert.Equal(HttpStatusCode.OK, first.StatusCode);
      Assert.False(string.IsNullOrEmpty(await ReadCredentialAsync(first)));
      Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
   }

   [Fact]
   public async Task Create_ui_link_launches_nothing_and_its_code_exchanges_once()
   {
      await using var harness = await ServerHarness.StartAsync();

      using var created = await harness.SendAsync(new PreparedCall("create_ui_link", harness.State.Token));
      var text = await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

      Assert.Equal(HttpStatusCode.OK, created.StatusCode);
      Assert.Null(harness.Launcher.LastUrl);
      var code = CodeOf((string)SuccessBody.Output(text)["url"]!);
      using var first = await harness.ExchangeAsync(code);
      using var second = await harness.ExchangeAsync(code);
      Assert.Equal(HttpStatusCode.OK, first.StatusCode);
      Assert.False(string.IsNullOrEmpty(await ReadCredentialAsync(first)));
      Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
   }

   [Fact]
   public async Task Event_stream_refuses_the_server_token_alone_and_opens_with_the_ui_credential()
   {
      await using var harness = await ServerHarness.StartAsync();

      using var refused = await harness.SendAsync(ServerHarness.Request(HttpMethod.Get, "api/events", harness.State.Token));
      await using var opened = await EventStream.OpenAsync(harness, ServerHarness.Request(HttpMethod.Get, "api/events", await harness.UiCredentialAsync()));

      Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
      Assert.Equal(HttpStatusCode.OK, opened.Response.StatusCode);
   }

   private static string CodeOf(string url)
   {
      const string marker = "#code=";
      var index = url.IndexOf(marker, StringComparison.Ordinal);
      Assert.True(index >= 0, $"The launched URL carries no code: {url}");
      return url[(index + marker.Length)..];
   }

   private static async Task<string> ReadCredentialAsync(HttpResponseMessage response)
   {
      using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
      return body.RootElement.GetProperty("credential").GetString()!;
   }
}
