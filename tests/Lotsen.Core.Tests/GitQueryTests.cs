using System.Text;
using Lotsen.Git;
using Lotsen.Hosting;
using Lotsen.Operations;
using Lotsen.Pinning;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lotsen.Tests;

/// <summary>Covers the queries at given commits that a clone answers: merge base, changed files, file text and diff.</summary>
[Trait("Category", "Git")]
public sealed class GitQueryTests : IDisposable
{
   private readonly GitFixture _fixture = new();

   [Fact]
   public async Task Merge_base_of_branches_with_commits_on_both_sides_is_their_fork_point()
   {
      _fixture.Commit("Start", ("a.txt", "one\n"));
      var forkPoint = _fixture.Commit("Fork point", ("b.txt", "two\n"));
      _fixture.Checkout("feature", create: true);
      _fixture.Commit("Feature one", ("a.txt", "one\nfeature\n"));
      var feature = _fixture.Commit("Feature two", ("c.txt", "three\n"));
      _fixture.Checkout("main");
      var main = _fixture.Commit("Main moves on", ("b.txt", "two\nmain\n"));
      var repository = await SyncAsync();

      var mergeBase = await repository.GetMergeBaseAsync(main, feature, TestContext.Current.CancellationToken);

      Assert.Equal(forkPoint, mergeBase);
   }

   [Fact]
   public async Task Commits_without_a_common_history_have_no_merge_base()
   {
      var main = _fixture.Commit("Start", ("a.txt", "one\n"));
      _fixture.Git(_fixture.OriginPath, "checkout", "--quiet", "--orphan", "unrelated");
      var unrelated = _fixture.Commit("Unrelated", ("z.txt", "zed\n"));
      var repository = await SyncAsync();

      Assert.Null(await repository.GetMergeBaseAsync(main, unrelated, TestContext.Current.CancellationToken));
   }

   [Fact]
   public async Task Changed_files_name_each_path_with_its_change_kind()
   {
      var baseCommit = _fixture.Commit(
         "Base",
         ("kept.txt", "kept\n"),
         ("modified.txt", "before\n"),
         ("deleted.txt", "gone soon\n"),
         ("src/nested/deep.txt", "deep\n"));
      var head = _fixture.Commit(
         "Head",
         ("modified.txt", "after\n"),
         ("deleted.txt", null),
         ("src/nested/added file.txt", "new\n"),
         ("src/nested/deep.txt", "deeper\n"),
         ("ünïcode.md", "text\n"));
      var repository = await SyncAsync();

      var changed = await repository.ListChangedFilesAsync(baseCommit, head, TestContext.Current.CancellationToken);

      Assert.Equal(
         [
            new ChangedFile("deleted.txt", FileChangeKind.Deleted),
            new ChangedFile("modified.txt", FileChangeKind.Modified),
            new ChangedFile("src/nested/added file.txt", FileChangeKind.Added),
            new ChangedFile("src/nested/deep.txt", FileChangeKind.Modified),
            new ChangedFile("ünïcode.md", FileChangeKind.Added),
         ],
         changed.WithoutObjectIds().OrderBy(file => file.Path, StringComparer.Ordinal));
   }

   /// <summary>A submodule's entry names a commit of another repository, so its object ID is that commit's.</summary>
   [Fact]
   public async Task Changed_files_carry_the_object_id_of_their_entry_at_the_head()
   {
      var baseCommit = _fixture.Commit("Base", ("modified.txt", "before\n"), ("deleted.txt", "gone\n"), ("old.txt", "one\ntwo\nthree\nfour\nfive\nsix\n"));
      _fixture.Commit("Head", ("modified.txt", "after\n"), ("deleted.txt", null), ("added.txt", "new\n"), ("old.txt", null), ("new.txt", "one\ntwo\nTHREE\nfour\nfive\nsix\n"));
      var head = CommitSubmodule(new string('2', 40));
      var repository = await SyncAsync();

      var changed = await repository.ListChangedFilesAsync(baseCommit, head, TestContext.Current.CancellationToken);

      string? HeadEntry(string path) => _fixture.Git(_fixture.OriginPath, "rev-parse", $"{head}:{path}").Trim();
      Assert.Equal(
         [
            ("added.txt", HeadEntry("added.txt")),
            ("deleted.txt", null),
            ("modified.txt", HeadEntry("modified.txt")),
            ("new.txt", HeadEntry("new.txt")),
            ("sub", new string('2', 40)),
         ],
         changed.Select(file => (file.Path, file.HeadObjectId)).OrderBy(file => file.Path, StringComparer.Ordinal));
   }

   [Fact]
   public async Task Changed_files_list_a_moved_file_with_a_small_edit_once_as_renamed_with_its_old_path()
   {
      var baseCommit = _fixture.Commit("Base", ("src/old.txt", "one\ntwo\nthree\nfour\nfive\nsix\n"), ("kept.txt", "kept\n"));
      var head = _fixture.Commit("Head", ("src/old.txt", null), ("lib/new.txt", "one\ntwo\nTHREE\nfour\nfive\nsix\n"));
      var repository = await SyncAsync();

      var changed = await repository.ListChangedFilesAsync(baseCommit, head, TestContext.Current.CancellationToken);

      Assert.Equal([new ChangedFile("lib/new.txt", FileChangeKind.Renamed, "src/old.txt")], changed.WithoutObjectIds());
   }

   /// <summary>The added file shares no line with the deleted one, so git's rename detection does not pair them.</summary>
   [Fact]
   public async Task Changed_files_keep_an_unrelated_deletion_and_addition_apart()
   {
      var baseCommit = _fixture.Commit("Base", ("old.txt", "one\ntwo\nthree\n"));
      var head = _fixture.Commit("Head", ("old.txt", null), ("new.txt", "alpha\nbeta\ngamma\n"));
      var repository = await SyncAsync();

      var changed = await repository.ListChangedFilesAsync(baseCommit, head, TestContext.Current.CancellationToken);

      Assert.Equal([new ChangedFile("new.txt", FileChangeKind.Added), new ChangedFile("old.txt", FileChangeKind.Deleted)], changed.WithoutObjectIds());
   }

   [Fact]
   public async Task Base_and_head_text_of_a_modified_file_match_the_fixture()
   {
      var baseCommit = _fixture.Commit("Base", ("src/app.cs", "class App\n{\n}\n"));
      var head = _fixture.Commit("Head", ("src/app.cs", "class App\n{\n    void Run() { }\n}\n"));
      var repository = await SyncAsync();

      Assert.Equal("class App\n{\n}\n", await repository.ReadFileAsync(baseCommit, "src/app.cs", TestContext.Current.CancellationToken));
      Assert.Equal("class App\n{\n    void Run() { }\n}\n", await repository.ReadFileAsync(head, "src/app.cs", TestContext.Current.CancellationToken));
   }

   [Fact]
   public async Task Path_that_is_not_a_file_at_the_commit_reads_as_null()
   {
      var commit = _fixture.Commit("Base", ("src/app.cs", "class App;\n"));
      var repository = await SyncAsync();

      Assert.Null(await repository.ReadFileAsync(commit, "missing.txt", TestContext.Current.CancellationToken));
      Assert.Null(await repository.ReadFileAsync(commit, "src", TestContext.Current.CancellationToken));
   }

   /// <summary>Git can hold a file whose path <see cref="RepositoryPath.IsValid"/> refuses, as one with a backslash on Linux, and the queries find no file or directory there.</summary>
   [Theory]
   [InlineData("")]
   [InlineData("/etc/passwd")]
   [InlineData("src/../secret")]
   [InlineData("src//app.cs")]
   [InlineData("src/")]
   [InlineData("src/..")]
   [InlineData("./src/app.cs")]
   [InlineData("src\\app.cs")]
   [InlineData("c:src/app.cs")]
   public async Task Path_that_is_not_a_repository_path_names_no_file_or_directory(string path)
   {
      string[] stored = OperatingSystem.IsWindows() ? ["src/app.cs"] : ["src/app.cs", "src\\app.cs", "c:src/app.cs"];
      var commit = _fixture.Commit("Base", [.. stored.Select(file => (file, (string?)"class App;\n"))]);
      var repository = await SyncAsync();

      Assert.Null(await repository.FindFileAsync(commit, path, TestContext.Current.CancellationToken));
      Assert.Null(await repository.ListDirectoryAsync(commit, path, TestContext.Current.CancellationToken));
   }

   [Fact]
   public async Task Diff_gives_each_change_as_ranges_of_the_base_and_head_text()
   {
      var baseCommit = _fixture.Commit("Base", ("list.txt", "one\ntwo\nthree\nfour\nfive\n"));
      var head = _fixture.Commit("Head", ("list.txt", "zero\none\nTWO\nthree\nfive\n"));
      var repository = await SyncAsync();

      var diff = await repository.DiffFileAsync(baseCommit, head, "list.txt", TestContext.Current.CancellationToken);

      Assert.Equal("one\ntwo\nthree\nfour\nfive\n", diff.BaseText);
      Assert.Equal("zero\none\nTWO\nthree\nfive\n", diff.HeadText);
      Assert.Equal([("", "zero\n"), ("two\n", "TWO\n"), ("four\n", "")], diff.Changes.Select(change => (diff.BaseText![change.FromA..change.ToA], diff.HeadText![change.FromB..change.ToB])));
   }

   /// <summary>The last line lacks a line break on both sides, so its range ends at the end of each text.</summary>
   [Fact]
   public async Task Diff_of_a_last_line_without_a_line_break_ends_at_the_end_of_the_text()
   {
      var baseCommit = _fixture.Commit("Base", ("a.txt", "one\nlast"));
      var head = _fixture.Commit("Head", ("a.txt", "one\nLAST"));
      var repository = await SyncAsync();

      var diff = await repository.DiffFileAsync(baseCommit, head, "a.txt", TestContext.Current.CancellationToken);

      var change = Assert.Single(diff.Changes);
      Assert.Equal(new TextChange(4, 8, 4, 8), change);
   }

   [Fact]
   public async Task Diff_of_an_added_file_inserts_its_whole_text()
   {
      var baseCommit = _fixture.Commit("Base", ("a.txt", "one\n"));
      var head = _fixture.Commit("Head", ("b.txt", "new\nfile\n"));
      var repository = await SyncAsync();

      var diff = await repository.DiffFileAsync(baseCommit, head, "b.txt", TestContext.Current.CancellationToken);

      Assert.Null(diff.BaseText);
      Assert.Equal("new\nfile\n", diff.HeadText);
      Assert.Equal([new TextChange(0, 0, 0, 9)], diff.Changes);
   }

   [Fact]
   public async Task Diff_of_a_deleted_file_removes_its_whole_text()
   {
      var baseCommit = _fixture.Commit("Base", ("a.txt", "one\ntwo\n"));
      var head = _fixture.Commit("Head", ("a.txt", null), ("b.txt", "other\n"));
      var repository = await SyncAsync();

      var diff = await repository.DiffFileAsync(baseCommit, head, "a.txt", TestContext.Current.CancellationToken);

      Assert.Equal("one\ntwo\n", diff.BaseText);
      Assert.Null(diff.HeadText);
      Assert.Equal([new TextChange(0, 8, 0, 0)], diff.Changes);
   }

   /// <summary>The head replaces the file with a directory, which the path also matches as a prefix.</summary>
   [Fact]
   public async Task Diff_of_a_file_replaced_by_a_directory_holds_only_the_file()
   {
      var baseCommit = _fixture.Commit("Base", ("x", "one\ntwo\n"));
      var head = _fixture.Commit("Head", ("x", null), ("x/y", "y1\ny2\ny3\n"));
      var repository = await SyncAsync();

      var diff = await repository.DiffFileAsync(baseCommit, head, "x", TestContext.Current.CancellationToken);

      Assert.Equal("one\ntwo\n", diff.BaseText);
      Assert.Null(diff.HeadText);
      Assert.Equal([new TextChange(0, 8, 0, 0)], diff.Changes);
   }

   [Fact]
   public async Task Diff_of_a_directory_replaced_by_a_file_holds_only_the_file()
   {
      var baseCommit = _fixture.Commit("Base", ("x/y", "y1\ny2\ny3\n"));
      var head = _fixture.Commit("Head", ("x/y", null), ("x", "one\n"));
      var repository = await SyncAsync();

      var diff = await repository.DiffFileAsync(baseCommit, head, "x", TestContext.Current.CancellationToken);

      Assert.Null(diff.BaseText);
      Assert.Equal("one\n", diff.HeadText);
      Assert.Equal([new TextChange(0, 0, 0, 4)], diff.Changes);
   }

   /// <summary>The other paths start with the diffed path's name but are other files.</summary>
   [Fact]
   public async Task Diff_of_a_file_leaves_out_paths_that_share_its_name_as_a_prefix()
   {
      var baseCommit = _fixture.Commit("Base", ("a", "one\n"), ("a.txt", "dot\n"), ("ab/c", "deep\n"));
      var head = _fixture.Commit("Head", ("a", "ONE\n"), ("a.txt", "DOT\nmore\n"), ("ab/c", "DEEP\n"));
      var repository = await SyncAsync();

      var diff = await repository.DiffFileAsync(baseCommit, head, "a", TestContext.Current.CancellationToken);

      Assert.Equal([new TextChange(0, 4, 0, 4)], diff.Changes);
   }

   [Fact]
   public async Task Diff_across_a_rename_compares_the_old_path_at_the_base_with_the_new_path_at_the_head()
   {
      var baseCommit = _fixture.Commit("Base", ("src/old.txt", "one\ntwo\nthree\nfour\n"), ("lib/new.txt", "other file\n"));
      var head = _fixture.Commit("Head", ("src/old.txt", null), ("lib/new.txt", "one\nTWO\nthree\nfour\n"));
      var repository = await SyncAsync();

      var diff = await repository.DiffFileAsync(baseCommit, "src/old.txt", head, "lib/new.txt", TestContext.Current.CancellationToken);

      Assert.Equal("one\ntwo\nthree\nfour\n", diff.BaseText);
      Assert.Equal("one\nTWO\nthree\nfour\n", diff.HeadText);
      Assert.Equal([new TextChange(4, 8, 4, 8)], diff.Changes);
   }

   /// <summary>Each line ends in a carriage return and a line feed, so each line break takes two UTF-16 code units.</summary>
   [Fact]
   public async Task Diff_across_a_rename_of_a_file_with_crlf_line_breaks_counts_both_characters_of_each_break()
   {
      var baseCommit = _fixture.Commit("Base", ("old.txt", "one\r\ntwo\r\nthree\r\n"));
      var head = _fixture.Commit("Head", ("old.txt", null), ("new.txt", "one\r\nTWO\r\nthree\r\n"));
      var repository = await SyncAsync();

      var diff = await repository.DiffFileAsync(baseCommit, "old.txt", head, "new.txt", TestContext.Current.CancellationToken);

      Assert.Equal([new TextChange(5, 10, 5, 10)], diff.Changes);
   }

   /// <summary>Under either setting, git's own diff of these texts would merge or move hunks, and the diff of the same texts at one path ignores both settings.</summary>
   [Theory]
   [InlineData("diff.interHunkContext", "10")]
   [InlineData("diff.algorithm", "histogram")]
   public async Task Diff_across_a_rename_ignores_the_diff_settings_of_the_git_configuration(string key, string value)
   {
      const string before = "b\nb\nc\nx\na\na\nx\n";
      const string after = "c\nb\nb\nx\nx\nx\nb\n";
      var baseCommit = _fixture.Commit("Base", ("old.txt", before), ("same.txt", before));
      var head = _fixture.Commit("Head", ("old.txt", null), ("new.txt", after), ("same.txt", after));
      var repository = await SyncAsync();
      _fixture.Git(repository.Path, "config", key, value);
      var token = TestContext.Current.CancellationToken;

      var renamed = await repository.DiffFileAsync(baseCommit, "old.txt", head, "new.txt", token);
      var samePath = await repository.DiffFileAsync(baseCommit, head, "same.txt", token);

      Assert.Equal([new TextChange(0, 0, 0, 2), new TextChange(4, 6, 6, 6), new TextChange(8, 12, 8, 8), new TextChange(14, 14, 10, 14)], renamed.Changes);
      Assert.Equal(samePath.Changes, renamed.Changes);
   }

   [Fact]
   public async Task Diff_across_a_rename_without_edits_has_no_changes()
   {
      var baseCommit = _fixture.Commit("Base", ("old.txt", "same\ntext\n"));
      var head = _fixture.Commit("Head", ("old.txt", null), ("new.txt", "same\ntext\n"));
      var repository = await SyncAsync();

      var diff = await repository.DiffFileAsync(baseCommit, "old.txt", head, "new.txt", TestContext.Current.CancellationToken);

      Assert.Equal("same\ntext\n", diff.HeadText);
      Assert.Empty(diff.Changes);
   }

   /// <summary>Every line differs, so git would not report the two paths as a rename.</summary>
   [Fact]
   public async Task Diff_across_two_paths_works_for_files_that_git_would_not_pair()
   {
      var baseCommit = _fixture.Commit("Base", ("a.txt", "one\ntwo\n"));
      var head = _fixture.Commit("Head", ("a.txt", null), ("b.txt", "alpha\nbeta\n"));
      var repository = await SyncAsync();

      var diff = await repository.DiffFileAsync(baseCommit, "a.txt", head, "b.txt", TestContext.Current.CancellationToken);

      Assert.Equal([new TextChange(0, 8, 0, 11)], diff.Changes);
   }

   /// <summary>The old path is not a file at the base, so the diff has only a head file.</summary>
   [Fact]
   public async Task Diff_across_two_paths_with_the_base_missing_inserts_the_whole_head_text()
   {
      var baseCommit = _fixture.Commit("Base", ("a.txt", "one\n"));
      var head = _fixture.Commit("Head", ("b.txt", "two\n"));
      var repository = await SyncAsync();

      var diff = await repository.DiffFileAsync(baseCommit, "missing.txt", head, "b.txt", TestContext.Current.CancellationToken);

      Assert.Null(diff.BaseText);
      Assert.Equal("two\n", diff.HeadText);
      Assert.Equal([new TextChange(0, 0, 0, 4)], diff.Changes);
   }

   [Fact]
   public async Task Diff_of_a_submodule_has_no_text_and_no_changes()
   {
      _fixture.Commit("Base", ("a.txt", "one\n"));
      var baseCommit = CommitSubmodule(new string('1', 40));
      var head = CommitSubmodule(new string('2', 40));
      var repository = await SyncAsync();

      var diff = await repository.DiffFileAsync(baseCommit, head, "sub", TestContext.Current.CancellationToken);

      Assert.Null(diff.BaseText);
      Assert.Null(diff.HeadText);
      Assert.Empty(diff.Changes);
   }

   /// <summary>The clone's directory is deleted while the repository is still in use.</summary>
   [Fact]
   public async Task Query_on_a_clone_whose_directory_is_gone_names_the_missing_directory()
   {
      var commit = _fixture.Commit("Base", ("a.txt", "one\n"));
      var repository = await SyncAsync();
      DirectoryTree.Delete(repository.Path);

      var exception = await Assert.ThrowsAsync<GitException>(() => repository.ReadFileAsync(commit, "a.txt", TestContext.Current.CancellationToken));

      Assert.Contains(repository.Path, exception.Message, StringComparison.Ordinal);
      Assert.DoesNotContain("git was not found", exception.Message, StringComparison.Ordinal);
   }

   /// <summary>Carriage returns are shown properly only later, but they must not break a query.</summary>
   [Fact]
   public async Task Carriage_returns_do_not_break_the_queries()
   {
      var baseCommit = _fixture.Commit("Base", ("crlf.txt", "one\r\ntwo\r\n"));
      var head = _fixture.Commit("Head", ("crlf.txt", "one\r\nTWO\r\n"));
      var repository = await SyncAsync();
      var token = TestContext.Current.CancellationToken;

      var changed = await repository.ListChangedFilesAsync(baseCommit, head, token);
      var crlf = await repository.DiffFileAsync(baseCommit, head, "crlf.txt", token);

      Assert.Equal([new ChangedFile("crlf.txt", FileChangeKind.Modified)], changed.WithoutObjectIds());
      Assert.Equal("two\r\n", crlf.BaseText![crlf.Changes.Single().FromA..crlf.Changes.Single().ToA]);
   }

   /// <summary>
   /// Git compares lines without the carriage return before their line feeds, so a line break added at the end of the last line counts as a line ending too, and a carriage return without a line
   /// feed does not. The large size is 40 bytes.
   /// </summary>
   [Fact]
   public async Task Changed_files_mark_a_file_whose_only_change_is_its_line_endings()
   {
      WriteBytes("binary.dat", [0x61, 0x00, 0x0A, 0x62, 0x0A]);
      var baseCommit = _fixture.Commit(
         "Base",
         ("lf-to-crlf.txt", "one\ntwo\n"),
         ("crlf-to-lf.txt", "one\r\ntwo\r\n"),
         ("crlf-and-edit.txt", "one\ntwo\n"),
         ("whitespace.txt", "one\ntwo\n"),
         ("final-line-break.txt", "one\ntwo"),
         ("lone-cr.txt", "one\ntwo"),
         ("mode-and-crlf.txt", "one\ntwo\n"),
         ("old.txt", "alpha\nbeta\ngamma\ndelta\n"),
         ("large.txt", "0123456789abcde\n0123456789abcde\n0123456789abcde\n"));
      WriteBytes("binary.dat", [0x61, 0x00, 0x0D, 0x0A, 0x62, 0x0D, 0x0A]);
      _fixture.Commit(
         "Head",
         ("lf-to-crlf.txt", "one\r\ntwo\r\n"),
         ("crlf-to-lf.txt", "one\ntwo\n"),
         ("crlf-and-edit.txt", "one\r\nTWO\r\n"),
         ("whitespace.txt", "one\n  two\n"),
         ("final-line-break.txt", "one\ntwo\n"),
         ("lone-cr.txt", "one\ntwo\r"),
         ("mode-and-crlf.txt", "one\r\ntwo\r\n"),
         ("old.txt", null),
         ("new.txt", "alpha\r\nbeta\r\ngamma\r\ndelta\r\n"),
         ("large.txt", "0123456789abcde\r\n0123456789abcde\r\n0123456789abcde\r\n"));
      _fixture.Git(_fixture.OriginPath, "update-index", "--chmod=+x", "mode-and-crlf.txt");
      _fixture.Git(_fixture.OriginPath, "commit", "--quiet", "--amend", "--no-edit");
      var head = CommitId.Parse(_fixture.Git(_fixture.OriginPath, "rev-parse", "HEAD").Trim());
      var repository = await SyncAsync();

      var changed = await repository.ListChangedFilesAsync(baseCommit, head, 40, TestContext.Current.CancellationToken);

      Assert.Equal(
         [
            new ChangedFile("binary.dat", FileChangeKind.Modified, Binary: true),
            new ChangedFile("crlf-and-edit.txt", FileChangeKind.Modified),
            new ChangedFile("crlf-to-lf.txt", FileChangeKind.Modified, LineEndingsOnly: true),
            new ChangedFile("final-line-break.txt", FileChangeKind.Modified, LineEndingsOnly: true),
            new ChangedFile("large.txt", FileChangeKind.Modified, Large: new LargeFile(51), LineEndingsOnly: true),
            new ChangedFile("lf-to-crlf.txt", FileChangeKind.Modified, LineEndingsOnly: true),
            new ChangedFile("lone-cr.txt", FileChangeKind.Modified),
            new ChangedFile("mode-and-crlf.txt", FileChangeKind.Modified),
            new ChangedFile("new.txt", FileChangeKind.Renamed, "old.txt", LineEndingsOnly: true),
            new ChangedFile("whitespace.txt", FileChangeKind.Modified),
         ],
         changed.WithoutObjectIds().OrderBy(file => file.Path, StringComparer.Ordinal));
   }

   [Fact]
   public async Task Diff_with_whitespace_ignored_has_no_changes_for_whitespace_and_line_ending_edits()
   {
      const string before = "class A\n{\n  int x;\n}\n";
      const string after = "class A  \r\n{\r\n\tint  x;\r\n}\r\n";
      var baseCommit = _fixture.Commit("Base", ("same.cs", before), ("old.cs", before));
      var head = _fixture.Commit("Head", ("same.cs", after), ("old.cs", null), ("new.cs", after));
      var repository = await SyncAsync();
      var token = TestContext.Current.CancellationToken;

      var compared = await repository.DiffFileAsync(baseCommit, head, "same.cs", token);
      var samePath = await repository.DiffFileAsync(baseCommit, "same.cs", head, "same.cs", DiffWhitespace.Ignore, token);
      var renamed = await repository.DiffFileAsync(baseCommit, "old.cs", head, "new.cs", DiffWhitespace.Ignore, token);

      Assert.NotEmpty(compared.Changes);
      Assert.Equal((before, after), (samePath.BaseText, samePath.HeadText));
      Assert.Empty(samePath.Changes);
      Assert.Equal((before, after), (renamed.BaseText, renamed.HeadText));
      Assert.Empty(renamed.Changes);
   }

   /// <summary>The base has CRLF line breaks and the head LF, so the offsets of the one remaining change differ on each side.</summary>
   [Fact]
   public async Task Diff_with_whitespace_ignored_gives_the_remaining_changes_as_offsets_into_the_original_texts()
   {
      const string before = "one\r\ntwo\r\nthree\r\n";
      const string after = "one\n  TWO\nthree\n";
      var baseCommit = _fixture.Commit("Base", ("same.txt", before), ("old.txt", before));
      var head = _fixture.Commit("Head", ("same.txt", after), ("old.txt", null), ("new.txt", after));
      var repository = await SyncAsync();
      var token = TestContext.Current.CancellationToken;

      var samePath = await repository.DiffFileAsync(baseCommit, "same.txt", head, "same.txt", DiffWhitespace.Ignore, token);
      var renamed = await repository.DiffFileAsync(baseCommit, "old.txt", head, "new.txt", DiffWhitespace.Ignore, token);

      Assert.Equal([new TextChange(5, 10, 4, 10)], samePath.Changes);
      Assert.Equal(samePath.Changes, renamed.Changes);
   }

   /// <summary>An added blank line is an added line, not a whitespace edit within one.</summary>
   [Fact]
   public async Task Diff_with_whitespace_ignored_keeps_an_added_blank_line()
   {
      var baseCommit = _fixture.Commit("Base", ("a.txt", "one\ntwo\n"));
      var head = _fixture.Commit("Head", ("a.txt", "one\n\ntwo\n"));
      var repository = await SyncAsync();

      var diff = await repository.DiffFileAsync(baseCommit, "a.txt", head, "a.txt", DiffWhitespace.Ignore, TestContext.Current.CancellationToken);

      Assert.Equal([new TextChange(4, 4, 4, 5)], diff.Changes);
   }

   /// <summary>The texts are those of <see cref="Diff_across_a_rename_ignores_the_diff_settings_of_the_git_configuration"/>, with CRLF line breaks at the head.</summary>
   [Theory]
   [InlineData("core.whitespace", "cr-at-eol,-trailing-space")]
   [InlineData("diff.interHunkContext", "10")]
   [InlineData("diff.algorithm", "histogram")]
   public async Task Diff_with_whitespace_ignored_ignores_the_diff_settings_of_the_git_configuration(string key, string value)
   {
      const string before = "b\nb\nc\nx\na\na\nx\n";
      const string after = "c\r\nb\r\nb\r\nx\r\nx\r\nx\r\nb\r\n";
      var baseCommit = _fixture.Commit("Base", ("old.txt", before), ("same.txt", before));
      var head = _fixture.Commit("Head", ("old.txt", null), ("new.txt", after), ("same.txt", after));
      var repository = await SyncAsync();
      _fixture.Git(repository.Path, "config", key, value);
      var token = TestContext.Current.CancellationToken;

      var renamed = await repository.DiffFileAsync(baseCommit, "old.txt", head, "new.txt", DiffWhitespace.Ignore, token);
      var samePath = await repository.DiffFileAsync(baseCommit, "same.txt", head, "same.txt", DiffWhitespace.Ignore, token);

      Assert.Equal([new TextChange(0, 0, 0, 3), new TextChange(4, 6, 9, 9), new TextChange(8, 12, 12, 12), new TextChange(14, 14, 15, 21)], renamed.Changes);
      Assert.Equal(samePath.Changes, renamed.Changes);
   }

   /// <summary>A file counts as binary on both routes when either side is binary: one file turns binary, another is binary on both sides and moves, and a UTF-16 file is added.</summary>
   [Fact]
   public async Task Changed_files_and_found_files_mark_each_file_that_git_treats_as_binary()
   {
      WriteBytes("old.bin", [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01, 0x02]);
      var baseCommit = _fixture.Commit("Base", ("text.txt", "one\n"), ("turns-binary.dat", "text\n"));
      File.Delete(Path.Combine(_fixture.OriginPath, "old.bin"));
      WriteBytes("new.bin", [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01, 0x02]);
      WriteBytes("turns-binary.dat", [0x74, 0x00, 0x0A]);
      WriteBytes("utf16.txt", [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("hello\n")]);
      var head = _fixture.Commit("Head", ("text.txt", "two\n"));
      var repository = await SyncAsync();
      var token = TestContext.Current.CancellationToken;

      var changed = await repository.ListChangedFilesAsync(baseCommit, head, token);
      var found = await repository.FindFileAsync(baseCommit, "turns-binary.dat", token);
      var turned = await repository.FindFileAsync(head, "turns-binary.dat", token);
      var utf16 = await repository.FindFileAsync(head, "utf16.txt", token);

      Assert.Equal(
         [
            new ChangedFile("new.bin", FileChangeKind.Renamed, "old.bin", Binary: true),
            new ChangedFile("text.txt", FileChangeKind.Modified),
            new ChangedFile("turns-binary.dat", FileChangeKind.Modified, Binary: true),
            new ChangedFile("utf16.txt", FileChangeKind.Added, Binary: true),
         ],
         changed.WithoutObjectIds().OrderBy(file => file.Path, StringComparer.Ordinal));
      Assert.False(found!.Binary);
      Assert.True(turned!.Binary);
      Assert.True(utf16!.Binary);
   }

   /// <summary>Git treats a blob as binary when a NUL byte appears in its first 8000 bytes, so a NUL at offset 7999 makes it binary and one at offset 8000 does not.</summary>
   [Fact]
   public async Task Binary_check_reads_the_first_8000_bytes_on_both_routes()
   {
      var baseCommit = _fixture.Commit("Base", ("a.txt", "one\n"));
      WriteBytes("nul-at-7999.txt", [.. Enumerable.Repeat((byte)'a', 7999), 0]);
      WriteBytes("nul-at-8000.txt", [.. Enumerable.Repeat((byte)'a', 8000), 0]);
      var head = _fixture.Commit("Head");
      var repository = await SyncAsync();
      var token = TestContext.Current.CancellationToken;

      var changed = await repository.ListChangedFilesAsync(baseCommit, head, token);
      var early = await repository.FindFileAsync(head, "nul-at-7999.txt", token);
      var late = await repository.FindFileAsync(head, "nul-at-8000.txt", token);

      Assert.Equal([new ChangedFile("nul-at-7999.txt", FileChangeKind.Added, Binary: true), new ChangedFile("nul-at-8000.txt", FileChangeKind.Added)], changed.WithoutObjectIds());
      Assert.Equal((true, 8000L), (early!.Binary, early.Size));
      Assert.Equal((false, 8001L), (late!.Binary, late.Size));
   }

   /// <summary>Above <see cref="ReadableFile.LargeSize"/> the lookup reads no content, so git checks the blob, by the same first 8000 bytes.</summary>
   [Fact]
   public async Task Large_file_is_binary_by_its_first_8000_bytes_as_a_small_one_is()
   {
      _fixture.Commit("Base", ("a.txt", "one\n"));
      var size = (int)ReadableFile.LargeSize + 1;
      var early = Enumerable.Repeat((byte)'a', size).ToArray();
      early[7999] = 0;
      var late = Enumerable.Repeat((byte)'a', size).ToArray();
      late[8000] = 0;
      WriteBytes("early.dat", early);
      WriteBytes("late.dat", late);
      var head = _fixture.Commit("Head");
      var repository = await SyncAsync();
      var token = TestContext.Current.CancellationToken;

      var found = await repository.FindFilesAsync([(head, "early.dat"), (head, "late.dat")], token);

      Assert.Equal((true, (long)size), (found[0]!.Binary, found[0]!.Size));
      Assert.Equal((false, (long)size), (found[1]!.Binary, found[1]!.Size));
   }

   [Fact]
   public async Task File_whose_path_holds_a_newline_is_found_and_read()
   {
      Assert.SkipWhen(OperatingSystem.IsWindows(), "A file name on Windows cannot hold a newline.");
      var commit = _fixture.Commit("Base", ("new\nline.txt", "text\n"));
      var repository = await SyncAsync();

      Assert.Equal("text\n", await repository.ReadFileAsync(commit, "new\nline.txt", TestContext.Current.CancellationToken));
   }

   /// <summary>The download leaves both blobs in the clone, so the lookup finds them in its first git run and reads them in its second, and the third is the diff.</summary>
   [Fact]
   public async Task Diff_of_a_changed_text_file_runs_three_git_processes()
   {
      var baseCommit = _fixture.Commit("Base", ("a.txt", "one\n"));
      var head = _fixture.Commit("Head", ("a.txt", "two\n"));
      var git = new RecordingGit(_fixture.ScratchPath);
      var repository = await SyncAsync(git.Options);
      var token = TestContext.Current.CancellationToken;
      await repository.GetChangedFilesAsync(baseCommit, head, download: true, token);
      var before = GitRuns(git);

      var diff = await repository.DiffFileAsync(baseCommit, head, "a.txt", token);

      Assert.Equal(("one\n", "two\n"), (diff.BaseText, diff.HeadText));
      Assert.Equal(3, GitRuns(git) - before);
   }

   /// <summary>The clone is bare, so git reads no <c>.gitattributes</c> from the commits.</summary>
   [Fact]
   public async Task Gitattributes_in_the_commits_do_not_make_a_text_file_binary()
   {
      var baseCommit = _fixture.Commit("Base", (".gitattributes", "*.txt binary\n*.md -diff\n"));
      var head = _fixture.Commit("Head", ("a.txt", "text\n"), ("b.md", "text\n"));
      var repository = await SyncAsync();
      var token = TestContext.Current.CancellationToken;

      var changed = await repository.ListChangedFilesAsync(baseCommit, head, token);
      var found = await repository.FindFileAsync(head, "a.txt", token);

      Assert.Equal([new ChangedFile("a.txt", FileChangeKind.Added), new ChangedFile("b.md", FileChangeKind.Added)], changed.WithoutObjectIds());
      Assert.False(found!.Binary);
   }

   /// <summary>
   /// Each git run gets <c>HOME</c> and <c>XDG_CONFIG_HOME</c> pointed into the test's own directory, where one attribute source holds <c>* -diff</c> and <c>*.dat diff</c>. Applied,
   /// these would mark every text file binary, empty both diffs, and mark the file with a NUL byte as text.
   /// </summary>
   [Theory]
   [InlineData("none")]
   [InlineData("core.attributesFile")]
   [InlineData("XDG_CONFIG_HOME")]
   [InlineData("HOME")]
   public async Task Users_attribute_files_do_not_change_the_binary_check_or_the_diffs(string source)
   {
      var baseCommit = _fixture.Commit("Base", ("modified.txt", "one\ntwo\n"), ("old.txt", "alpha\nbeta\ngamma\ndelta\n"));
      WriteBytes("b.dat", [0x62, 0x00, 0x0A]);
      var head = _fixture.Commit("Head", ("modified.txt", "one\nTWO\n"), ("old.txt", null), ("new.txt", "alpha\nbeta\nGAMMA\ndelta\n"), ("added.txt", "new\n"));
      var repository = await SyncAsync(UserFilesGit(source, "* -diff\n*.dat diff\n"));
      var token = TestContext.Current.CancellationToken;

      var changed = await repository.ListChangedFilesAsync(baseCommit, head, token);
      var dat = await repository.FindFileAsync(head, "b.dat", token);
      var text = await repository.FindFileAsync(head, "added.txt", token);
      var samePath = await repository.DiffFileAsync(baseCommit, head, "modified.txt", token);
      var renamed = await repository.DiffFileAsync(baseCommit, "old.txt", head, "new.txt", token);

      Assert.Equal(
         [
            new ChangedFile("added.txt", FileChangeKind.Added),
            new ChangedFile("b.dat", FileChangeKind.Added, Binary: true),
            new ChangedFile("modified.txt", FileChangeKind.Modified),
            new ChangedFile("new.txt", FileChangeKind.Renamed, "old.txt"),
         ],
         changed.WithoutObjectIds().OrderBy(file => file.Path, StringComparer.Ordinal));
      Assert.Equal((true, false), (dat!.Binary, text!.Binary));
      Assert.Equal([new TextChange(4, 8, 4, 8)], samePath.Changes);
      Assert.Equal([new TextChange(11, 17, 11, 17)], renamed.Changes);
   }

   /// <summary><c>git var</c> prints the path of each attribute file that git reads, and exits with 1 when the system file is disabled.</summary>
   [Fact]
   public async Task Git_reads_neither_the_system_nor_the_global_attribute_file()
   {
      var git = new GitRunner(new GitOptions());
      var token = TestContext.Current.CancellationToken;
      GitCall Var(string name) => new($"Git could not print {name}", ["var", name], TimeSpan.FromSeconds(30)) { WorkingDirectory = _fixture.ScratchPath, SuccessExitCodes = [0, 1] };

      var system = await git.RunAsync(Var("GIT_ATTR_SYSTEM"), token);
      var global = await git.RunAsync(Var("GIT_ATTR_GLOBAL"), token);

      Assert.Equal(("", ""), (system.Text.Trim(), global.Text.Trim()));
   }

   /// <summary>The large size is 10 bytes. A file whose larger side holds exactly 10 bytes is not large.</summary>
   [Fact]
   public async Task Changed_files_mark_a_file_above_the_large_size_on_either_side_with_its_larger_size()
   {
      var baseCommit = _fixture.Commit(
         "Base",
         ("small.txt", "1234\n"),
         ("shrinks.txt", "0123456789abcdefghi\n"),
         ("gone.txt", "0123456789a\n"),
         ("moved-old.txt", "0123456789\n0123456789\n"));
      var head = _fixture.Commit(
         "Head",
         ("small.txt", "12345\n"),
         ("shrinks.txt", "012\n"),
         ("gone.txt", null),
         ("exact.txt", "012345678\n"),
         ("grows.txt", "0123456789\n"),
         ("moved-old.txt", null),
         ("moved-new.txt", "0123456789\n0123456789\n"));
      var repository = await SyncAsync();

      var changed = await repository.ListChangedFilesAsync(baseCommit, head, 10, TestContext.Current.CancellationToken);

      Assert.Equal(
         [
            new ChangedFile("exact.txt", FileChangeKind.Added),
            new ChangedFile("gone.txt", FileChangeKind.Deleted, Large: new LargeFile(12)),
            new ChangedFile("grows.txt", FileChangeKind.Added, Large: new LargeFile(11)),
            new ChangedFile("moved-new.txt", FileChangeKind.Renamed, "moved-old.txt", Large: new LargeFile(22)),
            new ChangedFile("shrinks.txt", FileChangeKind.Modified, Large: new LargeFile(20)),
            new ChangedFile("small.txt", FileChangeKind.Modified),
         ],
         changed.WithoutObjectIds().OrderBy(file => file.Path, StringComparer.Ordinal));
   }

   /// <summary>A gitlink names a commit of another repository, which has no size here and must not be looked up.</summary>
   [Fact]
   public async Task Changed_files_list_a_submodule_without_marks()
   {
      var baseCommit = _fixture.Commit("Base", ("a.txt", "one\n"));
      var head = CommitSubmodule(new string('2', 40));
      var repository = await SyncAsync();

      var changed = await repository.ListChangedFilesAsync(baseCommit, head, 0, TestContext.Current.CancellationToken);

      Assert.Equal([new ChangedFile("sub", FileChangeKind.Added)], changed.WithoutObjectIds());
   }

   [Fact]
   public async Task Changed_files_download_the_content_of_every_changed_file_and_no_other()
   {
      var baseCommit = _fixture.Commit("Base", ("kept.txt", "kept\n"), ("modified.txt", "before\n"), ("deleted.txt", "gone\n"));
      var head = _fixture.Commit("Head", ("modified.txt", "after\n"), ("deleted.txt", null), ("added.txt", "new\n"));
      var repository = await SyncAsync();

      await repository.ListChangedFilesAsync(baseCommit, head, TestContext.Current.CancellationToken);

      // With --missing=print, rev-list prints each object that the clone lacks with a leading "?" and downloads nothing.
      var missing = _fixture.Git(repository.Path, "rev-list", "--objects", "--missing=print", baseCommit.Value, head.Value).Split('\n').Where(line => line.StartsWith('?')).ToList();
      Assert.Equal(["?" + _fixture.Git(_fixture.OriginPath, "rev-parse", $"{head}:kept.txt").Trim()], missing);
   }

   /// <summary>The origin lacks the submodule's commit, so the download would fail if it fetched that commit.</summary>
   [Fact]
   public async Task Download_of_the_changed_files_fetches_the_content_of_every_changed_file_and_no_other()
   {
      var baseCommit = _fixture.Commit("Base", ("kept.txt", "kept\n"), ("modified.txt", "before\n"), ("deleted.txt", "gone\n"), ("old.txt", "one\ntwo\nthree\nfour\nfive\nsix\n"));
      _fixture.Commit("Head", ("modified.txt", "after\n"), ("deleted.txt", null), ("added.txt", "new\n"), ("old.txt", null), ("new.txt", "one\ntwo\nTHREE\nfour\nfive\nsix\n"));
      var head = CommitSubmodule(new string('2', 40));
      var repository = await SyncAsync();

      await repository.GetChangedFilesAsync(baseCommit, head, download: true, TestContext.Current.CancellationToken);

      Assert.Equal(["?" + _fixture.Git(_fixture.OriginPath, "rev-parse", $"{head}:kept.txt").Trim()], MissingObjects(repository, baseCommit, head));
   }

   /// <summary>The origin moves away after the download, so a listing or a diff that downloaded anything would fail.</summary>
   [Fact]
   public async Task Changed_files_and_their_diffs_after_the_download_need_no_remote()
   {
      var baseCommit = _fixture.Commit("Base", ("modified.txt", "before\n"), ("crlf.txt", "one\ntwo\n"), ("old.txt", "one\ntwo\nthree\nfour\nfive\nsix\n"));
      _fixture.Commit("Head", ("modified.txt", "after\n"), ("crlf.txt", "one\r\ntwo\r\n"), ("old.txt", null), ("new.txt", "one\ntwo\nTHREE\nfour\nfive\nsix\n"));
      WriteBytes("logo.png", [0x89, 0x50, 0x00, 0x01]);
      var head = _fixture.Commit("Binary");
      var repository = await SyncAsync();
      var token = TestContext.Current.CancellationToken;
      await repository.GetChangedFilesAsync(baseCommit, head, download: true, token);
      Directory.Move(_fixture.OriginPath, Path.Combine(_fixture.ScratchPath, "moved-origin"));

      var changed = await repository.ListChangedFilesAsync(baseCommit, head, 10, token);
      var diff = await repository.DiffFileAsync(baseCommit, "old.txt", head, "new.txt", token);

      Assert.Equal(
         [
            new ChangedFile("crlf.txt", FileChangeKind.Modified, LineEndingsOnly: true),
            new ChangedFile("logo.png", FileChangeKind.Added, Binary: true),
            new ChangedFile("modified.txt", FileChangeKind.Modified),
            new ChangedFile("new.txt", FileChangeKind.Renamed, "old.txt", Large: new LargeFile(28)),
         ],
         changed.WithoutObjectIds().OrderBy(file => file.Path, StringComparer.Ordinal));
      Assert.Equal([new TextChange(8, 14, 8, 14)], diff.Changes);
   }

   /// <summary>A query of one second would end the downloading diff, which the double delays by two.</summary>
   [Fact]
   public async Task Download_of_the_changed_files_has_the_transfer_timeout()
   {
      var baseCommit = _fixture.Commit("Base", ("a.txt", "one\n"));
      var head = _fixture.Commit("Head", ("a.txt", "two\n"));
      var git = GitDouble.Options(async run => run.Command.Arguments.Contains("--raw") && await run.DelayAsync(TimeSpan.FromSeconds(2)) is { } ended ? ended : await run.GitAsync());

      var repository = await _fixture.NewCache(git with { QueryTimeout = TimeSpan.FromSeconds(1) }).OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, TestContext.Current.CancellationToken);

      await repository.GetChangedFilesAsync(baseCommit, head, download: true, TestContext.Current.CancellationToken);

      Assert.Empty(MissingObjects(repository, baseCommit, head));
   }

   /// <summary>
   /// The origin moves away after the download, so a read that downloaded anything would fail. A directory, a missing file and a path that <see cref="RepositoryPath.IsValid"/> refuses name no
   /// file, and a named directory would download every file under it.
   /// </summary>
   [Fact]
   public async Task Download_of_named_files_fetches_exactly_their_contents_and_their_reads_need_no_remote()
   {
      var commit = _fixture.Commit("Base", ("a.txt", "one\n"), ("src/b.txt", "two\nlines\n"), ("c.txt", "three\n"), ("dir/d.txt", "four\n"));
      var repository = await SyncAsync();
      var token = TestContext.Current.CancellationToken;
      var undownloaded = new[] { "?" + BlobOf(commit, "c.txt"), "?" + BlobOf(commit, "dir/d.txt") }.Order(StringComparer.Ordinal).ToList();

      await repository.DownloadFilesAsync(commit, ["a.txt", "src/b.txt", "a.txt", "dir", "missing.txt", "../c.txt"], token);
      var missing = MissingObjects(repository, commit, commit);
      Directory.Move(_fixture.OriginPath, Path.Combine(_fixture.ScratchPath, "moved-origin"));
      var a = await repository.FindFileAsync(commit, "a.txt", token);
      var b = await repository.FindFileAsync(commit, "src/b.txt", token);

      Assert.Equal(undownloaded, missing.Order(StringComparer.Ordinal));
      Assert.Equal(("one\n", "two\nlines\n"), (await repository.ReadTextAsync(a!, token), await repository.ReadTextAsync(b!, token)));
   }

   /// <summary>With a path inside it named too, git lists the directory's every file, though the request names only one of them.</summary>
   [Fact]
   public async Task Download_of_a_directory_and_a_file_in_it_fetches_only_that_file()
   {
      var commit = _fixture.Commit("Base", ("dir/a.txt", "a\n"), ("dir/b.txt", "b\n"));
      var repository = await SyncAsync();

      await repository.DownloadFilesAsync(commit, ["dir", "dir/a.txt"], TestContext.Current.CancellationToken);

      Assert.Equal(["?" + BlobOf(commit, "dir/b.txt")], MissingObjects(repository, commit, commit));
   }

   [Fact]
   public async Task Download_of_named_files_fetches_them_in_one_fetch()
   {
      var commit = _fixture.Commit("Base", ("a.txt", "one\n"), ("b.txt", "two\n"), ("src/c.txt", "three\n"));
      var git = new RecordingGit(_fixture.ScratchPath);
      var repository = await SyncAsync(git.Options);

      await repository.DownloadFilesAsync(commit, ["a.txt", "b.txt", "src/c.txt"], TestContext.Current.CancellationToken);

      Assert.Equal(1, git.ContentFetches);
      Assert.Empty(MissingObjects(repository, commit, commit));
   }

   /// <summary>A query of one second would end the downloading diff, which the double delays by two.</summary>
   [Fact]
   public async Task Download_of_named_files_has_the_transfer_timeout()
   {
      var commit = _fixture.Commit("Base", ("a.txt", "one\n"));
      var git = GitDouble.Options(async run => run.Command.Arguments.Contains("--raw") && await run.DelayAsync(TimeSpan.FromSeconds(2)) is { } ended ? ended : await run.GitAsync());

      var repository = await _fixture.NewCache(git with { QueryTimeout = TimeSpan.FromSeconds(1) }).OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, TestContext.Current.CancellationToken);

      await repository.DownloadFilesAsync(commit, ["a.txt"], TestContext.Current.CancellationToken);

      Assert.Empty(MissingObjects(repository, commit, commit));
   }

   [Fact]
   public async Task Found_file_has_its_blob_and_size_and_a_directory_or_submodule_is_not_found()
   {
      _fixture.Commit("Base", ("src/app.cs", "class App;\n"));
      var commit = CommitSubmodule(new string('1', 40));
      var repository = await SyncAsync();
      var token = TestContext.Current.CancellationToken;

      var file = await repository.FindFileAsync(commit, "src/app.cs", token);

      Assert.Equal(new FileBlob(commit, "src/app.cs", _fixture.Git(_fixture.OriginPath, "rev-parse", "HEAD:src/app.cs").Trim(), 11, Binary: false), file);
      Assert.Null(await repository.FindFileAsync(commit, "src", token));
      Assert.Null(await repository.FindFileAsync(commit, "sub", token));
   }

   [Fact]
   public async Task Directory_listing_names_each_entry_of_the_directory_at_the_commit_with_its_type()
   {
      var baseCommit = _fixture.Commit("Base", ("README.md", "readme\n"), ("src/app.cs", "class App;\n"), ("src/lib/util.cs", "class Util;\n"), ("src/gone.cs", "gone\n"));
      _fixture.Commit("Head", ("src/gone.cs", null), ("src/added.cs", "added\n"));
      CommitSymlink("src/link", "app.cs");
      var head = CommitSubmodule(new string('1', 40));
      var repository = await SyncAsync();
      var token = TestContext.Current.CancellationToken;

      var root = await repository.ListDirectoryAsync(head, null, token);
      var src = await repository.ListDirectoryAsync(head, "src", token);
      var srcAtBase = await repository.ListDirectoryAsync(baseCommit, "src", token);

      Assert.Equal(
         [new RepositoryEntry("README.md", RepositoryEntryType.File), new RepositoryEntry("src", RepositoryEntryType.Directory), new RepositoryEntry("sub", RepositoryEntryType.Submodule)],
         root);
      Assert.Equal(
         [
            new RepositoryEntry("src/added.cs", RepositoryEntryType.File),
            new RepositoryEntry("src/app.cs", RepositoryEntryType.File),
            new RepositoryEntry("src/lib", RepositoryEntryType.Directory),
            new RepositoryEntry("src/link", RepositoryEntryType.Symlink),
         ],
         src);
      Assert.Equal(
         [new RepositoryEntry("src/app.cs", RepositoryEntryType.File), new RepositoryEntry("src/gone.cs", RepositoryEntryType.File), new RepositoryEntry("src/lib", RepositoryEntryType.Directory)],
         srcAtBase);
   }

   /// <summary>Git reads the pattern characters in a directory's name as plain characters, so <c>a*</c> lists only its own entries.</summary>
   [Fact]
   public async Task Directory_listing_of_a_name_with_pattern_characters_holds_only_that_directory()
   {
      Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows file names cannot hold * or ?.");
      var commit = _fixture.Commit("Base", ("a*/x.txt", "x\n"), ("ab/y.txt", "y\n"), ("a?c/z.txt", "z\n"));
      var repository = await SyncAsync();

      var listed = await repository.ListDirectoryAsync(commit, "a*", TestContext.Current.CancellationToken);

      Assert.Equal([new RepositoryEntry("a*/x.txt", RepositoryEntryType.File)], listed);
   }

   [Fact]
   public async Task Directory_listing_of_a_path_that_is_not_a_directory_at_the_commit_is_null()
   {
      _fixture.Commit("Base", ("src/app.cs", "class App;\n"));
      var commit = CommitSubmodule(new string('1', 40));
      var repository = await SyncAsync();
      var token = TestContext.Current.CancellationToken;

      Assert.Null(await repository.ListDirectoryAsync(commit, "src/app.cs", token));
      Assert.Null(await repository.ListDirectoryAsync(commit, "missing", token));
      Assert.Null(await repository.ListDirectoryAsync(commit, "sub", token));
   }

   [Fact]
   public async Task Directory_listing_downloads_no_file_content()
   {
      var commit = _fixture.Commit("Base", ("a.txt", "a\n"), ("src/b.txt", "b\n"), ("src/deep/c.txt", "c\n"));
      var repository = await SyncAsync();
      var token = TestContext.Current.CancellationToken;

      await repository.ListDirectoryAsync(commit, null, token);
      await repository.ListDirectoryAsync(commit, "src", token);
      await repository.ListDirectoryAsync(commit, "src/deep", token);

      // With --missing=print, rev-list prints each object that the clone lacks with a leading "?" and downloads nothing.
      var missing = _fixture.Git(repository.Path, "rev-list", "--objects", "--missing=print", commit.Value).Split('\n').Count(line => line.StartsWith('?'));
      Assert.Equal(3, missing);
   }

   [Fact]
   public async Task Query_for_a_commit_the_clone_lacks_reports_the_reason_without_its_command_line()
   {
      var commit = _fixture.Commit("Base", ("a.txt", "one\n"));
      var repository = await SyncAsync();
      var unknown = CommitId.Parse(new string('a', 40));

      var exception = await Assert.ThrowsAsync<GitException>(() => repository.ListChangedFilesAsync(commit, unknown, TestContext.Current.CancellationToken));

      Assert.DoesNotContain("diff-tree", exception.Message, StringComparison.Ordinal);
      Assert.DoesNotContain("fatal:", exception.Message, StringComparison.Ordinal);
      Assert.Contains(unknown.Value, exception.Message, StringComparison.Ordinal);
   }

   public void Dispose() => _fixture.Dispose();

   private void WriteBytes(string path, byte[] content) => File.WriteAllBytes(Path.Combine(_fixture.OriginPath, path), content);

   // The double's runs of git, one argument list per line.
   private static int GitRuns(RecordingGit git) => git.Arguments.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

   // With --missing=print, rev-list prints each object that the clone lacks with a leading "?" and downloads nothing.
   private List<string> MissingObjects(GitRepository repository, CommitId from, CommitId to) =>
      [.. _fixture.Git(repository.Path, "rev-list", "--objects", "--missing=print", from.Value, to.Value).Split('\n').Where(line => line.StartsWith('?'))];

   private string BlobOf(CommitId commit, string path) => _fixture.Git(_fixture.OriginPath, "rev-parse", $"{commit}:{path}").Trim();

   // A gitlink names a commit of another repository, which need not exist here.
   private CommitId CommitSubmodule(string submoduleCommit)
   {
      _fixture.Git(_fixture.OriginPath, "update-index", "--add", "--cacheinfo", $"160000,{submoduleCommit},sub");
      _fixture.Git(_fixture.OriginPath, "commit", "--quiet", "--message", "Point the submodule at " + submoduleCommit);
      return CommitId.Parse(_fixture.Git(_fixture.OriginPath, "rev-parse", "HEAD").Trim());
   }

   // Stages a symbolic link from its target text, so the test does not depend on the file system's support for links.
   private void CommitSymlink(string path, string target)
   {
      var targetFile = Path.Combine(_fixture.ScratchPath, "link-target");
      File.WriteAllText(targetFile, target);
      var blob = _fixture.Git(_fixture.OriginPath, "hash-object", "-w", "--no-filters", targetFile).Trim();
      _fixture.Git(_fixture.OriginPath, "update-index", "--add", "--cacheinfo", $"120000,{blob},{path}");
      _fixture.Git(_fixture.OriginPath, "commit", "--quiet", "--message", "Link " + path);
   }

   private Task<GitRepository> SyncAsync(GitOptions? options = null) =>
      _fixture.NewCache(options)
         .OpenAsync(_fixture.Remote(), [], FetchPolicy.Sync, TestContext.Current.CancellationToken);

   // Returns git options whose home and XDG configuration directory lie in the fixture, with the attributes in the named source.
   private GitOptions UserFilesGit(string source, string attributes)
   {
      var home = Directory.CreateDirectory(Path.Combine(_fixture.ScratchPath, "home")).FullName;
      var xdg = Directory.CreateDirectory(Path.Combine(_fixture.ScratchPath, "xdg")).FullName;
      var attributesPath = source switch
      {
         "core.attributesFile" => Path.Combine(_fixture.ScratchPath, "attributes"),
         "XDG_CONFIG_HOME" => Path.Combine(xdg, "git", "attributes"),
         "HOME" => Path.Combine(home, ".config", "git", "attributes"),
         _ => null,
      };
      if (attributesPath is not null)
      {
         Directory.CreateDirectory(Path.GetDirectoryName(attributesPath)!);
         File.WriteAllText(attributesPath, attributes);
      }

      if (source == "core.attributesFile")
      {
         // Git reads a backslash in a configuration value as an escape, so a Windows path goes in with forward slashes.
         File.WriteAllText(Path.Combine(home, ".gitconfig"), $"[core]\n\tattributesFile = {attributesPath!.Replace('\\', '/')}\n");
      }

      // Without XDG_CONFIG_HOME, git looks for the default attributes file under HOME.
      return GitDouble.Options(run => run.GitAsync(run.Command with
      {
         Environment = new Dictionary<string, string?>(run.Command.Environment) { ["HOME"] = home, ["XDG_CONFIG_HOME"] = source == "HOME" ? null : xdg },
      }));
   }
}
