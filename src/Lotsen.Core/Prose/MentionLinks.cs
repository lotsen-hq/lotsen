using System.Globalization;
using Lotsen.Platforms;

namespace Lotsen.Prose;

/// <summary>Provides the web URLs of the work items and pull requests that one tour's prose mentions, as the platform of the tour's pull request builds them.</summary>
/// <remarks>
/// A mention is a Markdown link that Claude writes with a scheme of its own: <c>[#1234](workitem:1234)</c> for a work item and <c>[!42](pullrequest:42)</c> for a pull request in the tour's
/// repository. Its ID is a positive decimal integer, and its scheme reads in any letter case, as a <c>ref:</c> link's does.
/// </remarks>
internal sealed class MentionLinks
{
   private static readonly (string Scheme, MentionKind Kind)[] KindsBySchemes = [("workitem:", MentionKind.WorkItem), ("pullrequest:", MentionKind.PullRequest)];

   private readonly Func<MentionKind, int, Uri?> _urlOf;

   /// <param name="urlOf">Returns the web URL of a mention by its kind and ID, or null when it cannot build one.</param>
   public MentionLinks(Func<MentionKind, int, Uri?> urlOf)
   {
      _urlOf = urlOf;
   }

   /// <summary>The links of prose without a tour, which place no mention.</summary>
   public static MentionLinks None { get; } = new((_, _) => null);

   /// <summary>The schemes of a mention's link, in lowercase with their colon, such as <c>workitem:</c>.</summary>
   public static IEnumerable<string> Schemes => KindsBySchemes.Select(pair => pair.Scheme);

   /// <summary>Reads a link's destination as a mention.</summary>
   /// <returns>Whether the destination is a mention with a positive ID that fits an <see cref="int"/>.</returns>
   public static bool TryRead(string destination, out MentionKind kind, out int id)
   {
      foreach (var (scheme, schemeKind) in KindsBySchemes)
      {
         if (destination.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
         {
            kind = schemeKind;
            return int.TryParse(destination.AsSpan(scheme.Length), NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
         }
      }

      kind = default;
      id = 0;
      return false;
   }

   /// <summary>Returns the web URL of the mentioned work item or pull request.</summary>
   /// <returns>The URL, or null when the tour's platform cannot build one.</returns>
   public Uri? UrlOf(MentionKind kind, int id) => _urlOf(kind, id);
}
