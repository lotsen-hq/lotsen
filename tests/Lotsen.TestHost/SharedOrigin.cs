using System.Diagnostics;
using System.Text;
using Lotsen.Git;

namespace Lotsen.TestHost;

/// <summary>The one git repository that every world's pull requests come from, served over <c>file://</c> with filters and any commit by ID allowed.</summary>
/// <remarks>
/// A commit goes in through git's plumbing with an index file of its own, so commits of many worlds can run at once. Each world writes only its own refs, <c>refs/worlds/{id}/{branch}</c>, and
/// a clone fetches each pinned commit by its ID. A copied data directory keeps fetching from the same path, so a template's clone works in every copy.
/// </remarks>
internal sealed class SharedOrigin
{
   private readonly string _gitConfig;

   public SharedOrigin(string root)
   {
      Path = System.IO.Path.Combine(root, "origin.git");
      _gitConfig = System.IO.Path.Combine(root, "no-global-config");
      Directory.CreateDirectory(Path);
      Git(null, "init", "--quiet", "--bare", "--initial-branch=main");
      Git(null, "config", "uploadpack.allowFilter", "true");
      Git(null, "config", "uploadpack.allowAnySHA1InWant", "true");

      // A clone fetches the branches first, so the repository has one.
      var empty = Git(null, "mktree").Trim();
      Git(null, "update-ref", "refs/heads/main", Git(null, "commit-tree", empty, "-m", "The shared origin").Trim());
   }

   /// <summary>The bare repository's directory.</summary>
   public string Path { get; }

   public Uri Url => new(Path);

   /// <summary>Stores a commit of the files on the parent's tree, and points the world's branch at it.</summary>
   /// <param name="world">The world whose ref the commit goes on.</param>
   /// <param name="branch">The branch's name within the world, such as <c>main</c>.</param>
   /// <param name="parent">The parent commit, or null for a commit without one.</param>
   /// <param name="message">The commit message.</param>
   /// <param name="files">The content of each file by repository path. A null content deletes the file.</param>
   /// <returns>The new commit.</returns>
   public CommitId Commit(string world, string branch, CommitId? parent, string message, IReadOnlyDictionary<string, string?> files)
   {
      var index = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"lotsen-host-index-{Guid.NewGuid():N}");
      try
      {
         if (parent is { } from)
         {
            Git(index, "read-tree", from.Value);
         }
         else
         {
            Git(index, "read-tree", "--empty");
         }

         foreach (var (path, content) in files)
         {
            if (content is null)
            {
               // The bare repository has no work tree, and mode 0 in --index-info removes the entry without one.
               Git(index, ["update-index", "--index-info"], $"0 {new string('0', 40)}\t{path}\n");
               continue;
            }

            var blob = WriteObject(null, ["hash-object", "-w", "--stdin"], content).Trim();
            Git(index, "update-index", "--add", "--cacheinfo", $"100644,{blob},{path}");
         }

         var tree = WriteObject(index, ["write-tree"], input: null).Trim();
         string[] commitTree = parent is { } on ? ["commit-tree", tree, "-p", on.Value, "-m", message] : ["commit-tree", tree, "-m", message];
         var commit = WriteObject(null, commitTree, input: null).Trim();
         Git(null, "update-ref", $"refs/worlds/{world}/{branch}", commit);
         return CommitId.Parse(commit);
      }
      finally
      {
         File.Delete(index);
      }
   }

   // Worlds that commit the same files at once write the same objects, and git on Windows has failed such a write with "unable to write file … Permission denied". The same input writes the
   // same object, so the write runs again.
   private string WriteObject(string? index, string[] arguments, string? input)
   {
      var elapsed = Stopwatch.StartNew();
      while (true)
      {
         try
         {
            return Git(index, arguments, input);
         }
         catch (InvalidOperationException exception) when (exception.Message.Contains("unable to write", StringComparison.Ordinal) && elapsed.Elapsed < TimeSpan.FromSeconds(5))
         {
            Thread.Sleep(10);
         }
      }
   }

   private string Git(string? index, params string[] arguments) => Git(index, arguments, input: null);

   private string Git(string? index, string[] arguments, string? input)
   {
      var startInfo = new ProcessStartInfo("git")
      {
         WorkingDirectory = Path,
         RedirectStandardInput = true,
         StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
         RedirectStandardOutput = true,
         RedirectStandardError = true,
         UseShellExecute = false,
      };
      foreach (var argument in arguments)
      {
         startInfo.ArgumentList.Add(argument);
      }

      foreach (var name in startInfo.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToList())
      {
         startInfo.Environment.Remove(name);
      }

      startInfo.Environment["GIT_DIR"] = Path;
      startInfo.Environment["GIT_CONFIG_GLOBAL"] = _gitConfig;
      startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
      startInfo.Environment["GIT_AUTHOR_NAME"] = "World";
      startInfo.Environment["GIT_AUTHOR_EMAIL"] = "world@example.com";
      startInfo.Environment["GIT_COMMITTER_NAME"] = "World";
      startInfo.Environment["GIT_COMMITTER_EMAIL"] = "world@example.com";

      // A fixed date gives the same files the same commit in every run.
      startInfo.Environment["GIT_AUTHOR_DATE"] = "2026-01-01T12:00:00Z";
      startInfo.Environment["GIT_COMMITTER_DATE"] = "2026-01-01T12:00:00Z";
      if (index is not null)
      {
         startInfo.Environment["GIT_INDEX_FILE"] = index;
      }

      // A command such as mktree reads standard input, so every command gets its input, or none, and never the host's.
      using var process = Process.Start(startInfo)!;
      process.StandardInput.Write(input ?? "");
      process.StandardInput.Close();

      var error = process.StandardError.ReadToEndAsync();
      var output = process.StandardOutput.ReadToEnd();
      process.WaitForExit();
      return process.ExitCode == 0 ? output : throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error.Result}");
   }
}
