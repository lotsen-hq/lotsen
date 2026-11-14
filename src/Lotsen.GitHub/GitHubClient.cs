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

/// <summary>Reads GitHub pull requests through the REST API, as the user that the token provider signs in.</summary>
/// <remarks>
/// The client sends the token as a bearer header and puts it in no log line or exception message. Each resolve asks the provider for the token. A 401, 403 or 404 rejects the token at the
/// provider, because a new <c>gh auth login</c> may fix it, so the next request sends whatever token the GitHub CLI then gives.
/// </remarks>
internal sealed class GitHubClient : IDisposable
{
   // The REST API version that the decoded answer follows: https://docs.github.com/en/rest/about-the-rest-api/api-versions
   private const string ApiVersion = "2022-11-28";

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
      var token = await _tokens.GetTokenAsync(_options.Timeout, cancellationToken);
      var answer = await GetAsync(pullRequest, $"repos/{PathOf(pullRequest.Owner)}/{PathOf(pullRequest.Repository)}/pulls/{pullRequest.Number}", token, GitHubJsonContext.Default.PullResponse,
         absentOnNotFound: false, cancellationToken);
      if (answer is null
         || !CommitId.TryParse(answer.Head.Sha, out var head)
         || !CommitId.TryParse(answer.Base.Sha, out var reported)
         || !Uri.TryCreate(answer.Base.Repo.CloneUrl, UriKind.Absolute, out var remoteUrl)
         || remoteUrl.UserInfo.Length != 0)
      {
         throw Unreadable(pullRequest);
      }

      var target = answer.Merged ? reported : await BaseBranchCommitAsync(pullRequest, answer.Base, token, cancellationToken) ?? reported;
      return new GitHubPullRequestDetails(remoteUrl, head, target, answer.State, answer.Merged);
   }

   public void Dispose() => _http.Dispose();

   /// <summary>Returns the commit that the pull request's base branch points to, or null when the branch does not exist.</summary>
   private async Task<CommitId?> BaseBranchCommitAsync(GitHubPullRequest pullRequest, BaseResponse @base, AccessToken token, CancellationToken cancellationToken)
   {
      // GitHub answers 404 for a name that is not an existing ref: https://docs.github.com/en/rest/git/refs?apiVersion=2022-11-28#get-a-reference
      var reference = await GetAsync(pullRequest, $"repos/{PathOf(@base.Repo.FullName)}/git/ref/heads/{PathOf(@base.Ref)}", token, GitHubJsonContext.Default.ReferenceResponse,
         absentOnNotFound: true, cancellationToken);
      return reference is null ? null : CommitId.TryParse(reference.Object.Sha, out var commit) ? commit : throw Unreadable(pullRequest);
   }

   /// <summary>Returns GitHub's answer to a GET of the path below the API's base URL, or null for a 404 when <paramref name="absentOnNotFound"/> is true.</summary>
   /// <remarks>A 401, 403 or 404 that the method throws for rejects the token at the provider. A 404 that it returns null for keeps the token, because the request before it succeeded with it.</remarks>
   /// <exception cref="PlatformException">GitHub refused the request, could not be reached, or sent an answer that cannot be read.</exception>
   private async Task<T?> GetAsync<T>(GitHubPullRequest pullRequest, string path, AccessToken token, JsonTypeInfo<T> answerType, bool absentOnNotFound, CancellationToken cancellationToken)
      where T : class
   {
      var url = new Uri(_options.ApiBaseUrl, path);
      var response = await SendAsync(pullRequest, url, token, cancellationToken);

      // GitHub asks clients to follow its redirects: https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api#follow-redirects
      // Only one, and only within the API's own origin, so the token reaches no other server.
      if (SameOriginRedirect(url, response) is { } target)
      {
         response.Dispose();
         response = await SendAsync(pullRequest, target, token, cancellationToken);
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

            throw await RefusalAsync(pullRequest, response, token, cancellationToken);
         }

         try
         {
            return await JsonSerializer.DeserializeAsync(await response.Content.ReadAsStreamAsync(cancellationToken), answerType, cancellationToken) ?? throw Unreadable(pullRequest);
         }
         catch (JsonException)
         {
            throw Unreadable(pullRequest);
         }
      }
   }

   /// <summary>Returns a name such as <c>owner/repository</c> or a branch name with each segment escaped, for a request path.</summary>
   private static string PathOf(string name) => string.Join('/', name.Split('/').Select(Uri.EscapeDataString));

   private async Task<HttpResponseMessage> SendAsync(GitHubPullRequest pullRequest, Uri url, AccessToken token, CancellationToken cancellationToken)
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
         throw Failure(pullRequest, string.Create(CultureInfo.InvariantCulture, $"GitHub did not answer within {_options.Timeout.TotalSeconds:0.#} seconds."));
      }
      catch (HttpRequestException exception)
      {
         throw Failure(pullRequest, $"lotsen could not reach GitHub at {url.Authority}: {exception.Message}");
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

   private static async Task<PlatformException> RefusalAsync(GitHubPullRequest pullRequest, HttpResponseMessage response, AccessToken token, CancellationToken cancellationToken)
   {
      var status = (int)response.StatusCode;
      var message = await MessageAsync(response, cancellationToken) is { } text ? text.Replace(token.Value, "[redacted]", StringComparison.Ordinal) : null;
      return response.StatusCode switch
      {
         HttpStatusCode.Unauthorized => Failure(pullRequest, $"GitHub answered {status}: the token of your gh sign-in was refused. Run 'gh auth login', then try again."),

         // GitHub answers 404 for a private repository that the token cannot see: https://docs.github.com/en/rest/using-the-rest-api/troubleshooting-the-rest-api
         HttpStatusCode.NotFound => Failure(
            pullRequest,
            $"GitHub answered {status}: the pull request was not found, or your gh sign-in has no access to it. Check the URL, or run 'gh auth login' with an account that can see the repository, then try again."),
         HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests => Failure(
            pullRequest,
            $"GitHub answered {status}: {Sentence(message ?? "access refused")} Check that your gh sign-in has access to the repository, or wait until a rate limit that GitHub names resets, then try again."),
         HttpStatusCode.MovedPermanently => Failure(
            pullRequest,
            $"GitHub answered {status}: the pull request's repository has moved to an address that lotsen does not follow. Start a new tour from the pull request's current URL."),
         _ => Failure(pullRequest, $"GitHub answered {status}" + (message is null ? "." : ": " + Sentence(message))),
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

   private static PlatformException Unreadable(GitHubPullRequest pullRequest) => Failure(pullRequest, "GitHub sent an answer that lotsen cannot read.");

   private static PlatformException Failure(GitHubPullRequest pullRequest, string reason) => new($"lotsen could not read the pull request {pullRequest.Url.AbsoluteUri}. {reason}");
}

// The answers of the REST API, with only the properties that the client reads. The JSON reader skips every other property.
internal sealed record PullResponse(string State, bool Merged, HeadResponse Head, BaseResponse Base);

internal sealed record HeadResponse(string Sha);

internal sealed record BaseResponse(string Ref, string Sha, RepositoryResponse Repo);

internal sealed record RepositoryResponse(string FullName, string CloneUrl);

internal sealed record ReferenceResponse(ObjectResponse Object);

internal sealed record ObjectResponse(string Sha);

internal sealed record ErrorResponse(string? Message = null);

/// <summary>The source-generated JSON metadata for the answers of the GitHub REST API, whose properties are in snake case.</summary>
/// <remarks>A missing property without a default value fails deserialization, and so does a null in a property that the records do not mark as nullable.</remarks>
[JsonSourceGenerationOptions(
   JsonSerializerDefaults.Web,
   PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
   RespectNullableAnnotations = true,
   RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(PullResponse))]
[JsonSerializable(typeof(ReferenceResponse))]
[JsonSerializable(typeof(ErrorResponse))]
internal sealed partial class GitHubJsonContext : JsonSerializerContext;
