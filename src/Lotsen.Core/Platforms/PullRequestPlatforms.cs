using Lotsen.Prose;
using Lotsen.Tours;

namespace Lotsen.Platforms;

/// <summary>Provides the server's registered platforms, found by a target or by a pull request URL.</summary>
internal sealed class PullRequestPlatforms
{
   private readonly IReadOnlyList<IPullRequestPlatform> _platforms;

   /// <exception cref="ArgumentException">Two platforms have the same ID.</exception>
   public PullRequestPlatforms(IEnumerable<IPullRequestPlatform> platforms)
   {
      _platforms = [.. platforms];
      if (_platforms.GroupBy(platform => platform.Id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1) is { } duplicate)
      {
         throw new ArgumentException($"Two platforms have the ID '{duplicate.Key}'.", nameof(platforms));
      }
   }

   /// <summary>The URL forms that every platform accepts, in registration order, written for a refusal.</summary>
   public string ExpectedForms => string.Join("; or ", _platforms.Select(platform => platform.ExpectedForms));

   /// <summary>Returns the platform of a target, which resolves its pull request and supplies the credential of its remotes.</summary>
   /// <remarks>
   /// Every site that reaches a tour's platform or builds a <see cref="Pinning.TourRemote"/> for it takes the platform from here, so a target whose platform lotsen does not know has one served
   /// reason.
   /// </remarks>
   /// <exception cref="PlatformException">No registered platform has the target's platform ID.</exception>
   /// <exception cref="ArgumentException">The target is not a pull request. No other kind of target exists, so this is a fault of lotsen.</exception>
   public IPullRequestPlatform Of(TourTarget target) => target switch
   {
      PullRequestTarget pullRequest => Find(pullRequest) ?? throw new PlatformException(
         $"lotsen does not know the platform '{pullRequest.Platform}' of the pull request {pullRequest.Url.AbsoluteUri}, so it cannot reach the pull request or its repository. Run a lotsen build that supports that platform, then try again."),
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

   /// <summary>Returns whether a tour's target names the same pull request as the target.</summary>
   /// <returns>True when both have one platform ID and that platform's <see cref="IPullRequestPlatform.IsSamePullRequest"/> says so, or false when no registered platform has the ID.</returns>
   public bool IsSamePullRequest(TourTarget tourTarget, PullRequestTarget target) =>
      tourTarget is PullRequestTarget pullRequest && pullRequest.Platform == target.Platform && Find(target) is { } platform && platform.IsSamePullRequest(pullRequest.Url, target.Url);

   /// <summary>Returns the short name of a target: its platform's label, or the URL when its platform is not registered or does not recognize the URL.</summary>
   public string Label(TourTarget target) => target switch
   {
      PullRequestTarget pullRequest => Find(pullRequest)?.Label(pullRequest.Url) ?? pullRequest.Url.AbsoluteUri,
      _ => throw new ArgumentException($"lotsen cannot label a target of type {target.GetType().Name}.", nameof(target)),
   };

   /// <summary>Returns the links of the mentions in the prose of a tour of the target, which its platform's <see cref="IPullRequestPlatform.MentionUrl"/> builds.</summary>
   /// <remarks>A target whose platform is not registered gets <see cref="MentionLinks.None"/>, so its prose still renders.</remarks>
   public MentionLinks MentionsOf(TourTarget target) => target is PullRequestTarget pullRequest && Find(pullRequest) is { } platform
      ? new MentionLinks((kind, id) => platform.MentionUrl(pullRequest.Url, kind, id))
      : MentionLinks.None;

   private IPullRequestPlatform? Find(PullRequestTarget target) => _platforms.FirstOrDefault(platform => platform.Id == target.Platform);
}
