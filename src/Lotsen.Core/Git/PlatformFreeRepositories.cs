using Lotsen.Credentials;
using Lotsen.Hosting;
using Lotsen.Platforms;

namespace Lotsen.Git;

/// <summary>Reaches a platform-free repository with git alone: recognizes, labels and compares its URL, resolves its refs and supplies the credential of its remote.</summary>
/// <remarks>
/// Every git call to the remote carries <see cref="Credential"/>, which has no answer and keeps the user's git configuration and netrc file out of the call, as
/// <see cref="AnswerlessGitCredential"/> says. So when the remote, the target of its redirect or a proxy asks for a credential, the call fails with <see cref="PrivateRefusal"/>.
/// </remarks>
internal sealed class PlatformFreeRepositories
{
   /// <summary>The URL form that a platform-free repository takes, written for a refusal.</summary>
   public const string ExpectedForms = "the https URL of a public git repository on any other host, such as https://git.example.com/{path}";

   /// <summary>The reason of a git call to a platform-free remote in which the remote, the target of its redirect or a proxy asked git for a credential.</summary>
   public const string PrivateRefusal =
      "Git was asked for a credential, by the repository or by a proxy, but lotsen sends none to a repository without a platform, so only a public one works. "
      + "Give the URL of a public repository, or of a repository on a platform that lotsen supports.";

   /// <summary>The reason of a pin whose target has no platform and a URL that <see cref="Recognize"/> does not accept, which only an edited tour file holds.</summary>
   public const string UnreachableRefusal =
      "The tour's repository URL is not the https URL of a git repository that lotsen reaches without a platform, so lotsen does not read it. Create a tour of a repository URL that lotsen recognizes.";

   private readonly DataDirectory _dataDirectory;
   private readonly GitRunner _git;

   public PlatformFreeRepositories(DataDirectory dataDirectory, GitRunner git)
   {
      _dataDirectory = dataDirectory;
      _git = git;
   }

   /// <summary>The credential of every platform-free remote, which has no answer and fails with <see cref="PrivateRefusal"/>.</summary>
   public static IGitCredential Credential { get; } = new AnswerlessGitCredential(PrivateRefusal);

   /// <summary>Returns the normalized URL of the repository that the text names, when the text is a URL that git can clone from without a credential.</summary>
   /// <remarks>
   /// The text is an <c>https</c> URL, or an <c>http</c> URL to a loopback address, as <see cref="GitRemote"/> allows, with a path and with no user information, query or fragment. Its host is in
   /// canonical form: no dot at its end and nothing that IDNA maps to another name, because a platform owns a host only in that form. The normalized URL has the host in lowercase, no default port
   /// and no trailing slash. It keeps the path's letter case and any <c>.git</c> at its end, because git clones from the path as it is.
   /// </remarks>
   /// <returns>The URL, or null when the text is no such URL.</returns>
   public static Uri? Recognize(string text)
   {
      if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
         || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
         || uri.UserInfo.Length > 0
         || uri.Query.Length > 0
         || uri.Fragment.Length > 0
         || (uri.HostNameType == UriHostNameType.Dns && (uri.Host.EndsWith('.') || uri.Host != uri.IdnHost)))
      {
         return null;
      }

      var path = uri.AbsolutePath.TrimEnd('/');
      return path.Length == 0 ? null : new Uri($"{uri.Scheme}://{uri.Authority}{path}");
   }

   /// <summary>Returns the short name of a repository at a URL that <see cref="Recognize"/> returned: its host, with a port other than the default, and its path, such as <c>git.example.com/team/shop.git</c>.</summary>
   public static string Label(Uri url) => url.Authority + Uri.UnescapeDataString(url.AbsolutePath);

   /// <summary>Returns whether two URLs that <see cref="Recognize"/> returned name the same repository: whether they are equal.</summary>
   public static bool IsSame(Uri url, Uri other) => string.Equals(url.AbsoluteUri, other.AbsoluteUri, StringComparison.Ordinal);

   /// <summary>Returns the commit that a ref of the repository at a URL that <see cref="Recognize"/> returned points to, as git lists the remote's refs, with no credential.</summary>
   /// <remarks>
   /// The ref follows the rules of <see cref="IRepositoryPlatform.ResolveRepositoryAsync"/>. A full commit ID names that commit without a request, so the clone's fetch of the commit fails when the
   /// remote lacks it.
   /// </remarks>
   /// <param name="url">The repository's URL, which is also its clone URL.</param>
   /// <param name="ref">A branch, a tag or a full commit ID, or null for the repository's default branch.</param>
   /// <param name="cancellationToken">Stops git.</param>
   /// <exception cref="GitException">
   /// The URL is not one that <see cref="Recognize"/> returns, with <see cref="UnreachableRefusal"/> and before git runs, or the ref names nothing, or git could not list the remote's refs.
   /// </exception>
   /// <exception cref="AccessTokenUnavailableException">The remote asked for a credential. The reason is <see cref="PrivateRefusal"/>.</exception>
   public async Task<ResolvedRepository> ResolveAsync(Uri url, string? @ref, CancellationToken cancellationToken)
   {
      // A stored target is read back without Recognize, so an edited tour file could point git at a local path or another protocol.
      if (Recognize(url.AbsoluteUri) is null)
      {
         throw new GitException(UnreachableRefusal);
      }

      if (@ref is null)
      {
         var listed = await ListAsync(url, ["--symref", "--", url.AbsoluteUri, "HEAD"], cancellationToken);
         var head = listed.FirstOrDefault(line => line.Name == "HEAD" && line.Value.StartsWith("ref: refs/heads/", StringComparison.Ordinal));
         var commit = listed.FirstOrDefault(line => line.Name == "HEAD" && line.Commit is not null).Commit;
         if (head.Value is null || commit is not { } id)
         {
            throw new GitException("The repository names no default branch with a commit. Give the branch, tag or full commit ID to tour.");
         }

         return new ResolvedRepository(url, head.Value["ref: refs/heads/".Length..], RepositoryRefKind.Branch, id);
      }

      if (CommitId.TryParse(@ref, out var full))
      {
         return new ResolvedRepository(url, full.Value, RepositoryRefKind.Commit, full);
      }

      if (!RefName.IsValid(@ref))
      {
         throw new GitException($"'{@ref}' is not a branch, tag or full commit ID that git accepts. Give the ref's name as git shows it.");
      }

      var refs = await ListAsync(url, ["--", url.AbsoluteUri, $"refs/tags/{@ref}", $"refs/tags/{@ref}^{{}}", $"refs/heads/{@ref}"], cancellationToken);
      CommitId? Find(string name) => refs.FirstOrDefault(line => line.Name == name).Commit;

      // An annotated tag lists its own object, and then the commit it names under the name with ^{}.
      if ((Find($"refs/tags/{@ref}^{{}}") ?? Find($"refs/tags/{@ref}")) is { } tagged)
      {
         return new ResolvedRepository(url, @ref, RepositoryRefKind.Tag, tagged);
      }

      return Find($"refs/heads/{@ref}") is { } branch
         ? new ResolvedRepository(url, @ref, RepositoryRefKind.Branch, branch)
         : throw new GitException($"The repository has no branch or tag '{@ref}'. Give a branch, a tag or a full commit ID that the repository has.");
   }

   // Returns the lines of git ls-remote, each a name with the commit or the symbolic ref that it lists for the name.
   private async Task<IReadOnlyList<ListedRef>> ListAsync(Uri url, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
   {
      _dataDirectory.EnsureExists();
      var output = await _git.RunAsync(
         new GitCall($"Git could not list the refs of {url.AbsoluteUri}", ["ls-remote", .. arguments], _git.Options.QueryTimeout)
         {
            WorkingDirectory = _dataDirectory.Path,
            Remote = new GitRemote(url, Credential),
         },
         cancellationToken);
      return [.. output.Text
         .Split('\n', StringSplitOptions.RemoveEmptyEntries)
         .Select(line => line.Split('\t', 2))
         .Where(fields => fields.Length == 2)
         .Select(fields => new ListedRef(fields[1], fields[0], CommitId.TryParse(fields[0], out var commit) ? commit : null))];
   }

   private readonly record struct ListedRef(string Name, string Value, CommitId? Commit);
}
