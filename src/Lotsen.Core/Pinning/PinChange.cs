using Lotsen.Git;
using Lotsen.Tours;

namespace Lotsen.Pinning;

/// <summary>The two pins that a remap compares: two iterations of a pull request, or two snapshots of a repository.</summary>
/// <remarks>The kind decides the commit of each side, and whether the new pin has a diff that a change stop's primary passage must touch, which only a pull request's pin has.</remarks>
internal closed record PinChange
{
   private PinChange()
   {
   }

   /// <summary>The head of the pin that the remap starts from.</summary>
   public abstract CommitId FromHead { get; }

   /// <summary>The head of the new pin.</summary>
   public abstract CommitId ToHead { get; }

   /// <summary>Returns the commit that a side names on the pin that the remap starts from, or null for a side that the pin lacks.</summary>
   public abstract CommitId? FromCommitOf(FileSide side);

   /// <summary>Returns the commit that a side names on the new pin, or null for a side that the pin lacks.</summary>
   public abstract CommitId? ToCommitOf(FileSide side);

   /// <summary>Returns the key of what the new pin's diff shows of a path at a side, or null when the new pin has no diff.</summary>
   public abstract FactKey.PinDiff? DiffOf(FileSide side, string path);

   /// <summary>An iteration update: from the tour's iteration to another iteration of its pull request.</summary>
   /// <param name="From">The tour's pin.</param>
   /// <param name="To">The new pin.</param>
   internal sealed record Iterations(TourPin.Pinned From, TourPin.Pinned To) : PinChange
   {
      public override CommitId FromHead => From.Head;

      public override CommitId ToHead => To.Head;

      public override CommitId? FromCommitOf(FileSide side) => From.CommitOf(side);

      public override CommitId? ToCommitOf(FileSide side) => To.CommitOf(side);

      public override FactKey.PinDiff DiffOf(FileSide side, string path) => new(To.MergeBase, To.Head, side, path);
   }

   /// <summary>A move: from a repository tour's snapshot to a snapshot of a later commit. Neither has a base side or a diff.</summary>
   /// <param name="From">The tour's pin.</param>
   /// <param name="To">The new pin.</param>
   internal sealed record Snapshots(TourPin.Snapshot From, TourPin.Snapshot To) : PinChange
   {
      public override CommitId FromHead => From.Head;

      public override CommitId ToHead => To.Head;

      public override CommitId? FromCommitOf(FileSide side) => From.CommitOf(side);

      public override CommitId? ToCommitOf(FileSide side) => To.CommitOf(side);

      public override FactKey.PinDiff? DiffOf(FileSide side, string path) => null;
   }
}
