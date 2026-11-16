using System.Globalization;
using Lotsen.Credentials;
using Lotsen.Git;
using Lotsen.Platforms;

namespace Lotsen.Tests;

/// <summary>
/// A platform whose pull requests are <c>https://{host}/{repository}/pull/{id}</c> and whose repositories are <c>https://{host}/{repository}</c>, and which resolves them to the remotes and
/// commits that the test chooses.
/// </summary>
/// <remarks>A resolve that starts before the test chooses an answer or a failure waits for the server to stop, so its pin stays in progress.</remarks>
internal sealed class TestPlatform : IRepositoryPlatform
{
   public const string DefaultId = "test";

   public const string DefaultHost = "platform.test";

   /// <summary>A pull request URL of the default platform, in its normalized form.</summary>
   public const string PrUrl = $"https://{DefaultHost}/shop/pull/42";

   /// <summary>The label of <see cref="PrUrl"/>.</summary>
   public const string PrLabel = "shop #42";

   private readonly Lock _lock = new();
   private readonly string _host;
   private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
   private readonly TaskCompletionSource _firstResolve = new(TaskCreationOptions.RunContinuationsAsynchronously);
   private CommitId? _pushedHead;
   private int _pushedIteration;
   private Func<CancellationToken, Task<ResolvedPullRequest>>? _answer;
   private Func<string?, CancellationToken, Task<ResolvedRepository>>? _repositoryAnswer;
   private int _resolves;

   /// <param name="hold">Whether every resolve waits for <see cref="Release"/>.</param>
   public TestPlatform(string id = DefaultId, string host = DefaultHost, bool hold = false)
   {
      Id = id;
      _host = host;
      if (!hold)
      {
         _released.SetResult();
      }
   }

   public string Id { get; }

   public string ExpectedForms => $"https://{_host}/{{repository}}/pull/{{id}}";

   public string ExpectedRepositoryForms => $"https://{_host}/{{repository}}, optionally followed by /tree/{{ref}}";

   public IGitCredential GitCredential { get; init; } = new TestGitCredential("lotsen-test-token");

   /// <summary>Completes when the first resolve has started.</summary>
   public Task FirstResolve => _firstResolve.Task;

   /// <summary>The number of resolves started so far.</summary>
   public int Resolves => Volatile.Read(ref _resolves);

   /// <summary>Makes every pull request resolve to this remote, with the latest iteration's commits and number, and the pull request's state.</summary>
   /// <remarks>The remote URL is normalized as a platform's REST API would return it, so a file path becomes a <c>file://</c> URL.</remarks>
   public void PullRequest(Uri remoteUrl, CommitId source, CommitId target, int iteration = 1, PullRequestState state = PullRequestState.Active) =>
      Answer(_ => Task.FromResult(new ResolvedPullRequest(new Uri(remoteUrl.AbsoluteUri), source, target, iteration, state)));

   /// <summary>Adds an iteration with this source head, which every resolve then returns with the pull request's state.</summary>
   /// <remarks>
   /// The iteration's number counts the changes of the source head so far, as <see cref="ResolvedPullRequest.Iteration"/> asks of a platform without native numbers. A push back to an earlier
   /// head raises it too, and a push of the current head keeps it.
   /// </remarks>
   public void Push(Uri remoteUrl, CommitId source, CommitId target, PullRequestState state = PullRequestState.Active)
   {
      int iteration;
      lock (_lock)
      {
         if (_pushedHead != source)
         {
            _pushedHead = source;
            _pushedIteration++;
         }

         iteration = _pushedIteration;
      }

      PullRequest(remoteUrl, source, target, iteration, state);
   }

   /// <summary>Makes every resolve fail with this reason.</summary>
   public void Fail(string reason) => Answer(_ => Task.FromException<ResolvedPullRequest>(new PlatformException(reason)));

   /// <summary>Makes every resolve return what the function returns.</summary>
   public void Answer(Func<CancellationToken, Task<ResolvedPullRequest>> answer)
   {
      lock (_lock)
      {
         _answer = answer;
      }
   }

   /// <summary>Makes every repository resolve against these refs, to this remote.</summary>
   /// <remarks>A resolve follows the order of <see cref="IRepositoryPlatform.ResolveRepositoryAsync"/>. A commit that a branch or a tag points to is a commit of the repository too.</remarks>
   /// <param name="remoteUrl">The remote, normalized as for <see cref="PullRequest"/>.</param>
   /// <param name="head">The commit that the default branch points to.</param>
   /// <param name="defaultBranch">The name of the default branch.</param>
   /// <param name="branches">The other branches by name, with the commit each points to, or null for none.</param>
   /// <param name="tags">The tags by name, with the commit each names, or null for none.</param>
   /// <param name="commits">The commits that no branch or tag points to, or null for none.</param>
   public void Repository(
      Uri remoteUrl, CommitId head, string defaultBranch = "main", IReadOnlyDictionary<string, CommitId>? branches = null, IReadOnlyDictionary<string, CommitId>? tags = null,
      IReadOnlyList<CommitId>? commits = null)
   {
      var remote = new Uri(remoteUrl.AbsoluteUri);
      var allBranches = new Dictionary<string, CommitId>(branches ?? new Dictionary<string, CommitId>(), StringComparer.Ordinal) { [defaultBranch] = head };
      var allTags = new Dictionary<string, CommitId>(tags ?? new Dictionary<string, CommitId>(), StringComparer.Ordinal);
      var allCommits = (commits ?? []).Concat(allBranches.Values).Concat(allTags.Values).ToHashSet();
      AnswerRepository((@ref, _) =>
      {
         ResolvedRepository? resolved = @ref switch
         {
            null => new(remote, defaultBranch, RepositoryRefKind.Branch, head),
            _ when !RefName.IsValid(@ref) => null,
            _ when CommitId.TryParse(@ref, out var id) => allCommits.Contains(id) ? new(remote, id.Value, RepositoryRefKind.Commit, id) : null,
            _ when allTags.TryGetValue(@ref, out var tagged) => new(remote, @ref, RepositoryRefKind.Tag, tagged),
            _ when allBranches.TryGetValue(@ref, out var branchHead) => new(remote, @ref, RepositoryRefKind.Branch, branchHead),
            _ => null,
         };
         return resolved is null
            ? Task.FromException<ResolvedRepository>(new PlatformException($"The test platform has no branch, tag or commit '{@ref}' in the repository."))
            : Task.FromResult(resolved);
      });
   }

   /// <summary>Makes every repository resolve fail with this exception.</summary>
   public void FailRepository(ExternalFailureException exception) => AnswerRepository((_, _) => Task.FromException<ResolvedRepository>(exception));

   /// <summary>Makes every repository resolve return what the function returns for the resolve's ref.</summary>
   public void AnswerRepository(Func<string?, CancellationToken, Task<ResolvedRepository>> answer)
   {
      lock (_lock)
      {
         _repositoryAnswer = answer;
      }
   }

   /// <summary>Lets held and later resolves continue.</summary>
   public void Release() => _released.TrySetResult();

   public Uri? Recognize(string text)
   {
      if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host != _host)
      {
         return null;
      }

      var segments = uri.AbsolutePath.Trim('/').Split('/');
      return segments is [var repository, "pull", var id] && repository.Length > 0 && int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
         ? new Uri($"https://{_host}/{repository}/pull/{number}")
         : null;
   }

   // Recognize keeps the repository's letter case, and the repository compares in any letter case.
   public bool IsSamePullRequest(Uri url, Uri other) =>
      Recognize(url.AbsoluteUri) is { } recognized
      && Recognize(other.AbsoluteUri) is { } otherRecognized
      && string.Equals(recognized.AbsoluteUri, otherRecognized.AbsoluteUri, StringComparison.OrdinalIgnoreCase);

   public string? Label(Uri url) => Recognize(url.AbsoluteUri) is { } recognized ? $"{recognized.Segments[1].TrimEnd('/')} #{recognized.Segments[^1]}" : null;

   /// <summary>Returns <c>https://{host}/{repository}/workitems/{id}</c> for a work item, and the pull request URL with the mentioned ID for a pull request.</summary>
   public Uri? MentionUrl(Uri url, MentionKind kind, int id) => Recognize(url.AbsoluteUri) is { } recognized
      ? new Uri(recognized, kind == MentionKind.WorkItem ? $"../workitems/{id}" : id.ToString(CultureInfo.InvariantCulture))
      : null;

   public RecognizedRepository? RecognizeRepository(string text)
   {
      if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host != _host)
      {
         return null;
      }

      var segments = uri.AbsolutePath.Trim('/').Split('/');
      return segments switch
      {
         [var repository] when NameOf(repository) is { } name => new RecognizedRepository(RepositoryUrlOf(name), null),
         [var repository, "tree", .. var @ref] when NameOf(repository) is { } name && string.Join('/', @ref.Select(Uri.UnescapeDataString)) is var decoded && RefName.IsValid(decoded) =>
            new RecognizedRepository(RepositoryUrlOf(name), decoded),
         _ => null,
      };

      static string? NameOf(string segment) => segment.EndsWith(".git", StringComparison.Ordinal) ? segment[..^4] is { Length: > 0 } name ? name : null : segment.Length > 0 ? segment : null;
   }

   public string? RepositoryLabel(Uri url) => RecognizeRepository(url.AbsoluteUri) is { Ref: null } recognized ? recognized.Url.Segments[1] : null;

   /// <summary>Returns <c>https://{host}/{repository}/workitems/{id}</c> for a work item, and <c>https://{host}/{repository}/pull/{id}</c> for a pull request.</summary>
   public Uri? RepositoryMentionUrl(Uri url, MentionKind kind, int id) => RecognizeRepository(url.AbsoluteUri) is { Ref: null } recognized
      ? new Uri($"{recognized.Url.AbsoluteUri}/{(kind == MentionKind.WorkItem ? "workitems" : "pull")}/{id}")
      : null;

   public Task<ResolvedRepository> ResolveRepositoryAsync(Uri url, string? @ref, CancellationToken cancellationToken) =>
      AnswerAsync<ResolvedRepository>(() => _repositoryAnswer is { } answer ? token => answer(@ref, token) : null, cancellationToken);

   public Task<ResolvedPullRequest> ResolveAsync(Uri url, CancellationToken cancellationToken) => AnswerAsync(() => _answer, cancellationToken);

   private Uri RepositoryUrlOf(string repository) => new($"https://{_host}/{repository}");

   /// <summary>Counts a resolve, waits for <see cref="Release"/>, and returns what the answer that <paramref name="answerOf"/> reads under the lock returns.</summary>
   private async Task<T> AnswerAsync<T>(Func<Func<CancellationToken, Task<T>>?> answerOf, CancellationToken cancellationToken)
   {
      Interlocked.Increment(ref _resolves);
      _firstResolve.TrySetResult();
      await _released.Task.WaitAsync(cancellationToken);
      Func<CancellationToken, Task<T>>? answer;
      lock (_lock)
      {
         answer = answerOf();
      }

      if (answer is null)
      {
         await Task.Delay(Timeout.Infinite, cancellationToken);
      }

      return await answer!(cancellationToken);
   }
}
