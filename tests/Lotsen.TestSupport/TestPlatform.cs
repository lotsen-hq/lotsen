using System.Globalization;
using Lotsen.Credentials;
using Lotsen.Git;
using Lotsen.Platforms;

namespace Lotsen.Tests;

/// <summary>A platform whose pull requests are <c>https://{host}/{repository}/pull/{id}</c> and resolve to the remote and commits that the test chooses.</summary>
/// <remarks>A resolve that starts before the test chooses a pull request or a failure waits for the server to stop, so its pin stays in progress.</remarks>
internal sealed class TestPlatform : IPullRequestPlatform
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

   public async Task<ResolvedPullRequest> ResolveAsync(Uri url, CancellationToken cancellationToken)
   {
      Interlocked.Increment(ref _resolves);
      _firstResolve.TrySetResult();
      await _released.Task.WaitAsync(cancellationToken);
      Func<CancellationToken, Task<ResolvedPullRequest>>? answer;
      lock (_lock)
      {
         answer = _answer;
      }

      if (answer is null)
      {
         await Task.Delay(Timeout.Infinite, cancellationToken);
      }

      return await answer!(cancellationToken);
   }
}
