using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Lotsen.GitHub;

/// <summary>
/// A pull request on github.com, taken apart from a web URL that names it.
/// </summary>
/// <param name="Owner">The user or organization that owns the repository, in the letter case of the URL.</param>
/// <param name="Repository">The repository's name, in the letter case of the URL.</param>
/// <param name="Number">The pull request's number, from 1.</param>
/// <param name="Url">The normalized URL: <c>https://github.com/{owner}/{repository}/pull/{number}</c>, with no path below the number, query, fragment or leading zero.</param>
internal sealed record GitHubPullRequest(string Owner, string Repository, int Number, Uri Url)
{
   /// <summary>The URL forms that <see cref="TryParse"/> accepts, for messages.</summary>
   public const string ExpectedForms = "https://github.com/{owner}/{repository}/pull/{number}, optionally followed by a path such as /files";

   private const string Host = "github.com";

   /// <summary>Returns the web URL of an issue in the pull request's repository: <c>https://github.com/{owner}/{repository}/issues/{number}</c>.</summary>
   public Uri IssueUrl(int number) => new($"https://{Host}/{Owner}/{Repository}/issues/{number}");

   /// <summary>Returns the web URL of a pull request in the pull request's repository: <c>https://github.com/{owner}/{repository}/pull/{number}</c>.</summary>
   public Uri PullRequestUrl(int number) => new($"https://{Host}/{Owner}/{Repository}/pull/{number}");

   /// <summary>The short name of the pull request, such as <c>octo-org/hello-world #42</c>.</summary>
   public string Label => $"{Owner}/{Repository} #{Number}";

   /// <summary>Returns whether the other pull request is this one.</summary>
   /// <remarks>The owner and the repository compare in any letter case, and the number exactly.</remarks>
   public bool IsSame(GitHubPullRequest other) =>
      Number == other.Number && string.Equals(Owner, other.Owner, StringComparison.OrdinalIgnoreCase) && string.Equals(Repository, other.Repository, StringComparison.OrdinalIgnoreCase);

   /// <summary>Returns whether the text is a github.com web URL of one pull request, or of a page below it such as its changed files.</summary>
   /// <remarks>An <c>/issues/{number}</c> URL is refused even when the number names a pull request, because only the API can tell the two apart.</remarks>
   public static bool TryParse([NotNullWhen(true)] string? text, [NotNullWhen(true)] out GitHubPullRequest? pullRequest)
   {
      pullRequest = null;

      // Any segments after the number name a page of the pull request.
      if (!GitHubRepository.TrySegments(text, out var segments)
         || segments.Length < 5
         || !GitHubRepository.IsName(segments[1])
         || !GitHubRepository.IsName(segments[2])
         || segments[3] != "pull"
         || !int.TryParse(segments[4], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
         || number <= 0)
      {
         return false;
      }

      var owner = segments[1];
      var repository = segments[2];
      pullRequest = new GitHubPullRequest(owner, repository, number, new Uri($"https://{Host}/{owner}/{repository}/pull/{number}"));
      return true;
   }
}
