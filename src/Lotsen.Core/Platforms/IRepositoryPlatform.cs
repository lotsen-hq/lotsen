using Lotsen.Credentials;
using Lotsen.Git;

namespace Lotsen.Platforms;

/// <summary>Provides the repositories of a platform: it recognizes their URLs, labels them, resolves their refs to commits and builds the web URLs of the mentions in their tours.</summary>
/// <remarks>
/// A platform with repositories also has pull requests, and both sides share the platform's ID and its git credential. So a <see cref="Tours.RepositoryTarget"/> names the platform by its
/// <see cref="IPullRequestPlatform.Id"/>, and the clone of a repository uses the same <see cref="IPullRequestPlatform.GitCredential"/>. <c>RepositoryPlatformContractTests</c> checks the
/// promises of these members on every adapter.
/// </remarks>
public interface IRepositoryPlatform : IPullRequestPlatform
{
   /// <summary>The repository URL forms the platform accepts, written for a refusal, such as <c>https://example.com/{owner}/{repository}</c>.</summary>
   string ExpectedRepositoryForms { get; }

   /// <summary>Returns the normalized URL of the repository that the text names, with the ref that the text names.</summary>
   /// <returns>The repository, or null when the text is not one of this platform's repository URLs.</returns>
   RecognizedRepository? RecognizeRepository(string text);

   /// <summary>Returns a short name for a repository at a URL that <see cref="RecognizeRepository"/> returned, such as <c>octo-org/hello-world</c>.</summary>
   /// <returns>The name, or null when the URL is not one of this platform's repository URLs.</returns>
   string? RepositoryLabel(Uri url);

   /// <summary>Returns the web URL of a work item or a pull request that the prose of a repository's tour mentions, for a repository at a URL that <see cref="RecognizeRepository"/> returned.</summary>
   /// <remarks>A <see cref="MentionKind.PullRequest"/> mention names a pull request in that repository. The renderer links only an <c>https:</c> URL.</remarks>
   /// <param name="url">The URL of the tour's repository.</param>
   /// <param name="kind">What the mention names.</param>
   /// <param name="id">The platform's number of the work item or pull request, from 1.</param>
   /// <returns>The absolute URL that opens it in a browser, or null when the URL is not one of this platform's repository URLs.</returns>
   Uri? RepositoryMentionUrl(Uri url, MentionKind kind, int id);

   /// <summary>Returns the commit that a ref of the repository at a URL that <see cref="RecognizeRepository"/> returned points to, with the repository's clone URL.</summary>
   /// <remarks>
   /// A ref that <see cref="RefName.IsValid"/> refuses names nothing, and fails before any request. A ref that is a full commit ID names that commit, when the repository has it. Any other ref
   /// names the tag of that name, or else the branch of that name. An abbreviated commit ID is no commit ID, so it names only a tag or a branch of that name. Every failure outside the process
   /// throws one of the two exceptions below, and any other exception is a fault of the adapter.
   /// </remarks>
   /// <param name="url">The repository's URL.</param>
   /// <param name="ref">A branch, a tag or a full commit ID, or null for the repository's default branch.</param>
   /// <param name="cancellationToken">Cancels the requests.</param>
   /// <exception cref="PlatformException">The repository or the ref does not exist, or the platform refused the request or could not be reached.</exception>
   /// <exception cref="AccessTokenUnavailableException">The token provider has no token.</exception>
   Task<ResolvedRepository> ResolveRepositoryAsync(Uri url, string? @ref, CancellationToken cancellationToken);
}

/// <summary>A repository that a platform recognized in a text.</summary>
/// <param name="Url">The repository's normalized URL, which a target stores.</param>
/// <param name="Ref">
/// The ref that the text names, such as the branch of a URL that shows the repository at that branch, or null when the text names none. <see cref="RefName.IsValid"/> accepts every ref here.
/// </param>
public sealed record RecognizedRepository(Uri Url, string? Ref);

/// <summary>The commit that a ref of a repository points to, as its platform reports it.</summary>
/// <param name="RemoteUrl">The URL to clone the repository from, without user information.</param>
/// <param name="Ref">The ref that names the commit: the default branch's name when the resolve named no ref, or the commit ID for a <see cref="RepositoryRefKind.Commit"/>.</param>
/// <param name="Kind">What the ref is.</param>
/// <param name="Commit">The commit that the ref points to. A tag's commit is the commit that the tag finally names, also through an annotated tag.</param>
public sealed record ResolvedRepository(Uri RemoteUrl, string Ref, RepositoryRefKind Kind, CommitId Commit);

/// <summary>What a repository's ref is.</summary>
public enum RepositoryRefKind
{
   /// <summary>A branch, which can move to a later commit.</summary>
   Branch,

   /// <summary>A tag.</summary>
   Tag,

   /// <summary>A full commit ID.</summary>
   Commit,
}
