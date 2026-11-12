using System.Text.Json.Nodes;
using Lotsen.Hosting;
using Lotsen.Operations;
using Lotsen.Tours;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lotsen.Tests;

/// <summary>Covers how <see cref="PreferenceStore"/> keeps the page's preferences in the preference file, the limits it holds them to, and the <c>preferenceChanged</c> event of each change.</summary>
[Trait("Category", "Preferences")]
public sealed class PreferenceStoreTests : IDisposable
{
   private readonly DataDirectory _dataDirectory = new(Directory.CreateTempSubdirectory("lotsen-preferences-").FullName);
   private readonly EventBroadcaster _events = new();
   private readonly EventBroadcaster.Subscription _published;
   private readonly PreferenceStore _store;

   public PreferenceStoreTests()
   {
      _published = _events.Subscribe();
      _store = NewStore();
   }

   public void Dispose()
   {
      _published.Dispose();
      TestDirectories.Delete(_dataDirectory.Path, TimeSpan.FromSeconds(5));
   }

   private PreferenceStore NewStore() => new(_dataDirectory, _events, NullLogger<PreferenceStore>.Instance);

   private UpdatedPreference Write(string key, string? value)
   {
      var result = _store.Write(new WritePreferenceInput(key, value));
      Assert.True(result.Value is not null, $"The write was refused: {result.Error?.Reason}");
      return result.Value;
   }

   private OperationError Refused(string key, string? value)
   {
      var result = _store.Write(new WritePreferenceInput(key, value));
      Assert.True(result.Error is not null, $"The write was stored: {result.Value}");
      return result.Error;
   }

   private List<UpdatedPreference> PublishedChanges()
   {
      List<UpdatedPreference> changes = [];
      while (_published.Reader.TryRead(out var serverEvent))
      {
         Assert.Equal(ServerEvents.PreferenceChanged.Name, serverEvent.Type);
         var payload = JsonNode.Parse(serverEvent.Data)!;
         changes.Add(new UpdatedPreference((string)payload["key"]!, (string?)payload["value"]));
      }

      return changes;
   }

   private void WriteFile(string text) => File.WriteAllText(_dataDirectory.PreferencesFilePath, text);

   [Fact]
   public void Store_reads_back_what_it_wrote_replaced_and_deleted()
   {
      Write("lotsen.theme", "dark");
      Write("lotsen.diffLayout", "inline");
      Write("lotsen.theme", "light");
      Write("lotsen.diffLayout", null);

      Assert.Equal(new Dictionary<string, string> { ["lotsen.theme"] = "light" }, _store.Read().Preferences);
   }

   [Fact]
   public void A_new_store_on_the_same_data_directory_reads_the_preferences_from_the_file()
   {
      Write("lotsen.theme", "dark");
      Write("layouts/review", """{"version":1}""");

      Assert.Equal(new Dictionary<string, string> { ["layouts/review"] = """{"version":1}""", ["lotsen.theme"] = "dark" }, NewStore().Read().Preferences);
      var file = JsonNode.Parse(File.ReadAllText(_dataDirectory.PreferencesFilePath))!;
      Assert.Equal(PreferenceStore.FormatVersion, (int)file["version"]!);
   }

   [Fact]
   public void Each_change_publishes_preferenceChanged_and_a_write_of_the_text_the_key_holds_publishes_nothing()
   {
      var stored = Write("lotsen.theme", "dark");
      var again = Write("lotsen.theme", "dark");
      Write("lotsen.theme", null);
      Write("lotsen.theme", null);

      Assert.Equal(new UpdatedPreference("lotsen.theme", "dark"), stored);
      Assert.Equal(stored, again);
      Assert.Equal([new UpdatedPreference("lotsen.theme", "dark"), new UpdatedPreference("lotsen.theme", null)], PublishedChanges());
   }

   [Theory]
   [InlineData("missing", null)]
   [InlineData("not JSON", "{ not json")]
   [InlineData("of no format version", """{ "preferences": { "lotsen.theme": "dark" } }""")]
   [InlineData("of an older format", """{ "version": 0, "preferences": { "lotsen.theme": "dark" } }""")]
   [InlineData("with a text that is no string", """{ "version": 1, "preferences": { "lotsen.theme": 3 } }""")]
   [InlineData("with a key of the wrong form", """{ "version": 1, "preferences": { "a b": "dark" } }""")]
   public void A_file_that_is_missing_or_unreadable_holds_no_preferences_and_the_next_write_replaces_it(string _, string? text)
   {
      if (text is not null)
      {
         WriteFile(text);
      }

      Assert.Empty(_store.Read().Preferences);
      Write("lotsen.theme", "light");
      Assert.Equal(new Dictionary<string, string> { ["lotsen.theme"] = "light" }, NewStore().Read().Preferences);
   }

   [Fact]
   public void A_file_of_a_newer_format_holds_no_preferences_and_refuses_every_write()
   {
      const string newer = """{ "version": 2, "preferences": { "lotsen.theme": { "mode": "dark" } } }""";
      WriteFile(newer);

      Assert.Empty(_store.Read().Preferences);
      var refusal = Refused("lotsen.theme", "light");
      Assert.Equal(OperationErrorCode.NewerPreferences, refusal.Code);
      Assert.Equal("a newer version of lotsen stored the preferences, so this version changes none of them. Run the newer lotsen to change a preference.", refusal.Reason);
      Assert.Equal(OperationErrorCode.NewerPreferences, Refused("lotsen.theme", null).Code);
      Assert.Equal(newer, File.ReadAllText(_dataDirectory.PreferencesFilePath));
      Assert.Empty(PublishedChanges());
   }

   [Theory]
   [InlineData("")]
   [InlineData("a b")]
   [InlineData("/lotsen")]
   [InlineData("lotsen/")]
   [InlineData("layouts/review/wide")]
   [InlineData("lotsené")]
   public void Key_of_the_wrong_form_is_refused(string key)
   {
      var refusal = Refused(key, "dark");

      Assert.Equal(OperationErrorCode.InvalidInput, refusal.Code);
      Assert.Equal("key", refusal.Field?.ToString());
      Assert.Empty(_store.Read().Preferences);
   }

   [Fact]
   public void Key_and_text_at_their_limits_are_stored_and_one_character_more_is_refused()
   {
      var key = new string('k', PreferenceStore.MaxKeyLength);
      var text = new string('t', PreferenceStore.MaxValueLength);

      Write(key, text);

      Assert.Equal("key: the key holds 101 characters, and a key holds at most 100. Give a shorter key.", Refused(key + "k", "dark").Reason);
      Assert.Equal("value: the text holds 65537 characters, and a preference holds at most 65536. Store a shorter text.", Refused("lotsen.layout", text + "t").Reason);
      Assert.Equal([key], _store.Read().Preferences.Keys);
   }

   [Fact]
   public void Writes_of_different_keys_at_once_each_keep_their_key()
   {
      const int writers = 8;
      const int keysEach = 5;
      // The threads start together at the barrier, so their writes overlap.
      using var start = new Barrier(writers);
      var refusals = new System.Collections.Concurrent.ConcurrentBag<OperationError>();
      var threads = Enumerable.Range(0, writers).Select(writer => new Thread(() =>
      {
         start.SignalAndWait();
         for (var index = 0; index < keysEach; index++)
         {
            if (_store.Write(new WritePreferenceInput($"writer{writer}.key{index}", "on")).Error is { } refusal)
            {
               refusals.Add(refusal);
            }
         }
      })).ToList();

      threads.ForEach(thread => thread.Start());
      threads.ForEach(thread => thread.Join());

      Assert.Empty(refusals);
      Assert.Equal(writers * keysEach, NewStore().Read().Preferences.Count);
      Assert.Equal(writers * keysEach, PublishedChanges().Count);
   }

   [Fact]
   public void Store_full_of_keys_refuses_a_new_key_and_still_replaces_and_deletes_one()
   {
      for (var index = 0; index < PreferenceStore.MaxKeys; index++)
      {
         Write($"key.{index}", "on");
      }

      var refusal = Refused("key.new", "on");
      Write("key.0", "off");
      Write("key.1", null);
      Write("key.new", "on");

      Assert.Equal("key: lotsen stores at most 200 preferences and holds that many, so it takes no new key. Replace or delete a stored preference instead.", refusal.Reason);
      Assert.Equal(PreferenceStore.MaxKeys, _store.Read().Preferences.Count);
      Assert.Equal("off", _store.Read().Preferences["key.0"]);
   }
}
