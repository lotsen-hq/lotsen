using Lotsen.Operations;
using Lotsen.Prose;
using Lotsen.Tours;

namespace Lotsen.Tests;

/// <summary>Covers how a <c>ref:</c> link's destination is read as a code reference, and the reason given for one that cannot be read.</summary>
[Trait("Category", "Markdown")]
public sealed class CodeReferenceTests
{
   [Theory]
   [InlineData("ref:head/src/App.cs#L3-5", "Head", "src/App.cs", 3, 5)]
   [InlineData("ref:base/README.md#L1-1", "Base", "README.md", 1, 1)]
   [InlineData("REF:head/a.cs#L2-4", "Head", "a.cs", 2, 4)]
   [InlineData("ref:head/src/My%20File.cs#L10-20", "Head", "src/My File.cs", 10, 20)]
   [InlineData("ref:head/docs/r%C3%A4ksm%C3%B6rg%C3%A5s.md#L7-8", "Head", "docs/räksmörgås.md", 7, 8)]
   [InlineData("ref:base/a%23b%25c.txt#L1-2", "Base", "a#b%c.txt", 1, 2)]
   [InlineData("ref:head/dir%2Ffile.cs#L1-2", "Head", "dir/file.cs", 1, 2)]
   [InlineData("ref:head/a.cs#L007-9", "Head", "a.cs", 7, 9)]
   public void Well_formed_link_is_read(string link, string side, string path, int startLine, int endLine)
   {
      Assert.True(CodeReference.TryParse(link, out var reference, out var reason), reason);
      Assert.Equal(Enum.Parse<FileSide>(side), reference.Side);
      Assert.Equal(path, reference.Path);
      Assert.Equal(startLine, reference.StartLine);
      Assert.Equal(endLine, reference.EndLine);
   }

   [Theory]
   [InlineData("Head", "src/App.cs", 3, 5, "ref:head/src/App.cs#L3-5")]
   [InlineData("Base", "src/My File (1).cs", 1, 1, "ref:base/src/My%20File%20%281%29.cs#L1-1")]
   [InlineData("Head", "docs/räksmörgås.md", 7, 8, "ref:head/docs/r%C3%A4ksm%C3%B6rg%C3%A5s.md#L7-8")]
   [InlineData("Base", "a#b%c<d>.txt", 1, 2, "ref:base/a%23b%25c%3Cd%3E.txt#L1-2")]
   public void Formatted_link_reads_back_to_its_lines(string side, string path, int startLine, int endLine, string expected)
   {
      var link = CodeReference.Format(Enum.Parse<FileSide>(side), path, startLine, endLine);

      Assert.Equal(expected, link);
      Assert.True(CodeReference.TryParse(link, out var reference, out var reason), reason);
      Assert.Equal((Enum.Parse<FileSide>(side), path, startLine, endLine), (reference.Side, reference.Path, reference.StartLine, reference.EndLine));
   }

   [Theory]
   [InlineData("https://example.com/a.cs#L1-2", "ref:")]
   [InlineData("ref:", "side")]
   [InlineData("ref:a.cs#L1-2", "side")]
   [InlineData("ref:/a.cs#L1-2", "side")]
   [InlineData("ref:src/a.cs#L1-2", "side")]
   [InlineData("ref:Head/a.cs#L1-2", "side")]
   [InlineData("ref:head/#L1-2", "path")]
   [InlineData("ref:head/a.cs", "lines")]
   [InlineData("ref:head/a.cs#", "lines")]
   [InlineData("ref:head/a.cs#L5", "lines")]
   [InlineData("ref:head/a.cs#5-6", "lines")]
   [InlineData("ref:head/a.cs#l5-6", "lines")]
   [InlineData("ref:head/a.cs#L5-", "lines")]
   [InlineData("ref:head/a.cs#L-5-6", "lines")]
   [InlineData("ref:head/a.cs#L+5-6", "lines")]
   [InlineData("ref:head/a.cs#L5-6-7", "lines")]
   [InlineData("ref:head/a.cs#L5 -6", "lines")]
   [InlineData("ref:head/a.cs#L٣-٤", "lines")]
   [InlineData("ref:head/a.cs#L6-5", "end line")]
   [InlineData("ref:head/a.cs#L0-5", "start at 1")]
   [InlineData("ref:head/a.cs#L1-99999999999", "too large")]
   [InlineData("ref:head/a%2.cs#L1-2", "percent")]
   [InlineData("ref:head/a%G0.cs#L1-2", "percent")]
   [InlineData("ref:head/a.cs%#L1-2", "percent")]
   [InlineData("ref:head/a%FF.cs#L1-2", "UTF-8")]
   [InlineData("ref:head/a%00.cs#L1-2", "control")]
   [InlineData("ref:head/../secret.txt#L1-2", "path")]
   [InlineData("ref:head/a%2F..%2F..%2Fsecret.txt#L1-2", "path")]
   [InlineData("ref:head//a.cs#L1-2", "path")]
   [InlineData("ref:head/src\\a.cs#L1-2", "path")]
   public void Malformed_link_is_refused_with_a_reason(string link, string reasonMentions)
   {
      Assert.False(CodeReference.TryParse(link, out _, out var reason));
      Assert.Contains(reasonMentions, reason, StringComparison.Ordinal);
   }
}
