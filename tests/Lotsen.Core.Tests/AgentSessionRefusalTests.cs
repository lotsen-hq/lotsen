using System.Net;
using System.Text;
using System.Text.Json;
using Lotsen.Hosting;
using Lotsen.Json;
using Lotsen.Operations;
using Lotsen.Sessions;
using Lotsen.Tours;

namespace Lotsen.Tests;

/// <summary>Covers the one refusal contract of agent sessions: each refusal of a session open and of an ended session's credential is an <see cref="OperationError"/>.</summary>
[Trait("Category", "Roles")]
public sealed class AgentSessionRefusalTests
{
   private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

   public static TheoryData<string, string, Type, HttpStatusCode> Refusals => new()
   {
      { "the UI opens a session", "callerNotAllowed", typeof(RefusalKind.CallerRefused), HttpStatusCode.Forbidden },
      { "an agent session opens another", "callerNotAllowed", typeof(RefusalKind.CallerRefused), HttpStatusCode.Forbidden },
      { "the body is no versions", "invalidInput", typeof(RefusalKind.BadInput), HttpStatusCode.BadRequest },
      { "the adapter is another build", "buildMismatch", typeof(RefusalKind.Conflict), HttpStatusCode.Conflict },
      { "the server has decided to exit", "serverExiting", typeof(RefusalKind.Unavailable), HttpStatusCode.ServiceUnavailable },
      { "the session has ended", "sessionUnknown", typeof(RefusalKind.Unauthenticated), HttpStatusCode.Unauthorized },
   };

   [Theory]
   [MemberData(nameof(Refusals))]
   public async Task Refusal_is_an_operation_error_whose_code_has_the_kind_and_the_status(string situation, string code, Type kind, HttpStatusCode status)
   {
      var clock = new ManualTimeProvider();
      await using var harness = await ServerHarness.StartAsync(timeProvider: clock, configure: options => options with { IdleGracePeriod = TimeSpan.FromMinutes(1) });
      await using var held = await AgentSessionConnection.OpenAsync(harness);
      var sessionsBefore = harness.Sessions.Current.Count;

      using var response = await harness.SendAsync(await RequestAsync(harness, held, clock, situation));
      var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
      var error = JsonSerializer.Deserialize(body, LotsenJsonContext.Default.OperationError)!;

      Assert.Equal(status, response.StatusCode);
      Assert.Equal(code, error.Code.Name);
      Assert.IsType(kind, error.Code.Kind);
      Assert.Equal(status, OperationWire.StatusOf(error.Code.Kind));
      Assert.False(string.IsNullOrWhiteSpace(error.Reason), body);
      Assert.True(harness.Sessions.Current.Count <= sessionsBefore, $"The refused request opened a session: {situation}.");
   }

   [Fact]
   public async Task Build_mismatch_reason_names_both_builds_and_lotsen_restart()
   {
      await using var harness = await ServerHarness.StartAsync();
      var other = harness.Version with { Build = "0.0.0 (another build)" };

      using var response = await harness.SendAsync(AgentSessionConnection.OpenRequest(harness, harness.State.Token, other));
      var error = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), LotsenJsonContext.Default.OperationError)!;

      Assert.Equal(VersionInfo.MismatchReason(harness.Version, other), error.Reason);
   }

   // Every situation but the ended session opens with the server token, so only what the row names differs from a session the server accepts.
   private static async Task<HttpRequestMessage> RequestAsync(ServerHarness harness, AgentSessionConnection held, ManualTimeProvider clock, string situation)
   {
      switch (situation)
      {
         case "the UI opens a session":
            return AgentSessionConnection.OpenRequest(harness, await harness.UiCredentialAsync(), harness.Version);
         case "an agent session opens another":
            var nested = AgentSessionConnection.OpenRequest(harness, harness.State.Token, harness.Version);
            nested.Headers.Add(AgentSessions.CredentialHeader, held.Credential);
            return nested;
         case "the body is no versions":
            var malformed = AgentSessionConnection.OpenRequest(harness, harness.State.Token, harness.Version);
            malformed.Content = new StringContent("[]", Encoding.UTF8, "application/json");
            return malformed;
         case "the adapter is another build":
            return AgentSessionConnection.OpenRequest(harness, harness.State.Token, harness.Version with { Build = "0.0.0 (another build)" });
         case "the server has decided to exit":
            var session = Assert.Single(harness.Sessions.Current);
            await held.DisposeAsync();
            await Poll.EndedAsync(session, Timeout, "The held session did not end after its connection dropped.");
            clock.Advance(TimeSpan.FromMinutes(1));
            return AgentSessionConnection.OpenRequest(harness, harness.State.Token, harness.Version);
         case "the session has ended":
            var ended = Assert.Single(harness.Sessions.Current);
            await held.DisposeAsync();
            await Poll.EndedAsync(ended, Timeout, "The held session did not end after its connection dropped.");
            return ServerHarness.Request(HttpMethod.Get, "api/caller", harness.State.Token, held.Credential);
         default:
            throw new ArgumentOutOfRangeException(nameof(situation), situation, "The table has no such situation.");
      }
   }
}
