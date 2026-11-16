using System.Text.Json.Nodes;
using Lotsen.Git;

namespace Lotsen.Tests;

/// <summary>A <see cref="RecordingHttpServer"/> that answers as the GitHub REST API for one repository, <see cref="Url"/>, whose branches, tags and commits the test chooses.</summary>
internal sealed class FakeGitHubRepository : IAsyncDisposable
{
   public const string Url = "https://github.com/octo-org/hello-world";

   public const string RepositoryPath = "/repos/octo-org/hello-world";

   public static readonly Uri CloneUrl = new("https://github.com/octo-org/hello-world.git");

   private static readonly HttpAnswer NotFound = new(404, """{ "message": "Not Found", "documentation_url": "https://docs.github.com/rest" }""");

   private readonly Lock _lock = new();
   private readonly Dictionary<string, CommitId> _branches = new(StringComparer.Ordinal);
   private readonly Dictionary<string, (string Sha, string Type)> _tags = new(StringComparer.Ordinal);
   private readonly Dictionary<string, (string Sha, string Type)> _tagObjects = new(StringComparer.Ordinal);
   private readonly HashSet<CommitId> _commits = [];
   private string _fullName = "octo-org/hello-world";
   private string _defaultBranch = "main";
   private HttpAnswer? _answer;

   public FakeGitHubRepository() => Server = new RecordingHttpServer(answer: Answer);

   public RecordingHttpServer Server { get; }

   /// <summary>Returns the request target of a ref of the repository with GitHub's full name, such as <c>heads/main</c>, with each segment of its name escaped.</summary>
   public static string RefPath(string kind, string name, string fullName = "octo-org/hello-world") =>
      $"/repos/{fullName}/git/ref/{kind}/{string.Join('/', name.Split('/').Select(Uri.EscapeDataString))}";

   /// <summary>Makes the repository's default branch have this name, and GitHub name the repository by this full name, as it does for a renamed repository.</summary>
   public void Repository(string defaultBranch, string fullName = "octo-org/hello-world")
   {
      lock (_lock)
      {
         (_defaultBranch, _fullName) = (defaultBranch, fullName);
      }
   }

   /// <summary>Makes the repository have a branch that points to the commit.</summary>
   public void Branch(string name, CommitId commit)
   {
      lock (_lock)
      {
         _branches[name] = commit;
         _commits.Add(commit);
      }
   }

   /// <summary>Makes the repository have a lightweight tag that names the commit.</summary>
   public void Tag(string name, CommitId commit)
   {
      lock (_lock)
      {
         _tags[name] = (commit.Value, "commit");
         _commits.Add(commit);
      }
   }

   /// <summary>Makes the repository have a tag whose reference names an object of this type, such as an annotated tag's <c>tag</c> object or a <c>tree</c>.</summary>
   public void Tag(string name, string sha, string type)
   {
      lock (_lock)
      {
         _tags[name] = (sha, type);
      }
   }

   /// <summary>Makes the repository have an annotated tag object that names an object of this type.</summary>
   public void TagObject(string sha, string targetSha, string targetType)
   {
      lock (_lock)
      {
         _tagObjects[sha] = (targetSha, targetType);
      }
   }

   /// <summary>Makes the repository have the commit.</summary>
   public void Commit(CommitId commit)
   {
      lock (_lock)
      {
         _commits.Add(commit);
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

   public ValueTask DisposeAsync() => Server.DisposeAsync();

   private HttpAnswer Answer(string target)
   {
      lock (_lock)
      {
         if (_answer is { } answer)
         {
            return answer;
         }

         var repository = $"/repos/{_fullName}";
         if (target == RepositoryPath)
         {
            // The answer carries more than the client reads, as GitHub's does.
            return new HttpAnswer(200, $$"""
               {
                 "id": 1296269,
                 "name": "{{_fullName.Split('/')[1]}}",
                 "full_name": "{{_fullName}}",
                 "private": false,
                 "default_branch": "{{_defaultBranch}}",
                 "clone_url": "{{CloneUrl.AbsoluteUri}}",
                 "html_url": "{{Url}}"
               }
               """);
         }

         if (_branches.FirstOrDefault(branch => target == RefPath("heads", branch.Key, _fullName)) is { Key: not null } found)
         {
            return ReferenceAnswer($"refs/heads/{found.Key}", found.Value.Value, "commit");
         }

         if (_tags.FirstOrDefault(tag => target == RefPath("tags", tag.Key, _fullName)) is { Key: not null } tagged)
         {
            return ReferenceAnswer($"refs/tags/{tagged.Key}", tagged.Value.Sha, tagged.Value.Type);
         }

         if (target.StartsWith($"{repository}/git/tags/", StringComparison.Ordinal) && _tagObjects.TryGetValue(target[$"{repository}/git/tags/".Length..], out var tagObject))
         {
            return new HttpAnswer(200, $$"""
               { "sha": "{{target[$"{repository}/git/tags/".Length..]}}", "tag": "v1", "message": "Release", "object": { "sha": "{{tagObject.Sha}}", "type": "{{tagObject.Type}}" } }
               """);
         }

         if (target.StartsWith($"{repository}/git/commits/", StringComparison.Ordinal)
            && CommitId.TryParse(target[$"{repository}/git/commits/".Length..], out var commit)
            && _commits.Contains(commit))
         {
            return new HttpAnswer(200, $$"""{ "sha": "{{commit}}", "message": "Add a greeting", "tree": { "sha": "{{new string('f', 40)}}" } }""");
         }

         return NotFound;
      }
   }

   private static HttpAnswer ReferenceAnswer(string name, string sha, string type) => new(200, $$"""
      { "ref": "{{name}}", "node_id": "REF_kwDOABCDEFG", "object": { "sha": "{{sha}}", "type": "{{type}}" } }
      """);
}
