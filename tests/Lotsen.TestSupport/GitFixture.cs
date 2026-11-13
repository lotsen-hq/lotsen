using System.Diagnostics;
using System.Text;
using Lotsen.Credentials;
using Lotsen.Git;
using Lotsen.Hosting;
using Lotsen.Pinning;
using Lotsen.Tours;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lotsen.Tests;

/// <summary>A repository built on the fly in a temporary directory and served over <c>file://</c> with filters allowed, so that a blob-less clone of it is a real partial clone.</summary>
/// <remarks>The fixture's own git commands ignore the user's and the system's git configuration.</remarks>
internal sealed class GitFixture : IDisposable
{
   private readonly string _root;

   public GitFixture()
   {
      _root = Directory.CreateTempSubdirectory("lotsen-git-").FullName;
      OriginPath = Path.Combine(_root, "origin");
      DataDirectory = new DataDirectory(Path.Combine(_root, "data"));
      Directory.CreateDirectory(OriginPath);
      Git(OriginPath, "init", "--quiet", "--initial-branch=main");
      Git(OriginPath, "config", "uploadpack.allowFilter", "true");
   }

   /// <summary>The working tree of the served repository.</summary>
   public string OriginPath { get; }

   public Uri Url => new(OriginPath);

   public DataDirectory DataDirectory { get; }

   /// <summary>A directory for test files outside the data directory.</summary>
   public string ScratchPath => Directory.CreateDirectory(Path.Combine(_root, "scratch")).FullName;

   public TourRemote Remote(IGitCredential? credential = null) => TestRemote.Of(Url, credential);

   /// <summary>Returns a clone cache on the fixture's data directory, with the real clone directories unless the test gives its own.</summary>
   public CloneCache NewCache(GitOptions? options = null, ICloneDirectories? directories = null, TimeProvider? time = null, ILogger<CloneCache>? logger = null) =>
      new(DataDirectory, directories ?? new CloneDirectories(DataDirectory), new GitRunner(options ?? new GitOptions()), time ?? TimeProvider.System, logger ?? NullLogger<CloneCache>.Instance);

   /// <summary>Clones the URL into a new clone cache with the credential.</summary>
   /// <param name="transferTimeout">The longest one git run of the clone may take, which is also the validity that git asks of the credential.</param>
   public async Task CloneAsync(Uri url, IGitCredential credential, TimeSpan transferTimeout, CancellationToken cancellationToken) =>
      await NewCache(new GitOptions { TransferTimeout = transferTimeout }).OpenAsync(TestRemote.Of(url, credential), [], FetchPolicy.Sync, cancellationToken);

   /// <summary>Writes the files (a null content deletes the file), commits them on the current branch and returns the commit.</summary>
   public CommitId Commit(string message, params (string Path, string? Content)[] files)
   {
      foreach (var (path, content) in files)
      {
         var fullPath = Path.Combine(OriginPath, path);
         if (content is null)
         {
            File.Delete(fullPath);

            // An emptied directory would block a file of the same name in the same commit.
            for (var directory = Path.GetDirectoryName(fullPath)!;
               directory != OriginPath && !Directory.EnumerateFileSystemEntries(directory).Any();
               directory = Path.GetDirectoryName(directory)!)
            {
               Directory.Delete(directory);
            }

            continue;
         }

         Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
         File.WriteAllText(fullPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
      }

      Git(OriginPath, "add", "--all");
      Git(OriginPath, "commit", "--quiet", "--allow-empty", "--message", message);
      return CommitId.Parse(Git(OriginPath, "rev-parse", "HEAD").Trim());
   }

   public void Checkout(string branch, bool create = false)
   {
      if (create)
      {
         Git(OriginPath, "checkout", "--quiet", "-b", branch);
      }
      else
      {
         Git(OriginPath, "checkout", "--quiet", branch);
      }
   }

   /// <summary>Runs git in this directory with the fixture's isolated configuration and returns its output.</summary>
   public string Git(string directory, params string[] arguments)
   {
      var startInfo = new ProcessStartInfo("git")
      {
         WorkingDirectory = directory,
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

      startInfo.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(_root, "no-global-config");
      startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
      startInfo.Environment["GIT_AUTHOR_NAME"] = "Fixture";
      startInfo.Environment["GIT_AUTHOR_EMAIL"] = "fixture@example.com";
      startInfo.Environment["GIT_COMMITTER_NAME"] = "Fixture";
      startInfo.Environment["GIT_COMMITTER_EMAIL"] = "fixture@example.com";

      using var process = Process.Start(startInfo)!;
      var error = process.StandardError.ReadToEndAsync();
      var output = process.StandardOutput.ReadToEnd();
      process.WaitForExit();
      if (process.ExitCode != 0)
      {
         throw new InvalidOperationException($"Fixture git {string.Join(' ', arguments)} failed: {error.Result}");
      }

      return output;
   }

   /// <summary>Returns the directories of the clones in the data directory.</summary>
   public string[] Clones()
   {
      var clones = Path.Combine(DataDirectory.Path, "clones");
      return Directory.Exists(clones) ? Directory.GetDirectories(clones) : [];
   }

   /// <summary>Returns every file under the data directory.</summary>
   public IEnumerable<string> DataFiles() => Directory.Exists(DataDirectory.Path) ? Directory.EnumerateFiles(DataDirectory.Path, "*", SearchOption.AllDirectories) : [];

   public void Dispose() => TestDirectories.Delete(_root, TimeSpan.FromSeconds(5));
}

/// <summary>Builds the tour remotes of URLs that no tour stores, for the tests that open clones of them.</summary>
internal static class TestRemote
{
   private static readonly CommitId Unused = CommitId.Parse(new string('0', 40));

   public static TourRemote Of(Uri url, IGitCredential? credential = null) => TourRemote.OfPin(new TourPin.Pinned(Unused, Unused, 1, url), credential);
}

/// <summary>Returns one fixed token, counts the requests and records the rejections and the validity that the last request asked.</summary>
internal sealed class FixedAccessTokenProvider : IAccessTokenProvider
{
   private readonly List<string> _rejected = [];
   private readonly string _token;
   private int _requests;
   private long _lastValidityTicks;

   public FixedAccessTokenProvider(string token)
   {
      _token = token;
   }

   public int Requests => _requests;

   /// <summary>The validity that the last request asked.</summary>
   public TimeSpan LastValidity => TimeSpan.FromTicks(Interlocked.Read(ref _lastValidityTicks));

   /// <summary>The value of each token rejected so far.</summary>
   public IReadOnlyList<string> Rejected
   {
      get
      {
         lock (_rejected)
         {
            return [.. _rejected];
         }
      }
   }

   public void Reject(AccessToken rejected)
   {
      lock (_rejected)
      {
         _rejected.Add(rejected.Value);
      }
   }

   public ValueTask<AccessToken> GetTokenAsync(TimeSpan minimumValidity, CancellationToken cancellationToken)
   {
      Interlocked.Exchange(ref _lastValidityTicks, minimumValidity.Ticks);
      Interlocked.Increment(ref _requests);
      return ValueTask.FromResult(new AccessToken(_token));
   }
}

/// <summary>A git credential that answers with one scheme and a token that the test can change, and counts its answers and refusals.</summary>
internal sealed class TestGitCredential : IGitCredential
{
   public const string Scheme = "Bearer";

   public const string Hint = "Sign in to the test platform again, then try again.";

   private readonly List<TimeSpan> _validities = [];
   private volatile string _token;
   private int _refusals;

   public TestGitCredential(string token)
   {
      _token = token;
   }

   /// <summary>The <c>Authorization</c> header value that the current token gives.</summary>
   public string Header => $"{Scheme} {_token}";

   /// <summary>The validity that each answer so far was asked for, in order.</summary>
   public IReadOnlyList<TimeSpan> Validities
   {
      get
      {
         lock (_validities)
         {
            return [.. _validities];
         }
      }
   }

   public int Answers => Validities.Count;

   public int Refusals => Volatile.Read(ref _refusals);

   /// <summary>The reason that every answer throws as an <see cref="AccessTokenUnavailableException"/>, or null to answer.</summary>
   public string? Unavailable { get; set; }

   public string FixHint => Hint;

   /// <summary>Makes later answers carry this token.</summary>
   public void Use(string token) => _token = token;

   public ValueTask<GitCredentialAnswer> AnswerAsync(TimeSpan minimumValidity, CancellationToken cancellationToken)
   {
      lock (_validities)
      {
         _validities.Add(minimumValidity);
      }

      return Unavailable is { } reason
         ? ValueTask.FromException<GitCredentialAnswer>(new AccessTokenUnavailableException(reason))
         : ValueTask.FromResult(new GitCredentialAnswer(Scheme, _token, () => Interlocked.Increment(ref _refusals)));
   }
}
