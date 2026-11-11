using Lotsen.Prose;
using Lotsen.Tours;

namespace Lotsen.Tests;

/// <summary>Covers <see cref="StopProse"/> and <see cref="ProseField"/> on content that sets every prose field, with two checks, each row one prose field by its path.</summary>
[Trait("Category", "Rules")]
public sealed class StopProseTests
{
   private static readonly StopContent Full = new(
      "*headline*",
      [TourValues.Passage()],
      "*how*",
      "*why*",
      "*design choices*",
      "*pinned by*",
      [new StopCheck("c1", "*question 1*", "*recommendation 1*", [new SuggestedAnswer("*suggestion*", AnswerType.Decision)]), new StopCheck("c2", "*question 2*", "*recommendation 2*")],
      "*scenario*",
      ["tag"],
      StopPlacement.Inline);

   private static readonly Dictionary<string, (ProseField Field, string Prose)> Expected = new()
   {
      ["headline"] = (new ProseField.Headline(), "*headline*"),
      ["how"] = (new ProseField.How(), "*how*"),
      ["why"] = (new ProseField.Why(), "*why*"),
      ["designChoices"] = (new ProseField.DesignChoices(), "*design choices*"),
      ["pinnedBy"] = (new ProseField.PinnedBy(), "*pinned by*"),
      ["checks[c1].question"] = (new ProseField.CheckQuestion("c1"), "*question 1*"),
      ["checks[c1].recommendation"] = (new ProseField.CheckRecommendation("c1"), "*recommendation 1*"),
      ["checks[c2].question"] = (new ProseField.CheckQuestion("c2"), "*question 2*"),
      ["checks[c2].recommendation"] = (new ProseField.CheckRecommendation("c2"), "*recommendation 2*"),
      ["scenario"] = (new ProseField.Scenario(), "*scenario*"),
   };

   public static TheoryData<string> Rows => [.. Expected.Keys];

   [Fact]
   public void Fields_names_every_prose_field_of_the_content_in_field_order()
   {
      Assert.Equal(Expected.Values, StopProse.Fields(Full));
   }

   [Fact]
   public void Fields_leaves_out_each_optional_field_that_is_null()
   {
      var content = new StopContent("*headline*", [TourValues.Passage()], Checks: [new StopCheck("c1", "*question*")]);

      Assert.Equal([(new ProseField.Headline(), "*headline*"), (new ProseField.CheckQuestion("c1"), "*question*")], StopProse.Fields(content));
   }

   [Fact]
   public void Map_with_the_identity_returns_equal_content()
   {
      Assert.Equivalent(Full, StopProse.Map(Full, (_, prose) => prose), strict: true);
   }

   [Fact]
   public void Map_writes_each_fields_mapped_prose_back_into_that_field_and_keeps_the_rest_of_the_content()
   {
      var mapped = StopProse.Map(Full, (field, prose) => $"{field.Path}={prose}");

      Assert.Equal(Expected.Select(row => (row.Value.Field, $"{row.Key}={row.Value.Prose}")), StopProse.Fields(mapped));
      Assert.Equal((Full.Passages, Full.Tags, Full.Placement), (mapped.Passages, mapped.Tags, mapped.Placement));
      Assert.Equal(Full.Checks!.Select(check => (check.Id, check.Suggestions)), mapped.Checks!.Select(check => (check.Id, check.Suggestions)));
   }

   [Theory]
   [MemberData(nameof(Rows))]
   public void Read_returns_the_prose_that_Fields_gives_the_field(string row)
   {
      var (field, prose) = Expected[row];

      Assert.Equal(prose, StopProse.Read(Full, field));
   }

   [Theory]
   [MemberData(nameof(Rows))]
   public void Path_renders_the_name_that_a_stop_text_anchor_gives_the_field_and_parses_back_to_the_field(string row)
   {
      var field = Expected[row].Field;

      Assert.Equal(row, field.Path.ToString());
      Assert.Equal(field, ProseField.Parse(field.Path));
   }

   [Theory]
   [MemberData(nameof(Rows))]
   public void Html_of_the_content_holds_the_rendered_prose_of_the_field(string row)
   {
      var (field, prose) = Expected[row];
      var html = ProseHtml.Of(TourValues.Written("s1", StopKind.Risk, Full), MentionLinks.None)["content"]!["html"]!;

      var rendered = field switch
      {
         ProseField.Headline => html["headline"],
         ProseField.How => html["how"],
         ProseField.Why => html["why"],
         ProseField.DesignChoices => html["designChoices"],
         ProseField.PinnedBy => html["pinnedBy"],
         ProseField.Scenario => html["scenario"],
         ProseField.CheckQuestion question => html["checks"]![IndexOf(question.CheckId)]!["question"],
         ProseField.CheckRecommendation recommendation => html["checks"]![IndexOf(recommendation.CheckId)]!["recommendation"],
      };

      Assert.Equal(ProseRenderer.Render(prose).Html, (string?)rendered);
   }

   /// <summary>The schema description is a constant, so it lists the fields by hand.</summary>
   [Fact]
   public void Schema_description_names_every_kind_of_prose_field()
   {
      Assert.All(ProseField.Kinds, kind => Assert.Contains(kind.ToString(), ProseField.SchemaDescription, StringComparison.Ordinal));
   }

   [Theory]
   [InlineData("")]
   [InlineData("tags")]
   [InlineData("passages[0].text")]
   [InlineData("checks")]
   [InlineData("checks[c1]")]
   [InlineData("checks[c1].suggestions")]
   [InlineData("checks[c1].question.text")]
   [InlineData("headline.text")]
   public void Path_of_no_prose_field_parses_to_null(string path)
   {
      Assert.Null(ProseField.Parse(FieldPath.Parse(path)));
   }

   [Fact]
   public void Check_field_whose_ID_holds_a_bracket_parses_back_to_its_check()
   {
      var field = new ProseField.CheckRecommendation("a].question[b");

      Assert.Equal(field, ProseField.Parse(field.Path));
   }

   [Fact]
   public void Read_of_a_check_that_the_content_lacks_or_of_an_optional_field_that_is_null_is_null()
   {
      var content = new StopContent("*headline*", [TourValues.Passage()], Checks: [new StopCheck("c1", "*question*")]);

      Assert.Null(StopProse.Read(content, new ProseField.CheckQuestion("c9")));
      Assert.Null(StopProse.Read(content, new ProseField.CheckRecommendation("c1")));
      Assert.Null(StopProse.Read(content, new ProseField.How()));
   }

   private static int IndexOf(string checkId) => Full.Checks!.ToList().FindIndex(check => check.Id == checkId);
}
