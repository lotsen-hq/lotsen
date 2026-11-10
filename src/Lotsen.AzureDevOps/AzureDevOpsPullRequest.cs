using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Lotsen.AzureDevOps;

/// <summary>
/// An Azure DevOps pull request, taken apart from its web URL.
/// </summary>
/// <param name="Organization">The organization, as named in the URL.</param>
/// <param name="Project">The project's name, decoded.</param>
/// <param name="Repository">The repository's name, decoded.</param>
/// <param name="PullRequestId">The pull request's number.</param>
/// <param name="Url">The normalized URL: no query, fragment, trailing slash or <c>DefaultCollection</c>, and canonical escaping. The host form and the letter case of the names are kept.</param>
internal sealed record AzureDevOpsPullRequest(string Organization, string Project, string Repository, int PullRequestId, Uri Url)
{
   /// <summary>The URL forms that <see cref="TryParse"/> accepts, for messages.</summary>
   public const string ExpectedForms =
      "https://dev.azure.com/{organization}/{project}/_git/{repository}/pullrequest/{id} or https://{organization}.visualstudio.com/{project}/_git/{repository}/pullrequest/{id}";

   private const string DevAzureHost = "dev.azure.com";
   private const string VisualStudioSuffix = ".visualstudio.com";

   /// <summary>The organization's URL, which the REST API's paths start from, ending in a slash.</summary>
   public Uri OrganizationUrl => Url.Host == DevAzureHost ? new Uri($"https://{DevAzureHost}/{Uri.EscapeDataString(Organization)}/") : new Uri($"https://{Url.Host}/");

   /// <summary>The short name of the pull request, such as <c>contoso/shop/web #42</c>.</summary>
   public string Label => $"{Organization}/{Project}/{Repository} #{PullRequestId}";

   /// <summary>Returns whether the other pull request is this one.</summary>
   /// <remarks>The organization, the project and the repository compare in any letter case, the number exactly, and the host form does not count.</remarks>
   public bool IsSame(AzureDevOpsPullRequest other) =>
      PullRequestId == other.PullRequestId
      && string.Equals(Organization, other.Organization, StringComparison.OrdinalIgnoreCase)
      && string.Equals(Project, other.Project, StringComparison.OrdinalIgnoreCase)
      && string.Equals(Repository, other.Repository, StringComparison.OrdinalIgnoreCase);

   /// <summary>Returns whether the text is the HTTPS web URL of an Azure DevOps pull request.</summary>
   public static bool TryParse([NotNullWhen(true)] string? text, [NotNullWhen(true)] out AzureDevOpsPullRequest? pullRequest)
   {
      pullRequest = null;
      if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length > 0)
      {
         return false;
      }

      var segments = uri.AbsolutePath.Split('/');
      var path = segments.AsSpan(1, segments.Length - (segments[^1].Length == 0 ? 2 : 1));
      string? organization;
      if (uri.Host == DevAzureHost)
      {
         if (path.IsEmpty || !TryDecode(path[0], out organization))
         {
            return false;
         }

         path = path[1..];
      }
      else if (uri.Host.EndsWith(VisualStudioSuffix, StringComparison.Ordinal) && uri.Host[..^VisualStudioSuffix.Length] is { Length: > 0 } prefix && !prefix.Contains('.'))
      {
         organization = prefix;
         if (!path.IsEmpty && path[0].Equals("DefaultCollection", StringComparison.OrdinalIgnoreCase))
         {
            path = path[1..];
         }
      }
      else
      {
         return false;
      }

      if (path.Length != 5
         || !TryDecode(path[0], out var project)
         || !path[1].Equals("_git", StringComparison.OrdinalIgnoreCase)
         || !TryDecode(path[2], out var repository)
         || !path[3].Equals("pullrequest", StringComparison.OrdinalIgnoreCase)
         || !int.TryParse(path[4], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
         || id <= 0)
      {
         return false;
      }

      var tail = $"{Uri.EscapeDataString(project)}/_git/{Uri.EscapeDataString(repository)}/pullrequest/{id}";
      var url = uri.Host == DevAzureHost ? $"https://{DevAzureHost}/{Uri.EscapeDataString(organization)}/{tail}" : $"https://{uri.Host}/{tail}";
      pullRequest = new AzureDevOpsPullRequest(organization, project, repository, id, new Uri(url));
      return true;
   }

   // A decoded slash would move the parts of the path, and a blank name names nothing.
   private static bool TryDecode(string segment, [NotNullWhen(true)] out string? name)
   {
      name = Uri.UnescapeDataString(segment);
      return !string.IsNullOrWhiteSpace(name) && !name.Contains('/') && !name.Contains('\\');
   }
}
