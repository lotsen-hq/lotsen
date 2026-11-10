using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lotsen.Credentials;
using Lotsen.Processes;

namespace Lotsen.AzureDevOps;

/// <summary>The settings for asking the Azure CLI for a token.</summary>
internal sealed record AzureCliOptions
{
   /// <summary>The Azure CLI executable as a path, or null to look up <c>az</c> on <c>PATH</c>.</summary>
   public string? Executable { get; init; }

   /// <summary>The longest one run of the Azure CLI may take.</summary>
   public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(1);
}

/// <summary>Provides the Azure DevOps token of the user's Azure CLI sign-in, from <c>az account get-access-token</c>.</summary>
/// <remarks>
/// The token arrives on the standard output of <c>az</c> and stays in this provider's memory. A request reuses it while it stays valid for the validity that the request asks, and runs <c>az</c>
/// again otherwise. Concurrent requests share one run of <c>az</c>. A failure is not cached, so the next request runs <c>az</c> again, such as after the user ran <c>az login</c>.
/// </remarks>
internal sealed class AzureCliTokenProvider : IAccessTokenProvider
{
   /// <summary>The application ID of Azure DevOps, which names it as the resource of the token.</summary>
   private const string AzureDevOpsResource = "499b84ac-1321-427f-aa17-267ca6975798";

   /// <summary>How long before its expiry the provider stops using a token that arrived with less validity than its request asked.</summary>
   /// <remarks>
   /// The Azure CLI returns its cached token until 5 minutes before expiry, so asking again sooner returns the same token. A token that arrives with less than this left is returned and not kept.
   /// </remarks>
   public static readonly TimeSpan ReuseFloor = TimeSpan.FromMinutes(1);

   private const string ReadFailure = "The Azure CLI returned a token that lotsen cannot read. Update the Azure CLI, then try again.";

   private readonly SemaphoreSlim _gate = new(1, 1);
   private readonly AzureCliOptions _options;
   private readonly TimeProvider _timeProvider;
   private readonly ILogger<AzureCliTokenProvider> _logger;
   private volatile CachedToken? _cached;

   public AzureCliTokenProvider(AzureCliOptions options, TimeProvider timeProvider, ILogger<AzureCliTokenProvider> logger)
   {
      _options = options;
      _timeProvider = timeProvider;
      _logger = logger;
   }

   public async ValueTask<AccessToken> GetTokenAsync(TimeSpan minimumValidity, CancellationToken cancellationToken)
   {
      if (Fresh(_cached, minimumValidity) is { } cached)
      {
         return cached;
      }

      await _gate.WaitAsync(cancellationToken);
      try
      {
         if (Fresh(_cached, minimumValidity) is { } refreshed)
         {
            return refreshed;
         }

         var token = await RequestAsync(minimumValidity, cancellationToken);
         _cached = token;
         _logger.LogInformation("The Azure CLI gave an Azure DevOps token that expires at {ExpiresAt:u}.", token.ExpiresAt);
         return token.Token;
      }
      catch (AccessTokenUnavailableException exception)
      {
         _logger.LogWarning("{Reason}", exception.Message);
         throw;
      }
      finally
      {
         _gate.Release();
      }
   }

   public void Reject(AccessToken token)
   {
      // Only the rejected token leaves the cache, so a late rejection cannot drop a newer token.
      if (_cached is { } cached && ReferenceEquals(cached.Token, token) && Interlocked.CompareExchange(ref _cached, null, cached) == cached)
      {
         _logger.LogInformation("Azure DevOps refused the Azure CLI's token, so the next request asks the Azure CLI again.");
      }
   }

   /// <summary>Returns the full path of the executable that a command name runs, or null when none is on the path.</summary>
   /// <param name="name">The command's name, without an extension.</param>
   /// <param name="path">The directories to search, separated by <see cref="Path.PathSeparator"/>.</param>
   /// <param name="pathExtensions">The extensions that Windows runs, separated by semicolons, such as <c>.EXE;.CMD</c>. Null means the Windows default.</param>
   /// <param name="windows">Whether to search as Windows does: only for the name with one of the extensions.</param>
   public static string? FindOnPath(string name, string? path, string? pathExtensions, bool windows)
   {
      string[] fileNames = windows
         ? [.. (pathExtensions ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(extension => name + extension.ToLowerInvariant())]
         : [name];
      var directories = (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
      return directories
         .SelectMany(directory => fileNames.Select(fileName => Path.Combine(directory, fileName)))
         .FirstOrDefault(File.Exists);
   }

   // A token that arrived short of its request's validity is the best that the Azure CLI has until its own refresh, so it serves every request until the reuse floor.
   private AccessToken? Fresh(CachedToken? cached, TimeSpan minimumValidity)
   {
      var now = _timeProvider.GetUtcNow();
      return cached is not null && (now + minimumValidity <= cached.ExpiresAt || (cached.ArrivedShort && now < cached.ExpiresAt - ReuseFloor)) ? cached.Token : null;
   }

   private async Task<CachedToken> RequestAsync(TimeSpan minimumValidity, CancellationToken cancellationToken)
   {
      // On Windows the Azure CLI is az.cmd, and a process start without the shell finds only an .exe by its bare name.
      var executable = _options.Executable
         ?? FindOnPath(
            "az",
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATHEXT"),
            OperatingSystem.IsWindows())
         ?? throw NotFound();

      string[] arguments = ["account", "get-access-token", "--resource", AzureDevOpsResource, "--output", "json", "--only-show-errors"];
      return await ProcessRunner.RunAsync(new ProcessCommand(executable, arguments, _options.Timeout), cancellationToken) switch
      {
         ProcessOutcome.Exited { Code: 0 } exited => Read(exited.Output, minimumValidity),
         ProcessOutcome.Exited exited => throw Failed(exited.Error),
         ProcessOutcome.TimedOut => throw new AccessTokenUnavailableException(
            $"The Azure CLI did not answer within {_options.Timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} seconds. Check that 'az account get-access-token' works, then try again."),
         ProcessOutcome.Canceled => throw new OperationCanceledException(cancellationToken),
         ProcessOutcome.NotStarted => throw NotFound(),
      };
   }

   private CachedToken Read(byte[] output, TimeSpan minimumValidity)
   {
      AzureCliAccessToken? response;
      try
      {
         // The reader decodes the output by its byte order mark, such as UTF-16's, or as UTF-8 without one, and drops the mark, which the JSON reader would refuse.
         using var reader = new StreamReader(new MemoryStream(output), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
         response = JsonSerializer.Deserialize(reader.ReadToEnd(), AzureCliJsonContext.Default.AzureCliAccessToken);
      }
      catch (JsonException)
      {
         // The message of a JSON error can quote the output, which holds the token.
         throw new AccessTokenUnavailableException(ReadFailure);
      }

      if (response?.Token is not { } value || ExpiryOf(response) is not { } expiresAt)
      {
         throw new AccessTokenUnavailableException(ReadFailure);
      }

      AccessToken token;
      try
      {
         token = new AccessToken(value);
      }
      catch (ArgumentException)
      {
         throw new AccessTokenUnavailableException(ReadFailure);
      }

      if (expiresAt <= _timeProvider.GetUtcNow())
      {
         throw new AccessTokenUnavailableException("The Azure CLI returned an expired Azure DevOps token. Run 'az login', then try again.");
      }

      return new CachedToken(token, expiresAt, ArrivedShort: _timeProvider.GetUtcNow() + minimumValidity > expiresAt);
   }

   // Older versions of the Azure CLI give the expiry only as expiresOn, in local time without an offset.
   private DateTimeOffset? ExpiryOf(AzureCliAccessToken response)
   {
      if (response.ExpiresOnUnixSeconds is { } seconds)
      {
         return seconds is > 0 && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds() ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
      }

      if (!DateTime.TryParseExact(response.ExpiresOn, ["yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
      {
         return null;
      }

      var zone = _timeProvider.LocalTimeZone;
      return new DateTimeOffset(local, zone.GetUtcOffset(local));
   }

   /// <summary>Returns the Azure CLI's reason from its error output: its error lines without the prefix, or its last line.</summary>
   private static string Reason(string error)
   {
      var lines = error.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
      List<string> reasons =
      [
         .. lines
            .Where(line => line.StartsWith("ERROR:", StringComparison.Ordinal))
            .Select(line => line["ERROR:".Length..].Trim()),
      ];
      if (reasons.Count == 0 && lines.Length > 0)
      {
         reasons.Add(lines[^1]);
      }

      return string.Join(' ', reasons);
   }

   /// <summary>Returns the failure for an Azure CLI run that exited with an error.</summary>
   /// <remarks>
   /// A sign-in that is missing or expired names <c>az login</c> in the error line, or prints the commands that fix it on lines of their own. Those commands become the fix in the message.
   /// </remarks>
   private static AccessTokenUnavailableException Failed(string error)
   {
      var reason = Reason(error);
      var lines = error.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
      var commands = lines
         .Where(line => line.StartsWith("az login", StringComparison.Ordinal) || line.StartsWith("az logout", StringComparison.Ordinal))
         .Select(line => $"'{line}'")
         .ToList();
      if (commands.Count == 0 && !reason.Contains("az login", StringComparison.OrdinalIgnoreCase))
      {
         return new AccessTokenUnavailableException(
            "The Azure CLI could not get an Azure DevOps token. Check that 'az login' works, then try again. The Azure CLI said: "
            + (reason.Length == 0 ? "nothing." : reason));
      }

      var fix = commands.Count == 0 ? "'az login'" : string.Join(", then ", commands);
      return new AccessTokenUnavailableException($"The Azure CLI is not signed in, or its sign-in has expired. Run {fix}, then try again. The Azure CLI said: {reason}");
   }

   private static AccessTokenUnavailableException NotFound() => new("The Azure CLI (az) was not found. Install the Azure CLI, put it on PATH and run 'az login', then try again.");

   private sealed record CachedToken(AccessToken Token, DateTimeOffset ExpiresAt, bool ArrivedShort);
}

/// <summary>The output of <c>az account get-access-token</c>.</summary>
/// <param name="Token">The access token.</param>
/// <param name="ExpiresOn">The expiry in local time without an offset, such as <c>2026-10-22 22:51:12.000000</c>.</param>
/// <param name="ExpiresOnUnixSeconds">The expiry in seconds since 1970 in UTC, or null from versions that leave it out.</param>
internal sealed record AzureCliAccessToken(
   [property: JsonPropertyName("accessToken")] string? Token,
   string? ExpiresOn,
   [property: JsonPropertyName("expires_on"), JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] long? ExpiresOnUnixSeconds);

/// <summary>The source-generated JSON metadata for the output of the Azure CLI.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(AzureCliAccessToken))]
internal sealed partial class AzureCliJsonContext : JsonSerializerContext;
