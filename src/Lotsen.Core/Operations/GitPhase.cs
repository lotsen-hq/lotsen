using Lotsen.Git;
using Lotsen.Tours;

namespace Lotsen.Operations;

/// <summary>The git phase of a write, which reads the tour's clone outside the lock and records the pin that it read at.</summary>
/// <remarks><see cref="TourWriter"/> refuses the write when the latest tour's pin differs from the recorded one.</remarks>
internal sealed class GitPhase
{
   private readonly Tour _tour;
   private readonly CancellationToken _cancellationToken;
   private TourPin.Pinned? _used;
   private FieldPath? _field;

   internal GitPhase(Tour tour, CancellationToken cancellationToken)
   {
      _tour = tour;
      _cancellationToken = cancellationToken;
   }

   /// <summary>Returns the read tour's pin, and records that the write relies on it.</summary>
   /// <param name="field">The input's field whose check relies on the pin, which a refusal after a pin change names. The last field recorded wins.</param>
   /// <returns>The pin, or <see cref="OperationErrorCode.TourNotPinned"/> when the tour is not pinned.</returns>
   public OperationResult<TourPin.Pinned> Pin(FieldPath field)
   {
      if (_tour.Pin is not TourPin.Pinned pinned)
      {
         return OperationError.TourNotPinned(_tour);
      }

      _used = pinned;
      _field = field;
      return pinned;
   }

   /// <summary>Downloads into the read tour's clone the files that the phase reads, as <see cref="FileOperations.DownloadFilesAsync"/> does, so the reads after it download nothing.</summary>
   /// <param name="files">The operations that open the tour's clone.</param>
   /// <param name="reads">The files that the phase reads, each with its side.</param>
   /// <remarks>
   /// The write's token stops the wait for the download, not the download. A tour that is not pinned downloads nothing, and the phase's first read refuses it. The download does not record
   /// the pin, because the write stores nothing that the download read.
   /// </remarks>
   public async Task DownloadAsync(FileOperations files, IEnumerable<(FileSide Side, string Path)> reads)
   {
      IReadOnlyCollection<(FileSide Side, string Path)> distinct = [.. reads.Distinct()];
      if (_tour.Pin is TourPin.Pinned pinned && distinct.Count > 0)
      {
         await files.DownloadFilesAsync(_tour, pinned, distinct, _cancellationToken);
      }
   }

   /// <summary>Runs a query on the read tour's clone at its pin, as <see cref="FileOperations.OnRepositoryAsync"/> does, and records the pin as <see cref="Pin"/> does.</summary>
   public async Task<OperationResult<T>> OnRepositoryAsync<T>(FileOperations files, FieldPath field, Func<GitRepository, TourPin.Pinned, Task<OperationResult<T>>> query)
      where T : class
   {
      var pin = Pin(field);
      if (pin.Value is not { } pinned)
      {
         return pin.Error!;
      }

      return await files.OnRepositoryAsync(_tour, pinned, repository => query(repository, pinned), _cancellationToken);
   }

   // Returns the refusal when the latest tour's pin is not the one that the phase read at.
   internal OperationError? Recheck(Tour latest) => _used is { } used && latest.Pin != used ? OperationError.PinChanged(_field!.Value) : null;
}

/// <summary>What a git phase gathered, with the checks on the latest tour that what it read still holds, and the clone use that the write keeps until it has stored.</summary>
/// <param name="Value">What the phase gathered.</param>
/// <param name="Basis">The checks on the latest tour, each returning the refusal when what the phase read no longer holds, or null.</param>
/// <param name="Held">What the writer disposes after the store, such as a clone use, or null for nothing.</param>
internal sealed record Gathered<T>(T Value, IReadOnlyList<Func<Tour, OperationError?>> Basis, IDisposable? Held = null)
{
   /// <summary>Returns the gathering with one more check on the latest tour: the write is refused with <paramref name="refusal"/> when <paramref name="broken"/> holds.</summary>
   public Gathered<T> Unless(Func<Tour, bool> broken, OperationError refusal) => Unless(latest => broken(latest) ? refusal : null);

   /// <summary>Returns the gathering with one more check on the latest tour, which returns the refusal or null.</summary>
   public Gathered<T> Unless(Func<Tour, OperationError?> check) => this with { Basis = [.. Basis, check] };

   internal OperationError? Recheck(Tour latest) => Basis.Select(check => check(latest)).FirstOrDefault(refusal => refusal is not null);
}

/// <summary>Builds a <see cref="Gathered{T}"/>.</summary>
internal static class Gathered
{
   /// <summary>Returns a gathering of the value with no check on the latest tour.</summary>
   public static Gathered<T> Of<T>(T value, IDisposable? held = null) => new(value, [], held);
}
