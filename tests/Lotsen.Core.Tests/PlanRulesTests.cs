using Lotsen.Tours;
using static Lotsen.Tests.TourValues;

namespace Lotsen.Tests;

/// <summary>Covers the rules of <see cref="PlanRules"/>, one row per rule, on plan values.</summary>
[Trait("Category", "Rules")]
public sealed class PlanRulesTests
{
   private static readonly Chapter A = new("a", "A");
   private static readonly Chapter B = new("b", "B");

   private static readonly Dictionary<string, (Plan Plan, string Path, string Problem)> Broken = new()
   {
      ["no chapter"] = (new Plan([], [Pending("s1")], []), "chapters", "a plan needs at least one chapter."),
      ["null chapter"] = (new Plan([null!], [Pending("s1")], []), "chapters[0]", "a chapter cannot be null."),
      ["empty chapter ID"] = (new Plan([new Chapter(" ", "A")], [Pending("s1", chapter: " ")], []), "chapters[0].id", "a chapter ID cannot be empty."),
      ["empty chapter title"] = (new Plan([new Chapter("a", "")], [Pending("s1")], []), "chapters[0].title", "a chapter title cannot be empty."),
      ["repeated chapter ID"] = (new Plan([A, A with { Title = "B" }], [Pending("s1")], []), "chapters[1].id", "another chapter already has the ID 'a'."),
      ["no stop"] = (new Plan([A], [], []), "stops", "a plan needs at least one stop."),
      ["null stop"] = (new Plan([A], [Pending("s1"), null!], []), "stops[1]", "a stop cannot be null."),
      ["empty stop title"] = (Plan(Pending("s1") with { Title = " " }), "stops[0].title", "a stop title cannot be empty."),
      ["empty stop ID"] = (Plan(Pending("")), "stops[0].id", "a stop ID cannot be empty."),
      ["repeated stop ID"] = (Plan(Pending("s1"), Pending("s1")), "stops[1].id", "another stop already has the ID 's1'."),
      ["overview after the first stop"] = (Plan(Pending("s1"), Pending("s2", StopKind.Overview)), "stops[1].kind", "only the first stop can be an overview."),
      ["unknown chapter"] = (Plan(Pending("s1", chapter: "z")), "stops[0].chapter", "no chapter has the ID 'z'. The chapter IDs are 'a'."),
      ["first stop in a later chapter"] = (
         new Plan([A, B], [Pending("s1", chapter: "b"), Pending("s2", chapter: "a")], []),
         "stops[0].chapter",
         "stops come chapter by chapter in the order of chapters, so the first stop belongs to the first chapter, 'a', not 'b'."),
      ["stop back in an earlier chapter"] = (
         new Plan([A, B], [Pending("s1"), Pending("s2", chapter: "b"), Pending("s3")], []),
         "stops[2].chapter",
         "stops come chapter by chapter in the order of chapters, so after a stop of the last chapter, 'b', every stop belongs to it, not 'a'."),
      ["chapter without stops"] = (new Plan([A, B], [Pending("s1")], []), "chapters[1]", "chapter 'b' has no stops."),
      ["anchor hint outside the repository"] = (Plan(Pending("s1", hint: new AnchorHint("../a.cs", null))), "stops[0].anchorHint.path", "give a relative repository path with forward slashes and no empty, '.' or '..' segments."),
      ["anchor hint before line 1"] = (Plan(Pending("s1", hint: new AnchorHint("src/a.cs", 0))), "stops[0].anchorHint.line", "a line number starts at 1."),
      ["null file mark"] = (Plan() with { FileMarks = [null!] }, "fileMarks[0]", "a file mark cannot be null."),
      ["file mark outside the repository"] = (Plan() with { FileMarks = [Mark("/a.cs")] }, "fileMarks[0].path", "give a relative repository path with forward slashes and no empty, '.' or '..' segments."),
      ["two file marks on one path"] = (Plan() with { FileMarks = [Mark("src/a.cs"), Mark("src/a.cs")] }, "fileMarks[1].path", "another file mark already names 'src/a.cs'. Give each file one mark."),
      ["file mark without a reason"] = (Plan() with { FileMarks = [Mark("src/a.cs", " ")] }, "fileMarks[0].reason", "a file mark needs a reason."),
      ["file mark reason of two lines"] = (Plan() with { FileMarks = [Mark("src/a.cs", "One.\nTwo.")] }, "fileMarks[0].reason", "a reason is one line of plain text, without line breaks, tabs or other control characters."),
   };

   public static TheoryData<string> Rules => [.. Broken.Keys];

   [Theory]
   [MemberData(nameof(Rules))]
   public void Broken_rule_names_its_field_and_its_problem(string rule)
   {
      var (plan, path, problem) = Broken[rule];

      var fault = PlanRules.Check(plan);

      Assert.NotNull(fault);
      Assert.Equal(path, fault.Path.ToString());
      Assert.Equal(problem, fault.Problem);
   }

   [Fact]
   public void Plan_that_follows_every_rule_has_no_fault()
   {
      var plan = new Plan(
         [A, B],
         [Pending("s1", StopKind.Overview), Written("s2"), Pending("s3", chapter: "b", hint: new AnchorHint("src/b.cs", 3))],
         [Mark("src/a.cs"), Mark("src/b.cs")]);

      Assert.Null(PlanRules.Check(plan));
   }

   private static FileMark Mark(string path, string reason = "Generated.") => new(path, FileMarkKind.Generated, reason);
}
