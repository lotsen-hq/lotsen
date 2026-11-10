using System.Text.Json;
using Lotsen.Json;
using Lotsen.Tours;

namespace Lotsen.Tests;

/// <summary>Covers how a field path renders, and how it is taken below or put under another path.</summary>
[Trait("Category", "Rules")]
public sealed class FieldPathTests
{
   private static readonly FieldPath StopKind = FieldPath.Root.Then("stops").At(2).Then("kind");

   [Fact]
   public void Paths_render_with_dots_between_properties_and_brackets_around_indices_and_keys()
   {
      Assert.Equal("stops[2].kind", StopKind.ToString());
      Assert.Equal("checks[ab2c].question", ((FieldPath)"checks").Key("ab2c").Then("question").ToString());
      Assert.Equal("", FieldPath.Root.ToString());
      Assert.True(FieldPath.Root.IsRoot);
      Assert.False(StopKind.IsRoot);
   }

   [Theory]
   [InlineData("")]
   [InlineData("stops.kind")]
   [InlineData("stops[2]")]
   public void A_string_converts_only_when_it_names_one_property(string text) => Assert.Throws<ArgumentException>(() => (FieldPath)text);

   [Fact]
   public void After_returns_the_rest_of_the_path_below_the_prefix()
   {
      var stops = FieldPath.Root.Then("stops");

      Assert.Equal("kind", StopKind.After(stops.At(2))?.ToString());
      Assert.Equal("[2].kind", StopKind.After(stops)?.ToString());
      Assert.Equal(FieldPath.Root, StopKind.After(StopKind));
      Assert.Equal(StopKind, StopKind.After(FieldPath.Root));
   }

   [Fact]
   public void After_is_null_for_a_prefix_that_the_path_is_not_under()
   {
      Assert.Null(StopKind.After(FieldPath.Root.Then("stops").At(1)));
      Assert.Null(StopKind.After(FieldPath.Root.Then("stop")));
      Assert.Null(StopKind.After(FieldPath.Root.Then("stops").At(20)));
      Assert.Null(FieldPath.Root.After(FieldPath.Root.Then("stops")));
   }

   [Fact]
   public void Under_puts_the_path_below_the_prefix()
   {
      Assert.Equal("plan.stops[2].kind", StopKind.Under("plan").ToString());
      Assert.Equal("stops[2].kind", StopKind.After(FieldPath.Root.Then("stops"))!.Value.Under("stops").ToString());
      Assert.Equal(StopKind, StopKind.Under(FieldPath.Root));
      Assert.Equal("plan", FieldPath.Root.Under("plan").ToString());
   }

   [Fact]
   public void Json_writes_the_rendered_path_and_reads_it_back()
   {
      var json = JsonSerializer.Serialize(StopKind, LotsenJsonContext.Default.FieldPath);

      Assert.Equal("\"stops[2].kind\"", json);
      Assert.Equal(StopKind, JsonSerializer.Deserialize(json, LotsenJsonContext.Default.FieldPath));
   }
}
