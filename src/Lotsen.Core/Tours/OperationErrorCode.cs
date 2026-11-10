using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Lotsen.Json;

namespace Lotsen.Tours;

/// <summary>What kind of failure an <see cref="OperationErrorCode"/> is, which decides the HTTP status of its refusal.</summary>
internal closed record RefusalKind
{
   /// <summary>The input is malformed or breaks a rule.</summary>
   internal sealed record BadInput : RefusalKind;

   /// <summary>The input names something that does not exist.</summary>
   internal sealed record NotFound : RefusalKind;

   /// <summary>The caller may not do what the request asks.</summary>
   internal sealed record CallerRefused : RefusalKind;

   /// <summary>What the input names is in a state that the operation does not accept now.</summary>
   internal sealed record Conflict : RefusalKind;

   /// <summary>A service that lotsen depends on, such as a git remote, failed.</summary>
   internal sealed record Upstream : RefusalKind;

   /// <summary>lotsen itself failed, as with a stored tour that it cannot read.</summary>
   internal sealed record Broken : RefusalKind;

   /// <summary>The server cannot take the request now, as while it stops.</summary>
   internal sealed record Unavailable : RefusalKind;

   /// <summary>The server does not know the credential that the request carries.</summary>
   internal sealed record Unauthenticated : RefusalKind;
}

/// <summary>Why an operation was refused: one of a closed set of codes, each with its <see cref="RefusalKind"/>.</summary>
/// <remarks>JSON writes and reads a code as its <see cref="Name"/>, and refuses any other value.</remarks>
[JsonConverter(typeof(Json))]
internal sealed class OperationErrorCode
{
   private OperationErrorCode(string name, RefusalKind kind)
   {
      Name = name;
      Kind = kind;
      Declared.Codes.Add(this);
   }

   // The static constructor runs after every field initializer, so the codes are complete wherever a field is declared in the file.
   static OperationErrorCode()
   {
      All = [.. Declared.Codes];
      ByName = All.ToFrozenDictionary(code => code.Name, StringComparer.Ordinal);
   }

   /// <summary>The code as JSON names it, in camelCase.</summary>
   public string Name { get; }

   public RefusalKind Kind { get; }

   /// <summary>The input is malformed or breaks a rule. The reason names the field.</summary>
   public static readonly OperationErrorCode InvalidInput = new("invalidInput", new RefusalKind.BadInput());

   /// <summary>No tour has the given ID.</summary>
   public static readonly OperationErrorCode TourNotFound = new("tourNotFound", new RefusalKind.NotFound());

   /// <summary>The tour's plan has no stop with the given ID, or the tour has no plan.</summary>
   public static readonly OperationErrorCode StopNotFound = new("stopNotFound", new RefusalKind.NotFound());

   /// <summary>The tour is stored but its file cannot be read.</summary>
   public static readonly OperationErrorCode TourUnreadable = new("tourUnreadable", new RefusalKind.Broken());

   /// <summary>The tour is not pinned, so it has no commits to read files at. The error carries the tour's pin.</summary>
   public static readonly OperationErrorCode TourNotPinned = new("tourNotPinned", new RefusalKind.Conflict());

   /// <summary>The path names no file on the requested side, or is not a path in the repository.</summary>
   public static readonly OperationErrorCode FileNotFound = new("fileNotFound", new RefusalKind.NotFound());

   /// <summary>Git could not read the tour's repository, as when the remote cannot be reached or there is no token.</summary>
   public static readonly OperationErrorCode RepositoryUnavailable = new("repositoryUnavailable", new RefusalKind.Upstream());

   /// <summary>The operation's access declaration does not admit the caller, as when the CLI writes a tour's plan, or the session that the request names cannot act on what it names.</summary>
   public static readonly OperationErrorCode CallerNotAllowed = new("callerNotAllowed", new RefusalKind.CallerRefused());

   /// <summary>Another agent session is bound to the tour, and the caller did not ask to take it over.</summary>
   public static readonly OperationErrorCode AttachedToAnotherSession = new("attachedToAnotherSession", new RefusalKind.Conflict());

   /// <summary>No agent session is bound to the tour, so no session can receive the question, or the calling session cannot write what only the bound session writes.</summary>
   public static readonly OperationErrorCode NoSessionAttached = new("noSessionAttached", new RefusalKind.Conflict());

   /// <summary>No question of the tour has the given ID.</summary>
   public static readonly OperationErrorCode QuestionNotFound = new("questionNotFound", new RefusalKind.NotFound());

   /// <summary>No thread of the tour has the given ID.</summary>
   public static readonly OperationErrorCode ThreadNotFound = new("threadNotFound", new RefusalKind.NotFound());

   /// <summary>The question's last request does not wait for an answer, so there is nothing to cancel.</summary>
   public static readonly OperationErrorCode RequestNotPending = new("requestNotPending", new RefusalKind.Conflict());

   /// <summary>The question has its answer, so it takes no other reply and cannot be sent again or canceled.</summary>
   public static readonly OperationErrorCode QuestionAnswered = new("questionAnswered", new RefusalKind.Conflict());

   /// <summary>
   /// The thread's last question waits for its answer, so the thread takes no follow-up, and the bound session can add no message to it, outdate it or delete it. A thread of a wait group
   /// waits too, so the tour takes no other thread of that group that would wait, until it is answered or canceled.
   /// </summary>
   public static readonly OperationErrorCode QuestionWaiting = new("questionWaiting", new RefusalKind.Conflict());

   /// <summary>The question has a follow-up in its thread, so it cannot be sent again.</summary>
   public static readonly OperationErrorCode QuestionFollowedUp = new("questionFollowedUp", new RefusalKind.Conflict());

   /// <summary>The nonce is not the calling session's nonce, or no event has carried a nonce to the session yet.</summary>
   public static readonly OperationErrorCode WrongNonce = new("wrongNonce", new RefusalKind.Conflict());

   /// <summary>The thread is not a note, because the reviewer sent it or asked it at once, so it cannot be edited, discarded or sent as a note.</summary>
   public static readonly OperationErrorCode NoteSent = new("noteSent", new RefusalKind.Conflict());

   /// <summary>The thread is a note that the reviewer has not sent, so it has no question to follow up on.</summary>
   public static readonly OperationErrorCode ThreadIsNote = new("threadIsNote", new RefusalKind.Conflict());

   /// <summary>The stop's current content has no check with the given ID.</summary>
   public static readonly OperationErrorCode CheckNotFound = new("checkNotFound", new RefusalKind.NotFound());

   /// <summary>The check has no answer.</summary>
   public static readonly OperationErrorCode AnswerNotFound = new("answerNotFound", new RefusalKind.NotFound());

   /// <summary>No answer of the tour is in the batch with the given ID.</summary>
   public static readonly OperationErrorCode BatchNotFound = new("batchNotFound", new RefusalKind.NotFound());

   /// <summary>The tour's plan waits for the reviewer's approval, so no stop can be written yet.</summary>
   public static readonly OperationErrorCode PlanAwaitsApproval = new("planAwaitsApproval", new RefusalKind.Conflict());

   /// <summary>The tour has no answer to send: no draft, and no sent answer whose session lost the tour or ended before it received the answer.</summary>
   public static readonly OperationErrorCode NoAnswersToSend = new("noAnswersToSend", new RefusalKind.Conflict());

   /// <summary>
   /// The tour has no available iteration to update to, or the pull request's latest iteration is not the available one, because its head or merge base moved again or lotsen could not read it.
   /// </summary>
   public static readonly OperationErrorCode IterationNotAvailable = new("iterationNotAvailable", new RefusalKind.Conflict());

   /// <summary>The tour changed while the operation ran, each time it tried, so nothing was stored. The same request can succeed when it is sent again.</summary>
   public static readonly OperationErrorCode TourChanged = new("tourChanged", new RefusalKind.Conflict());

   /// <summary>The operation built a tour that breaks a rule of a stored tour, a fault of the server, so nothing was stored.</summary>
   public static readonly OperationErrorCode TourRuleBroken = new("tourRuleBroken", new RefusalKind.Broken());

   /// <summary>The request names no operation of the catalog.</summary>
   public static readonly OperationErrorCode OperationNotFound = new("operationNotFound", new RefusalKind.NotFound());

   /// <summary>The adapter that opens an agent session is another build or protocol than the server. The reason names both.</summary>
   public static readonly OperationErrorCode BuildMismatch = new("buildMismatch", new RefusalKind.Conflict());

   /// <summary>The background server has decided to exit, after its grace period or for an adapter of another build while no agent session was open, so it opens no agent session.</summary>
   public static readonly OperationErrorCode ServerExiting = new("serverExiting", new RefusalKind.Unavailable());

   /// <summary>The agent session that the request's session credential names has ended, or the server never opened it.</summary>
   public static readonly OperationErrorCode SessionUnknown = new("sessionUnknown", new RefusalKind.Unauthenticated());

   /// <summary>Every code, in declaration order.</summary>
   public static IReadOnlyList<OperationErrorCode> All { get; }

   private static readonly FrozenDictionary<string, OperationErrorCode> ByName;

   public override string ToString() => Name;

   /// <summary>Converts a code to and from its name, and refuses any other value, as <see cref="StrictEnumConverter{TEnum}"/> does for an enum.</summary>
   internal sealed class Json : JsonConverter<OperationErrorCode>, IJsonSchemaConverter
   {
      public override OperationErrorCode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
         reader.TokenType == JsonTokenType.String && ByName.TryGetValue(reader.GetString()!, out var code)
            ? code
            : throw new JsonException($"Expected one of {string.Join(", ", All.Select(code => code.Name))}.");

      public override void Write(Utf8JsonWriter writer, OperationErrorCode value, JsonSerializerOptions options) => writer.WriteStringValue(value.Name);

      public JsonObject GetSchema() => new()
      {
         ["type"] = "string",
         ["enum"] = new JsonArray([.. All.Select(code => JsonValue.Create(code.Name))]),
      };
   }

   // A nested type, so the list exists before the first code's initializer runs, whatever the order of the fields.
   private static class Declared
   {
      public static readonly List<OperationErrorCode> Codes = [];
   }
}
