using System.Text.Json;
using Lotsen.Hosting;
using Lotsen.Json;

namespace Lotsen.Tours;

/// <summary>
/// Persists tour documents: plain storage, which knows no rule and no field of a tour. The <c>TourWriter</c> is its only caller, and it decodes every document that the store returns.
/// </summary>
/// <remarks>
/// The store takes no lock. Its caller runs every create, update and delete one at a time, so a change always receives the latest stored document and no update is lost.
/// </remarks>
internal interface ITourStore
{
   /// <summary>Stores the document of a new tour, under a new ID that no stored tour has.</summary>
   /// <param name="document">Returns the document for the new ID.</param>
   /// <returns>The stored document.</returns>
   TourDocument Create(Func<TourId, TourDocument> document);

   /// <summary>Returns the document of the tour with this ID, as the file holds it.</summary>
   TourRead Read(TourId id);

   /// <summary>Returns the document of every stored tour, or why it could not be read, in no particular order.</summary>
   /// <remarks>A tour that cannot be read is one entry, so one damaged file cannot hide the other tours.</remarks>
   IReadOnlyList<TourRead> ReadAll();

   /// <summary>Applies a change to the document of the tour with this ID and stores the result.</summary>
   /// <param name="id">The ID of the tour to change.</param>
   /// <param name="change">Returns the next document from the latest stored one. When it returns the document it was given, the store writes nothing. An exception that it throws leaves the file as it is.</param>
   /// <returns>The stored document, or the read that found no document to change, as after <see cref="Delete"/>. Nothing was changed or written then, so an update never brings a deleted tour back.</returns>
   TourRead Update(TourId id, Func<TourDocument, TourDocument> change);

   /// <summary>Removes the file of the tour with this ID for good.</summary>
   /// <returns>The document as it was stored, or the read that found no document. Nothing was removed then.</returns>
   TourRead Delete(TourId id);

   /// <summary>Returns the absolute path of the file that holds the tour with this ID, whether or not the file exists.</summary>
   string PathOf(TourId id);
}

/// <summary>What a read of a tour's file found.</summary>
/// <param name="Id">The tour's ID.</param>
internal closed record TourRead(TourId Id)
{
   /// <summary>No file has the ID.</summary>
   internal sealed record Missing(TourId Id) : TourRead(Id);

   /// <summary>The file's document, as the file holds it.</summary>
   /// <param name="Document">The document, which no rule has checked.</param>
   internal sealed record Found(TourId Id, TourDocument Document) : TourRead(Id);

   /// <summary>A file whose top-level <c>schemaVersion</c> names another layout than <see cref="TourDocument.CurrentSchemaVersion"/>, which this build can never read.</summary>
   internal sealed record OtherSchema(TourId Id) : TourRead(Id);

   /// <summary>A file that cannot be opened, is malformed or holds a value that a type of the tour refuses.</summary>
   /// <param name="Cause">What failed. Its message may name the file.</param>
   internal sealed record Unreadable(TourId Id, Exception Cause) : TourRead(Id);
}

/// <summary>
/// Stores each tour's document as one JSON file, <c>tours/&lt;id&gt;.json</c> in the data directory.
/// </summary>
/// <remarks>
/// Writes are atomic, as <see cref="UserOnlyStorage.WriteFileAtomically"/> describes, so reads take no lock. The store takes no lock to write either, so a data directory has one writer: its
/// temporary file's name holds only the process ID, and two writes of one tour at once would share it. The <see cref="ClaimLock"/> keeps a second server process off the data directory, and
/// within the server the <c>TourWriter</c> builds its own store and runs its writes one at a time.
/// </remarks>
internal sealed class FileTourStore : ITourStore
{
   private const string Extension = ".json";

   private readonly DataDirectory _dataDirectory;

   public FileTourStore(DataDirectory dataDirectory)
   {
      _dataDirectory = dataDirectory;
   }

   public TourDocument Create(Func<TourId, TourDocument> document)
   {
      EnsureToursDirectory();
      while (true)
      {
         var id = TourId.New();

         // An 80-bit ID repeats only by chance, but a repeat would overwrite another tour.
         if (File.Exists(PathOf(id)))
         {
            continue;
         }

         var created = document(id);
         Write(id, created);
         return created;
      }
   }

   public TourRead Read(TourId id)
   {
      try
      {
         // Sharing delete lets a writer on Windows rename over the file while this read holds it open.
         using var stream = new FileStream(PathOf(id), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
         return JsonSerializer.Deserialize(stream, LotsenJsonContext.Default.TourDocument) switch
         {
            null => new TourRead.Unreadable(id, new InvalidDataException($"Tour {id} holds null.")),
            { SchemaVersion: not TourDocument.CurrentSchemaVersion } => new TourRead.OtherSchema(id),
            var document => new TourRead.Found(id, document),
         };
      }
      catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
      {
         return new TourRead.Missing(id);
      }
      catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
      {
         return NamesOtherSchema(id) ? new TourRead.OtherSchema(id) : new TourRead.Unreadable(id, exception);
      }
   }

   public IReadOnlyList<TourRead> ReadAll()
   {
      if (!Directory.Exists(_dataDirectory.ToursPath))
      {
         return [];
      }

      var reads = new List<TourRead>();
      foreach (var path in Directory.EnumerateFiles(_dataDirectory.ToursPath, $"*{Extension}"))
      {
         // The ID check skips files that are not tours and keeps every read inside the tours directory.
         if (TourId.TryParse(Path.GetFileNameWithoutExtension(path), out var id) && Read(id) is not TourRead.Missing and var read)
         {
            reads.Add(read);
         }
      }

      return reads;
   }

   public TourRead Update(TourId id, Func<TourDocument, TourDocument> change)
   {
      var read = Read(id);
      if (read is not TourRead.Found { Document: var current })
      {
         return read;
      }

      var changed = change(current);
      if (ReferenceEquals(changed, current))
      {
         return read;
      }

      Write(id, changed);
      return new TourRead.Found(id, changed);
   }

   public TourRead Delete(TourId id)
   {
      var read = Read(id);
      if (read is TourRead.Found)
      {
         File.Delete(PathOf(id));
      }

      return read;
   }

   public string PathOf(TourId id) => Path.GetFullPath(Path.Combine(_dataDirectory.ToursPath, id.Value + Extension));

   private void EnsureToursDirectory()
   {
      _dataDirectory.EnsureExists();
      UserOnlyStorage.CreateDirectory(_dataDirectory.ToursPath);
   }

   private void Write(TourId id, TourDocument document) =>
      UserOnlyStorage.WriteFileAtomically(PathOf(id), stream => JsonSerializer.Serialize(stream, document, LotsenJsonContext.Default.TourDocument));

   // Returns whether the file's top-level schemaVersion is a number other than the current one. The layout can differ with the version, so the file is read without the tour's types.
   private bool NamesOtherSchema(TourId id)
   {
      try
      {
         using var stream = new FileStream(PathOf(id), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
         using var document = JsonDocument.Parse(stream);
         return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("schemaVersion", out var version)
            && version.ValueKind == JsonValueKind.Number
            && (!version.TryGetInt32(out var number) || number != TourDocument.CurrentSchemaVersion);
      }
      catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
      {
         return false;
      }
   }
}
