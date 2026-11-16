using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Lotsen.Credentials;
using Lotsen.Git;
using Lotsen.Platforms;

namespace Lotsen.GitHub;

/// <summary>The settings for calling the GitHub REST API.</summary>
internal sealed record GitHubOptions
{
   /// <summary>The URL that every request path is relative to, ending in a slash. Tests point it at a local server.</summary>
   public Uri ApiBaseUrl { get; init; } = new("https://api.github.com/");

   /// <summary>The longest one request may take.</summary>
   public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>The details of a pull request that resolving it needs.</summary>
/// <param name="RemoteUrl">The clone URL of the base repository, without user information.</param>
/// <param name="Head">The head commit of the pull request's source branch.</param>
/// <param name="Target">The commit of the target branch that the pull request's changes are shown from, as <see cref="GitHubClient.GetPullRequestAsync"/> chooses it.</param>
/// <param name="State">GitHub's state of the pull request, <c>open</c> or <c>closed</c>.</param>
/// <param name="Merged">Whether the pull request was merged.</param>
internal sealed record GitHubPullRequestDetails(Uri RemoteUrl, CommitId Head, CommitId Target, string State, bool Merged);

/// <summary>Reads GitHub pull requests and repositories through the REST API, as the user that the token provider signs in.</summary>
/// <remarks>
/// The client sends the token as a bearer header and puts it in no log line or exception message. Each resolve asks the provider for the token. A 401, 403 or 404 rejects the token at the
/// provider, because a new <c>gh auth login</c> may fix it, so the next request sends whatever token the GitHub CLI then gives.
/// </remarks>
internal sealed class GitHubClient : IDisposable
{
   // The REST API version that the decoded answer follows: https://docs.github.com/en/rest/about-the-rest-api/api-versions
   private const string ApiVersion = "2022-11-28";

   private const int MaxTagDepth = 8;

   private readonly IAccessTokenProvider _tokens;
   private readonly GitHubOptions _options;
   private readonly ILogger<GitHubClient> _logger;
   private readonly HttpClient _http;

   public GitHubClient(IAccessTokenProvider tokens, GitHubOptions options, ILogger<GitHubClient> logger)
   {
      _tokens = tokens;
      _options = options;
      _logger = logger;
      _http = new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false })
      {
         Timeout = options.Timeout,
      };
   }

   /// <summary>Returns the pull request's commits, base repository and state.</summary>
   /// <remarks>
   /// The target commit is the commit that the base branch points to, because GitHub's <c>base.sha</c> can lag behind the branch. A merged pull request takes <c>base.sha</c>, because after a
   /// merge commit the base branch contains the pull request's head, so their merge base is the head itself and shows no change. A base branch that no longer exists also leaves <c>base.sha</c>.
   /// </remarks>
   /// <exception cref="PlatformException">A request failed or one of GitHub's answers could not be read.</exception>
   /// <exception cref="AccessTokenUnavailableException">The token provider has no token. No request was sent then.</exception>
   public async Task<GitHubPullRequestDetails> GetPullRequestAsync(GitHubPullRequest pullRequest, CancellationToken cancellationToken)
   {
      var subject = Subject.Of(pullRequest);
      var token = await _tokens.GetTokenAsync(_options.Timeout, cancellationToken);
      var answer = await GetAsync(subject, $"repos/{PathOf(pullRequest.Owner)}/{PathOf(pullRequest.Repository)}/pulls/{pullRequest.Number}", token, GitHubJsonContext.Default.PullResponse,
         absentOnNotFound: false, cancellationToken);
      if (answer is null
         || !CommitId.TryParse(answer.Head.Sha, out var head)
         || !CommitId.TryParse(answer.Base.Sha, out var reported)
         || !Uri.TryCreate(answer.Base.Repo.CloneUrl, UriKind.Absolute, out var remoteUrl)
         || remoteUrl.UserInfo.Length != 0)
      {
         throw Unreadable(subject);
      }

      var target = answer.Merged ? reported : await BranchCommitAsync(subject, answer.Base.Repo.FullName, answer.Base.Ref, token, cancellationToken) ?? reported;
      return new GitHubPullRequestDetails(remoteUrl, head, target, answer.State, answer.Merged);
   }

   /// <summary>Returns the commit that a ref of the repository points to, with the repository's clone URL.</summary>
   /// <remarks>The ref resolves in the order that <see cref="IRepositoryPlatform.ResolveRepositoryAsync"/> promises. The requests after the first use the repository's name in GitHub's answer.</remarks>
   /// <param name="repository">The repository.</param>
   /// <param name="ref">A branch, a tag or a full commit ID, or null for the default branch.</param>
   /// <param name="cancellationToken">Cancels the requests.</param>
   /// <exception cref="PlatformException">The ref does not exist, a request failed, or one of GitHub's answers could not be read.</exception>
   /// <exception cref="AccessTokenUnavailableException">The token provider has no token. No request was sent then.</exception>
   public async Task<ResolvedRepository> GetRepositoryAsync(GitHubRepository repository, string? @ref, CancellationToken cancellationToken)
   {
      var subject = Subject.Of(repository);

      // A ref with a dot segment would make another request path, such as that of another ref.
      if (@ref is not null && !RefName.IsValid(@ref))
      {
         throw MissingRef(subject, @ref);
      }

      var token = await _tokens.GetTokenAsync(_options.Timeout, cancellationToken);
      var answer = await GetAsync(subject, $"repos/{PathOf(repository.Owner)}/{PathOf(repository.Name)}", token, GitHubJsonContext.Default.RepoResponse, absentOnNotFound: false,
         cancellationToken);
      if (answer is null || !Uri.TryCreate(answer.CloneUrl, UriKind.Absolute, out var remoteUrl) || remoteUrl.UserInfo.Length != 0)
      {
         throw Unreadable(subject);
      }

      if (@ref is null)
      {
         var head = await BranchCommitAsync(subject, answer.FullName, answer.DefaultBranch, token, cancellationToken)
            ?? throw Failure(subject, $"GitHub has no default branch '{answer.DefaultBranch}' in the repository, so the repository has no commit to show yet. Push a commit to the repository, or name a ref, then try again.");
         return new ResolvedRepository(remoteUrl, answer.DefaultBranch, RepositoryRefKind.Branch, head);
      }

      if (CommitId.TryParse(@ref, out var id))
      {
         // GitHub answers 404 for a commit that the repository lacks: https://docs.github.com/en/rest/git/commits?apiVersion=2022-11-28#get-a-commit-object
         var commit = await GetAsync(subject, $"repos/{PathOf(answer.FullName)}/git/commits/{id}", token, GitHubJsonContext.Default.ObjectResponse, absentOnNotFound: true,
            cancellationToken);
         return commit is null ? throw MissingRef(subject, @ref)
            : CommitId.TryParse(commit.Sha, out var found) && found == id ? new ResolvedRepository(remoteUrl, id.Value, RepositoryRefKind.Commit, id)
            : throw Unreadable(subject);
      }

      if (await TagCommitAsync(subject, answer.FullName, @ref, token, cancellationToken) is { } tagged)
      {
         return new ResolvedRepository(remoteUrl, @ref, RepositoryRefKind.Tag, tagged);
      }

      return await BranchCommitAsync(subject, answer.FullName, @ref, token, cancellationToken) is { } branchHead
         ? new ResolvedRepository(remoteUrl, @ref, RepositoryRefKind.Branch, branchHead)
         : throw MissingRef(subject, @ref);
   }

   public void Dispose() => _http.Dispose();

   /// <summary>Returns the commit that a branch of the repository with GitHub's full name points to, or null when the branch does not exist.</summary>
   private async Task<CommitId?> BranchCommitAsync(Subject subject, string fullName, string branch, AccessToken token, CancellationToken cancellationToken)
   {
      // GitHub answers 404 for a name that is not an existing ref: https://docs.github.com/en/rest/git/refs?apiVersion=2022-11-28#get-a-reference
      var reference = await GetAsync(subject, $"repos/{PathOf(fullName)}/git/ref/heads/{PathOf(branch)}", token, GitHubJsonContext.Default.ReferenceResponse, absentOnNotFound: true,
         cancellationToken);
      return reference is null ? null : CommitId.TryParse(reference.Object.Sha, out var commit) ? commit : throw Unreadable(subject);
   }

   /// <summary>Returns the commit that a tag of the repository with GitHub's full name finally names, through any annotated tags, or null when the tag does not exist.</summary>
   /// <exception cref="PlatformException">The tag names no commit, such as a tree, or a request failed.</exception>
   private async Task<CommitId?> TagCommitAsync(Subject subject, string fullName, string tag, AccessToken token, CancellationToken cancellationToken)
   {
      var reference = await GetAsync(subject, $"repos/{PathOf(fullName)}/git/ref/tags/{PathOf(tag)}", token, GitHubJsonContext.Default.ReferenceResponse, absentOnNotFound: true,
         cancellationToken);
      var target = reference?.Object;

      // An annotated tag names a tag object, which names the next object: https://docs.github.com/en/rest/git/tags?apiVersion=2022-11-28#get-a-tag
      // The bound stops a server whose tags name each other from taking requests forever.
      for (var peeled = 0; target is { Type: "tag" } && peeled < MaxTagDepth; peeled++)
      {
         var tagObject = await GetAsync(subject, $"repos/{PathOf(fullName)}/git/tags/{Uri.EscapeDataString(target.Sha)}", token, GitHubJsonContext.Default.ReferenceResponse,
            absentOnNotFound: false, cancellationToken);
         target = tagObject?.Object;
      }

      return target switch
      {
         null => null,
         { Type: "commit" } when CommitId.TryParse(target.Sha, out var commit) => commit,
         { Type: "tree" or "blob" } => throw Failure(subject, $"The tag '{tag}' names a {target.Type}, not a commit. Name a branch, a tag of a commit or a full commit ID, then try again."),
         _ => throw Unreadable(subject),
      };
   }

   /// <summary>Returns GitHub's answer to a GET of the path below the API's base URL, or null for a 404 when <paramref name="absentOnNotFound"/> is true.</summary>
   /// <remarks>A 401, 403 or 404 that the method throws for rejects the token at the provider. A 404 that it returns null for keeps the token, because the request before it succeeded with it.</remarks>
   /// <exception cref="PlatformException">GitHub refused the request, could not be reached, or sent an answer that cannot be read.</exception>
   private async Task<T?> GetAsync<T>(Subject subject, string path, AccessToken token, JsonTypeInfo<T> answerType, bool absentOnNotFound, CancellationToken cancellationToken)
      where T : class
   {
      var url = new Uri(_options.ApiBaseUrl, path);
      var response = await SendAsync(subject, url, token, cancellationToken);

      // GitHub asks clients to follow its redirects: https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api#follow-redirects
      // Only one, and only within the API's own origin, so the token reaches no other server.
      if (SameOriginRedirect(url, response) is { } target)
      {
         response.Dispose();
         response = await SendAsync(subject, target, token, cancellationToken);
      }

      using (response)
      {
         if (response.StatusCode == HttpStatusCode.NotFound && absentOnNotFound)
         {
            return null;
         }

         if (response.StatusCode != HttpStatusCode.OK)
         {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
               _tokens.Reject(token);
            }

            throw await RefusalAsync(subject, response, token, cancellationToken);
         }

         try
         {
            return await JsonSerializer.DeserializeAsync(await response.Content.ReadAsStreamAsync(cancellationToken), answerType, cancellationToken) ?? throw Unreadable(subject);
         }
         catch (JsonException)
         {
            throw Unreadable(subject);
         }
      }
   }

   /// <summary>Returns a name such as <c>owner/repository</c> or a branch name with each segment escaped, for a request path.</summary>
   private static string PathOf(string name) => string.Join('/', name.Split('/').Select(Uri.EscapeDataString));

   private async Task<HttpResponseMessage> SendAsync(Subject subject, Uri url, AccessToken token, CancellationToken cancellationToken)
   {
      using var request = new HttpRequestMessage(HttpMethod.Get, url);
      request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
      request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
      request.Headers.Add("X-GitHub-Api-Version", ApiVersion);

      // GitHub refuses a request without a User-Agent: https://docs.github.com/en/rest/using-the-rest-api/getting-started-with-the-rest-api
      request.Headers.UserAgent.ParseAdd("lotsen");

      var started = Stopwatch.GetTimestamp();
      HttpResponseMessage response;
      try
      {
         response = await _http.SendAsync(request, cancellationToken);
      }
      catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
      {
         throw Failure(subject, string.Create(CultureInfo.InvariantCulture, $"GitHub did not answer within {_options.Timeout.TotalSeconds:0.#} seconds."));
      }
      catch (HttpRequestException exception)
      {
         throw Failure(subject, $"lotsen could not reach GitHub at {url.Authority}: {exception.Message}");
      }

      _logger.LogDebug("GET {Url} answered {Status} in {Milliseconds} ms.", url, (int)response.StatusCode, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
      return response;
   }

   /// <summary>Returns the target of a redirect answer when it has the API's scheme, host and port, or null for any other answer or target.</summary>
   private Uri? SameOriginRedirect(Uri url, HttpResponseMessage response) =>
      response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect
      && response.Headers.Location is { } location
      && new Uri(url, location) is var target
      && Uri.Compare(target, _options.ApiBaseUrl, UriComponents.SchemeAndServer, UriFormat.UriEscaped, StringComparison.OrdinalIgnoreCase) == 0
         ? target
         : null;

   private static async Task<PlatformException> RefusalAsync(Subject subject, HttpResponseMessage response, AccessToken token, CancellationToken cancellationToken)
   {
      var status = (int)response.StatusCode;
      var message = await MessageAsync(response, cancellationToken) is { } text ? text.Replace(token.Value, "[redacted]", StringComparison.Ordinal) : null;
      return response.StatusCode switch
      {
         HttpStatusCode.Unauthorized => Failure(subject, $"GitHub answered {status}: the token of your gh sign-in was refused. Run 'gh auth login', then try again."),

         // GitHub answers 404 for a private repository that the token cannot see: https://docs.github.com/en/rest/using-the-rest-api/troubleshooting-the-rest-api
         HttpStatusCode.NotFound => Failure(
            subject,
            $"GitHub answered {status}: the {subject.Kind} was not found, or your gh sign-in has no access to it. Check the URL, or run 'gh auth login' with an account that can see the repository, then try again."),
         HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests => Failure(
            subject,
            $"GitHub answered {status}: {Sentence(message ?? "access refused")} Check that your gh sign-in has access to the repository, or wait until a rate limit that GitHub names resets, then try again."),
         HttpStatusCode.MovedPermanently => Failure(
            subject,
            $"GitHub answered {status}: {subject.Repository} has moved to an address that lotsen does not follow. Start a new tour from the {subject.Kind}'s current URL."),
         _ => Failure(subject, $"GitHub answered {status}" + (message is null ? "." : ": " + Sentence(message))),
      };
   }

   /// <summary>Returns the message of a GitHub error answer, or null when the answer holds none.</summary>
   private static async Task<string?> MessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
   {
      try
      {
         var error = await JsonSerializer.DeserializeAsync(await response.Content.ReadAsStreamAsync(cancellationToken), GitHubJsonContext.Default.ErrorResponse, cancellationToken);
         return string.IsNullOrWhiteSpace(error?.Message) ? null : error.Message.Trim();
      }
      catch (JsonException)
      {
         return null;
      }
   }

   private static string Sentence(string text) => text.EndsWith('.') ? text : text + ".";

   private static PlatformException Unreadable(Subject subject) => Failure(subject, "GitHub sent an answer that lotsen cannot read.");

   private static PlatformException MissingRef(Subject subject, string @ref) =>
      Failure(subject, $"GitHub has no branch, tag or commit '{@ref}' in the repository. Check the ref, and give a commit by its full ID, then try again.");

   private static PlatformException Failure(Subject subject, string reason) => new($"lotsen could not read the {subject.Kind} {subject.Url.AbsoluteUri}. {reason}");

   /// <summary>What a request reads, which its failures name.</summary>
   /// <param name="Kind">The kind of what it reads: <c>pull request</c> or <c>repository</c>.</param>
   /// <param name="Url">The normalized URL of what it reads.</param>
   /// <param name="Repository">The repository that a moved answer names: <c>the repository</c>, or <c>the pull request's repository</c>.</param>
   private sealed record Subject(string Kind, Uri Url, string Repository)
   {
      public static Subject Of(GitHubPullRequest pullRequest) => new("pull request", pullRequest.Url, "the pull request's repository");

      public static Subject Of(GitHubRepository repository) => new("repository", repository.Url, "the repository");
   }
}

// The answers of the REST API, with only the properties that the client reads. The JSON reader skips every other property.
internal sealed record PullResponse(string State, bool Merged, HeadResponse Head, BaseResponse Base);

internal sealed record HeadResponse(string Sha);

internal sealed record BaseResponse(string Ref, string Sha, RepositoryResponse Repo);

internal sealed record RepositoryResponse(string FullName, string CloneUrl);

internal sealed record RepoResponse(string FullName, string DefaultBranch, string CloneUrl);

internal sealed record ReferenceResponse(ObjectResponse Object);

// A commit's own answer has no type: https://docs.github.com/en/rest/git/commits?apiVersion=2022-11-28#get-a-commit-object
internal sealed record ObjectResponse(string Sha, string? Type = null);

internal sealed record ErrorResponse(string? Message = null);

/// <summary>The source-generated JSON metadata for the answers of the GitHub REST API, whose properties are in snake case.</summary>
/// <remarks>A missing property without a default value fails deserialization, and so does a null in a property that the records do not mark as nullable.</remarks>
[JsonSourceGenerationOptions(
   JsonSerializerDefaults.Web,
   PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
   RespectNullableAnnotations = true,
   RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(PullResponse))]
[JsonSerializable(typeof(RepoResponse))]
[JsonSerializable(typeof(ReferenceResponse))]
[JsonSerializable(typeof(ObjectResponse))]
[JsonSerializable(typeof(ErrorResponse))]
internal sealed partial class GitHubJsonContext : JsonSerializerContext;
