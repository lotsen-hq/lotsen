using Lotsen.Git;

namespace Lotsen.Tests;

/// <summary>Covers the one rule for a repository path, <see cref="RepositoryPath.IsValid"/>, which the operations and the git layer both apply.</summary>
[Trait("Category", "Git")]
public sealed class RepositoryPathTests
{
   [Theory]
   [InlineData("a.txt", true)]
   [InlineData("src/nested/deep file.cs", true)]
   [InlineData("ünïcode.md", true)]
   [InlineData(".github/ci.yml", true)]
   [InlineData("a..b", true)]
   [InlineData(null, false)]
   [InlineData("", false)]
   [InlineData("   ", false)]
   [InlineData("/etc/passwd", false)]
   [InlineData("src/", false)]
   [InlineData("src//a.cs", false)]
   [InlineData("./a.cs", false)]
   [InlineData("src/../secret", false)]
   [InlineData("..", false)]
   [InlineData("src\\a.cs", false)]
   [InlineData("c:a.cs", false)]
   [InlineData("C:/a.cs", false)]
   public void Path_is_valid_only_when_relative_with_forward_slashes_and_plain_segments(string? path, bool valid) => Assert.Equal(valid, RepositoryPath.IsValid(path));
}
