using System.Diagnostics.CodeAnalysis;
using Lotsen.Credentials;
using Lotsen.Git;
using Lotsen.Tours;

namespace Lotsen.Pinning;

/// <summary>The key of a git fact: a question about named commits, whose answer never changes once read, because commits are immutable.</summary>
/// <remarks>Every git read that the remap and the iteration core decide from has a key here, so a fact that the memo holds stands for the read.</remarks>
internal abstract record FactKey
{
   private FactKey()
   {
   }

   /// <summary>A key whose answer has the type <typeparamref name="T"/>.</summary>
   internal abstract record Of<T> : FactKey;

   /// <summary>The merge base of a source commit and a target commit, or null when they share no history.</summary>
   internal sealed record MergeBaseOf(CommitId Source, CommitId Target) : Of<CommitId?>;

   /// <summary>The paths that differ between two commits, renames followed, as <see cref="GitRepository.GetChangedPathsAsync"/> lists them.</summary>
   internal sealed record ChangedPaths(CommitId From, CommitId To) : Of<IReadOnlyList<ChangedPath>>;

   /// <summary>The blob of a path at a commit, with its size and whether git treats it as binary, or null when the path names no file there.</summary>
   internal sealed record Blob(CommitId Commit, string Path) : Of<FileBlob?>;

   /// <summary>The lines of a path at a commit, or null when the path names no text file of a readable size there.</summary>
   internal sealed record Lines(CommitId Commit, string Path) : Of<FileLines?>;

   /// <summary>The hunks of git's diff of a file from one commit to another, or null when either path names no text file of a readable size at its commit.</summary>
   internal sealed record Hunks(CommitId From, string Path, CommitId To, string NewPath) : Of<IReadOnlyList<LineHunk>?>;

   /// <summary>What the diff of a pin from its merge base to its head shows of a path at one side.</summary>
   internal sealed record PinDiff(CommitId MergeBase, CommitId Head, FileSide Side, string Path) : Of<DiffTouch>;
}

/// <summary>What a pin's diff shows of one file at one side, from which <see cref="Touches"/> decides whether lines there touch the diff.</summary>
internal closed record DiffTouch
{
   private DiffTouch()
   {
   }

   /// <summary>Returns whether the lines touch the diff, the rule that the primary passage of a change stop follows.</summary>
   /// <remarks>
   /// The lines touch the diff when they overlap a changed region of their side, the same regions that the viewer's diff of the file shows. A region that is empty on the lines' side marks where the
   /// other side's lines go, and it touches lines that it lies inside or borders.
   /// </remarks>
   /// <param name="startLine">The first line, from 1.</param>
   /// <param name="endLine">The last line, not before <paramref name="startLine"/> and not after the file's last line at the side.</param>
   public abstract bool Touches(int startLine, int endLine);

   /// <summary>A file that the diff does not change at the side.</summary>
   internal sealed record Unchanged : DiffTouch
   {
      public override bool Touches(int startLine, int endLine) => false;
   }

   /// <summary>A binary file, or one larger than <see cref="ReadableFile.MaxSize"/>, which the viewer shows as changed throughout.</summary>
   internal sealed record Throughout : DiffTouch
   {
      public override bool Touches(int startLine, int endLine) => true;
   }

   /// <summary>The changed regions of a text file at the side, as the viewer's diff of the file shows them.</summary>
   /// <param name="Lines">The file's lines at the side, whose offsets the regions index.</param>
   /// <param name="Changed">The regions' start and end offsets at the side, in order. A region with equal ends marks where the other side's lines go.</param>
   internal sealed record Regions(FileLines Lines, IReadOnlyList<(int From, int To)> Changed) : DiffTouch
   {
      // An empty region touches lines that it lies inside or borders.
      public override bool Touches(int startLine, int endLine)
      {
         var (from, to) = (Lines.StartOf(startLine), Lines.EndOf(endLine));
         return Changed.Any(region => region.From == region.To ? from <= region.From && region.From <= to : region.From < to && region.To > from);
      }
   }
}

/// <summary>A set of git facts by key. A fact may be null, which is an answer too, as each <see cref="FactKey"/> says.</summary>
internal abstract class FactSet
{
   /// <summary>The set without facts.</summary>
   public static FactSet Empty { get; } = new Snapshot(new Dictionary<FactKey, object?>());

   /// <summary>Returns the set of the facts.</summary>
   public static FactSet Of(IReadOnlyDictionary<FactKey, object?> facts) => new Snapshot(facts);

   /// <summary>Returns the set of this set's facts and those of <paramref name="more"/>.</summary>
   public FactSet With(FactSet more) => new Union(this, more);

   /// <summary>Returns whether the set holds the fact, with the fact when it does.</summary>
   public bool TryGet<T>(FactKey.Of<T> key, [MaybeNullWhen(false)] out T value)
   {
      if (TryFind(key, out var found))
      {
         value = (T)found!;
         return true;
      }

      value = default;
      return false;
   }

   /// <summary>Returns whether the set holds the fact of the key, with the fact as an object when it does.</summary>
   internal abstract bool TryFind(FactKey key, out object? value);

   private sealed class Snapshot : FactSet
   {
      private readonly IReadOnlyDictionary<FactKey, object?> _facts;

      public Snapshot(IReadOnlyDictionary<FactKey, object?> facts)
      {
         _facts = facts;
      }

      internal override bool TryFind(FactKey key, out object? value) => _facts.TryGetValue(key, out value);
   }

   private sealed class Union : FactSet
   {
      private readonly FactSet _first;
      private readonly FactSet _second;

      public Union(FactSet first, FactSet second)
      {
         _first = first;
         _second = second;
      }

      internal override bool TryFind(FactKey key, out object? value) => _first.TryFind(key, out value) || _second.TryFind(key, out value);
   }
}

/// <summary>Builds a <see cref="FactSet"/> fact by fact, with each fact typed by its key.</summary>
internal sealed class FactSetBuilder
{
   private readonly Dictionary<FactKey, object?> _facts = [];

   /// <summary>Adds the fact, or replaces the one of the same key, and returns the builder.</summary>
   public FactSetBuilder Add<T>(FactKey.Of<T> key, T value)
   {
      _facts[key] = value;
      return this;
   }

   /// <summary>Returns the set of the facts added so far.</summary>
   public FactSet Build() => FactSet.Of(new Dictionary<FactKey, object?>(_facts));
}

/// <summary>Reads facts from a <see cref="FactSet"/> and records each key that the set lacks, so a pass of the pure core continues past a miss and collects every miss it can reach.</summary>
internal sealed class FactReader
{
   private readonly FactSet _facts;
   private readonly HashSet<FactKey> _missing = [];

   public FactReader(FactSet facts)
   {
      _facts = facts;
   }

   /// <summary>The keys that the set lacked, in no order.</summary>
   public IReadOnlySet<FactKey> Missing => _missing;

   /// <summary>Returns whether the set holds the fact, with the fact when it does. A miss records the key.</summary>
   public bool TryGet<T>(FactKey.Of<T> key, [MaybeNullWhen(false)] out T value)
   {
      if (_facts.TryGet(key, out value))
      {
         return true;
      }

      _missing.Add(key);
      return false;
   }
}

/// <summary>A pass of the pure core: decided, or in need of facts that the set lacked.</summary>
/// <typeparam name="T">The decision's value.</typeparam>
internal closed record Decision<T>
{
   private Decision()
   {
   }

   /// <summary>A pass that decided.</summary>
   internal sealed record Decided(T Value) : Decision<T>;

   /// <summary>A pass that needs these facts first. Each later pass may need more, whose keys depend on these facts.</summary>
   internal sealed record NeedsFacts(IReadOnlySet<FactKey> Missing) : Decision<T>;

   /// <summary>Returns the decision of a pass that read through <paramref name="facts"/>: the value when nothing was missing, or the misses.</summary>
   public static Decision<T> Of(FactReader facts, Func<T> value) => facts.Missing.Count > 0 ? new NeedsFacts(facts.Missing) : new Decided(value());
}

/// <summary>How a fact scope brings the commits it names into the clone: a sync, or a fetch only when the clone lacks one of them.</summary>
internal enum FetchPolicy
{
   /// <summary>Fetch the remote's branches and every commit that the clone still lacks, as <see cref="CloneCache.OpenAsync"/> describes.</summary>
   Sync,

   /// <summary>Fetch only when the clone lacks one of the commits, as <see cref="CloneCache.OpenAsync"/> describes.</summary>
   WhenMissing,
}

/// <summary>Provides git facts: the memo of the facts read so far, and scopes that read more from a clone.</summary>
internal interface IGitFacts
{
   /// <summary>The facts that the memo holds, read without git.</summary>
   /// <remarks>An evicted fact disappears from the set, so a pass over it may need the fact again.</remarks>
   FactSet Known { get; }

   /// <summary>Returns a scope over the clone of the remote, which holds a clone use from now until it is disposed.</summary>
   /// <param name="remote">The remote whose clone the scope reads.</param>
   /// <param name="commits">The commits that the scope's reads name, which its first git read brings into the clone as <paramref name="policy"/> says.</param>
   /// <param name="policy">How the scope's first git read brings the commits into the clone.</param>
   /// <exception cref="UnsupportedRemoteException">The remote's URL is not one that <see cref="GitRemote"/> allows.</exception>
   IFactScope Open(TourRemote remote, IReadOnlyCollection<CommitId> commits, FetchPolicy policy);

   /// <summary>Returns whether a scope downloaded the contents of the files that differ between the commits into the remote's current clone.</summary>
   bool Downloaded(Uri remote, CommitId mergeBase, CommitId head);
}

/// <summary>A clone use with reads of git facts from the clone. It opens the clone at most once, on its first call that needs git, and ends the use when disposed.</summary>
/// <remarks>Store what refers to the clone before the scope is disposed, so a prune always finds the clone in use or referred to.</remarks>
internal interface IFactScope : IAsyncDisposable
{
   /// <summary>The facts of the keys that the scope was asked to read, which it keeps whatever the memo evicts, with the rest of the memo.</summary>
   FactSet Known { get; }

   /// <summary>Opens the clone now and brings the scope's commits into it as its policy says, unless an earlier call opened it.</summary>
   /// <exception cref="GitException">Git failed.</exception>
   /// <exception cref="AccessTokenUnavailableException">The clone needed a fetch, the remote asked for authentication, and the remote's git credential has none.</exception>
   Task OpenCloneAsync(CancellationToken cancellationToken);

   /// <summary>Reads the facts of the keys from the clone, adds them to <see cref="Known"/> and the memo, and may add the facts that it read on the way.</summary>
   /// <exception cref="GitException">Git failed.</exception>
   /// <exception cref="AccessTokenUnavailableException">The clone needed a fetch, the remote asked for authentication, and the remote's git credential has none.</exception>
   Task ReadAsync(IReadOnlyCollection<FactKey> keys, CancellationToken cancellationToken);

   /// <summary>Downloads the contents of the files that differ between the commits into the clone, as <see cref="GitRepository.DownloadChangedFilesAsync"/> does, unless the scope downloaded them already.</summary>
   /// <exception cref="GitException">Git failed.</exception>
   /// <exception cref="AccessTokenUnavailableException">The clone needed a fetch, the remote asked for authentication, and the remote's git credential has none.</exception>
   Task WarmAsync(CommitId mergeBase, CommitId head, CancellationToken cancellationToken);
}

/// <summary>Runs passes of the pure core and reads the facts that each lacks, until a pass decides.</summary>
internal static class FactRounds
{
   // A fact's key depends on at most three facts before it: the changed paths, then the hunks or a blob, then the moved lines' diff. The bound is a guard for a pass that never decides.
   private const int MaxRounds = 8;

   /// <summary>Returns the value of the first pass that decides, after reading what each pass before it lacked.</summary>
   /// <param name="scope">The scope whose facts the passes read and which reads the missing ones.</param>
   /// <param name="pass">One pass of the pure core over the facts.</param>
   /// <param name="cancellationToken">Stops git.</param>
   /// <exception cref="InvalidOperationException">A pass asked for a fact that the scope had read, or the passes did not decide within the bound. Both are faults of the core.</exception>
   /// <exception cref="GitException">Git failed.</exception>
   /// <exception cref="AccessTokenUnavailableException">The clone needed a fetch, the remote asked for authentication, and the remote's git credential has none.</exception>
   public static async Task<T> SettleAsync<T>(IFactScope scope, Func<FactSet, Decision<T>> pass, CancellationToken cancellationToken)
   {
      var read = new HashSet<FactKey>();
      for (var round = 0; round < MaxRounds; round++)
      {
         switch (pass(scope.Known))
         {
            case Decision<T>.Decided { Value: var value }:
               return value;
            case Decision<T>.NeedsFacts { Missing: var missing }:
               if (missing.All(read.Contains))
               {
                  throw new InvalidOperationException("A pass of the iteration core asked for facts that the scope had read already.");
               }

               read.UnionWith(missing);
               await scope.ReadAsync(missing, cancellationToken);
               break;
         }
      }

      throw new InvalidOperationException($"The iteration core did not decide within {MaxRounds} rounds of facts.");
   }
}
