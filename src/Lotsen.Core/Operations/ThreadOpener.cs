using Lotsen.Tours;

namespace Lotsen.Operations;

/// <summary>What a new thread starts with: the reviewer's question to the bound session, the reviewer's note, or the bound session's agent message.</summary>
/// <remarks>Each <see cref="ThreadOpener"/> method stores one of them, chosen by the pass that it takes.</remarks>
internal enum ThreadOpening
{
   Question,
   Note,
   AgentMessage,
}

/// <summary>The input of a new thread, whichever operation starts it.</summary>
/// <param name="TourId">The thread's tour.</param>
/// <param name="Text">The first message's words.</param>
/// <param name="Anchor">Where the thread belongs.</param>
/// <param name="StopId">The stop that the reviewer views, or null for none.</param>
internal sealed record NewThreadInput(TourId TourId, string Text, AnchorInput Anchor, string? StopId)
{
   public static NewThreadInput Of(AskQuestionInput input) => new(input.TourId, input.Text, input.Anchor, input.StopId);
}

/// <summary>A new thread with the tour that one write stored it in.</summary>
internal sealed record StoredThread(Tour Tour, QuestionThread Thread);

/// <summary>Stores new threads: a question, a note or an agent message.</summary>
internal static class ThreadOpener
{
   /// <summary>The most characters that a code anchor's text may hold. Every read of the tour returns the text of each thread's anchor.</summary>
   public const int MaxCodeAnchorLength = 20_000;

   /// <summary>Stores the reviewer's question as a new thread at the input's anchor, and sends it to the tour's bound session.</summary>
   /// <remarks>
   /// The tour needs a bound session. A code range or a file anchor is checked against the tour's pinned commits as a stop's passage is, except that it may point at unchanged code, and a code
   /// range's text holds at most <see cref="MaxCodeAnchorLength"/> characters. A stop text anchor takes the stop's version that is current when the thread is stored.
   /// </remarks>
   /// <param name="writer">Writes the tour.</param>
   /// <param name="files">Opens the tour's clone for the anchor.</param>
   /// <param name="pass">The pass of the operation that asks.</param>
   /// <param name="input">The thread's input.</param>
   /// <param name="cancellationToken">Stops git.</param>
   public static Task<OperationResult<StoredThread>> StoreQuestionAsync(TourWriter writer, FileOperations files, Access.ReviewerToSession.Pass pass, NewThreadInput input, CancellationToken cancellationToken) =>
      writer.WriteAfterGitAsync<ThreadAnchor, StoredThread>(
         pass, input.TourId, Check(ThreadOpening.Question, input), Gather(files, ThreadOpening.Question, input),
         (edit, anchor) => Stored(edit, edit.Threads.Ask(Placed(edit, anchor), input.Text, input.StopId)), cancellationToken);

   /// <summary>Stores the reviewer's note as a new thread at the input's anchor, with the anchor checks of <see cref="StoreQuestionAsync"/>. The tour needs no bound session.</summary>
   public static Task<OperationResult<StoredThread>> StoreNoteAsync(TourWriter writer, FileOperations files, Access.Reviewer.Pass pass, NewThreadInput input, CancellationToken cancellationToken) =>
      writer.WriteAfterGitAsync<ThreadAnchor, StoredThread>(
         pass, input.TourId, Check(ThreadOpening.Note, input), Gather(files, ThreadOpening.Note, input),
         (edit, anchor) => Stored(edit, edit.Threads.Apply(new ThreadCommand.SaveNote(Placed(edit, anchor), input.Text, input.StopId))), cancellationToken);

   /// <summary>
   /// Stores the bound session's agent message as a new thread at the input's anchor, with the anchor checks of <see cref="StoreQuestionAsync"/>. Its <c>ref:</c> links pass the checks of a
   /// reply.
   /// </summary>
   public static Task<OperationResult<StoredThread>> StoreAgentMessageAsync(TourWriter writer, FileOperations files, Access.BoundSession.Pass pass, NewThreadInput input, CancellationToken cancellationToken) =>
      writer.WriteAfterGitAsync<ThreadAnchor, StoredThread>(
         pass, input.TourId, Check(ThreadOpening.AgentMessage, input), Gather(files, ThreadOpening.AgentMessage, input),
         (edit, anchor) => Stored(edit, edit.Threads.Apply(new ThreadCommand.Start(Placed(edit, anchor), input.Text, edit.Recipient))), cancellationToken);

   /// <summary>Returns the refusal of an agent message without text, or null.</summary>
   public static OperationError? CheckAgentText(string? text) => string.IsNullOrWhiteSpace(text) ? OperationError.Invalid("text", "a message needs text.") : null;

   private static OperationError? CheckWords(ThreadOpening opening, NewThreadInput input) => opening == ThreadOpening.AgentMessage ? CheckAgentText(input.Text) : ReviewerWords.Check(input.Text);

   private static Func<Tour, OperationError?> Check(ThreadOpening opening, NewThreadInput input) => tour =>
      CheckWords(opening, input) ?? AnchorChecks.CheckFields(input.Anchor) ?? AnchorChecks.CheckPlanReferences(tour, input.Anchor, input.StopId);

   private static Func<Tour, GitPhase, CancellationToken, Task<OperationResult<Gathered<ThreadAnchor>>>> Gather(FileOperations files, ThreadOpening opening, NewThreadInput input) =>
      async (tour, git, token) =>
      {
         if (opening == ThreadOpening.AgentMessage && (await ProseLinks.CheckAsync(git, files, input.Text, FileOf(input.Anchor), token)).Error is { } linkError)
         {
            return linkError;
         }

         var anchor = await ReadAnchorAsync(git, files, tour, input.Anchor, token);
         return anchor.Value is { } read ? Gathered.Of(read) : anchor.Error!;
      };

   // Returns the file that a code range or a file anchor reads, or none for another anchor.
   private static IReadOnlyList<(FileSide Side, string Path)> FileOf(AnchorInput anchor) => anchor switch
   {
      AnchorInput.CodeRange code => [(code.Side, code.Path)],
      AnchorInput.WholeFile file => [(file.Side, file.Path)],
      _ => [],
   };

   // A stop text anchor takes the stop's version on the latest tour, which the plan checks found written on the read tour and on the latest one.
   private static ThreadAnchor Placed(TourEdit edit, ThreadAnchor anchor) =>
      anchor is ThreadAnchor.StopText selected ? selected with { Version = ((Stop.Written)edit.Latest.FindStop(selected.StopId)!).Version } : anchor;

   private static Edited<StoredThread> Stored(TourEdit edit, OperationResult<ThreadWrite> write) => edit.Store(write, (stored, written) => new StoredThread(stored.Tour, written.Thread!));

   // Reads the text of a code range and the commit of a file from the tour's clone, and converts every other anchor as it is.
   private static async Task<OperationResult<ThreadAnchor>> ReadAnchorAsync(GitPhase git, FileOperations files, Tour tour, AnchorInput anchor, CancellationToken cancellationToken)
   {
      switch (anchor)
      {
         case AnchorInput.StopText selected:
            // The anchor checks refused a stop without content, on the read tour and on the latest one.
            return new ThreadAnchor.StopText(selected.StopId, selected.Field, selected.Text, ((Stop.Written)tour.FindStop(selected.StopId)!).Version);
         case AnchorInput.WholeStop stop:
            return new ThreadAnchor.WholeStop(stop.StopId);
         case AnchorInput.WholeTour:
            return new ThreadAnchor.WholeTour();
      }

      return await git.OnPassagesAsync<ThreadAnchor>(files, "anchor", async checks =>
      {
         if (anchor is AnchorInput.CodeRange code)
         {
            var lines = await checks.ReadLinesAsync(code.Side, code.Path, code.StartLine, code.EndLine, "anchor", cancellationToken);
            if (lines.Value is not { } read)
            {
               return lines.Error!;
            }

            var text = read.Text(code.StartLine, code.EndLine);
            return text.Length > MaxCodeAnchorLength
               ? OperationError.Invalid(
                  ((FieldPath)"anchor").Then("endLine"),
                  $"lines {code.StartLine}-{code.EndLine} of '{code.Path}' at the {StopPassages.SideName(code.Side)} hold {text.Length} characters, but a question's code range holds at most {MaxCodeAnchorLength}. Select fewer lines, or ask about the whole file.")
               : new ThreadAnchor.CodeRange(code.Side, code.Path, code.StartLine, code.EndLine, checks.CommitOf(code.Side), text);
         }

         var file = (AnchorInput.WholeFile)anchor;
         var found = await checks.FindFileAsync(file.Side, file.Path, "anchor", cancellationToken);
         return found.Value is { } blob ? new ThreadAnchor.WholeFile(file.Side, file.Path, blob.Commit) : found.Error!;
      });
   }
}
