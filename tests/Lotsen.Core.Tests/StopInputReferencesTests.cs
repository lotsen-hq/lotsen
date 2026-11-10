using Lotsen.Operations;
using Lotsen.Tours;
using static Lotsen.Tests.TourValues;

namespace Lotsen.Tests;

/// <summary>Covers how <see cref="StopRules.ReadReferences(PutStopInput)"/> names each code reference of <c>put_stop</c>'s input by the input path of its field.</summary>
[Trait("Category", "Rules")]
public sealed class StopInputReferencesTests
{
   [Fact]
   public void Reference_in_a_check_is_named_by_the_checks_index_in_the_input()
   {
      var input = new PutStopInput(Id, "s1", "h", Checks: [new CheckInput("q"), new CheckInput("q", "[r](ref:head/a.txt#L1-1)", Id: "kept"), new CheckInput("[q](ref:head/a.txt#L2-2)")]);

      var references = StopRules.ReadReferences(input).Value;

      Assert.Equal(["checks[1].recommendation", "checks[2].question"], references!.Select(reference => reference.Field.ToString()));
   }

   /// <summary>The input breaks rules that <see cref="StopRules.CheckFields"/> refuses, so the method names each field by its index without relying on that check.</summary>
   [Fact]
   public void Checks_that_share_an_ID_and_a_null_passage_still_give_each_reference_under_its_own_field()
   {
      var input = new PutStopInput(
         Id,
         "s1",
         "h",
         Checks: [new CheckInput("[a](ref:head/a.txt#L1-1)", Id: "c1"), new CheckInput("[b](ref:head/a.txt#L2-2)", Id: "c1")],
         Passages: [null!]);

      var references = StopRules.ReadReferences(input).Value;

      Assert.Equal(["checks[0].question", "checks[1].question"], references!.Select(reference => reference.Field.ToString()));
   }
}
