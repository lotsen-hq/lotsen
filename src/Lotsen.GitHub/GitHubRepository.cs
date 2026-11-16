using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Lotsen.Git;

namespace Lotsen.GitHub;

/// <summary>A repository on github.com, taken apart from a web or clone URL that names it.</summary>
/// <param name="Owner">The user or organization that owns the repository, in the letter case of the URL.</param>
/// <param name="Name">The repository's name, in the letter case of the URL.</param>
/// <param name="Url">The normalized URL: <c>https://github.com/{owner}/{repository}</c>, with no <c>.git</c>, path below the name, query or fragment.</param>
internal sealed record GitHubRepository(string Owner, string Name, Uri Url)
{
   /// <summary>The URL forms that <see cref="TryParse"/> accepts, for messages.</summary>
   public const string ExpectedForms =
      "https://github.com/{owner}/{repository}, optionally ending in .git or followed by /tree/{ref}, or an SSH remote, git@github.com:{owner}/{repository} or ssh://git@github.com/{owner}/{repository}, optionally ending in .git";

   private const string Host = "github.com";
   private const string WwwHost = "www.github.com";
   private const string GitSuffix = ".git";
   private const string SshUser = "git";
   private const int SshPort = 22;

   private static readonly SearchValues<char> NameCharacters = SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.");

   /// <summary>The short name of the repository, such as <c>octo-org/hello-world</c>.</summary>
   public string Label => $"{Owner}/{Name}";

   /// <summary>Returns whether the other names the same repository, with the owners and the names compared in any letter case.</summary>
   public bool IsSame(GitHubRepository other) => string.Equals(Owner, other.Owner, StringComparison.OrdinalIgnoreCase) && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

   /// <summary>Returns the web URL of an issue in the repository: <c>https://github.com/{owner}/{repository}/issues/{number}</c>.</summary>
   public Uri IssueUrl(int number) => new($"https://{Host}/{Owner}/{Name}/issues/{number}");

   /// <summary>Returns the web URL of a pull request in the repository: <c>https://github.com/{owner}/{repository}/pull/{number}</c>.</summary>
   public Uri PullRequestUrl(int number) => new($"https://{Host}/{Owner}/{Name}/pull/{number}");

   /// <summary>Returns whether the text is a github.com URL of one repository, or of the repository's tree at a ref, or an SSH remote of one repository.</summary>
   /// <remarks>
   /// <para>
   /// An SSH remote, as <c>git remote get-url</c> prints it for a clone over SSH, names the repository by the same normalized https URL, so its tour clones over https with lotsen's sign-in.
   /// It names no ref.
   /// </para>
   /// <para>
   /// Everything after <c>/tree/</c> is the ref, so a ref may hold slashes. A web URL of a folder at a ref therefore yields a ref that names the folder too, and no branch or tag of that name
   /// exists then.
   /// </para>
   /// </remarks>
   /// <param name="text">The text.</param>
   /// <param name="repository">The repository, or null when the text names none.</param>
   /// <param name="ref">The ref after <c>/tree/</c>, with its percent-encoding decoded, or null when the URL names no ref. A URL whose ref <see cref="RefName.IsValid"/> refuses names no repository.</param>
   public static bool TryParse([NotNullWhen(true)] string? text, [NotNullWhen(true)] out GitHubRepository? repository, out string? @ref)
   {
      repository = null;
      @ref = null;
      if (TrySshPath(text, out var sshPath))
      {
         repository = sshPath.Split('/') is [var sshOwner, var sshName] ? OfClone(sshOwner, sshName) : null;
         return repository is not null;
      }

      if (!TrySegments(text, out var segments))
      {
         return false;
      }

      // A trailing slash leaves an empty last segment, and an empty segment anywhere else names nothing.
      var parts = segments[1..] is [.. var rest, ""] ? rest : segments[1..];
      switch (parts)
      {
         case [var owner, var name] when OfClone(owner, name) is { } clone:
            repository = clone;
            return true;
         case [var owner, var name, "tree", .. var refSegments] when IsName(owner) && IsName(name)
            && string.Join('/', refSegments.Select(Uri.UnescapeDataString)) is var decoded && RefName.IsValid(decoded):
            repository = Of(owner, name);
            @ref = decoded;
            return true;
         default:
            return false;
      }
   }

   /// <summary>Returns the segments of the path of a github.com web URL, the empty one before its leading slash first.</summary>
   /// <returns>Whether the text is an absolute <c>https:</c> or <c>http:</c> URL of github.com on the default port, without user information.</returns>
   internal static bool TrySegments([NotNullWhen(true)] string? text, [NotNullWhen(true)] out string[]? segments)
   {
      segments = null;
      if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
         || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
         || !uri.IsDefaultPort
         || uri.UserInfo.Length > 0
         || uri.Host is not (Host or WwwHost))
      {
         return false;
      }

      segments = uri.AbsolutePath.Split('/');
      return true;
   }

   /// <summary>Returns whether a path segment is an owner's or a repository's name.</summary>
   /// <remarks>A name of these characters needs no escaping in a URL, and a name that is a dot segment would move the parts of the path.</remarks>
   internal static bool IsName(string segment) => segment.Length > 0 && segment is not ("." or "..") && !segment.AsSpan().ContainsAnyExcept(NameCharacters);

   // Returns the path of an SSH remote of github.com for the git user, without its leading slash: the part after the colon of git@github.com:{path}, or of ssh://git@github.com[:22]/{path}.
   // The path is the text as git sends it, so a caller that checks its segments sees the repository that git reaches.
   private static bool TrySshPath(string? text, [NotNullWhen(true)] out string? path)
   {
      path = null;
      if (text is null)
      {
         return false;
      }

      const string SshScheme = "ssh://";
      var userAt = $"{SshUser}@";
      var ssh = text.StartsWith(SshScheme, StringComparison.OrdinalIgnoreCase);
      var start = ssh ? SshScheme.Length : 0;
      if (!text.AsSpan(start).StartsWith(userAt, StringComparison.Ordinal))
      {
         return false;
      }

      var hostStart = start + userAt.Length;
      var hostEnd = text.IndexOf(ssh ? '/' : ':', hostStart);
      if (hostEnd < 0)
      {
         return false;
      }

      var authority = text[hostStart..hostEnd];
      var hostMatches = string.Equals(authority, Host, StringComparison.OrdinalIgnoreCase)
         || (ssh && string.Equals(authority, $"{Host}:{SshPort}", StringComparison.OrdinalIgnoreCase));
      if (!hostMatches)
      {
         return false;
      }

      path = text[(hostEnd + 1)..];
      return true;
   }

   // A clone URL ends in .git, which is no part of the name.
   private static GitHubRepository? OfClone(string owner, string name) =>
      IsName(owner) && (name.EndsWith(GitSuffix, StringComparison.Ordinal) ? name[..^GitSuffix.Length] : name) is var bare && IsName(bare) ? Of(owner, bare) : null;

   private static GitHubRepository Of(string owner, string name) => new(owner, name, new Uri($"https://{Host}/{owner}/{name}"));
}
