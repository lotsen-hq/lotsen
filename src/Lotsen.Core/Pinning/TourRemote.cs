using Lotsen.Credentials;
using Lotsen.Git;
using Lotsen.Platforms;
using Lotsen.Tours;

namespace Lotsen.Pinning;

/// <summary>A remote that a tour uses: the only kind of remote whose clone the clone cache opens.</summary>
/// <remarks>
/// Its factories are the one answer to which remotes a tour uses. A prune keeps the clones of the remotes that <see cref="Of"/> lists, and every opener takes its remote from a factory whose
/// remote that list holds once the opener stored its result. A new kind of remote that a tour can use, such as a fork's, gets its factory here and its place in <see cref="Of"/>.
/// </remarks>
internal sealed class TourRemote
{
   private TourRemote(GitRemote remote)
   {
      Remote = remote;
   }

   /// <summary>The remote, with the credential that git presents to it.</summary>
   public GitRemote Remote { get; }

   /// <summary>The remote's URL, which names its clone.</summary>
   public Uri Url => Remote.Url;

   /// <summary>Returns every remote that the tour uses: its pin's, then its available iteration's when that differs.</summary>
   /// <param name="tour">The tour. A tour that pins or whose pin failed uses none.</param>
   /// <param name="credential">The credential of the tour's platform, or null for none, as for a prune, which runs no git against the remotes.</param>
   public static IReadOnlyList<TourRemote> Of(Tour tour, IGitCredential? credential)
   {
      var pinUrl = tour.Pin switch
      {
         TourPin.Pinned pinned => pinned.RemoteUrl,
         TourPin.Snapshot snapshot => snapshot.RemoteUrl,
         TourPin.Pinning or TourPin.Failed => null,
      };
      var remotes = new List<TourRemote>();
      foreach (var url in new[] { pinUrl, tour.AvailableIteration?.Pin.RemoteUrl })
      {
         if (url is not null && !remotes.Exists(remote => remote.Url == url))
         {
            remotes.Add(new TourRemote(new GitRemote(url, credential)));
         }
      }

      return remotes;
   }

   /// <summary>Returns the remote of a pin, which the iteration update opens for the available iteration's pin and the file operations open for the tour's pin.</summary>
   public static TourRemote OfPin(TourPin.Pinned pin, IGitCredential? credential) => new(new GitRemote(pin.RemoteUrl, credential));

   /// <summary>Returns the remote of a snapshot, which the file operations open for a repository tour's pin.</summary>
   public static TourRemote OfSnapshot(TourPin.Snapshot pin, IGitCredential? credential) => new(new GitRemote(pin.RemoteUrl, credential));

   /// <summary>Returns the remote of a repository that its platform resolved, which the first pin of a repository tour opens, and which the tour's snapshot stores.</summary>
   public static TourRemote OfRepository(ResolvedRepository repository, IGitCredential? credential) => new(new GitRemote(repository.RemoteUrl, credential));

   /// <summary>Returns the remote of an iteration that the platform resolved, which the first pin and the iteration check open, and which a pin of the iteration stores.</summary>
   public static TourRemote OfIteration(ResolvedPullRequest iteration, IGitCredential? credential) => new(new GitRemote(iteration.RemoteUrl, credential));
}
