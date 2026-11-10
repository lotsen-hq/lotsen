using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Lotsen.Credentials;
using Lotsen.Git;

namespace Lotsen.AzureDevOps;

/// <summary>The settings for calling the Azure DevOps REST API.</summary>
internal sealed record AzureDevOpsOptions
{
   /// <summary>The longest one request may take.</summary>
   public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

   /// <summary>The scheme, host and port that every request goes to, or null for the pull request's own host.</summary>
   /// <remarks>Tests point it at a local server. The path of each request stays as the real host would get it.</remarks>
   public Uri? ServerOverride { get; init; }
}

/// <summary>Why an Azure DevOps request failed.</summary>
internal enum AzureDevOpsFailure
{
   /// <summary>A refusal of the token: the user is not signed in, or Azure DevOps rejected the token.</summary>
   NotSignedIn,

   /// <summary>A refusal of the signed-in user, who cannot see the pull request.</summary>
   NoAccess,

   /// <summary>A pull request, repository, project or organization that does not exist.</summary>
   NotFound,

   /// <summary>Any other failure, such as an unreachable server, a server error or an answer lotsen cannot read.</summary>
   Other,
}

/// <summary>The exception for an Azure DevOps request that failed.</summary>
internal sealed class AzureDevOpsException : Exception
{
   /// <param name="failure">Why the request failed.</param>
   /// <param name="message">The reason, which names the pull request and, where there is one, the fix. It never holds the token.</param>
   public AzureDevOpsException(AzureDevOpsFailure failure, string message)
      : base(message)
   {
      Failure = failure;
   }

   public AzureDevOpsFailure Failure { get; }
}

/// <summary>The state of a pull request.</summary>
internal enum PullRequestStatus
{
   Active,
   Completed,
   Abandoned,

   /// <summary>A state that Azure DevOps reports and lotsen does not know, such as <c>notSet</c>.</summary>
   Other,
}

/// <summary>The metadata of a pull request that pinning a tour needs.</summary>
/// <param name="Status">The pull request's state.</param>
/// <param name="RemoteUrl">The URL to clone the pull request's repository from, without user information.</param>
internal sealed record PullRequestMetadata(PullRequestStatus Status, Uri RemoteUrl);

/// <summary>One iteration of a pull request: the state of its branches after one push.</summary>
/// <param name="Id">The iteration's number, from 1.</param>
/// <param name="SourceCommit">The head of the source branch in this iteration.</param>
/// <param name="TargetCommit">The head of the target branch in this iteration.</param>
internal sealed record PullRequestIteration(int Id, CommitId SourceCommit, CommitId TargetCommit);

/// <summary>Reads the metadata of Azure DevOps pull requests through the REST API, as the user that the token provider signs in.</summary>
/// <remarks>
/// The client sends the token as a bearer header and puts it in no log line or exception message. Each request asks the provider for the token, so a refreshed token takes effect at once. A refusal of
/// the token or of its account rejects the token at the provider, so the next request after a new sign-in sends the new token.
/// </remarks>
internal sealed class AzureDevOpsClient : IDisposable
{
   private const string ApiVersion = "7.1";

   private readonly IAccessTokenProvider _tokens;
   private readonly AzureDevOpsOptions _options;
   private readonly ILogger<AzureDevOpsClient> _logger;
   private readonly HttpClient _http;

   public AzureDevOpsClient(IAccessTokenProvider tokens, AzureDevOpsOptions options, ILogger<AzureDevOpsClient> logger)
   {
      _tokens = tokens;
      _options = options;
      _logger = logger;
      _http = new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false })
      {
         Timeout = options.Timeout,
      };
   }

   /// <summary>Returns the pull request's metadata.</summary>
   /// <exception cref="AzureDevOpsException">The request failed.</exception>
   /// <exception cref="AccessTokenUnavailableException">The token provider has no token. No request was sent then.</exception>
   public async Task<PullRequestMetadata> GetPullRequestAsync(AzureDevOpsPullRequest pullRequest, CancellationToken cancellationToken)
   {
      var response = await GetAsync(pullRequest, "", "the pull request", AzureDevOpsJsonContext.Default.PullRequestResponse, cancellationToken);
      if (!Uri.TryCreate(response.Repository.RemoteUrl, UriKind.Absolute, out var remoteUrl))
      {
         throw Unreadable(pullRequest, "the pull request");
      }

      // Azure DevOps puts the organization in the remote URL as a user name, and git would store it in the clone.
      // The pin stores the Uri's original string, which this keeps in its canonical form, as Remote_url_without_user_information_keeps_its_canonical_text pins.
      var withoutUserInfo = new Uri(remoteUrl.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.UserInfo, UriFormat.UriEscaped));
      return new PullRequestMetadata(StatusOf(response.Status), withoutUserInfo);
   }

   /// <summary>Returns the pull request's iterations.</summary>
   /// <returns>The iterations, in the order of their numbers.</returns>
   /// <exception cref="AzureDevOpsException">The request failed.</exception>
   /// <exception cref="AccessTokenUnavailableException">The token provider has no token. No request was sent then.</exception>
   public async Task<IReadOnlyList<PullRequestIteration>> GetIterationsAsync(AzureDevOpsPullRequest pullRequest, CancellationToken cancellationToken)
   {
      const string what = "the updates of the pull request";
      var response = await GetAsync(pullRequest, "/iterations", what, AzureDevOpsJsonContext.Default.IterationListResponse, cancellationToken);
      List<PullRequestIteration> iterations = [];
      foreach (var iteration in response.Value)
      {
         if (!CommitId.TryParse(iteration.SourceRefCommit.CommitId, out var source) || !CommitId.TryParse(iteration.TargetRefCommit.CommitId, out var target))
         {
            throw Unreadable(pullRequest, what);
         }

         iterations.Add(new PullRequestIteration(iteration.Id, source, target));
      }

      return [.. iterations.OrderBy(iteration => iteration.Id)];
   }

   public void Dispose() => _http.Dispose();

   private async Task<T> GetAsync<T>(AzureDevOpsPullRequest pullRequest, string pathSuffix, string what, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
   {
      var token = await _tokens.GetTokenAsync(_options.Timeout, cancellationToken);
      var url = RequestUrl(pullRequest, pathSuffix);
      using var request = new HttpRequestMessage(HttpMethod.Get, url);
      request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
      request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

      // Asks Azure DevOps to answer a rejected token with 401, not with a sign-in page.
      request.Headers.Add("X-TFS-FedAuthRedirect", "Suppress");

      var started = Stopwatch.GetTimestamp();
      HttpResponseMessage response;
      try
      {
         response = await _http.SendAsync(request, cancellationToken);
      }
      catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
      {
         throw Failure(pullRequest, AzureDevOpsFailure.Other, what, string.Create(CultureInfo.InvariantCulture, $"Azure DevOps did not answer within {_options.Timeout.TotalSeconds:0.#} seconds."));
      }
      catch (HttpRequestException exception)
      {
         throw Failure(pullRequest, AzureDevOpsFailure.Other, what, $"lotsen could not reach Azure DevOps at {url.Host}: {exception.Message}");
      }

      using (response)
      {
         _logger.LogDebug("GET {Url} answered {Status} in {Milliseconds} ms.", url, (int)response.StatusCode, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
         if (response.StatusCode != HttpStatusCode.OK)
         {
            var refusal = await RefusalAsync(pullRequest, what, response, token, cancellationToken);
            if (refusal.Failure is AzureDevOpsFailure.NotSignedIn or AzureDevOpsFailure.NoAccess)
            {
               _tokens.Reject(token);
            }

            throw refusal;
         }

         try
         {
            return await JsonSerializer.DeserializeAsync(await response.Content.ReadAsStreamAsync(cancellationToken), typeInfo, cancellationToken) ?? throw Unreadable(pullRequest, what);
         }
         catch (JsonException)
         {
            throw Unreadable(pullRequest, what);
         }
      }
   }

   private Uri RequestUrl(AzureDevOpsPullRequest pullRequest, string pathSuffix)
   {
      var path = $"{Uri.EscapeDataString(pullRequest.Project)}/_apis/git/repositories/{Uri.EscapeDataString(pullRequest.Repository)}/pullrequests/{pullRequest.PullRequestId}{pathSuffix}";
      var url = new Uri(pullRequest.OrganizationUrl, $"{path}?api-version={ApiVersion}");
      return _options.ServerOverride is { } server ? new UriBuilder(url) { Scheme = server.Scheme, Host = server.Host, Port = server.Port }.Uri : url;
   }

   private static async Task<AzureDevOpsException> RefusalAsync(AzureDevOpsPullRequest pullRequest, string what, HttpResponseMessage response, AccessToken token, CancellationToken cancellationToken)
   {
      var status = (int)response.StatusCode;
      return response.StatusCode switch
      {
         // Azure DevOps answers 203 with a sign-in page when it does not take the token.
         HttpStatusCode.Unauthorized or HttpStatusCode.NonAuthoritativeInformation => Failure(
            pullRequest,
            AzureDevOpsFailure.NotSignedIn,
            what,
            $"Azure DevOps answered {status}: not signed in or token rejected. Run 'az login' with an account that can see the pull request, then try again."),
         HttpStatusCode.Forbidden => Failure(pullRequest, AzureDevOpsFailure.NoAccess, what, $"Azure DevOps answered {status}: no access. Check that the signed-in account can see the repository."),
         HttpStatusCode.NotFound => Failure(
            pullRequest,
            AzureDevOpsFailure.NotFound,
            what,
            $"Azure DevOps answered {status}: pull request not found. Check the URL, and that the signed-in account can see the repository."),
         _ => Failure(
            pullRequest,
            AzureDevOpsFailure.Other,
            what,
            $"Azure DevOps answered {status}"
            + (await MessageAsync(response, cancellationToken) is { } message ? ": " + message.Replace(token.Value, "[redacted]", StringComparison.Ordinal) : ".")),
      };
   }

   /// <summary>Returns the message of an Azure DevOps error answer, or null when the answer holds none.</summary>
   private static async Task<string?> MessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
   {
      try
      {
         var error = await JsonSerializer.DeserializeAsync(await response.Content.ReadAsStreamAsync(cancellationToken), AzureDevOpsJsonContext.Default.ErrorResponse, cancellationToken);
         return string.IsNullOrWhiteSpace(error?.Message) ? null : error.Message.Trim();
      }
      catch (JsonException)
      {
         return null;
      }
   }

   private static PullRequestStatus StatusOf(string status) => status switch
   {
      "active" => PullRequestStatus.Active,
      "completed" => PullRequestStatus.Completed,
      "abandoned" => PullRequestStatus.Abandoned,
      _ => PullRequestStatus.Other,
   };

   private static AzureDevOpsException Unreadable(AzureDevOpsPullRequest pullRequest, string what) =>
      Failure(pullRequest, AzureDevOpsFailure.Other, what, "Azure DevOps sent an answer that lotsen cannot read.");

   private static AzureDevOpsException Failure(AzureDevOpsPullRequest pullRequest, AzureDevOpsFailure failure, string what, string reason) =>
      new(failure, $"lotsen could not read {what} {pullRequest.Url.AbsoluteUri}. {reason}");
}

// The answers of the REST API, with only the properties that the client reads. The JSON reader skips every other property, so one that is missing or malformed fails no read.
internal sealed record PullRequestResponse(string Status, RepositoryResponse Repository);

internal sealed record RepositoryResponse(string RemoteUrl);

internal sealed record CommitResponse(string CommitId);

internal sealed record IterationListResponse(IReadOnlyList<IterationResponse> Value);

internal sealed record IterationResponse(int Id, CommitResponse SourceRefCommit, CommitResponse TargetRefCommit);

internal sealed record ErrorResponse(string? Message = null);

/// <summary>
/// The source-generated JSON metadata for the answers of the Azure DevOps REST API.
/// </summary>
/// <remarks>A missing property without a default value fails deserialization, and so does a null in a property that the records do not mark as nullable.</remarks>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(PullRequestResponse))]
[JsonSerializable(typeof(IterationListResponse))]
[JsonSerializable(typeof(ErrorResponse))]
internal sealed partial class AzureDevOpsJsonContext : JsonSerializerContext;
