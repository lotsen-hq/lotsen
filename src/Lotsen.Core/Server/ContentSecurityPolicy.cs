namespace Lotsen.Server;

/// <summary>Sets the content security policy, the second line of defense if untrusted text ever reaches the page as markup.</summary>
/// <remarks>
/// Scripts load only from the server. Styles load from the server or carry the response's style nonce, which the UI's renderer needs for the styles it creates at run time.
/// Each response gets a new nonce.
/// </remarks>
internal static class ContentSecurityPolicy
{
   /// <summary>The text in an HTML file that the server replaces with the response's style nonce.</summary>
   public const string NoncePlaceholder = "__LOTSEN_CSP_NONCE__";

   private static readonly object NonceKey = new();

   /// <summary>Creates the response's style nonce and sets the policy header that allows it.</summary>
   public static void Apply(HttpContext context)
   {
      var nonce = Secrets.NewRandomValue(16);
      context.Items[NonceKey] = nonce;
      context.Response.Headers.ContentSecurityPolicy =
         "default-src 'self'; " +
         "script-src 'self'; " +
         $"style-src 'self' 'nonce-{nonce}'; " +
         "connect-src 'self'; " +
         "img-src 'self' data:; " +
         "object-src 'none'; " +
         "base-uri 'none'; " +
         "frame-ancestors 'none'; " +
         "form-action 'none'";
   }

   /// <summary>Returns the style nonce that <see cref="Apply"/> set for this response.</summary>
   public static string GetNonce(HttpContext context) => context.Items[NonceKey] as string ?? throw new InvalidOperationException("The content security policy was not applied to this response.");
}
