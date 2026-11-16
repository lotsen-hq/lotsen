using System.Diagnostics;
using Lotsen.Git;

namespace Lotsen.Tests;

/// <summary>Covers which names <see cref="RefName.IsValid"/> accepts as a branch's or a tag's name, and that git's own check agrees.</summary>
[Trait("Category", "Platform")]
public sealed class RefNameTests
{
   public static TheoryData<string> Valid => ["main", "feature/x", "release/2.0/rc", "v1.2.0", "a@b", "lock", "0123456789abcdef0123456789abcdef01234567", "ü"];

   public static TheoryData<string> Invalid =>
   [
      "", " ", "\t", "a b", "a\nb", "a\u007fb", "..", "x/../y", "x/../../heads/main", "a..b", ".", ".hidden", "a/.b", "a.lock", "a/b.lock/c", ".lock", "a~1", "a^", "a:b", "a?", "a*", "a[",
      "/a", "a/", "a//b", "a.", "a@{1}", "@", "a\\b",
   ];

   [Theory]
   [MemberData(nameof(Valid))]
   public void Name_that_git_accepts_is_valid(string name)
   {
      Assert.True(RefName.IsValid(name));
   }

   [Theory]
   [MemberData(nameof(Invalid))]
   public void Name_that_git_refuses_is_invalid(string name)
   {
      Assert.False(RefName.IsValid(name));
   }

   /// <summary>Git accepts whitespace and format characters other than ASCII in a name, which a repository target's ref never holds.</summary>
   [Theory]
   [InlineData("\u00a0")]
   [InlineData("a\u2003b")]
   [InlineData("\u200b")]
   [InlineData("main\u200b")]
   [InlineData("\u202e")]
   [InlineData("ma\u202ein")]
   public void Name_with_any_whitespace_or_format_character_is_invalid(string name)
   {
      Assert.False(RefName.IsValid(name));
   }

   [Fact]
   public void Null_is_invalid()
   {
      Assert.False(RefName.IsValid(null));
   }

   [Theory]
   [MemberData(nameof(Valid))]
   [MemberData(nameof(Invalid))]
   public async Task Git_agrees(string name)
   {
      var startInfo = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
      foreach (var argument in new[] { "check-ref-format", "--allow-onelevel", name })
      {
         startInfo.ArgumentList.Add(argument);
      }

      using var git = Process.Start(startInfo)!;
      await git.WaitForExitAsync(TestContext.Current.CancellationToken);

      Assert.Equal(RefName.IsValid(name), git.ExitCode == 0);
   }
}
