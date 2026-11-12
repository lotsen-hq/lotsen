using Lotsen.Tours;
using static Lotsen.Tests.TourValues;

namespace Lotsen.Tests;

/// <summary>Covers the rules of <see cref="PlanRules"/>, one row per rule, on plan values.</summary>
[Trait("Category", "Rules")]
public sealed class PlanRulesTests
{
   private static readonly Chapter A = new("a", "A");
   private static readonly Chapter B = new("b", "B");
   private static readonly Part P = new("p", "P");
   private static readonly Part Q = new("q", "Q");

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
      ["chapter part in a plan without parts"] = (new Plan([A, B with { Part = "p" }], [Pending("s1"), Pending("s2", chapter: "b")], []), "chapters[1].part", "the plan has no parts, so no chapter names one. Give parts to group the chapters, or leave out part on every chapter."),
      ["empty list of parts"] = (Parted([], A), "parts", "a plan with parts needs at least one part. Leave out parts for a plan without parts."),
      ["null part"] = (Parted([P, null!], A with { Part = "p" }), "parts[1]", "a part cannot be null."),
      ["empty part ID"] = (Parted([new Part(" ", "P")], A with { Part = " " }), "parts[0].id", "a part ID cannot be empty."),
      ["empty part title"] = (Parted([new Part("p", "")], A with { Part = "p" }), "parts[0].title", "a part title cannot be empty."),
      ["repeated part ID"] = (Parted([P, P with { Title = "Q" }], A with { Part = "p" }), "parts[1].id", "another part already has the ID 'p'."),
      ["chapter without a part"] = (Parted([P], A with { Part = "p" }, B), "chapters[1].part", "in a plan with parts, every chapter names its part. The part IDs are 'p'."),
      ["unknown part"] = (Parted([P], A with { Part = "z" }), "chapters[0].part", "no part has the ID 'z'. The part IDs are 'p'."),
      ["first chapter in a later part"] = (
         Parted([P, Q], A with { Part = "q" }, B with { Part = "p" }),
         "chapters[0].part",
         "chapters come part by part in the order of parts, so the first chapter belongs to the first part, 'p', not 'q'."),
      ["chapter back in an earlier part"] = (
         Parted([P, Q], A with { Part = "p" }, B with { Part = "q" }, new Chapter("c", "C", "p")),
         "chapters[2].part",
         "chapters come part by part in the order of parts, so after a chapter of the last part, 'q', every chapter belongs to it, not 'p'."),
      ["part without chapters"] = (Parted([P, Q], A with { Part = "p" }, B with { Part = "p" }), "parts[1]", "part 'q' has no chapters."),
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

   [Fact]
   public void Plan_with_parts_that_follows_every_rule_has_no_fault()
   {
      var plan = Parted([P, Q], A with { Part = "p" }, B with { Part = "p" }, new Chapter("c", "C", "q"));

      Assert.Null(PlanRules.Check(plan));
   }

   // A plan with one stop in each chapter, so only the parts can break a rule.
   private static Plan Parted(IReadOnlyList<Part> parts, params Chapter[] chapters) =>
      new(chapters, [.. chapters.Select((chapter, i) => Pending($"s{i + 1}", chapter: chapter.Id))], [], parts);

   private static FileMark Mark(string path, string reason = "Generated.") => new(path, FileMarkKind.Generated, reason);
}
