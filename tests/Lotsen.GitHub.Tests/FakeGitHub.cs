using System.Text.Json.Nodes;
using Lotsen.Credentials;
using Lotsen.Git;
using Lotsen.GitHub;
using Lotsen.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lotsen.Tests;

/// <summary>A <see cref="RecordingHttpServer"/> that answers as the GitHub REST API for one pull request, <see cref="PrUrl"/>, whose commits and state the test chooses.</summary>
internal sealed class FakeGitHub : IAsyncDisposable
{
   public const string PrUrl = "https://github.com/octo-org/hello-world/pull/42";

   public const string PullRequestPath = "/repos/octo-org/hello-world/pulls/42";

   public static readonly Uri CloneUrl = new("https://github.com/octo-org/hello-world.git");

   private readonly Lock _lock = new();
   private CommitId _head = CommitId.Parse(new string('1', 40));
   private CommitId _base = CommitId.Parse(new string('a', 40));
   private string _state = "open";
   private bool _merged;
   private bool _draft;
   private Uri _headCloneUrl = CloneUrl;
   private HttpAnswer? _answer;

   /// <param name="hold">Whether every answer waits for <see cref="RecordingHttpServer.Release"/>.</param>
   public FakeGitHub(bool hold = false) => Server = new RecordingHttpServer(hold, Answer);

   public RecordingHttpServer Server { get; }

   /// <summary>Makes the pull request report these commits and this state, from a head branch in the base repository, and answer every request again.</summary>
   public void PullRequest(CommitId head, CommitId @base, string state = "open", bool merged = false, bool draft = false)
   {
      lock (_lock)
      {
         (_head, _base, _state, _merged, _draft, _headCloneUrl, _answer) = (head, @base, state, merged, draft, CloneUrl, null);
      }
   }

   /// <summary>Makes the pull request's head branch live in the fork at this clone URL.</summary>
   public void FromFork(Uri headCloneUrl)
   {
      lock (_lock)
      {
         _headCloneUrl = headCloneUrl;
      }
   }

   /// <summary>Makes every request get this status and an error answer with the message, as GitHub's refusals do.</summary>
   public void Refuse(int status, string message = "Refused by the fake.") =>
      Respond(new HttpAnswer(status, new JsonObject { ["message"] = message, ["documentation_url"] = "https://docs.github.com/rest" }.ToJsonString()));

   /// <summary>Makes every request get this answer.</summary>
   public void Respond(HttpAnswer answer)
   {
      lock (_lock)
      {
         _answer = answer;
      }
   }

   /// <summary>Returns a platform that calls this server with the tokens of the provider and keeps its iteration records in the data directory.</summary>
   public GitHubPlatform Platform(IAccessTokenProvider tokens, DataDirectory dataDirectory, ILoggerFactory? logs = null) => PlatformAt(Server.BaseUrl, tokens, dataDirectory, logs);

   /// <summary>Returns a platform that calls the API at this base URL.</summary>
   public static GitHubPlatform PlatformAt(Uri apiBaseUrl, IAccessTokenProvider tokens, DataDirectory dataDirectory, ILoggerFactory? logs = null, TimeSpan? timeout = null)
   {
      var loggers = logs ?? NullLoggerFactory.Instance;
      var options = new GitHubOptions { ApiBaseUrl = apiBaseUrl, Timeout = timeout ?? TimeSpan.FromSeconds(30) };
      return new GitHubPlatform(
         new GitHubClient(tokens, options, loggers.CreateLogger<GitHubClient>()),
         new GitHubIterationStore(dataDirectory, loggers.CreateLogger<GitHubIterationStore>()),
         tokens);
   }

   /// <summary>Returns a platform for a test that resolves nothing, so it sends no request and writes no file.</summary>
   public static GitHubPlatform Offline() =>
      PlatformAt(new Uri("http://127.0.0.1:9/"), new FixedAccessTokenProvider("token-one"), new DataDirectory(Path.Combine(Path.GetTempPath(), "lotsen-unused-data")));

   public ValueTask DisposeAsync() => Server.DisposeAsync();

   private HttpAnswer Answer(string target)
   {
      lock (_lock)
      {
         if (_answer is { } answer)
         {
            return answer;
         }

         if (target != PullRequestPath)
         {
            return new HttpAnswer(404, """{ "message": "Not Found", "documentation_url": "https://docs.github.com/rest" }""");
         }

         // The answer carries more than the client reads, as GitHub's does.
         return new HttpAnswer(200, $$"""
            {
              "url": "https://api.github.com/repos/octo-org/hello-world/pulls/42",
              "number": 42,
              "state": "{{_state}}",
              "locked": false,
              "title": "Add a greeting",
              "user": { "login": "octocat", "id": 1 },
              "draft": {{(_draft ? "true" : "false")}},
              "merged": {{(_merged ? "true" : "false")}},
              "merged_at": {{(_merged ? "\"2026-11-13T12:00:00Z\"" : "null")}},
              "head": {
                "label": "octocat:feature",
                "ref": "feature",
                "sha": "{{_head}}",
                "repo": { "full_name": "octocat/hello-world", "clone_url": "{{_headCloneUrl.AbsoluteUri}}" }
              },
              "base": {
                "label": "octo-org:main",
                "ref": "main",
                "sha": "{{_base}}",
                "repo": { "full_name": "octo-org/hello-world", "private": false, "clone_url": "{{CloneUrl.AbsoluteUri}}" }
              }
            }
            """);
      }
   }
}
