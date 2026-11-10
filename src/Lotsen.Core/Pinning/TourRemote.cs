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
   /// <param name="tour">The tour. A tour that is not pinned uses none.</param>
   /// <param name="credential">The credential of the tour's platform, or null for none, as for a prune, which runs no git against the remotes.</param>
   public static IReadOnlyList<TourRemote> Of(Tour tour, IGitCredential? credential)
   {
      var remotes = new List<TourRemote>();
      foreach (var pin in new[] { tour.Pin as TourPin.Pinned, tour.AvailableIteration?.Pin })
      {
         if (pin is not null && !remotes.Exists(remote => remote.Url == pin.RemoteUrl))
         {
            remotes.Add(OfPin(pin, credential));
         }
      }

      return remotes;
   }

   /// <summary>Returns the remote of a pin, which the iteration update opens for the available iteration's pin and the file operations open for the tour's pin.</summary>
   public static TourRemote OfPin(TourPin.Pinned pin, IGitCredential? credential) => new(new GitRemote(pin.RemoteUrl, credential));

   /// <summary>Returns the remote of an iteration that the platform resolved, which the first pin and the iteration check open, and which a pin of the iteration stores.</summary>
   public static TourRemote OfIteration(ResolvedPullRequest iteration, IGitCredential? credential) => new(new GitRemote(iteration.RemoteUrl, credential));
}
