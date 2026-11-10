using Lotsen.Tours;

namespace Lotsen.Tests;

/// <summary>Covers which text the tour store accepts as a tour ID.</summary>
[Trait("Category", "Store")]
public sealed class TourIdTests
{
   [Fact]
   public void New_id_parses_back_to_itself()
   {
      var id = TourId.New();

      Assert.True(TourId.TryParse(id.ToString(), out var parsed));
      Assert.Equal(id, parsed);
   }

   [Fact]
   public void New_ids_are_url_safe_and_differ()
   {
      var ids = Enumerable.Range(0, 1000).Select(_ => TourId.New().ToString()).ToList();

      Assert.Equal(ids.Count, ids.Distinct().Count());
      Assert.All(ids, id => Assert.Equal(Uri.EscapeDataString(id), id));
   }

   [Theory]
   [InlineData(null)]
   [InlineData("")]
   [InlineData("..")]
   [InlineData("../../etc/passwd")]
   [InlineData("..\\..\\windows")]
   [InlineData("abcdefgh/ijklmnop")]
   [InlineData("ABCDEFGHIJKLMNOP")]
   [InlineData("abcdefghijklmno")]
   [InlineData("abcdefghijklmnopq")]
   [InlineData("abcdefghijklmn01")]
   [InlineData("abcdefghijklmno.")]
   public void Malformed_id_is_refused(string? text)
   {
      Assert.False(TourId.TryParse(text, out _));
   }
}
