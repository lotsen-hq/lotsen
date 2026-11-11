using System.Text.Json.Serialization;
using Lotsen.Credentials;
using Lotsen.Git;
using Lotsen.Json;

namespace Lotsen.Platforms;

/// <summary>Provides the pull requests of one hosting platform: it recognizes their URLs, labels them, resolves them to commits and builds the web URLs of the mentions in their tours.</summary>
/// <remarks>
/// Tour creation, pinning and the file operations reach a platform only through this interface, so a new platform needs no change to them. <c>PlatformContractTests</c> checks the promises of its
/// members on every adapter.
/// </remarks>
public interface IPullRequestPlatform
{
   /// <summary>The platform's ID, which a stored <see cref="Tours.PullRequestTarget"/> names. It never changes, because stored tours keep it.</summary>
   string Id { get; }

   /// <summary>The URL forms the platform accepts, written for a refusal, such as <c>https://example.com/{owner}/{repository}/pull/{id}</c>.</summary>
   string ExpectedForms { get; }

   /// <summary>The credential that git presents to the platform's remotes when they ask for authentication.</summary>
   IGitCredential GitCredential { get; }

   /// <summary>Returns the normalized URL of the pull request that the text names.</summary>
   /// <returns>The URL that a target stores, or null when the text is not one of this platform's pull request URLs.</returns>
   Uri? Recognize(string text);

   /// <summary>Returns whether two URLs that <see cref="Recognize"/> returned name the same pull request.</summary>
   /// <remarks><see cref="Recognize"/> can return different URLs for one pull request, such as one for each host form, so a caller compares targets only with this member.</remarks>
   bool IsSamePullRequest(Uri url, Uri other);

   /// <summary>Returns a short name for a pull request, such as <c>contoso/shop/web #42</c>.</summary>
   /// <returns>The name, or null when the URL is not one of this platform's pull request URLs.</returns>
   string? Label(Uri url);

   /// <summary>Returns the web URL of a work item or a pull request that the prose of a tour mentions, for a tour of the pull request at a URL that <see cref="Recognize"/> returned.</summary>
   /// <remarks>
   /// A <see cref="MentionKind.PullRequest"/> mention names a pull request in the repository of the pull request at <paramref name="url"/>. The renderer links only an <c>https:</c> URL.
   /// </remarks>
   /// <param name="url">The URL of the tour's pull request.</param>
   /// <param name="kind">What the mention names.</param>
   /// <param name="id">The platform's number of the work item or pull request, from 1.</param>
   /// <returns>The absolute URL that opens it in a browser, or null when the URL is not one of this platform's pull request URLs.</returns>
   Uri? MentionUrl(Uri url, MentionKind kind, int id);

   /// <summary>Returns the latest iteration and the state of the pull request at a URL that <see cref="Recognize"/> returned.</summary>
   /// <remarks>
   /// The iteration's number keeps the promise of <see cref="ResolvedPullRequest.Iteration"/>. Every failure outside the process throws one of the two exceptions below, and any other exception
   /// is a fault of the adapter.
   /// </remarks>
   /// <exception cref="PlatformException">The platform refused the request or could not be reached.</exception>
   /// <exception cref="AccessTokenUnavailableException">The token provider has no token.</exception>
   Task<ResolvedPullRequest> ResolveAsync(Uri url, CancellationToken cancellationToken);
}

/// <summary>A pull request's latest iteration and state, as its platform reports them.</summary>
/// <param name="RemoteUrl">The URL to clone the repository from, without user information.</param>
/// <param name="SourceCommit">The head of the source branch in the iteration.</param>
/// <param name="TargetCommit">The head of the target branch in the iteration.</param>
/// <param name="Iteration">
/// The platform's number for the iteration, from 1. It rises by at least one with each change of the source head, a change back to an earlier head included, and never falls for one pull
/// request. A platform without native numbers counts the changes of the source head that it has resolved for the pull request, and keeps that count in its own storage.
/// </param>
/// <param name="State">Whether the pull request is open, completed or abandoned.</param>
public sealed record ResolvedPullRequest(Uri RemoteUrl, CommitId SourceCommit, CommitId TargetCommit, int Iteration, PullRequestState State);

/// <summary>Whether a pull request is open, completed or abandoned, in lotsen's words. Each platform maps its own states onto these.</summary>
[JsonConverter(typeof(StrictEnumConverter<PullRequestState>))]
public enum PullRequestState
{
   /// <summary>A pull request that is open, or in a state that its platform names and lotsen does not know.</summary>
   Active,

   /// <summary>A pull request that was merged into its target.</summary>
   Completed,

   /// <summary>A pull request that was closed without a merge.</summary>
   Abandoned,
}

/// <summary>What a mention in prose names on the platform of the tour's pull request.</summary>
public enum MentionKind
{
   /// <summary>A work item, which a <c>workitem:</c> link names.</summary>
   WorkItem,

   /// <summary>A pull request in the repository of the tour's pull request, which a <c>pullrequest:</c> link names.</summary>
   PullRequest,
}

/// <summary>The exception for a platform request that failed, or for a target whose platform lotsen does not know.</summary>
public sealed class PlatformException : ExternalFailureException
{
   /// <param name="message">The reason, written for the reviewer and Claude, which names the pull request and the fix where there is one. It never holds a token.</param>
   /// <param name="innerException">The platform's own exception, or null for none.</param>
   public PlatformException(string message, Exception? innerException = null)
      : base(message, innerException)
   {
   }
}
