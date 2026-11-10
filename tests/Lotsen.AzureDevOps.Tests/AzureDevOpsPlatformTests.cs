using Lotsen.AzureDevOps;
using Lotsen.Credentials;
using Lotsen.Platforms;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lotsen.Tests;

/// <summary>Covers the state that the Azure DevOps platform reports beside a pull request's latest iteration, from the pull request's status.</summary>
[Trait("Category", "AzureDevOps")]
public sealed class AzureDevOpsPlatformTests
{
   /// <summary>
   /// The documented statuses are notSet, active, abandoned, completed and all
   /// (PullRequestStatus in https://learn.microsoft.com/en-us/rest/api/azure/devops/git/pull-requests/get-pull-request?view=azure-devops-rest-7.1). A status that a later API version adds counts as active too.
   /// </summary>
   [Theory]
   [InlineData("active", PullRequestState.Active)]
   [InlineData("completed", PullRequestState.Completed)]
   [InlineData("abandoned", PullRequestState.Abandoned)]
   [InlineData("notSet", PullRequestState.Active)]
   [InlineData("all", PullRequestState.Active)]
   [InlineData("queued", PullRequestState.Active)]
   public async Task Status_of_the_pull_request_gives_its_state_beside_the_latest_iteration(string status, PullRequestState expected)
   {
      var json = AzureDevOpsClientTests.PullRequestJson.Replace("\"status\": \"active\"", $"\"status\": \"{status}\"", StringComparison.Ordinal);
      await using var server = new RecordingHttpServer(answer: target =>
         new HttpAnswer(200, target.Contains("/iterations?", StringComparison.Ordinal) ? AzureDevOpsClientTests.IterationsJson : json));
      var tokens = new FixedAccessTokenProvider("token-one");
      using var platform = new AzureDevOpsPlatform(
         new AzureDevOpsClient(tokens, new AzureDevOpsOptions { ServerOverride = server.BaseUrl }, NullLogger<AzureDevOpsClient>.Instance),
         tokens);

      var resolved = await platform.ResolveAsync(new Uri(AzureDevOpsClientTests.DevAzureUrl), TestContext.Current.CancellationToken);

      Assert.Equal(expected, resolved.State);
      Assert.Equal(2, resolved.Iteration);
   }
}
