using System.ComponentModel;
using System.Text.Json.Serialization;
using Lotsen.Json;
using Lotsen.Prose;
using Lotsen.Sessions;
using Lotsen.Tours;

namespace Lotsen.Operations;

/// <summary>The input of <c>reply</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReplyInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId,
   [property: Description("The question's ID, as the question's channel event gave it.")]
   string QuestionId,
   [property: Description("How the reply resolves the request: done when the answer is complete or you did what the reviewer asked, needsAnswer when the reviewer must answer a question of yours before you can continue, or declined when you will not do what the reviewer asked.")]
   ReplyResolution Resolution,
   [property: Description("The answer in Markdown, which may link to code as [label](ref:<side>/<path>#L<start>-<end>). done and needsAnswer need it. declined may leave it out.")]
   string? Text = null);

/// <summary><c>reply</c>, with which a session answers a question.</summary>
/// <remarks>
/// A question has at most one answer: the first reply wins, and the replied request holds the resolution. Until then, the session that a request went to may answer it, whatever the request's
/// state and whatever session holds the tour now, because a session that lost the tour or a canceled request may still produce the answer that the reviewer waits for. The writer checks the
/// question again on the latest tour, so two replies that race store one answer, and the second is refused.
/// </remarks>
internal sealed class ReplyOperations
{
   private readonly TourWriter _writer;
   private readonly FileOperations _files;

   public ReplyOperations(TourWriter writer, FileOperations files)
   {
      _writer = writer;
      _files = files;
   }

   /// <summary>Stores a session's answer to a question as an agent message in the question's thread, gives the answered request the reply's resolution, and returns the thread.</summary>
   /// <remarks>
   /// The reply answers the first request of the question that went to the calling session, and every other pending request of the question becomes canceled. A question that has its answer
   /// refuses every later reply. The reply's state rule is that of <see cref="TourThreads"/>. The reply's <c>ref:</c> links pass the checks of a stop's prose.
   /// </remarks>
   public Task<OperationResult<QuestionThread>> ReplyAsync(Access.AnySession.Pass pass, ReplyInput input, CancellationToken cancellationToken)
   {
      var text = string.IsNullOrWhiteSpace(input.Text) ? null : input.Text;
      var reply = new ThreadCommand.Reply(input.QuestionId, input.Resolution, text, pass.Recipient);
      return _writer.WriteAfterGitAsync<IReadOnlyList<ProseReference>, QuestionThread>(
         pass,
         input.TourId,
         tour => TourThreads.Check(tour, reply)
            ?? (text is null && input.Resolution != ReplyResolution.Declined ? OperationError.Invalid("text", "a reply needs an answer, unless it declines the request.") : null),
         async (_, git, token) =>
         {
            var links = await ProseLinks.CheckAsync(git, _files, text, token);
            return links.Value is { } references ? Gathered.Of(references) : links.Error!;
         },
         (edit, _) => edit.Store(edit.Threads.Apply(reply), (_, written) => written.Thread!),
         cancellationToken);
   }
}
