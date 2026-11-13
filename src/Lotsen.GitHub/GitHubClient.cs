using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
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
/// <param name="Base">The commit of the target branch that GitHub reports with the pull request.</param>
/// <param name="State">GitHub's state of the pull request, <c>open</c> or <c>closed</c>.</param>
/// <param name="Merged">Whether the pull request was merged.</param>
internal sealed record GitHubPullRequestDetails(Uri RemoteUrl, CommitId Head, CommitId Base, string State, bool Merged);

/// <summary>Reads GitHub pull requests through the REST API, as the user that the token provider signs in.</summary>
/// <remarks>
/// The client sends the token as a bearer header and puts it in no log line or exception message. Each request asks the provider for the token. A 401, 403 or 404 rejects the token at the
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
   /// <exception cref="PlatformException">The request failed or GitHub's answer could not be read.</exception>
   /// <exception cref="AccessTokenUnavailableException">The token provider has no token. No request was sent then.</exception>
   public async Task<GitHubPullRequestDetails> GetPullRequestAsync(GitHubPullRequest pullRequest, CancellationToken cancellationToken)
   {
      var token = await _tokens.GetTokenAsync(_options.Timeout, cancellationToken);
      var url = new Uri(_options.ApiBaseUrl, $"repos/{Uri.EscapeDataString(pullRequest.Owner)}/{Uri.EscapeDataString(pullRequest.Repository)}/pulls/{pullRequest.Number}");
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

      using (response)
      {
         _logger.LogDebug("GET {Url} answered {Status} in {Milliseconds} ms.", url, (int)response.StatusCode, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
         if (response.StatusCode != HttpStatusCode.OK)
         {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
               _tokens.Reject(token);
            }

            throw await RefusalAsync(pullRequest, response, token, cancellationToken);
         }

         PullResponse? answer;
         try
         {
            answer = await JsonSerializer.DeserializeAsync(await response.Content.ReadAsStreamAsync(cancellationToken), GitHubJsonContext.Default.PullResponse, cancellationToken);
         }
         catch (JsonException)
         {
            throw Unreadable(pullRequest);
         }

         return answer is not null
            && CommitId.TryParse(answer.Head.Sha, out var head)
            && CommitId.TryParse(answer.Base.Sha, out var @base)
            && Uri.TryCreate(answer.Base.Repo.CloneUrl, UriKind.Absolute, out var remoteUrl)
            && remoteUrl.UserInfo.Length == 0
            ? new GitHubPullRequestDetails(remoteUrl, head, @base, answer.State, answer.Merged)
            : throw Unreadable(pullRequest);
      }
   }

   public void Dispose() => _http.Dispose();

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
            $"GitHub answered {status}: the pull request's repository has moved, so this URL no longer resolves. Start a new tour from the pull request's current URL."),
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

internal sealed record BaseResponse(string Sha, RepositoryResponse Repo);

internal sealed record RepositoryResponse(string CloneUrl);

internal sealed record ErrorResponse(string? Message = null);

/// <summary>The source-generated JSON metadata for the answers of the GitHub REST API, whose properties are in snake case.</summary>
/// <remarks>A missing property without a default value fails deserialization, and so does a null in a property that the records do not mark as nullable.</remarks>
[JsonSourceGenerationOptions(
   JsonSerializerDefaults.Web,
   PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
   RespectNullableAnnotations = true,
   RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(PullResponse))]
[JsonSerializable(typeof(ErrorResponse))]
internal sealed partial class GitHubJsonContext : JsonSerializerContext;
