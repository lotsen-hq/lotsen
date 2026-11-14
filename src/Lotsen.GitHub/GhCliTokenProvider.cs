using System.Globalization;
using System.Text;
using Lotsen.Credentials;
using Lotsen.Processes;

namespace Lotsen.GitHub;

/// <summary>The settings for asking the GitHub CLI for a token.</summary>
internal sealed record GhCliOptions
{
   /// <summary>The GitHub CLI executable: a name to look up on <c>PATH</c>, or a path.</summary>
   public string Executable { get; init; } = "gh";

   /// <summary>The longest one run of the GitHub CLI may take.</summary>
   public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(1);
}

/// <summary>Provides the github.com token of the user's GitHub CLI sign-in, from <c>gh auth token</c>.</summary>
/// <remarks>
/// The token arrives on the standard output of <c>gh</c> and stays in this provider's memory. The GitHub CLI reports no expiry, so a request reuses the token, whatever validity it asks,
/// for <see cref="ReuseWindow"/> after it arrived, or until <see cref="Reject"/> drops it. A <c>gh</c> sign-out or account switch does not revoke the token, so it reaches lotsen at the next run.
/// Requests that wait on a successful run of <c>gh</c> share its token. A failure is not cached, so the next request runs <c>gh</c> again, such as after the user ran <c>gh auth login</c>.
/// </remarks>
internal sealed class GhCliTokenProvider : IAccessTokenProvider
{
   private const string Host = "github.com";

   private const string Fix = "Run 'gh auth login', then try again.";

   /// <summary>How long requests reuse a token before the provider runs <c>gh</c> again, which bounds how long a <c>gh</c> sign-out or account switch takes to reach lotsen.</summary>
   internal static readonly TimeSpan ReuseWindow = TimeSpan.FromMinutes(5);

   private readonly SemaphoreSlim _gate = new(1, 1);
   private readonly GhCliOptions _options;
   private readonly TimeProvider _timeProvider;
   private readonly ILogger<GhCliTokenProvider> _logger;
   private volatile CachedToken? _cached;

   public GhCliTokenProvider(GhCliOptions options, TimeProvider timeProvider, ILogger<GhCliTokenProvider> logger)
   {
      _options = options;
      _timeProvider = timeProvider;
      _logger = logger;
   }

   public async ValueTask<AccessToken> GetTokenAsync(TimeSpan minimumValidity, CancellationToken cancellationToken)
   {
      if (Fresh(_cached) is { } cached)
      {
         return cached;
      }

      await _gate.WaitAsync(cancellationToken);
      try
      {
         if (Fresh(_cached) is { } refreshed)
         {
            return refreshed;
         }

         var token = await RequestAsync(cancellationToken);
         _cached = new CachedToken(token, _timeProvider.GetUtcNow());
         _logger.LogInformation("The GitHub CLI gave a token for {Host}.", Host);
         return token;
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
         _logger.LogInformation("The GitHub CLI's token was dropped, so the next request asks the GitHub CLI again.");
      }
   }

   private AccessToken? Fresh(CachedToken? cached) => cached is not null && _timeProvider.GetUtcNow() < cached.ArrivedAt + ReuseWindow ? cached.Token : null;

   private async Task<AccessToken> RequestAsync(CancellationToken cancellationToken)
   {
      string[] arguments = ["auth", "token", "--hostname", Host];
      return await ProcessRunner.RunAsync(new ProcessCommand(_options.Executable, arguments, _options.Timeout), cancellationToken) switch
      {
         ProcessOutcome.Exited { Code: 0 } exited => Read(exited.Output),
         ProcessOutcome.Exited exited => throw new AccessTokenUnavailableException(
            $"The GitHub CLI gave no token for {Host}. {Fix} The GitHub CLI said: {Reason(exited.Error)}"),
         ProcessOutcome.TimedOut => throw new AccessTokenUnavailableException(
            $"The GitHub CLI did not answer within {_options.Timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} seconds. {Fix}"),
         ProcessOutcome.Canceled => throw new OperationCanceledException(cancellationToken),
         ProcessOutcome.NotStarted { Failure: StartFailure.ExecutableMissing } => throw new AccessTokenUnavailableException(
            $"The GitHub CLI (gh) was not found. Install the GitHub CLI and put it on PATH. {Fix}"),
         ProcessOutcome.NotStarted notStarted => throw new AccessTokenUnavailableException($"The GitHub CLI could not be started: {notStarted.Reason}. {Fix}"),
      };
   }

   /// <summary>Returns the token from the output, which holds the token and a line break.</summary>
   /// <remarks>No message quotes the output, because the output holds the token.</remarks>
   private static AccessToken Read(byte[] output)
   {
      var value = Encoding.UTF8.GetString(output).Trim();
      if (value.Length == 0)
      {
         throw new AccessTokenUnavailableException($"The GitHub CLI printed no token for {Host}. {Fix}");
      }

      try
      {
         return new AccessToken(value);
      }
      catch (ArgumentException)
      {
         throw new AccessTokenUnavailableException($"The GitHub CLI printed a token that lotsen cannot read. Update the GitHub CLI. {Fix}");
      }
   }

   /// <summary>Returns the GitHub CLI's error output as one line.</summary>
   private static string Reason(string error)
   {
      var lines = error.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
      return lines.Length == 0 ? "nothing." : string.Join(' ', lines);
   }

   private sealed record CachedToken(AccessToken Token, DateTimeOffset ArrivedAt);
}
