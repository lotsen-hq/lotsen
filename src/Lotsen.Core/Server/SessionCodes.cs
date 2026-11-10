using System.Collections.Concurrent;

namespace Lotsen.Server;

/// <summary>
/// Issues and redeems one-time codes, the only credential that a link to the UI may carry.
/// </summary>
/// <remarks>
/// A code is redeemed at most once and only within <see cref="Lifetime"/> of its issue. Redeeming it yields the UI credential, so a link that leaks after use or after expiry grants nothing.
/// </remarks>
internal sealed class SessionCodes
{
   /// <summary>How long a code stays redeemable after its issue.</summary>
   public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

   private readonly ConcurrentDictionary<string, DateTimeOffset> _expiries = new(StringComparer.Ordinal);
   private readonly TimeProvider _timeProvider;

   public SessionCodes(TimeProvider timeProvider)
   {
      _timeProvider = timeProvider;
   }

   /// <summary>Returns a new code, as unpadded base64url text.</summary>
   public string Issue()
   {
      var now = _timeProvider.GetUtcNow();
      foreach (var (code, expiry) in _expiries)
      {
         if (expiry <= now)
         {
            _expiries.TryRemove(code, out _);
         }
      }

      var issued = Secrets.NewRandomValue(16);
      _expiries[issued] = now + Lifetime;
      return issued;
   }

   /// <summary>Returns whether the code was issued, unexpired and not redeemed before. The code is spent either way.</summary>
   public bool TryRedeem(string? code) => !string.IsNullOrEmpty(code) && _expiries.TryRemove(code, out var expiry) && _timeProvider.GetUtcNow() < expiry;
}
