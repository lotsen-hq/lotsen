using System.Diagnostics.CodeAnalysis;
using Lotsen.Git;

namespace Lotsen.AzureDevOps;

/// <summary>An Azure DevOps repository, taken apart from a web URL or an SSH remote that names it.</summary>
/// <param name="Organization">The organization, decoded, in the letter case of the URL, or in lowercase when the host names it.</param>
/// <param name="Project">The project's name, decoded.</param>
/// <param name="Name">The repository's name, decoded.</param>
/// <param name="Url">
/// The normalized URL: <c>https://dev.azure.com/{organization}/{project}/_git/{repository}</c>, or <c>https://{organization}.visualstudio.com/{project}/_git/{repository}</c> for a web URL on
/// that host, with no query, fragment, trailing slash or <c>DefaultCollection</c>, and canonical escaping.
/// </param>
internal sealed record AzureDevOpsRepository(string Organization, string Project, string Name, Uri Url)
{
   /// <summary>The URL forms that <see cref="TryParse"/> accepts, for messages.</summary>
   public const string ExpectedForms =
      "https://dev.azure.com/{organization}/{project}/_git/{repository} or https://{organization}.visualstudio.com/{project}/_git/{repository}, optionally followed by ?version=GB{branch}, "
      + "?version=GT{tag} or ?version=GC{commit}, or an SSH remote, git@ssh.dev.azure.com:v3/{organization}/{project}/{repository}, "
      + "ssh://git@ssh.dev.azure.com/v3/{organization}/{project}/{repository} or {organization}@vs-ssh.visualstudio.com:v3/{organization}/{project}/{repository}";

   private const string DevAzureHost = "dev.azure.com";
   private const string VisualStudioSuffix = ".visualstudio.com";
   private const string SshHost = "ssh.dev.azure.com";
   private const string VisualStudioSshHost = "vs-ssh.visualstudio.com";
   private const string SshUser = "git";
   private const int SshPort = 22;

   /// <summary>The organization's URL, which the REST API's paths and the web URLs start from, ending in a slash.</summary>
   public Uri OrganizationUrl => OrganizationUrlOf(Url.Host, Organization);

   /// <summary>The short name of the repository, such as <c>contoso/shop/web</c>.</summary>
   public string Label => $"{Organization}/{Project}/{Name}";

   /// <summary>Returns whether the other names the same repository.</summary>
   /// <remarks>The organization, the project and the name compare in any letter case, and the host form does not count.</remarks>
   public bool IsSame(AzureDevOpsRepository other) =>
      string.Equals(Organization, other.Organization, StringComparison.OrdinalIgnoreCase)
      && string.Equals(Project, other.Project, StringComparison.OrdinalIgnoreCase)
      && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

   /// <summary>Returns whether the host, in lowercase, is one that Azure DevOps serves its web URLs or its git remotes at: dev.azure.com, ssh.dev.azure.com or a host under visualstudio.com.</summary>
   public static bool IsHost(string host) => host is DevAzureHost or SshHost || host.EndsWith(VisualStudioSuffix, StringComparison.Ordinal);

   /// <summary>Returns the web URL of a work item in the repository's project: <c>{organization URL}/{project}/_workitems/edit/{id}</c>.</summary>
   public Uri WorkItemUrl(int id) => new($"{OrganizationUrl.AbsoluteUri}{Uri.EscapeDataString(Project)}/_workitems/edit/{id}");

   /// <summary>Returns the web URL of a pull request in the repository: <c>{repository URL}/pullrequest/{id}</c>.</summary>
   public Uri PullRequestUrl(int id) => new($"{Url.OriginalString}/pullrequest/{id}");

   /// <summary>Returns whether the text is the HTTPS web URL of one repository, optionally with a version parameter, or an SSH remote of one repository.</summary>
   /// <remarks>
   /// <para>
   /// The web URL's <c>version</c> query parameter names the ref: <c>GB</c> and a branch, <c>GT</c> and a tag, or <c>GC</c> and a full commit ID. Every other query parameter and the fragment name
   /// nothing.
   /// </para>
   /// <para>
   /// An SSH remote, as <c>git remote get-url</c> prints it for a clone over SSH, names the repository by its https URL on dev.azure.com, so its tour clones over https with the az
   /// sign-in.
   /// </para>
   /// </remarks>
   /// <param name="text">The text.</param>
   /// <param name="repository">The repository, or null when the text names none.</param>
   /// <param name="ref">
   /// The branch or tag after <c>GB</c> or <c>GT</c>, with its percent-encoding decoded, or the commit ID after <c>GC</c> in lowercase, or null when the text has no version parameter. A
   /// text whose version parameter has another prefix, or names a ref that <see cref="RefName.IsValid"/> refuses or a commit by anything but its full ID, names no repository.
   /// </param>
   public static bool TryParse([NotNullWhen(true)] string? text, [NotNullWhen(true)] out AzureDevOpsRepository? repository, out string? @ref)
   {
      @ref = null;
      if (TrySshPath(text, out var sshPath))
      {
         repository = sshPath is [var organization, var project, var name] ? Of(DevAzureHost, organization, project, name) : null;
         return repository is not null;
      }

      repository = TryWebPath(text, out var uri, out var host, out var webOrganization, out var path) && path.Length == 3 && TryVersion(uri.Query, out @ref)
         ? OfWebPath(host, webOrganization, path)
         : null;
      return repository is not null;
   }

   /// <summary>Returns the host, the organization and the rest of the path of an Azure DevOps web URL, with the organization and the path's segments still percent-encoded.</summary>
   /// <remarks>The path leaves out the organization on dev.azure.com, <c>DefaultCollection</c> on the visualstudio.com host, and the empty segment of a trailing slash.</remarks>
   /// <returns>Whether the text is an absolute <c>https:</c> URL on the default port, without user information, of dev.azure.com or of an organization's visualstudio.com host.</returns>
   internal static bool TryWebPath(
      [NotNullWhen(true)] string? text,
      [NotNullWhen(true)] out Uri? uri,
      [NotNullWhen(true)] out string? host,
      [NotNullWhen(true)] out string? organization,
      [NotNullWhen(true)] out string[]? path)
   {
      (host, organization, path) = (null, null, null);
      if (!Uri.TryCreate(text, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length > 0)
      {
         return false;
      }

      var segments = uri.AbsolutePath.Split('/');
      var rest = segments[1..(segments[^1].Length == 0 ? ^1 : ^0)];
      if (uri.Host == DevAzureHost)
      {
         if (rest is not [var first, ..])
         {
            return false;
         }

         (organization, rest) = (first, rest[1..]);
      }
      else if (uri.Host.EndsWith(VisualStudioSuffix, StringComparison.Ordinal) && uri.Host[..^VisualStudioSuffix.Length] is { Length: > 0 } prefix && !prefix.Contains('.'))
      {
         organization = prefix;
         if (rest is [var first, ..] && first.Equals("DefaultCollection", StringComparison.OrdinalIgnoreCase))
         {
            rest = rest[1..];
         }
      }
      else
      {
         return false;
      }

      (host, path) = (uri.Host, rest);
      return true;
   }

   /// <summary>Returns the repository that the first three segments of a web URL's path name: <c>{project}/_git/{repository}</c>, percent-encoded as the organization is.</summary>
   /// <returns>The repository, or null when the segments name none.</returns>
   internal static AzureDevOpsRepository? OfWebPath(string host, string organization, string[] path) =>
      path is [var project, var git, var name, ..] && git.Equals("_git", StringComparison.OrdinalIgnoreCase) ? Of(host, organization, project, name) : null;

   // A decoded slash or backslash would move the parts of the path, a dot segment would collapse in the normalized URL, and a blank name or a control character names nothing.
   private static bool TryDecode(string segment, [NotNullWhen(true)] out string? name)
   {
      name = Uri.UnescapeDataString(segment);
      return !string.IsNullOrWhiteSpace(name) && name is not ("." or "..") && !name.Any(c => c is '/' or '\\' || char.IsControl(c));
   }

   // The only decode of each name, so a percent sign that the URL escapes stays part of the name.
   private static AzureDevOpsRepository? Of(string host, string organizationSegment, string projectSegment, string nameSegment)
   {
      if (!TryDecode(organizationSegment, out var organization) || !TryDecode(projectSegment, out var project) || !TryDecode(nameSegment, out var name))
      {
         return null;
      }

      var url = new Uri($"{OrganizationUrlOf(host, organization).AbsoluteUri}{Uri.EscapeDataString(project)}/_git/{Uri.EscapeDataString(name)}");
      return new AzureDevOpsRepository(organization, project, name, url);
   }

   private static Uri OrganizationUrlOf(string host, string organization) =>
      host == DevAzureHost ? new Uri($"https://{DevAzureHost}/{Uri.EscapeDataString(organization)}/") : new Uri($"https://{host}/");

   // Takes the ref from the first version parameter of a query, which may hold none.
   private static bool TryVersion(string query, out string? @ref)
   {
      @ref = null;
      var version = query.TrimStart('?').Split('&').Select(parameter => parameter.Split('=', 2)).FirstOrDefault(pair => pair is ["version", _]);
      if (version is null)
      {
         return true;
      }

      var value = Uri.UnescapeDataString(version[1]);
      if (value.StartsWith("GB", StringComparison.Ordinal) || value.StartsWith("GT", StringComparison.Ordinal))
      {
         @ref = RefName.IsValid(value[2..]) ? value[2..] : null;
         return @ref is not null;
      }

      if (value.StartsWith("GC", StringComparison.Ordinal) && CommitId.TryParse(value[2..], out var commit))
      {
         @ref = commit.Value;
         return true;
      }

      return false;
   }

   // Returns the organization, the project and the repository of an SSH remote, still percent-encoded: v3/{organization}/{project}/{repository} after the colon of
   // git@ssh.dev.azure.com: or of {organization}@vs-ssh.visualstudio.com:, or after ssh://git@ssh.dev.azure.com[:22]/.
   private static bool TrySshPath(string? text, [NotNullWhen(true)] out string[]? path)
   {
      path = null;
      if (text is null)
      {
         return false;
      }

      const string SshScheme = "ssh://";
      var ssh = text.StartsWith(SshScheme, StringComparison.OrdinalIgnoreCase);
      var start = ssh ? SshScheme.Length : 0;
      var at = text.IndexOf('@', start);
      var hostEnd = at < 0 ? -1 : text.IndexOf(ssh ? '/' : ':', at + 1);
      if (hostEnd < 0)
      {
         return false;
      }

      var user = text[start..at];
      var authority = text[(at + 1)..hostEnd];
      var rest = text[(hostEnd + 1)..];

      // A query or a fragment would follow the path of an ssh: URL, so the path holds neither.
      if (rest.Contains('?') || rest.Contains('#') || rest.Split('/') is not ["v3", var organization, _, _] parts)
      {
         return false;
      }

      var hostMatches = ssh
         ? user == SshUser && (Is(authority, SshHost) || Is(authority, $"{SshHost}:{SshPort}"))
         : (user == SshUser && Is(authority, SshHost)) || (Is(authority, VisualStudioSshHost) && Is(user, organization));
      path = hostMatches ? parts[1..] : null;
      return path is not null;
   }

   private static bool Is(string text, string expected) => string.Equals(text, expected, StringComparison.OrdinalIgnoreCase);
}
