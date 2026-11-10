using Lotsen.Git;

namespace Lotsen.Tests;

/// <summary>Covers which texts name a commit by its full object ID.</summary>
[Trait("Category", "Git")]
public sealed class CommitIdTests
{
   [Theory]
   [InlineData("0123456789abcdef0123456789abcdef01234567")]
   [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
   public void Full_hexadecimal_object_id_is_a_commit_id(string text)
   {
      Assert.True(CommitId.TryParse(text, out var id));
      Assert.Equal(text, id.Value);
   }

   [Fact]
   public void Upper_case_object_id_is_kept_in_lower_case()
   {
      Assert.Equal(new string('a', 40), CommitId.Parse(new string('A', 40)).Value);
   }

   [Theory]
   [InlineData(null)]
   [InlineData("")]
   [InlineData("0123456")]
   [InlineData("main")]
   [InlineData("--output=/tmp/x")]
   [InlineData("0123456789abcdef0123456789abcdef0123456g")]
   [InlineData("0123456789abcdef0123456789abcdef01234567 ")]
   public void Anything_else_is_not_a_commit_id(string? text)
   {
      Assert.False(CommitId.TryParse(text, out _));
   }
}
