using Lotsen.Credentials;
using Lotsen.Git;
using Lotsen.Prose;
using Lotsen.Tours;

namespace Lotsen.Platforms;

/// <summary>Provides the server's registered platforms, found by a target, by a pull request URL or by a repository URL, and the platform-free repositories beside them.</summary>
/// <remarks>
/// A registered platform that is an <see cref="IRepositoryPlatform"/> also hosts repositories. Any other platform recognizes no repository URL and resolves no repository target. A repository URL
/// that no platform recognizes, on a host that no platform owns, is platform-free when <see cref="PlatformFreeRepositories.Recognize"/> accepts it.
/// </remarks>
internal sealed class PullRequestPlatforms
{
   private readonly IReadOnlyList<IPullRequestPlatform> _platforms;
   private readonly IReadOnlyList<IRepositoryPlatform> _repositoryPlatforms;

   /// <exception cref="ArgumentException">Two platforms have the same ID.</exception>
   public PullRequestPlatforms(IEnumerable<IPullRequestPlatform> platforms)
   {
      _platforms = [.. platforms];
      if (_platforms.GroupBy(platform => platform.Id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1) is { } duplicate)
      {
         throw new ArgumentException($"Two platforms have the ID '{duplicate.Key}'.", nameof(platforms));
      }

      _repositoryPlatforms = [.. _platforms.OfType<IRepositoryPlatform>()];
   }

   /// <summary>The URL forms that every platform accepts, in registration order, written for a refusal.</summary>
   public string ExpectedForms => string.Join("; or ", _platforms.Select(platform => platform.ExpectedForms));

   /// <summary>The repository URL forms that every platform with repositories accepts, in registration order, then the form of a platform-free repository, written for a refusal.</summary>
   public string ExpectedRepositoryForms => string.Join("; or ", _repositoryPlatforms.Select(platform => platform.ExpectedRepositoryForms).Append(PlatformFreeRepositories.ExpectedForms));

   /// <summary>Returns the platform of a target, which resolves its pull request and supplies the credential of its remotes.</summary>
   /// <remarks>
   /// Every site that reaches a tour's platform or builds a <see cref="Pinning.TourRemote"/> for it takes the platform from here, so a target whose platform lotsen does not know has one served
   /// reason.
   /// </remarks>
   /// <exception cref="PlatformException">No registered platform has the target's platform ID, or the target is a repository, whose platform <see cref="RepositoryOf"/> returns.</exception>
   /// <exception cref="ArgumentException">The target is of another kind. No other kind of target exists, so this is a fault of lotsen.</exception>
   public IPullRequestPlatform Of(TourTarget target) => target switch
   {
      PullRequestTarget pullRequest => Find(pullRequest) ?? throw new PlatformException(
         $"lotsen does not know the platform '{pullRequest.Platform}' of the pull request {pullRequest.Url.AbsoluteUri}, so it cannot reach the pull request or its repository. Run a lotsen build that supports that platform, then try again."),
      RepositoryTarget repository => throw new PlatformException($"lotsen reaches a platform only for a pull request's tour, and this tour is of the repository {repository.Url.AbsoluteUri}."),
      _ => throw new ArgumentException($"lotsen knows no platform for a target of type {target.GetType().Name}.", nameof(target)),
   };

   /// <summary>Returns the target of the pull request that the text names, from the first platform that recognizes it.</summary>
   /// <returns>The target with its normalized URL, or null when no platform recognizes the text.</returns>
   public PullRequestTarget? Recognize(string text)
   {
      foreach (var platform in _platforms)
      {
         if (platform.Recognize(text) is { } url)
         {
            return new PullRequestTarget(platform.Id, url);
         }
      }

      return null;
   }

   /// <summary>Returns the target of the repository that the text names, at the ref that the text names, from the first platform that recognizes it.</summary>
   /// <returns>
   /// The target with its normalized URL, a platform-free target without a ref when no platform recognizes the text, or null when the text is neither, as on a host that a platform owns.
   /// </returns>
   public RepositoryTarget? RecognizeRepository(string text)
   {
      foreach (var platform in _repositoryPlatforms)
      {
         if (platform.RecognizeRepository(text) is { } repository)
         {
            return new RepositoryTarget(repository.Url, repository.Ref, platform.Id);
         }
      }

      return PlatformFreeRepositories.Recognize(text) is { } url && !_platforms.Any(platform => platform.OwnsHost(url)) ? new RepositoryTarget(url, null, null) : null;
   }

   /// <summary>Returns the platform of a repository target, which resolves its ref and supplies the credential of its remote.</summary>
   /// <exception cref="PlatformException">No registered platform with repositories has the target's platform ID.</exception>
   /// <exception cref="ArgumentException">The target is platform-free. A caller handles such a repository with <see cref="PlatformFreeRepositories"/> before it asks for a platform, so this is a fault of lotsen.</exception>
   public IRepositoryPlatform RepositoryOf(RepositoryTarget target)
   {
      if (target.Platform is null)
      {
         throw new ArgumentException($"The repository {target.Url.AbsoluteUri} has no platform.", nameof(target));
      }

      return FindRepository(target) ?? throw new PlatformException(
         $"lotsen does not know the platform '{target.Platform}' of the repository {target.Url.AbsoluteUri}, so it cannot reach the repository. Run a lotsen build that supports repositories on that platform, then try again.");
   }

   /// <summary>Returns the commit that a repository target's ref names, through the target's platform, or through git for a platform-free repository.</summary>
   /// <param name="target">The repository, at its ref, or at its default branch when the ref is null.</param>
   /// <param name="platformFree">Resolves the ref of a platform-free repository.</param>
   /// <param name="cancellationToken">Stops the platform's request and git.</param>
   /// <exception cref="ExternalFailureException">The platform or git could not resolve the ref, as for a missing sign-in or a ref that names nothing. The reason names the fix where there is one.</exception>
   public Task<ResolvedRepository> ResolveAsync(RepositoryTarget target, PlatformFreeRepositories platformFree, CancellationToken cancellationToken) => target.Platform is null
      ? platformFree.ResolveAsync(target.Url, target.Ref, cancellationToken)
      : RepositoryOf(target).ResolveRepositoryAsync(target.Url, target.Ref, cancellationToken);

   /// <summary>Returns whether a tour's target names the same pull request as the target.</summary>
   /// <returns>True when both have one platform ID and that platform's <see cref="IPullRequestPlatform.IsSamePullRequest"/> says so, or false when no registered platform has the ID.</returns>
   public bool IsSamePullRequest(TourTarget tourTarget, PullRequestTarget target) =>
      tourTarget is PullRequestTarget pullRequest && pullRequest.Platform == target.Platform && Find(target) is { } platform && platform.IsSamePullRequest(pullRequest.Url, target.Url);

   /// <summary>Returns whether a tour's target names the same repository as the target, whatever the ref of either.</summary>
   /// <returns>
   /// True when both have one platform ID and that platform's <see cref="IRepositoryPlatform.IsSameRepository"/> says so, or when both are platform-free and
   /// <see cref="PlatformFreeRepositories.IsSame"/> says so. False when no registered platform with repositories has the ID.
   /// </returns>
   public bool IsSameRepository(TourTarget tourTarget, RepositoryTarget target) => tourTarget is RepositoryTarget repository && repository.Platform == target.Platform && (target.Platform is null
      ? PlatformFreeRepositories.IsSame(repository.Url, target.Url)
      : FindRepository(target) is { } platform && platform.IsSameRepository(repository.Url, target.Url));

   /// <summary>Returns the git credential of a target's remotes, which its platform supplies.</summary>
   /// <returns>The platform's credential, or <see cref="PlatformFreeRepositories.Credential"/> for a platform-free repository.</returns>
   /// <exception cref="PlatformException">No registered platform has the target's platform ID.</exception>
   public IGitCredential CredentialOf(TourTarget target) => target switch
   {
      RepositoryTarget { Platform: null } => PlatformFreeRepositories.Credential,
      RepositoryTarget repository => RepositoryOf(repository).GitCredential,
      _ => Of(target).GitCredential,
   };

   /// <summary>
   /// Returns the short name of a target: its platform's label, <see cref="PlatformFreeRepositories.Label"/> for a platform-free repository, or the URL when its platform is not registered or does
   /// not recognize the URL.
   /// </summary>
   /// <remarks>A repository's name ends in <c>@</c> and its ref, such as <c>octo-org/hello-world@v1.2.0</c>, unless the target follows the default branch.</remarks>
   public string Label(TourTarget target) => target switch
   {
      PullRequestTarget pullRequest => Find(pullRequest)?.Label(pullRequest.Url) ?? pullRequest.Url.AbsoluteUri,
      RepositoryTarget repository => (repository.Platform is null ? PlatformFreeRepositories.Label(repository.Url) : FindRepository(repository)?.RepositoryLabel(repository.Url) ?? repository.Url.AbsoluteUri)
         + (repository.Ref is { } @ref ? "@" + @ref : ""),
      _ => throw new ArgumentException($"lotsen cannot label a target of type {target.GetType().Name}.", nameof(target)),
   };

   /// <summary>
   /// Returns the links of the mentions in the prose of a tour of the target, which its platform's <see cref="IPullRequestPlatform.MentionUrl"/> or
   /// <see cref="IRepositoryPlatform.RepositoryMentionUrl"/> builds.
   /// </summary>
   /// <remarks>
   /// A target whose platform is not registered gets <see cref="MentionLinks.None"/>, so the prose still renders. So does a platform-free repository, because git alone knows no work items or
   /// pull requests, and its mentions render as their text.
   /// </remarks>
   public MentionLinks MentionsOf(TourTarget target) => target switch
   {
      PullRequestTarget pullRequest when Find(pullRequest) is { } platform => new MentionLinks((kind, id) => platform.MentionUrl(pullRequest.Url, kind, id)),
      RepositoryTarget repository when FindRepository(repository) is { } platform => new MentionLinks((kind, id) => platform.RepositoryMentionUrl(repository.Url, kind, id)),
      _ => MentionLinks.None,
   };

   private IPullRequestPlatform? Find(PullRequestTarget target) => _platforms.FirstOrDefault(platform => platform.Id == target.Platform);

   private IRepositoryPlatform? FindRepository(RepositoryTarget target) => _repositoryPlatforms.FirstOrDefault(platform => platform.Id == target.Platform);
}
