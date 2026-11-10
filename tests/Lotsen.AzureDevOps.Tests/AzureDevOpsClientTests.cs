using System.Text.Json.Nodes;
using Lotsen.AzureDevOps;
using Lotsen.Credentials;
using Lotsen.Git;
using Lotsen.Tours;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lotsen.Tests;

/// <summary>Covers how the Azure DevOps client reads a pull request and its iterations for both host forms, and the reason it gives for each failure.</summary>
[Trait("Category", "AzureDevOps")]
public sealed class AzureDevOpsClientTests
{
   internal const string SourceCommit = "1111111111111111111111111111111111111111";
   internal const string TargetCommit = "2222222222222222222222222222222222222222";
   internal const string LaterSourceCommit = "3333333333333333333333333333333333333333";

   internal static readonly string PullRequestJson = $$"""
      {
        "repository": {
          "id": "7d2c1b0e-0000-0000-0000-000000000001",
          "name": "web app",
          "url": "https://dev.azure.com/contoso/_apis/git/repositories/7d2c1b0e-0000-0000-0000-000000000001",
          "project": { "id": "0f0e0d0c-0000-0000-0000-000000000002", "name": "My Shop" },
          "remoteUrl": "https://contoso@dev.azure.com/contoso/My%20Shop/_git/web%20app"
        },
        "pullRequestId": 42,
        "codeReviewId": 42,
        "status": "active",
        "createdBy": { "displayName": "Someone" },
        "title": "Add a thing",
        "sourceRefName": "refs/heads/feature/thing",
        "targetRefName": "refs/heads/main",
        "mergeStatus": "succeeded",
        "lastMergeSourceCommit": { "commitId": "{{SourceCommit}}", "url": "https://example.invalid" },
        "lastMergeTargetCommit": { "commitId": "{{TargetCommit.ToUpperInvariant()}}" },
        "lastMergeCommit": { "commitId": "4444444444444444444444444444444444444444" }
      }
      """;

   internal static readonly string IterationsJson = $$"""
      {
        "value": [
          {
            "id": 2,
            "description": "Updated",
            "sourceRefCommit": { "commitId": "{{LaterSourceCommit}}" },
            "targetRefCommit": { "commitId": "{{TargetCommit}}" },
            "commonRefCommit": { "commitId": "{{TargetCommit}}" },
            "reason": "push"
          },
          {
            "id": 1,
            "sourceRefCommit": { "commitId": "{{SourceCommit}}" },
            "targetRefCommit": { "commitId": "{{TargetCommit}}" },
            "reason": "create"
          }
        ],
        "count": 2
      }
      """;

   // The remote URL of PullRequestJson without the organization as its user name. A comparison of Uri values ignores user information, so the tests compare this text.
   private const string StrippedRemoteUrl = "https://dev.azure.com/contoso/My%20Shop/_git/web%20app";

   internal const string DevAzureUrl = "https://dev.azure.com/contoso/My%20Shop/_git/web%20app/pullrequest/42";

   public static readonly TheoryData<string, string> HostForms = new()
   {
      { DevAzureUrl, "/contoso/My%20Shop/_apis/git/repositories/web%20app/pullrequests/42" },
      { "https://contoso.visualstudio.com/My%20Shop/_git/web%20app/pullrequest/42", "/My%20Shop/_apis/git/repositories/web%20app/pullrequests/42" },
   };

   [Theory]
   [MemberData(nameof(HostForms))]
   public async Task Pull_request_metadata_is_read(string url, string path)
   {
      await using var server = new RecordingHttpServer(answer: target => target == $"{path}?api-version=7.1" ? new HttpAnswer(200, PullRequestJson) : new HttpAnswer(500));
      using var client = NewClient(server, new FixedAccessTokenProvider("token-one"));

      var metadata = await client.GetPullRequestAsync(PullRequest(url), TestContext.Current.CancellationToken);

      Assert.Equal(PullRequestStatus.Active, metadata.Status);
      Assert.Equal(StrippedRemoteUrl, metadata.RemoteUrl.AbsoluteUri);

      var request = Assert.Single(server.Requests);
      Assert.StartsWith($"GET {path}?api-version=7.1 HTTP/1.1\r\n", request, StringComparison.Ordinal);
      Assert.Contains("Authorization: Bearer token-one\r\n", request, StringComparison.Ordinal);
   }

   /// <summary>System.Text.Json writes a Uri as its original string, so that string is the remote URL that the pin stores.</summary>
   [Theory]
   [InlineData("https://contoso@dev.azure.com/contoso/My%20Shop/_git/web%20app", StrippedRemoteUrl)]
   [InlineData("https://contoso:secret@dev.azure.com:443/contoso/shop/_git/web", "https://dev.azure.com/contoso/shop/_git/web")]
   [InlineData("https://contoso@example.test:8443/contoso/shop/_git/web", "https://example.test:8443/contoso/shop/_git/web")]
   [InlineData("file:///C:/repos/web", "file:///C:/repos/web")]
   [InlineData("file:///srv/repos/web", "file:///srv/repos/web")]
   public async Task Remote_url_without_user_information_keeps_its_canonical_text(string remoteUrl, string expected)
   {
      var json = JsonNode.Parse(PullRequestJson)!.AsObject();
      json["repository"]!["remoteUrl"] = remoteUrl;
      await using var server = new RecordingHttpServer(answer: _ => new HttpAnswer(200, json.ToJsonString()));
      using var client = NewClient(server, new FixedAccessTokenProvider("token-one"));

      var metadata = await client.GetPullRequestAsync(PullRequest(DevAzureUrl), TestContext.Current.CancellationToken);

      Assert.Equal(expected, metadata.RemoteUrl.OriginalString);
   }

   [Theory]
   [MemberData(nameof(HostForms))]
   public async Task Iterations_are_read_in_order(string url, string path)
   {
      await using var server = new RecordingHttpServer(answer: target => target == $"{path}/iterations?api-version=7.1" ? new HttpAnswer(200, IterationsJson) : new HttpAnswer(500));
      using var client = NewClient(server, new FixedAccessTokenProvider("token-one"));

      var iterations = await client.GetIterationsAsync(PullRequest(url), TestContext.Current.CancellationToken);

      Assert.Equal(
         [
            new PullRequestIteration(1, CommitId.Parse(SourceCommit), CommitId.Parse(TargetCommit)),
            new PullRequestIteration(2, CommitId.Parse(LaterSourceCommit), CommitId.Parse(TargetCommit)),
         ],
         iterations);
      Assert.Contains("Authorization: Bearer token-one\r\n", Assert.Single(server.Requests), StringComparison.Ordinal);
   }

   /// <summary>The client reads only the status and the remote URL, so a property that nothing reads fails no read, whether it is missing or malformed.</summary>
   [Theory]
   [InlineData("sourceRefName", null)]
   [InlineData("targetRefName", null)]
   [InlineData("lastMergeSourceCommit", null)]
   [InlineData("lastMergeTargetCommit", null)]
   [InlineData("pullRequestId", null)]
   [InlineData("lastMergeSourceCommit", """{ "commitId": "not a commit" }""")]
   [InlineData("lastMergeTargetCommit", "null")]
   [InlineData("sourceRefName", "42")]
   [InlineData("pullRequestId", "\"forty-two\"")]
   public async Task Pull_request_whose_unread_property_is_missing_or_malformed_is_read(string property, string? value)
   {
      var json = JsonNode.Parse(PullRequestJson)!.AsObject();
      json.Remove(property);
      if (value is not null)
      {
         json[property] = JsonNode.Parse(value);
      }

      await using var server = new RecordingHttpServer(answer: _ => new HttpAnswer(200, json.ToJsonString()));
      using var client = NewClient(server, new FixedAccessTokenProvider("token-one"));

      var metadata = await client.GetPullRequestAsync(PullRequest(DevAzureUrl), TestContext.Current.CancellationToken);

      Assert.Equal(PullRequestStatus.Active, metadata.Status);
      Assert.Equal(StrippedRemoteUrl, metadata.RemoteUrl.AbsoluteUri);
   }

   [Fact]
   public async Task Repository_with_only_its_remote_url_is_read()
   {
      var json = JsonNode.Parse(PullRequestJson)!.AsObject();
      json["repository"] = new JsonObject { ["remoteUrl"] = "https://contoso@dev.azure.com/contoso/My%20Shop/_git/web%20app" };
      await using var server = new RecordingHttpServer(answer: _ => new HttpAnswer(200, json.ToJsonString()));
      using var client = NewClient(server, new FixedAccessTokenProvider("token-one"));

      var metadata = await client.GetPullRequestAsync(PullRequest(DevAzureUrl), TestContext.Current.CancellationToken);

      Assert.Equal(StrippedRemoteUrl, metadata.RemoteUrl.AbsoluteUri);
   }

   [Theory]
   [InlineData(401, nameof(AzureDevOpsFailure.NotSignedIn), "not signed in or token rejected")]
   [InlineData(203, nameof(AzureDevOpsFailure.NotSignedIn), "not signed in or token rejected")]
   [InlineData(403, nameof(AzureDevOpsFailure.NoAccess), "no access")]
   [InlineData(404, nameof(AzureDevOpsFailure.NotFound), "pull request not found")]
   public async Task Refusal_maps_to_its_reason(int status, string failure, string reason)
   {
      await using var server = new RecordingHttpServer(answer: _ => new HttpAnswer(status, "<html>Sign in</html>"));
      using var client = NewClient(server, new FixedAccessTokenProvider("token-one"));
      var pullRequest = PullRequest(DevAzureUrl);

      var pullRequestFailure = await Assert.ThrowsAsync<AzureDevOpsException>(() => client.GetPullRequestAsync(pullRequest, TestContext.Current.CancellationToken));
      var iterationsFailure = await Assert.ThrowsAsync<AzureDevOpsException>(() => client.GetIterationsAsync(pullRequest, TestContext.Current.CancellationToken));

      Assert.All([pullRequestFailure, iterationsFailure], exception =>
      {
         Assert.Equal(failure, exception.Failure.ToString());
         Assert.Contains(reason, exception.Message, StringComparison.Ordinal);
         Assert.Contains(pullRequest.Url.AbsoluteUri, exception.Message, StringComparison.Ordinal);
      });
      if (failure == nameof(AzureDevOpsFailure.NotSignedIn))
      {
         Assert.Contains("az login", pullRequestFailure.Message, StringComparison.Ordinal);
      }
   }

   /// <summary>A refusal of the token or of its account needs a new token, which a new sign-in brings.</summary>
   [Theory]
   [InlineData(401, true)]
   [InlineData(203, true)]
   [InlineData(403, true)]
   [InlineData(404, false)]
   [InlineData(500, false)]
   public async Task Refusal_of_the_token_or_its_account_rejects_the_token_at_its_provider(int status, bool rejects)
   {
      await using var server = new RecordingHttpServer(answer: _ => new HttpAnswer(status, "{}"));
      var tokens = new FixedAccessTokenProvider("token-one");
      using var client = NewClient(server, tokens);

      await Assert.ThrowsAsync<AzureDevOpsException>(() => client.GetPullRequestAsync(PullRequest(DevAzureUrl), TestContext.Current.CancellationToken));

      Assert.Equal(rejects ? ["token-one"] : [], tokens.Rejected);
   }

   [Fact]
   public async Task Other_failure_gives_the_status_and_the_message_of_Azure_DevOps()
   {
      await using var server = new RecordingHttpServer(answer: _ => new HttpAnswer(500, """{"$id": "1", "message": "TF400898: An internal error occurred.", "typeKey": "X", "errorCode": 0}"""));
      using var client = NewClient(server, new FixedAccessTokenProvider("token-one"));

      var exception = await Assert.ThrowsAsync<AzureDevOpsException>(() => client.GetPullRequestAsync(PullRequest(DevAzureUrl), TestContext.Current.CancellationToken));

      Assert.Equal(AzureDevOpsFailure.Other, exception.Failure);
      Assert.Contains("500", exception.Message, StringComparison.Ordinal);
      Assert.Contains("TF400898: An internal error occurred.", exception.Message, StringComparison.Ordinal);
   }

   [Theory]
   [InlineData("<html>not json</html>")]
   [InlineData("""{"pullRequestId": 42}""")]
   [InlineData("""{"value": [{"id": 1, "sourceRefCommit": {"commitId": "abc"}, "targetRefCommit": {"commitId": "abc"}}]}""")]
   public async Task Unreadable_answer_is_a_failure_with_a_reason(string body)
   {
      await using var server = new RecordingHttpServer(answer: _ => new HttpAnswer(200, body));
      using var client = NewClient(server, new FixedAccessTokenProvider("token-one"));
      var pullRequest = PullRequest(DevAzureUrl);

      var exception = body.Contains("value", StringComparison.Ordinal)
         ? await Assert.ThrowsAsync<AzureDevOpsException>(() => client.GetIterationsAsync(pullRequest, TestContext.Current.CancellationToken))
         : await Assert.ThrowsAsync<AzureDevOpsException>(() => client.GetPullRequestAsync(pullRequest, TestContext.Current.CancellationToken));

      Assert.Equal(AzureDevOpsFailure.Other, exception.Failure);
      Assert.Contains("cannot read", exception.Message, StringComparison.Ordinal);
   }

   [Fact]
   public async Task Unreachable_server_is_a_failure_with_a_reason()
   {
      Uri unreachable;
      await using (var server = new RecordingHttpServer())
      {
         unreachable = server.BaseUrl;
      }

      using var client = new AzureDevOpsClient(new FixedAccessTokenProvider("token-one"), new AzureDevOpsOptions { ServerOverride = unreachable }, NullLogger<AzureDevOpsClient>.Instance);

      var exception = await Assert.ThrowsAsync<AzureDevOpsException>(() => client.GetPullRequestAsync(PullRequest(DevAzureUrl), TestContext.Current.CancellationToken));

      Assert.Equal(AzureDevOpsFailure.Other, exception.Failure);
      Assert.Contains("could not reach Azure DevOps", exception.Message, StringComparison.Ordinal);
   }

   [Fact]
   public async Task Missing_token_stops_the_request()
   {
      await using var server = new RecordingHttpServer(answer: _ => new HttpAnswer(200, PullRequestJson));
      using var client = NewClient(server, new UnavailableAccessTokenProvider());

      var exception = await Assert.ThrowsAsync<AccessTokenUnavailableException>(() => client.GetPullRequestAsync(PullRequest(DevAzureUrl), TestContext.Current.CancellationToken));

      Assert.Equal("Run 'az login'.", exception.Message);
      Assert.Empty(server.Requests);
   }

   private static AzureDevOpsPullRequest PullRequest(string url) => AzureDevOpsPullRequest.TryParse(url, out var pullRequest) ? pullRequest : throw new ArgumentException(url);

   private static AzureDevOpsClient NewClient(RecordingHttpServer server, IAccessTokenProvider tokens) =>
      new(tokens, new AzureDevOpsOptions { ServerOverride = server.BaseUrl }, NullLogger<AzureDevOpsClient>.Instance);

   private sealed class UnavailableAccessTokenProvider : IAccessTokenProvider
   {
      public ValueTask<AccessToken> GetTokenAsync(TimeSpan minimumValidity, CancellationToken cancellationToken) => throw new AccessTokenUnavailableException("Run 'az login'.");
   }
}
