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

namespace Lotsen.AzureDevOps;

/// <summary>The settings for calling the Azure DevOps REST API.</summary>
internal sealed record AzureDevOpsOptions
{
   /// <summary>The longest one request may take.</summary>
   public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

   /// <summary>The scheme, host and port that every request goes to, or null for the host of the pull request or the repository.</summary>
   /// <remarks>Tests point it at a local server. The path of each request stays as the real host would get it.</remarks>
   public Uri? ServerOverride { get; init; }
}

/// <summary>Why an Azure DevOps request failed.</summary>
internal enum AzureDevOpsFailure
{
   /// <summary>A refusal of the token: the user is not signed in, or Azure DevOps rejected the token.</summary>
   NotSignedIn,

   /// <summary>A refusal of the signed-in user, who cannot see the pull request or the repository.</summary>
   NoAccess,

   /// <summary>A pull request, repository, project, organization, ref or commit that does not exist.</summary>
   NotFound,

   /// <summary>Any other failure, such as an unreachable server, a server error or an answer lotsen cannot read.</summary>
   Other,
}

/// <summary>The exception for an Azure DevOps request that failed.</summary>
internal sealed class AzureDevOpsException : Exception
{
   /// <param name="failure">Why the request failed.</param>
   /// <param name="message">The reason, which names the pull request or the repository and, where there is one, the fix. It never holds the token.</param>
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

/// <summary>Reads the metadata of Azure DevOps pull requests and repositories through the REST API, as the user that the token provider signs in.</summary>
/// <remarks>
/// The client sends the token as a bearer header and puts it in no log line or exception message. Each request asks the provider for the token, so a refreshed token takes effect at once. A refusal of
/// the token or of its account rejects the token at the provider, so the next request after a new sign-in sends the new token.
/// </remarks>
internal sealed class AzureDevOpsClient : IDisposable
{
   private const string ApiVersion = "7.1";
   private const string NoCommitYet = "so the repository has no commit to show yet. Push a commit to the repository, or name a ref, then try again.";

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
      var subject = Subject.Of(pullRequest, "the pull request");
      var response = await GetAsync(subject, PullRequestPath(pullRequest), "", AzureDevOpsJsonContext.Default.PullRequestResponse, cancellationToken);
      return new PullRequestMetadata(StatusOf(response.Status), RemoteUrlOf(subject, response.Repository.RemoteUrl));
   }

   /// <summary>Returns the pull request's iterations.</summary>
   /// <returns>The iterations, in the order of their numbers.</returns>
   /// <exception cref="AzureDevOpsException">The request failed.</exception>
   /// <exception cref="AccessTokenUnavailableException">The token provider has no token. No request was sent then.</exception>
   public async Task<IReadOnlyList<PullRequestIteration>> GetIterationsAsync(AzureDevOpsPullRequest pullRequest, CancellationToken cancellationToken)
   {
      var subject = Subject.Of(pullRequest, "the updates of the pull request");
      var response = await GetAsync(subject, PullRequestPath(pullRequest) + "/iterations", "", AzureDevOpsJsonContext.Default.IterationListResponse, cancellationToken);
      List<PullRequestIteration> iterations = [];
      foreach (var iteration in response.Value)
      {
         if (!CommitId.TryParse(iteration.SourceRefCommit.CommitId, out var source) || !CommitId.TryParse(iteration.TargetRefCommit.CommitId, out var target))
         {
            throw Unreadable(subject);
         }

         iterations.Add(new PullRequestIteration(iteration.Id, source, target));
      }

      return [.. iterations.OrderBy(iteration => iteration.Id)];
   }

   /// <summary>Returns the commit that a ref of the repository points to, with the repository's clone URL.</summary>
   /// <remarks>
   /// The ref resolves in the order that <see cref="IRepositoryPlatform.ResolveRepositoryAsync"/> promises. The requests after the first name the repository by the ID in Azure DevOps's answer.
   /// </remarks>
   /// <param name="repository">The repository.</param>
   /// <param name="ref">A branch, a tag or a full commit ID, or null for the default branch.</param>
   /// <param name="cancellationToken">Cancels the requests.</param>
   /// <exception cref="AzureDevOpsException">The ref does not exist, a request failed, or one of Azure DevOps's answers could not be read.</exception>
   /// <exception cref="AccessTokenUnavailableException">The token provider has no token. No request was sent then.</exception>
   public async Task<ResolvedRepository> GetRepositoryAsync(AzureDevOpsRepository repository, string? @ref, CancellationToken cancellationToken)
   {
      var subject = Subject.Of(repository);

      if (@ref is not null && !RefName.IsValid(@ref))
      {
         throw MissingRef(subject, @ref);
      }

      var repositories = $"{Uri.EscapeDataString(repository.Project)}/_apis/git/repositories/";
      var answer = await GetAsync(subject, repositories + Uri.EscapeDataString(repository.Name), "", AzureDevOpsJsonContext.Default.GitRepositoryResponse, cancellationToken);
      var remoteUrl = RemoteUrlOf(subject, answer.RemoteUrl);
      var byId = repositories + Uri.EscapeDataString(answer.Id);
      if (@ref is null)
      {
         const string BranchPrefix = "refs/heads/";
         if (answer.DefaultBranch is not { } defaultRef || !defaultRef.StartsWith(BranchPrefix, StringComparison.Ordinal))
         {
            throw Failure(subject, AzureDevOpsFailure.NotFound, $"Azure DevOps names no default branch of the repository, {NoCommitYet}");
         }

         var defaultBranch = defaultRef[BranchPrefix.Length..];
         var head = await RefCommitAsync(subject, byId, "heads", defaultBranch, cancellationToken)
            ?? throw Failure(subject, AzureDevOpsFailure.NotFound, $"Azure DevOps has no default branch '{defaultBranch}' in the repository, {NoCommitYet}");
         return new ResolvedRepository(remoteUrl, defaultBranch, RepositoryRefKind.Branch, head);
      }

      if (CommitId.TryParse(@ref, out var id))
      {
         // The documentation names no status for a commit that the repository lacks, so a 404 after the repository's own answer counts as that.
         // https://learn.microsoft.com/en-us/rest/api/azure/devops/git/commits/get?view=azure-devops-rest-7.1
         var commit = await FindAsync(subject, $"{byId}/commits/{id}", "", AzureDevOpsJsonContext.Default.CommitResponse, cancellationToken);
         return commit is null ? throw MissingRef(subject, @ref)
            : CommitId.TryParse(commit.CommitId, out var found) && found == id ? new ResolvedRepository(remoteUrl, id.Value, RepositoryRefKind.Commit, id)
            : throw Unreadable(subject);
      }

      if (await RefCommitAsync(subject, byId, "tags", @ref, cancellationToken) is { } tagged)
      {
         return new ResolvedRepository(remoteUrl, @ref, RepositoryRefKind.Tag, tagged);
      }

      return await RefCommitAsync(subject, byId, "heads", @ref, cancellationToken) is { } branchHead
         ? new ResolvedRepository(remoteUrl, @ref, RepositoryRefKind.Branch, branchHead)
         : throw MissingRef(subject, @ref);
   }

   public void Dispose() => _http.Dispose();

   /// <summary>Returns the commit that a branch or a tag of the repository points to, or null when no ref has that exact name.</summary>
   /// <remarks>An annotated tag's ref names the tag object, so the commit is the one that Azure DevOps peels it to.</remarks>
   /// <param name="subject">The repository, for the failures.</param>
   /// <param name="repositoryPath">The request path of the repository, by its ID.</param>
   /// <param name="kind"><c>heads</c> or <c>tags</c>.</param>
   /// <param name="name">The branch's or the tag's name.</param>
   /// <param name="cancellationToken">Cancels the request.</param>
   private async Task<CommitId?> RefCommitAsync(Subject subject, string repositoryPath, string kind, string name, CancellationToken cancellationToken)
   {
      // The filter keeps every ref whose name starts with it, and peelTags fills in an annotated tag's commit:
      // https://learn.microsoft.com/en-us/rest/api/azure/devops/git/refs/list?view=azure-devops-rest-7.1
      var answer = await GetAsync(subject, $"{repositoryPath}/refs", $"filter={Uri.EscapeDataString($"{kind}/{name}")}&peelTags=true&", AzureDevOpsJsonContext.Default.RefListResponse,
         cancellationToken);
      var found = answer.Value.FirstOrDefault(entry => entry.Name == $"refs/{kind}/{name}");
      return found is null ? null
         : CommitId.TryParse(string.IsNullOrEmpty(found.PeeledObjectId) ? found.ObjectId : found.PeeledObjectId, out var commit) ? commit
         : throw Unreadable(subject);
   }

   /// <summary>Returns the answer to a GET of the path below the organization's URL.</summary>
   private async Task<T> GetAsync<T>(Subject subject, string path, string query, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
      where T : class =>
      await ReadAsync(subject, path, query, typeInfo, absentOnNotFound: false, cancellationToken) ?? throw Unreadable(subject);

   /// <summary>Returns the answer to a GET of the path below the organization's URL, or null for a 404.</summary>
   /// <remarks>A 404 keeps the token, because only a refusal of the token or of its account rejects it.</remarks>
   private Task<T?> FindAsync<T>(Subject subject, string path, string query, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
      where T : class =>
      ReadAsync(subject, path, query, typeInfo, absentOnNotFound: true, cancellationToken);

   /// <summary>Returns the answer to a GET of the path below the organization's URL, or null for a 404 when <paramref name="absentOnNotFound"/> is true.</summary>
   /// <param name="subject">What the request reads, which its failures name.</param>
   /// <param name="path">The request path below the organization's URL, escaped.</param>
   /// <param name="query">The query parameters before <c>api-version</c>, escaped, each followed by <c>&amp;</c>, or empty.</param>
   /// <param name="typeInfo">The answer's JSON metadata.</param>
   /// <param name="absentOnNotFound">Whether a 404 returns null.</param>
   /// <param name="cancellationToken">Cancels the request.</param>
   private async Task<T?> ReadAsync<T>(Subject subject, string path, string query, JsonTypeInfo<T> typeInfo, bool absentOnNotFound, CancellationToken cancellationToken)
      where T : class
   {
      var token = await _tokens.GetTokenAsync(_options.Timeout, cancellationToken);
      var url = RequestUrl(subject, path, query);
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
         throw Failure(subject, AzureDevOpsFailure.Other, string.Create(CultureInfo.InvariantCulture, $"Azure DevOps did not answer within {_options.Timeout.TotalSeconds:0.#} seconds."));
      }
      catch (HttpRequestException exception)
      {
         throw Failure(subject, AzureDevOpsFailure.Other, $"lotsen could not reach Azure DevOps at {url.Host}: {exception.Message}");
      }

      using (response)
      {
         _logger.LogDebug("GET {Url} answered {Status} in {Milliseconds} ms.", url, (int)response.StatusCode, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
         if (response.StatusCode == HttpStatusCode.NotFound && absentOnNotFound)
         {
            return null;
         }

         if (response.StatusCode != HttpStatusCode.OK)
         {
            var refusal = await RefusalAsync(subject, response, token, cancellationToken);
            if (refusal.Failure is AzureDevOpsFailure.NotSignedIn or AzureDevOpsFailure.NoAccess)
            {
               _tokens.Reject(token);
            }

            throw refusal;
         }

         try
         {
            return await JsonSerializer.DeserializeAsync(await response.Content.ReadAsStreamAsync(cancellationToken), typeInfo, cancellationToken) ?? throw Unreadable(subject);
         }
         catch (JsonException)
         {
            throw Unreadable(subject);
         }
      }
   }

   private Uri RequestUrl(Subject subject, string path, string query)
   {
      var url = new Uri(subject.OrganizationUrl, $"{path}?{query}api-version={ApiVersion}");
      return _options.ServerOverride is { } server ? new UriBuilder(url) { Scheme = server.Scheme, Host = server.Host, Port = server.Port }.Uri : url;
   }

   private static string PullRequestPath(AzureDevOpsPullRequest pullRequest) =>
      $"{Uri.EscapeDataString(pullRequest.Repository.Project)}/_apis/git/repositories/{Uri.EscapeDataString(pullRequest.Repository.Name)}/pullrequests/{pullRequest.PullRequestId}";

   private static Uri RemoteUrlOf(Subject subject, string remoteUrl)
   {
      if (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out var url))
      {
         throw Unreadable(subject);
      }

      // Azure DevOps puts the organization in the remote URL as a user name, and git would store it in the clone.
      // The pin stores the Uri's original string, which this keeps in its canonical form, as Remote_url_without_user_information_keeps_its_canonical_text pins.
      return new Uri(url.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.UserInfo, UriFormat.UriEscaped));
   }

   private static async Task<AzureDevOpsException> RefusalAsync(Subject subject, HttpResponseMessage response, AccessToken token, CancellationToken cancellationToken)
   {
      var status = (int)response.StatusCode;
      return response.StatusCode switch
      {
         // Azure DevOps answers 203 with a sign-in page when it does not take the token.
         HttpStatusCode.Unauthorized or HttpStatusCode.NonAuthoritativeInformation => Failure(
            subject,
            AzureDevOpsFailure.NotSignedIn,
            $"Azure DevOps answered {status}: not signed in or token rejected. Run 'az login' with an account that can see the {subject.Kind}, then try again."),
         HttpStatusCode.Forbidden => Failure(subject, AzureDevOpsFailure.NoAccess, $"Azure DevOps answered {status}: no access. Check that the signed-in account can see the repository."),
         HttpStatusCode.NotFound => Failure(
            subject,
            AzureDevOpsFailure.NotFound,
            $"Azure DevOps answered {status}: {subject.Kind} not found. Check the URL, and that the signed-in account can see the repository."),
         _ => Failure(
            subject,
            AzureDevOpsFailure.Other,
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

   private static AzureDevOpsException Unreadable(Subject subject) => Failure(subject, AzureDevOpsFailure.Other, "Azure DevOps sent an answer that lotsen cannot read.");

   private static AzureDevOpsException MissingRef(Subject subject, string @ref) => Failure(
      subject,
      AzureDevOpsFailure.NotFound,
      $"Azure DevOps has no branch, tag or commit '{@ref}' in the repository. Check the ref, and give a commit by its full ID, then try again.");

   private static AzureDevOpsException Failure(Subject subject, AzureDevOpsFailure failure, string reason) => new(failure, $"lotsen could not read {subject.What} {subject.Url.AbsoluteUri}. {reason}");

   /// <summary>What a request reads, which its failures name.</summary>
   /// <param name="What">What the request reads, such as <c>the updates of the pull request</c>.</param>
   /// <param name="Kind">The kind of the URL's target: <c>pull request</c> or <c>repository</c>.</param>
   /// <param name="Url">The normalized URL of the pull request or the repository.</param>
   /// <param name="OrganizationUrl">The organization's URL, which the request paths start from.</param>
   private sealed record Subject(string What, string Kind, Uri Url, Uri OrganizationUrl)
   {
      public static Subject Of(AzureDevOpsPullRequest pullRequest, string what) => new(what, "pull request", pullRequest.Url, pullRequest.Repository.OrganizationUrl);

      public static Subject Of(AzureDevOpsRepository repository) => new("the repository", "repository", repository.Url, repository.OrganizationUrl);
   }
}

// The answers of the REST API, with only the properties that the client reads. The JSON reader skips every other property, so one that is missing or malformed fails no read.
internal sealed record PullRequestResponse(string Status, RepositoryResponse Repository);

internal sealed record RepositoryResponse(string RemoteUrl);

internal sealed record CommitResponse(string CommitId);

internal sealed record IterationListResponse(IReadOnlyList<IterationResponse> Value);

internal sealed record IterationResponse(int Id, CommitResponse SourceRefCommit, CommitResponse TargetRefCommit);

internal sealed record GitRepositoryResponse(string Id, string RemoteUrl, string? DefaultBranch = null);

internal sealed record RefListResponse(IReadOnlyList<RefResponse> Value);

internal sealed record RefResponse(string Name, string ObjectId, string? PeeledObjectId = null);

internal sealed record ErrorResponse(string? Message = null);

/// <summary>
/// The source-generated JSON metadata for the answers of the Azure DevOps REST API.
/// </summary>
/// <remarks>A missing property without a default value fails deserialization, and so does a null in a property that the records do not mark as nullable.</remarks>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(PullRequestResponse))]
[JsonSerializable(typeof(IterationListResponse))]
[JsonSerializable(typeof(GitRepositoryResponse))]
[JsonSerializable(typeof(RefListResponse))]
[JsonSerializable(typeof(CommitResponse))]
[JsonSerializable(typeof(ErrorResponse))]
internal sealed partial class AzureDevOpsJsonContext : JsonSerializerContext;
