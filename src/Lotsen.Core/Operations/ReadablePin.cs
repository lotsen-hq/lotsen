using Lotsen.Credentials;
using Lotsen.Git;
using Lotsen.Pinning;
using Lotsen.Tours;

namespace Lotsen.Operations;

/// <summary>The commits of a tour's pin that its files are read at: a pull request's merge base and head, or a snapshot's one commit as its head.</summary>
/// <remarks>Only <see cref="Diff"/> gives a check the pin of a pull request's iteration, so a check that reads the diff cannot run on a snapshot.</remarks>
internal sealed class ReadablePin
{
   private readonly Func<IGitCredential?, TourRemote> _remote;

   private ReadablePin(TourPin pin, Func<IGitCredential?, TourRemote> remote, CommitId head, TourPin.Pinned? diff)
   {
      _remote = remote;
      Pin = pin;
      Head = head;
      Diff = diff;
   }

   /// <summary>The tour's pin that the commits come from.</summary>
   public TourPin Pin { get; }

   /// <summary>The commit of the head side.</summary>
   public CommitId Head { get; }

   /// <summary>The pin of a pull request's iteration, whose merge base and head the diff compares, or null for a snapshot, which has no diff.</summary>
   public TourPin.Pinned? Diff { get; }

   /// <summary>The commits that the pin's clone must hold.</summary>
   public IReadOnlyList<CommitId> Commits => Diff is { } diff ? [diff.MergeBase, Head] : [Head];

   /// <summary>Returns the commits of a pull request's iteration.</summary>
   public static ReadablePin Of(TourPin.Pinned pin) => new(pin, credential => TourRemote.OfPin(pin, credential), pin.Head, pin);

   /// <summary>Returns the commits of a pin.</summary>
   /// <returns>The pin's commits, or null for a pin that names none yet.</returns>
   public static ReadablePin? Of(TourPin pin) => pin switch
   {
      TourPin.Pinned pinned => Of(pinned),
      TourPin.Snapshot snapshot => new(snapshot, credential => TourRemote.OfSnapshot(snapshot, credential), snapshot.Head, null),
      TourPin.Pinning or TourPin.Failed => null,
   };

   /// <summary>Returns the commit that a side names.</summary>
   /// <returns>The side's commit, or null for the base of a snapshot.</returns>
   public CommitId? CommitOf(FileSide side) => side == FileSide.Head ? Head : Diff?.MergeBase;

   /// <summary>Returns the remote that holds the pin's commits, with the credential of the tour's platform.</summary>
   public TourRemote Remote(IGitCredential? credential) => _remote(credential);
}
