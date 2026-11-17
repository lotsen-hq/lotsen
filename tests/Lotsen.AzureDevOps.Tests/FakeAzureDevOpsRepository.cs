using System.Text.Json.Nodes;
using Lotsen.AzureDevOps;
using Lotsen.Credentials;
using Lotsen.Git;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lotsen.Tests;

/// <summary>A <see cref="RecordingHttpServer"/> that answers as the Azure DevOps REST API for one repository, <see cref="Url"/>, whose branches, tags and commits the test chooses.</summary>
/// <remarks>
/// It answers a request for the refs as Azure DevOps documents it: the filter keeps each ref whose name after <c>refs/</c> starts with it, and only <c>peelTags=true</c> fills in the commit of
/// an annotated tag.
/// </remarks>
internal sealed class FakeAzureDevOpsRepository : IAsyncDisposable
{
   public const string Url = "https://dev.azure.com/contoso/shop/_git/web";

   public const string Id = "7d2c1b0e-0000-0000-0000-000000000001";

   public const string RepositoryPath = "/contoso/shop/_apis/git/repositories/web";

   public const string ByIdPath = $"/contoso/shop/_apis/git/repositories/{Id}";

   public static readonly Uri CloneUrl = new(Url);

   private readonly Lock _lock = new();
   private readonly List<(string Name, string ObjectId, string? Peeled)> _refs = [];
   private readonly HashSet<CommitId> _commits = [];
   private string? _defaultBranch = "refs/heads/main";
   private HttpAnswer? _refusal;

   public FakeAzureDevOpsRepository() => Server = new RecordingHttpServer(answer: Answer);

   public RecordingHttpServer Server { get; }

   /// <summary>Returns the platform with a client that sends every request to the server, with the token provider's tokens.</summary>
   public static AzureDevOpsPlatform PlatformAt(Uri server, IAccessTokenProvider tokens) =>
      new(new AzureDevOpsClient(tokens, new AzureDevOpsOptions { ServerOverride = server }, NullLogger<AzureDevOpsClient>.Instance), tokens);

   /// <summary>Returns the request target of the refs whose names start with the filter, such as <c>heads/main</c>.</summary>
   public static string RefsTarget(string filter) => $"{ByIdPath}/refs?filter={Uri.EscapeDataString(filter)}&peelTags=true&api-version=7.1";

   /// <summary>Makes the repository's default branch have this name, or makes the repository name no default branch, as Azure DevOps does for an empty repository.</summary>
   public void Repository(string? defaultBranch)
   {
      lock (_lock)
      {
         _defaultBranch = defaultBranch is null ? null : $"refs/heads/{defaultBranch}";
      }
   }

   /// <summary>Makes the repository have a branch that points to the commit, listed after the refs that the test added before.</summary>
   public void Branch(string name, CommitId commit) => AddRef($"refs/heads/{name}", commit.Value, peeled: null, commit);

   /// <summary>Makes the repository have a lightweight tag that names the commit.</summary>
   public void Tag(string name, CommitId commit) => AddRef($"refs/tags/{name}", commit.Value, peeled: null, commit);

   /// <summary>Makes the repository have an annotated tag, whose tag object names the commit.</summary>
   public void AnnotatedTag(string name, string tagObject, CommitId commit) => AddRef($"refs/tags/{name}", tagObject, commit.Value, commit);

   /// <summary>Makes the repository have the commit.</summary>
   public void Commit(CommitId commit)
   {
      lock (_lock)
      {
         _commits.Add(commit);
      }
   }

   /// <summary>Makes every request get this status and an error answer with the message, as a refusal does.</summary>
   public void Refuse(int status, string message = "Refused by the fake.")
   {
      lock (_lock)
      {
         _refusal = new HttpAnswer(status, new JsonObject { ["message"] = message }.ToJsonString());
      }
   }

   public ValueTask DisposeAsync() => Server.DisposeAsync();

   private void AddRef(string name, string objectId, string? peeled, CommitId commit)
   {
      lock (_lock)
      {
         _refs.RemoveAll(entry => entry.Name == name);
         _refs.Add((name, objectId, peeled));
         _commits.Add(commit);
      }
   }

   private HttpAnswer Answer(string target)
   {
      lock (_lock)
      {
         if (_refusal is { } refusal)
         {
            return refusal;
         }

         var (path, query) = target.Split('?', 2) is [var p, var q] ? (p, Query(q)) : (target, new Dictionary<string, string>());
         if (query.GetValueOrDefault("api-version") != "7.1")
         {
            return NotFound("No such path on the fake.");
         }

         if (path == RepositoryPath)
         {
            // The answer carries more than the client reads, as Azure DevOps's does, and its remote URL names the organization as the user.
            var repository = new JsonObject
            {
               ["id"] = Id,
               ["name"] = "web",
               ["project"] = new JsonObject { ["id"] = "0f0e0d0c-0000-0000-0000-000000000002", ["name"] = "shop" },
               ["remoteUrl"] = "https://contoso@dev.azure.com/contoso/shop/_git/web",
               ["sshUrl"] = "git@ssh.dev.azure.com:v3/contoso/shop/web",
               ["webUrl"] = Url,
            };
            if (_defaultBranch is { } defaultBranch)
            {
               repository["defaultBranch"] = defaultBranch;
            }

            return new HttpAnswer(200, repository.ToJsonString());
         }

         if (path == $"{ByIdPath}/refs")
         {
            var filter = "refs/" + query.GetValueOrDefault("filter", "");
            var peel = query.GetValueOrDefault("peelTags") == "true";
            var value = new JsonArray();
            foreach (var (name, objectId, peeled) in _refs.Where(entry => entry.Name.StartsWith(filter, StringComparison.Ordinal)))
            {
               var entry = new JsonObject { ["name"] = name, ["objectId"] = objectId, ["url"] = $"https://dev.azure.com/contoso/_apis/git/repositories/{Id}/refs?filter={name}" };
               if (peel && peeled is not null)
               {
                  entry["peeledObjectId"] = peeled;
               }

               value.Add(entry);
            }

            return new HttpAnswer(200, new JsonObject { ["value"] = value, ["count"] = value.Count }.ToJsonString());
         }

         if (path.StartsWith($"{ByIdPath}/commits/", StringComparison.Ordinal))
         {
            var id = path[$"{ByIdPath}/commits/".Length..];
            return CommitId.TryParse(id, out var commit) && _commits.Contains(commit)
               ? new HttpAnswer(200, new JsonObject { ["commitId"] = commit.Value, ["comment"] = "Add a greeting", ["treeId"] = new string('f', 40) }.ToJsonString())
               : NotFound($"The commit {id} was not found.");
         }

         return NotFound("No such path on the fake.");
      }
   }

   private static HttpAnswer NotFound(string message) => new(404, new JsonObject { ["message"] = message }.ToJsonString());

   private static Dictionary<string, string> Query(string query) =>
      query.Split('&').Select(pair => pair.Split('=', 2)).Where(pair => pair.Length == 2).ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]), StringComparer.Ordinal);
}
