using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Lotsen.Hosting;
using Lotsen.Json;
using Lotsen.Tours;

namespace Lotsen.Operations;

/// <summary>The input of <c>write_preference</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record WritePreferenceInput(
   [property: Description("The preference's key: 1 to 100 letters, digits, '.', '_' or '-', with at most one '/' between two of them.")]
   string Key,
   [property: Description("The preference's new text, of at most 65536 characters, or null to delete the preference.")]
   string? Value);

/// <summary>The output of <c>read_preferences</c>.</summary>
/// <param name="Preferences">Every stored preference's text, by its key.</param>
internal sealed record PreferenceList(IReadOnlyDictionary<string, string> Preferences);

/// <summary>A preference after a write: the output of <c>write_preference</c> and the payload of <see cref="ServerEvents.PreferenceChanged"/>.</summary>
/// <param name="Key">The preference's key.</param>
/// <param name="Value">The preference's stored text, or null when the key holds none.</param>
internal sealed record UpdatedPreference(string Key, string? Value);

/// <summary>The preference file's content, of format <see cref="PreferenceStore.FormatVersion"/>.</summary>
/// <param name="Version">The file's format.</param>
/// <param name="Preferences">Every preference's text, by its key.</param>
internal sealed record PreferenceFile(int Version, IReadOnlyDictionary<string, string> Preferences);

/// <summary>
/// The page's preference store: a text under each key, in the preference file of the data directory, which the server stores without knowing what a text means.
/// </summary>
/// <remarks>
/// <para>
/// A browser keeps its storage per origin, and the origin holds the server's port, which can change with each start. So the page keeps its preferences here, where every start finds them.
/// </para>
/// <para>
/// A missing or unreadable file holds no preferences, and the next write replaces an unreadable one. A file of a newer format also holds none, and every write is refused, so an older lotsen
/// never replaces what a newer one wrote.
/// </para>
/// </remarks>
internal sealed partial class PreferenceStore
{
   /// <summary>The format of the preference file that this build reads and writes. Change it, with a migration, when the file's layout changes.</summary>
   public const int FormatVersion = 1;

   /// <summary>The most characters of a key.</summary>
   public const int MaxKeyLength = 100;

   /// <summary>The most characters of a preference's text.</summary>
   public const int MaxValueLength = 64 * 1024;

   /// <summary>The most keys that the store holds.</summary>
   public const int MaxKeys = 200;

   private readonly DataDirectory _dataDirectory;
   private readonly EventBroadcaster _events;
   private readonly ILogger<PreferenceStore> _logger;

   // Serializes the reads and writes, so each one reads the file that the last write wrote.
   private readonly Lock _lock = new();

   public PreferenceStore(DataDirectory dataDirectory, EventBroadcaster events, ILogger<PreferenceStore> logger)
   {
      _dataDirectory = dataDirectory;
      _events = events;
      _logger = logger;
   }

   /// <summary>Returns every stored preference.</summary>
   /// <remarks>It waits for a write in progress, so a page that loads after another page's write reads that write.</remarks>
   public PreferenceList Read()
   {
      lock (_lock)
      {
         return new(Load().Preferences);
      }
   }

   /// <summary>Stores the input's text under its key, or deletes the key for a null text, and publishes <see cref="ServerEvents.PreferenceChanged"/> when the key's text changed.</summary>
   /// <returns>The key with its stored text, or the refusal of a key, a text or a count of keys that breaks the store's limits, or of a file of a newer format.</returns>
   public OperationResult<UpdatedPreference> Write(WritePreferenceInput input)
   {
      if (KeyFault(input.Key) is { } keyFault)
      {
         return OperationError.Invalid("key", keyFault);
      }

      if (input.Value is { Length: > MaxValueLength } value)
      {
         return OperationError.Invalid("value", $"the text holds {value.Length} characters, and a preference holds at most {MaxValueLength}. Store a shorter text.");
      }

      lock (_lock)
      {
         var stored = Load();
         if (stored.Newer)
         {
            return OperationError.NewerPreferences();
         }

         var before = stored.Preferences.GetValueOrDefault(input.Key);
         if (before == input.Value)
         {
            return new UpdatedPreference(input.Key, before);
         }

         if (before is null && stored.Preferences.Count >= MaxKeys)
         {
            return OperationError.Invalid("key", $"lotsen stores at most {MaxKeys} preferences and holds that many, so it takes no new key. Replace or delete a stored preference instead.");
         }

         var next = new SortedDictionary<string, string>(stored.Preferences.ToDictionary(), StringComparer.Ordinal);
         if (input.Value is null)
         {
            next.Remove(input.Key);
         }
         else
         {
            next[input.Key] = input.Value;
         }

         _dataDirectory.EnsureExists();
         UserOnlyStorage.WriteFileAtomically(
            _dataDirectory.PreferencesFilePath, stream => JsonSerializer.Serialize(stream, new PreferenceFile(FormatVersion, next), LotsenJsonContext.Default.PreferenceFile));
         var updated = new UpdatedPreference(input.Key, input.Value);

         // Under the lock, so the events of two writes of one key reach every page in the order of the writes.
         _events.Publish(ServerEvents.ForPreferenceChanged(updated));
         return updated;
      }
   }

   /// <summary>Returns why the key breaks the form of a key, or null when it fits.</summary>
   public static string? KeyFault(string key) => key.Length switch
   {
      0 => "the key is empty. Give a key of 1 to 100 characters.",
      > MaxKeyLength => $"the key holds {key.Length} characters, and a key holds at most {MaxKeyLength}. Give a shorter key.",
      _ when !KeyForm().IsMatch(key) => "a key holds only letters, digits, '.', '_' and '-', with at most one '/' between two of them. Give a key of that form.",
      _ => null,
   };

   [GeneratedRegex(@"^[A-Za-z0-9._-]+(?:/[A-Za-z0-9._-]+)?\z", RegexOptions.CultureInvariant)]
   private static partial Regex KeyForm();

   // Returns what the file holds, or no preferences for a file that is missing or unreadable or whose content breaks the store's limits.
   private Stored Load()
   {
      var path = _dataDirectory.PreferencesFilePath;
      try
      {
         // Sharing delete lets a write on Windows rename over the file while this read holds it open.
         using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
         using var document = JsonDocument.Parse(stream);
         var root = document.RootElement;
         if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number
            && (!version.TryGetInt32(out var number) || number > FormatVersion))
         {
            _logger.LogWarning("The preferences in {Path} come from a newer lotsen, so this one reads none and changes none.", path);
            return new Stored(Empty, Newer: true);
         }

         if (root.Deserialize(LotsenJsonContext.Default.PreferenceFile) is not { Version: FormatVersion } file
            || file.Preferences.Count > MaxKeys
            || file.Preferences.Any(preference => KeyFault(preference.Key) is not null || preference.Value is null || preference.Value.Length > MaxValueLength))
         {
            return Unreadable(path, "it is not a preference file of this lotsen");
         }

         return new Stored(file.Preferences, Newer: false);
      }
      catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
      {
         return new Stored(Empty, Newer: false);
      }
      catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
      {
         return Unreadable(path, exception.Message);
      }
   }

   private Stored Unreadable(string path, string reason)
   {
      _logger.LogWarning("The preferences in {Path} could not be read, so lotsen holds none until the next write replaces the file: {Reason}", path, reason);
      return new Stored(Empty, Newer: false);
   }

   private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

   /// <summary>What the preference file holds.</summary>
   /// <param name="Preferences">Every preference's text, by its key.</param>
   /// <param name="Newer">Whether a newer lotsen wrote the file, so no write may replace it.</param>
   private sealed record Stored(IReadOnlyDictionary<string, string> Preferences, bool Newer);
}
