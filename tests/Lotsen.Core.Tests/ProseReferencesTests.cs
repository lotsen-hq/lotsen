using Lotsen.Prose;
using Lotsen.Tours;

namespace Lotsen.Tests;

/// <summary>Covers how <see cref="ProseReferences"/> reads the code references of the <c>ref:</c> links in prose, and in each prose field of a stop's content.</summary>
[Trait("Category", "Markdown")]
public sealed class ProseReferencesTests
{
   [Fact]
   public void Content_with_a_link_in_every_prose_field_gives_each_reference_under_its_field_in_field_order()
   {
      var content = new StopContent(
         "[a](ref:head/a.txt#L1-1)",
         [TourValues.Passage()],
         "[b](ref:head/a.txt#L2-2)",
         "[c](ref:head/a.txt#L3-3)",
         "[d](ref:head/a.txt#L4-4)",
         "[e](ref:head/a.txt#L5-5)",
         [new StopCheck("c1", "[f](ref:head/a.txt#L6-6)", "[g](ref:head/a.txt#L7-7)"), new StopCheck("c2", "[h](ref:base/a.txt#L8-8)", "[i](ref:base/a.txt#L9-9)")],
         "[j](ref:head/a.txt#L10-10)");

      var references = ProseReferences.Of(content).Value;

      Assert.NotNull(references);
      Assert.Equal(
         [
            ("headline", 1), ("how", 2), ("why", 3), ("designChoices", 4), ("pinnedBy", 5), ("checks[c1].question", 6), ("checks[c1].recommendation", 7), ("checks[c2].question", 8),
            ("checks[c2].recommendation", 9), ("scenario", 10),
         ],
         references.Select(reference => (reference.Field.ToString(), reference.Reference.StartLine)));
   }

   [Fact]
   public void Malformed_link_is_the_fault_of_its_field_and_names_the_link()
   {
      var read = ProseReferences.Read([("how", "See [x](ref:head/a.txt#L1-1)."), ("why", "See [y](ref:head/a.txt#L5-3).")]);

      Assert.Null(read.Value);
      Assert.Equal("why", read.Fault!.Path.ToString());
      Assert.StartsWith("the link 'ref:head/a.txt#L5-3' is not a well-formed code reference.", read.Fault.Problem, StringComparison.Ordinal);
   }

   [Fact]
   public void Prose_that_nests_too_deep_is_the_fault_of_its_field()
   {
      var tooDeep = string.Concat(Enumerable.Repeat("> ", ProseRenderer.MaxDepth + 1)) + "x";

      var read = ProseReferences.Read([("text", tooDeep)]);

      Assert.Equal("text", read.Fault!.Path.ToString());
   }

   [Fact]
   public void Prose_without_links_has_no_references()
   {
      Assert.Equal([], ProseReferences.Read([("text", "No `ref:head/a.txt#L1-1` link.")]).Value!);
   }
}
