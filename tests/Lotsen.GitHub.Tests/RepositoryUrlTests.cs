using Lotsen.GitHub;
using Lotsen.Platforms;

namespace Lotsen.Tests;

/// <summary>Covers which URLs count as GitHub repositories, and how they are taken apart into a repository and a ref, normalized, labeled and mentioned.</summary>
[Trait("Category", "Api")]
public sealed class RepositoryUrlTests
{
   private readonly GitHubPlatform _platform = FakeGitHub.Offline();

   [Theory]
   [InlineData("https://github.com/octo-org/hello-world")]
   [InlineData("https://github.com/octo-org/hello-world/")]
   [InlineData("https://github.com/octo-org/hello-world.git")]
   [InlineData("https://github.com/octo-org/hello-world.git/")]
   [InlineData("http://github.com/octo-org/hello-world")]
   [InlineData("https://www.github.com/octo-org/hello-world")]
   [InlineData("https://GitHub.com/octo-org/hello-world")]
   [InlineData("https://github.com:443/octo-org/hello-world")]
   [InlineData("https://github.com/octo-org/hello-world?tab=readme-ov-file")]
   [InlineData("https://github.com/octo-org/hello-world#readme")]
   public void Repository_url_is_parsed_normalized_and_labeled_without_a_ref(string text)
   {
      Assert.True(GitHubRepository.TryParse(text, out var repository, out var @ref));

      Assert.Null(@ref);
      Assert.Equal("octo-org", repository.Owner);
      Assert.Equal("hello-world", repository.Name);
      Assert.Equal("https://github.com/octo-org/hello-world", repository.Url.OriginalString);
      Assert.Equal("octo-org/hello-world", repository.Label);
   }

   [Theory]
   [InlineData("https://github.com/octo-org/hello-world/tree/main", "main")]
   [InlineData("https://github.com/octo-org/hello-world/tree/main/", "main")]
   [InlineData("https://github.com/octo-org/hello-world/tree/v1.2.0", "v1.2.0")]
   [InlineData("https://github.com/octo-org/hello-world/tree/feature/greeting", "feature/greeting")]
   [InlineData("https://github.com/octo-org/hello-world/tree/fix%231", "fix#1")]
   [InlineData("https://github.com/octo-org/hello-world/tree/0123456789abcdef0123456789abcdef01234567", "0123456789abcdef0123456789abcdef01234567")]
   [InlineData("https://github.com/octo-org/hello-world/tree/main?w=1#readme", "main")]
   public void Url_of_the_tree_at_a_ref_names_the_repository_and_the_ref(string text, string expected)
   {
      Assert.True(GitHubRepository.TryParse(text, out var repository, out var @ref));

      Assert.Equal("https://github.com/octo-org/hello-world", repository.Url.OriginalString);
      Assert.Equal(expected, @ref);
   }

   /// <summary>Only the API can tell a branch with a slash from a folder below a branch, so the whole rest is the ref, and a resolve finds no such ref.</summary>
   [Fact]
   public void Url_of_a_folder_at_a_ref_names_the_folder_as_part_of_the_ref()
   {
      Assert.True(GitHubRepository.TryParse("https://github.com/octo-org/hello-world/tree/main/src/app", out _, out var @ref));

      Assert.Equal("main/src/app", @ref);
   }

   [Theory]
   [InlineData("https://github.com/Octo-Org/Hello-World.git", "Octo-Org", "Hello-World", "https://github.com/Octo-Org/Hello-World")]
   [InlineData("https://github.com/octo/.github", "octo", ".github", "https://github.com/octo/.github")]
   [InlineData("https://github.com/octo/my_repo.js", "octo", "my_repo.js", "https://github.com/octo/my_repo.js")]
   public void Owner_and_name_keep_their_letter_case_and_characters(string text, string owner, string name, string normalized)
   {
      Assert.True(GitHubRepository.TryParse(text, out var repository, out _));

      Assert.Equal((owner, name, normalized), (repository.Owner, repository.Name, repository.Url.OriginalString));
   }

   [Theory]
   [InlineData(null)]
   [InlineData("")]
   [InlineData("github.com/octo/hello")]
   [InlineData("git@github.com:octo/hello.git")]
   [InlineData("ssh://git@github.com/octo/hello.git")]
   [InlineData("ftp://github.com/octo/hello")]
   [InlineData("https://github.com:8443/octo/hello")]
   [InlineData("https://user@github.com/octo/hello")]
   [InlineData("https://gist.github.com/octo/hello")]
   [InlineData("https://api.github.com/octo/hello")]
   [InlineData("https://github.com.evil.example/octo/hello")]
   [InlineData("https://gitlab.com/octo/hello")]
   [InlineData("https://github.com/")]
   [InlineData("https://github.com/octo")]
   [InlineData("https://github.com/octo/hello/pull/42")]
   [InlineData("https://github.com/octo/hello/issues/42")]
   [InlineData("https://github.com/octo/hello/blob/main/README.md")]
   [InlineData("https://github.com/octo/hello/commit/0123456789abcdef0123456789abcdef01234567")]
   [InlineData("https://github.com/octo/hello/tree")]
   [InlineData("https://github.com/octo/hello/tree/")]
   [InlineData("https://github.com/octo/hello/tree//main")]
   [InlineData("https://github.com/octo//hello")]
   [InlineData("https://github.com/octo/hello%20world")]
   [InlineData("https://github.com/octo/%2E%2E")]
   [InlineData("https://github.com/octo/.git")]
   [InlineData("https://github.com/octo/hello/tree/%20")]
   [InlineData("https://github.com/octo/hello/tree/%09")]
   [InlineData("https://github.com/octo/hello/tree/x%2F..%2F..%2Fheads%2Fmain")]
   [InlineData("https://github.com/octo/hello/tree/main..x")]
   [InlineData("https://github.com/octo/hello/tree/.hidden")]
   [InlineData("https://github.com/octo/hello/tree/v1.lock")]
   [InlineData("https://github.com/octo/hello/tree/a~1")]
   [InlineData("https://github.com/octo/hello/tree/a:b")]
   [InlineData("https://github.com/octo/hello/tree/a.")]
   [InlineData("https://github.com/octo/hello/tree/a@%7B1%7D")]
   [InlineData("https://github.com/octo/hello/tree/@")]
   [InlineData("https://github.com/octo/hello/tree/a%5Cb")]
   public void Other_url_is_not_a_repository(string? text)
   {
      Assert.False(GitHubRepository.TryParse(text, out _, out _));
   }

   [Fact]
   public void Platform_recognizes_labels_and_mentions_a_repository_url()
   {
      var recognized = _platform.RecognizeRepository("http://www.github.com/Octo-Org/Hello-World/tree/release/2.0?w=1");

      Assert.Equal(new RecognizedRepository(new Uri("https://github.com/Octo-Org/Hello-World"), "release/2.0"), recognized);
      Assert.Equal("Octo-Org/Hello-World", _platform.RepositoryLabel(recognized!.Url));
      Assert.Equal("https://github.com/Octo-Org/Hello-World/issues/7", _platform.RepositoryMentionUrl(recognized.Url, MentionKind.WorkItem, 7)?.AbsoluteUri);
      Assert.Equal("https://github.com/Octo-Org/Hello-World/pull/7", _platform.RepositoryMentionUrl(recognized.Url, MentionKind.PullRequest, 7)?.AbsoluteUri);
   }

   [Fact]
   public void Platform_neither_labels_nor_mentions_a_url_at_a_ref()
   {
      var url = new Uri("https://github.com/octo-org/hello-world/tree/main");

      Assert.Null(_platform.RepositoryLabel(url));
      Assert.Null(_platform.RepositoryMentionUrl(url, MentionKind.WorkItem, 7));
   }

   [Fact]
   public void Platform_names_the_repository_forms()
   {
      Assert.Equal(GitHubRepository.ExpectedForms, _platform.ExpectedRepositoryForms);
      Assert.StartsWith("https://github.com/{owner}/{repository}", _platform.ExpectedRepositoryForms, StringComparison.Ordinal);
   }
}
