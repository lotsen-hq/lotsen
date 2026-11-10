using System.Text.Json;
using Lotsen.Git;
using Lotsen.Json;
using Lotsen.Tours;
using static Lotsen.Tests.TourValues;

namespace Lotsen.Tests;

/// <summary>
/// Covers the leaf values of a tour that check themselves when they are built: an answer, a fingerprint, the reading progress, a pull request target, a synthesis, and the path of a code or file
/// anchor.
/// </summary>
[Trait("Category", "Rules")]
public sealed class TourValueTests
{
   private static readonly AnswerBatch Sent = new("b1", "session1", Now);
   private static readonly ThreadAnchor.CodeRange CodeAnchor = new(FileSide.Head, "src/a.cs", 1, 2, Head, "line 1\nline 2\n");
   private static readonly ThreadAnchor.WholeFile FileAnchor = new(FileSide.Head, "src/a.cs", Head);
   private static readonly TourDocument.StoredTour Stored = TourDocument.New(Id, Tour().Target, Familiarity.Some, PlanApproval.NotNeeded, Now).Value!;

   [Theory]
   [InlineData("draft with a batch")]
   [InlineData("sent without a batch")]
   [InlineData("sent with a received time")]
   [InlineData("received without a received time")]
   [InlineData("outdated on version 0")]
   public void Answer_whose_state_and_batch_disagree_or_whose_version_is_below_1_is_refused(string answer) =>
      Assert.Throws<ArgumentException>(() => answer switch
      {
         "draft with a batch" => Answer(AnswerState.Draft, Sent),
         "sent without a batch" => Answer(AnswerState.Sent, null),
         "sent with a received time" => Answer(AnswerState.Sent, Sent with { ReceivedAt = Now }),
         "received without a received time" => Answer(AnswerState.Received, Sent),
         _ => Answer(AnswerState.Draft, null, outdatedOn: 0),
      });

   [Fact]
   public void Answer_moves_from_draft_to_sent_to_received_and_back_to_a_draft()
   {
      var received = Answer(AnswerState.Draft, null).SentIn(Sent).AsReceived(Now);

      Assert.Equal((AnswerState.Received, Now), (received.State, received.Batch?.ReceivedAt));
      Assert.Equal((AnswerState.Draft, null), (received.Revised("No.", AnswerType.Concern, Now).State, received.Revised("No.", AnswerType.Concern, Now).Batch));
      Assert.Equal(1, received.OutdatedAt(1).OutdatedOn);
   }

   [Theory]
   [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
   [InlineData("0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef", false)]
   [InlineData("0123456789abcdef", false)]
   [InlineData("", false)]
   [InlineData(null, false)]
   public void Fingerprint_parses_only_64_hexadecimal_digits_in_lowercase(string? text, bool parses)
   {
      Assert.Equal(parses, SummaryFingerprint.TryParse(text, out var fingerprint));
      Assert.Equal(parses, fingerprint.IsValid);
      Assert.Equal(parses ? text : "", fingerprint.ToString());
   }

   [Theory]
   [InlineData(false, null)]
   [InlineData(true, "")]
   public void Progress_on_the_summary_page_of_a_tour_that_is_not_finished_or_on_a_stop_without_an_ID_is_refused(bool finished, string? lastStop) =>
      Assert.Throws<ArgumentException>(() => new ReadingProgress(finished, lastStop));

   [Theory]
   [InlineData(true, null)]
   [InlineData(false, "s1")]
   [InlineData(true, "s1")]
   public void Progress_on_a_stop_or_on_the_summary_page_of_a_finished_tour_is_built(bool finished, string? lastStop) =>
      Assert.Equal((finished, lastStop), (new ReadingProgress(finished, lastStop).Finished, new ReadingProgress(finished, lastStop).LastStop));

   [Theory]
   [InlineData("shop/pull/42")]
   [InlineData("/shop/pull/42")]
   public void Target_with_a_relative_url_is_refused(string url) => Assert.Throws<ArgumentException>(() => new PullRequestTarget("test", new Uri(url, UriKind.Relative)));

   [Theory]
   [InlineData(" ", false, true)]
   [InlineData("Keep it.", true, true)]
   [InlineData("Keep it.", false, false)]
   public void Synthesis_without_text_a_write_time_or_a_fingerprint_is_refused(string text, bool unwritten, bool validFingerprint)
   {
      _ = SummaryFingerprint.TryParse(validFingerprint ? new string('a', 64) : "x", out var fingerprint);

      Assert.Throws<ArgumentException>(() => new TourSynthesis(text, unwritten ? default : Now, Pinned, fingerprint));
   }

   [Theory]
   [InlineData("moved/deep/b.txt")]
   [InlineData("1:b.txt")]
   public void Code_anchor_and_file_anchor_hold_a_path_that_RepositoryPath_accepts(string path)
   {
      Assert.Equal(path, new ThreadAnchor.CodeRange(FileSide.Head, path, 1, 2, Head, "line 1\nline 2\n").Path);
      Assert.Equal(path, (CodeAnchor with { Path = path }).Path);
      Assert.Equal(path, new ThreadAnchor.WholeFile(FileSide.Head, path, Head).Path);
      Assert.Equal(path, (FileAnchor with { Path = path }).Path);
   }

   /// <summary>Each row builds the anchor with its constructor and with a <c>with</c> expression, which copies a record without its primary constructor.</summary>
   [Theory]
   [InlineData(@"moved\b.txt")]
   [InlineData("c:b.txt")]
   [InlineData("   ")]
   public void Code_anchor_and_file_anchor_with_a_path_that_RepositoryPath_refuses_are_refused(string path)
   {
      Assert.Throws<ArgumentException>(() => new ThreadAnchor.CodeRange(FileSide.Head, path, 1, 2, Head, "line 1\nline 2\n"));
      Assert.Throws<ArgumentException>(() => CodeAnchor with { Path = path });
      Assert.Throws<ArgumentException>(() => new ThreadAnchor.WholeFile(FileSide.Head, path, Head));
      Assert.Throws<ArgumentException>(() => FileAnchor with { Path = path });
   }

   /// <summary>The tour's file holds one note on the anchor, whose path the test changes in the JSON alone.</summary>
   [Theory]
   [InlineData("code", @"moved\b.txt")]
   [InlineData("code", "c:b.txt")]
   [InlineData("file", "   ")]
   [InlineData("file", @"moved\b.txt")]
   public void Stored_tour_whose_code_or_file_anchor_has_a_path_that_RepositoryPath_refuses_is_refused(string kind, string path)
   {
      var stored = Stored.Next(Stored.Tour with { Threads = ThreadFixtures.Threads(ThreadFixtures.Note("t1", kind == "code" ? CodeAnchor : FileAnchor)) }, Now).Value!;
      var file = JsonSerializer.Serialize(stored.Document, LotsenJsonContext.Default.TourDocument);
      var changed = file.Replace("\"path\":\"src/a.cs\"", $"\"path\":\"{JsonEncodedText.Encode(path)}\"");
      Assert.NotEqual(file, changed);

      Assert.NotNull(JsonSerializer.Deserialize(file, LotsenJsonContext.Default.TourDocument));
      Assert.Throws<ArgumentException>(() => JsonSerializer.Deserialize(changed, LotsenJsonContext.Default.TourDocument));
   }

   private static CheckAnswer Answer(AnswerState state, AnswerBatch? batch, int? outdatedOn = null) => new("s1", "c1", "Yes.", AnswerType.Decision, state, Now, batch, outdatedOn);
}
