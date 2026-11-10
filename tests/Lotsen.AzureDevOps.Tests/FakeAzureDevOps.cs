using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Lotsen.Git;

namespace Lotsen.Tests;

/// <summary>A <see cref="RecordingHttpServer"/> that answers as the Azure DevOps REST API for one pull request, <see cref="PrUrl"/>, whose repository the test chooses.</summary>
internal sealed class FakeAzureDevOps : IAsyncDisposable
{
   public const string PrUrl = "https://dev.azure.com/contoso/shop/_git/web/pullrequest/42";

   private const string PullRequestPath = "/contoso/shop/_apis/git/repositories/web/pullrequests/42";

   private readonly Lock _lock = new();
   private Uri _remoteUrl = new("https://dev.azure.com/contoso/shop/_git/web");
   private IReadOnlyList<(CommitId Source, CommitId Target)> _iterations = [];
   private string _status = "active";
   private HttpAnswer? _refusal;
   private IReadOnlyList<string> _omitted = [];

   /// <param name="hold">Whether every answer waits for <see cref="RecordingHttpServer.Release"/>.</param>
   public FakeAzureDevOps(bool hold = false) => Server = new RecordingHttpServer(hold, Answer);

   public RecordingHttpServer Server { get; }

   /// <summary>Makes the pull request's repository the one at this URL, with one iteration per pair of commits.</summary>
   public void PullRequest(Uri remoteUrl, params (CommitId Source, CommitId Target)[] iterations)
   {
      lock (_lock)
      {
         _remoteUrl = remoteUrl;
         _iterations = iterations;
         _refusal = null;
      }
   }

   /// <summary>Makes the pull request report this status, such as <c>completed</c>.</summary>
   public void Status(string status)
   {
      lock (_lock)
      {
         _status = status;
      }
   }

   /// <summary>Makes the pull request's answer omit these top-level properties, such as <c>sourceRefName</c>.</summary>
   public void Omit(params string[] properties)
   {
      lock (_lock)
      {
         _omitted = properties;
      }
   }

   /// <summary>Makes every request get this status and an error answer with the message, as a refusal does.</summary>
   public void Refuse(int status, string message = "Refused by the fake.")
   {
      lock (_lock)
      {
         _refusal = new HttpAnswer(status, $$"""{ "message": "{{message}}" }""");
      }
   }

   public ValueTask DisposeAsync() => Server.DisposeAsync();

   private HttpAnswer Answer(string target)
   {
      lock (_lock)
      {
         if (_refusal is { } refusal)
         {
            return refusal;
         }

         if (target == $"{PullRequestPath}?api-version=7.1")
         {
            // A pull request without iterations has no merge commits to report.
            var lastMerge = _iterations.Count > 0
               ? $$""", "lastMergeSourceCommit": { "commitId": "{{_iterations[^1].Source}}" }, "lastMergeTargetCommit": { "commitId": "{{_iterations[^1].Target}}" }"""
               : "";
            var pullRequest = JsonNode.Parse($$"""
               {
                 "repository": { "id": "7d2c1b0e-0000-0000-0000-000000000001", "name": "web", "remoteUrl": "{{_remoteUrl.AbsoluteUri}}" },
                 "pullRequestId": 42,
                 "status": "{{_status}}",
                 "sourceRefName": "refs/heads/feature",
                 "targetRefName": "refs/heads/main"{{lastMerge}}
               }
               """)!.AsObject();
            foreach (var property in _omitted)
            {
               pullRequest.Remove(property);
            }

            return new HttpAnswer(200, pullRequest.ToJsonString());
         }

         if (target == $"{PullRequestPath}/iterations?api-version=7.1")
         {
            var value = new StringBuilder();
            for (var i = 0; i < _iterations.Count; i++)
            {
               value.Append(CultureInfo.InvariantCulture, $$"""
                  {{(i > 0 ? "," : "")}}{ "id": {{i + 1}}, "sourceRefCommit": { "commitId": "{{_iterations[i].Source}}" }, "targetRefCommit": { "commitId": "{{_iterations[i].Target}}" } }
                  """);
            }

            return new HttpAnswer(200, $$"""{ "value": [{{value}}], "count": {{_iterations.Count}} }""");
         }

         return new HttpAnswer(404, """{ "message": "No such path on the fake." }""");
      }
   }
}
