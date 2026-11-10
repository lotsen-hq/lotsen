using Lotsen.Hosting;
using Lotsen.Tours;

namespace Lotsen.Tests;

/// <summary>Covers how the tour store keeps tour documents as files in the data directory: plain persistence, which knows no rule of a tour.</summary>
[Trait("Category", "Store")]
public sealed class TourStoreTests : IDisposable
{
   private static readonly PullRequestTarget PullRequest = new("test", new Uri("https://platform.test/shop/pull/42"));
   private static readonly DateTimeOffset Now = new(2026, 10, 22, 9, 30, 0, TimeSpan.Zero);

   private readonly DataDirectory _dataDirectory = new(Path.Combine(Directory.CreateTempSubdirectory("lotsen-tests-").FullName, "data"));

   private string ToursPath => Path.Combine(_dataDirectory.Path, "tours");

   [Fact]
   public void Created_document_reads_back_as_it_was_stored()
   {
      var store = NewStore();

      var created = Create(store);

      var read = Assert.IsType<TourRead.Found>(NewStore().Read(created.Tour.Id));
      Assert.Equal(TourDocument.CurrentSchemaVersion, read.Document.SchemaVersion);
      Assert.Equivalent(created.Tour, read.Document.Tour, strict: true);
      Assert.Empty(read.Document.ReviewMarks);
   }

   [Fact]
   public void Update_that_returns_the_document_it_was_given_stores_nothing()
   {
      var store = NewStore();
      var id = Create(store).Tour.Id;
      var before = File.ReadAllBytes(PathOf(id));

      var read = store.Update(id, document => document);

      Assert.IsType<TourRead.Found>(read);
      Assert.Equal(before, File.ReadAllBytes(PathOf(id)));
   }

   [Fact]
   public void Tours_are_readable_by_the_user_only()
   {
      if (OperatingSystem.IsWindows())
      {
         Assert.Skip("Unix file modes do not apply on Windows.");
         return;
      }

      var id = Create(NewStore()).Tour.Id;

      Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(ToursPath));
      Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(PathOf(id)));
   }

   [Fact]
   public void Write_that_fails_part_way_leaves_the_previous_document_intact()
   {
      var store = NewStore();
      var created = Create(store);
      var before = File.ReadAllBytes(PathOf(created.Tour.Id));

      Assert.ThrowsAny<NotSupportedException>(() => store.Update(created.Tour.Id, _ => Next(created, tour => tour with { Target = new UnregisteredTarget() })));

      Assert.Equal(before, File.ReadAllBytes(PathOf(created.Tour.Id)));
      Assert.Single(Directory.GetFiles(ToursPath));
   }

   [Fact]
   public void Change_that_throws_stores_nothing()
   {
      var store = NewStore();
      var id = Create(store).Tour.Id;
      var before = File.ReadAllBytes(PathOf(id));

      Assert.Throws<InvalidOperationException>(() => store.Update(id, _ => throw new InvalidOperationException("The change refuses.")));

      Assert.Equal(before, File.ReadAllBytes(PathOf(id)));
   }

   /// <summary>A crash between the write and the rename leaves a partial temporary file beside the tour.</summary>
   [Fact]
   public void Partial_file_left_by_a_crash_is_not_read_as_a_tour()
   {
      var store = NewStore();
      var created = Create(store);
      var tourPath = PathOf(created.Tour.Id);
      var content = File.ReadAllText(tourPath);
      File.WriteAllText($"{tourPath}.{Environment.ProcessId}.tmp", content[..(content.Length / 2)]);
      File.WriteAllText($"{tourPath}.99999.tmp", content[..(content.Length / 3)]);

      Assert.IsType<TourRead.Found>(store.Read(created.Tour.Id));
      Assert.Equal([created.Tour.Id], store.ReadAll().Select(read => read.Id));

      store.Update(created.Tour.Id, _ => Next(created, tour => tour with { Familiarity = Familiarity.New }));
      Assert.Equal(Familiarity.New, Found(NewStore(), created.Tour.Id).Tour.Familiarity);
   }

   [Fact]
   public void Read_all_returns_every_stored_tour_and_ignores_other_files()
   {
      var store = NewStore();
      TourId[] ids = [Create(store).Tour.Id, Create(store).Tour.Id, Create(store).Tour.Id];
      File.WriteAllText(Path.Combine(ToursPath, "notes.txt"), "not a tour");
      File.WriteAllText(Path.Combine(ToursPath, "Not-An-Id.json"), "{}");
      File.WriteAllText($"{PathOf(ids[0])}.1234.tmp", "{");

      var reads = NewStore().ReadAll();

      Assert.All(reads, read => Assert.IsType<TourRead.Found>(read));
      Assert.Equal(ids.Select(id => id.Value).Order(), reads.Select(read => read.Id.Value).Order());
   }

   [Fact]
   public void Read_all_is_empty_before_the_first_tour() => Assert.Empty(NewStore().ReadAll());

   [Fact]
   public void Read_all_names_the_unreadable_files_beside_the_readable_ones()
   {
      var store = NewStore();
      var readable = Create(store).Tour.Id;
      var unreadable = Create(store).Tour.Id;
      File.WriteAllText(PathOf(unreadable), "{");

      var reads = NewStore().ReadAll().ToDictionary(read => read.Id);

      Assert.IsType<TourRead.Found>(reads[readable]);
      Assert.IsType<TourRead.Unreadable>(reads[unreadable]);
   }

   /// <summary>A file of another schema version can also have another layout, so a case removes a property that the current layout needs.</summary>
   [Theory]
   [InlineData(-1, null, true)]
   [InlineData(1, null, true)]
   [InlineData(-1, ",\"threads\":[]", true)]
   [InlineData(0, ",\"threads\":[]", false)]
   [InlineData(null, null, false)]
   [InlineData(0, "truncated", false)]
   public void File_of_another_schema_version_reads_apart_from_the_other_unreadable_files(int? versionOffset, string? removed, bool otherSchema)
   {
      var store = NewStore();
      var id = Create(store).Tour.Id;
      var current = $"\"schemaVersion\":{TourDocument.CurrentSchemaVersion}";
      var content = File.ReadAllText(PathOf(id));
      Assert.Contains(current, content);
      content = content.Replace(current, versionOffset is { } offset ? $"\"schemaVersion\":{TourDocument.CurrentSchemaVersion + offset}" : $"\"schemaVersion\":\"{TourDocument.CurrentSchemaVersion}\"");
      content = removed switch
      {
         null => content,
         "truncated" => content[..(content.Length / 2)],
         _ => content.Replace(removed, ""),
      };
      File.WriteAllText(PathOf(id), content);

      var read = NewStore().Read(id);

      Assert.Equal(otherSchema, read is TourRead.OtherSchema);
      Assert.Equal(!otherSchema, read is TourRead.Unreadable);
   }

   [Fact]
   public void Tour_file_that_cannot_be_opened_is_unreadable_and_leaves_the_other_tours_readable()
   {
      if (OperatingSystem.IsWindows())
      {
         Assert.Skip("Unix file modes do not apply on Windows.");
         return;
      }

      var store = NewStore();
      var closed = Create(store).Tour.Id;
      var open = Create(store).Tour.Id;
      File.SetUnixFileMode(PathOf(closed), UnixFileMode.None);

      Assert.IsType<TourRead.Unreadable>(store.Read(closed));
      Assert.IsType<TourRead.Found>(store.ReadAll().Single(read => read.Id == open));
   }

   [Fact]
   public void Each_tour_gets_its_own_id()
   {
      var store = NewStore();

      var ids = Enumerable.Range(0, 20).Select(_ => Create(store).Tour.Id).ToList();

      Assert.Equal(ids.Count, ids.Distinct().Count());
   }

   [Fact]
   public void Unknown_tour_is_missing_and_an_update_of_it_runs_no_change()
   {
      var store = NewStore();
      Create(store);
      var unknown = TourId.New();
      var changes = 0;

      Assert.IsType<TourRead.Missing>(store.Read(unknown));
      Assert.IsType<TourRead.Missing>(store.Update(unknown, document =>
      {
         changes++;
         return document;
      }));
      Assert.IsType<TourRead.Missing>(store.Delete(unknown));
      Assert.Equal(0, changes);
      Assert.False(File.Exists(PathOf(unknown)));
   }

   /// <summary>The store writes a document as it is, so only <see cref="TourDocument.Decode"/> refuses a file that holds another tour.</summary>
   [Fact]
   public void Decode_refuses_a_file_that_holds_another_tour()
   {
      var store = NewStore();
      var first = Create(store).Tour.Id;
      var second = Create(store).Tour.Id;
      File.Copy(PathOf(first), PathOf(second), overwrite: true);

      var decoded = Assert.IsType<TourRead.Found>(store.Read(second)).Document.Decode(second);

      Assert.Equal(("tour.id", $"the file of tour {second} holds tour {first}."), (decoded.Fault?.Path.ToString(), decoded.Fault?.Problem));
   }

   [Fact]
   public void Deleted_tour_leaves_no_file_and_is_missing_from_every_read()
   {
      var store = NewStore();
      var kept = Create(store).Tour.Id;
      var deleted = Create(store).Tour.Id;

      var removed = store.Delete(deleted);

      Assert.Equal(deleted, Assert.IsType<TourRead.Found>(removed).Document.Tour.Id);
      Assert.False(File.Exists(PathOf(deleted)));
      Assert.IsType<TourRead.Missing>(NewStore().Read(deleted));
      Assert.Equal([kept], NewStore().ReadAll().Select(read => read.Id));
      Assert.IsType<TourRead.Missing>(store.Delete(deleted));
   }

   [Fact]
   public void Update_of_a_deleted_tour_stores_nothing_and_brings_no_file_back()
   {
      var store = NewStore();
      var created = Create(store);
      store.Delete(created.Tour.Id);
      var changes = 0;

      var updated = store.Update(created.Tour.Id, _ =>
      {
         changes++;
         return Next(created, tour => tour with { Pin = new TourPin.Failed("A late pin.") });
      });

      Assert.IsType<TourRead.Missing>(updated);
      Assert.Equal(0, changes);
      Assert.Empty(Directory.EnumerateFileSystemEntries(ToursPath));
   }

   public void Dispose() => Directory.Delete(Path.GetDirectoryName(_dataDirectory.Path)!, recursive: true);

   private static TourDocument.StoredTour Create(FileTourStore store)
   {
      TourDocument.StoredTour? created = null;
      store.Create(id =>
      {
         created = TourDocument.New(id, PullRequest, Familiarity.Some, PlanApproval.NotNeeded, Now).Value!;
         return created.Document;
      });
      return created!;
   }

   private static TourDocument Next(TourDocument.StoredTour stored, Func<Tour, Tour> change) => stored.Next(change(stored.Tour), Now).Value!.Document;

   private static TourDocument WithMark(TourId id, TourDocument document, string path)
   {
      var stored = document.Decode(id).Value!;
      return stored.WithMarks([.. stored.ReviewMarks, new ReviewMark(path, null)]).Value!.Document;
   }

   private static TourDocument.StoredTour Found(FileTourStore store, TourId id) => Assert.IsType<TourRead.Found>(store.Read(id)).Document.Decode(id).Value!;

   private FileTourStore NewStore() => new(_dataDirectory);

   private string PathOf(TourId id) => Path.Combine(ToursPath, $"{id}.json");

   private sealed record UnregisteredTarget : TourTarget;
}
