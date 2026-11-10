using System.Text.Json;
using System.Text.Json.Serialization;
using Lotsen.Git;
using Lotsen.Json;

namespace Lotsen.Tours;

/// <summary>A conversation about one place in a tour: the reviewer's unsent note, or a thread sent to an agent session. The <c>kind</c> property in JSON names the derived type.</summary>
/// <remarks>
/// <para>
/// Each kind has a private constructor that checks the kind's invariants and read-only properties, so a thread is built only by its own transitions, and a stored file that breaks an invariant
/// fails to load. <see cref="TourThreads"/> decides every change of a tour's threads, and its table holds the rules for when a thread is open, resolved, outdated or waiting.
/// </para>
/// <para>
/// Sending a note makes the note's words the first question of an <see cref="Sent.Asked"/> thread with the same ID and anchor. A <see cref="Sent.Decided"/> thread reports a reviewer decision
/// on the tour anchor. A <see cref="Sent.Started"/> thread has no question until the reviewer follows up in it.
/// </para>
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Note), "note")]
[JsonDerivedType(typeof(Sent.Asked), "asked")]
[JsonDerivedType(typeof(Sent.Decided), "decided")]
[JsonDerivedType(typeof(Sent.Started), "started")]
internal closed record QuestionThread
{
   private QuestionThread(string id, ThreadAnchor anchor, QuestionThreadState state)
   {
      Id = id ?? throw StoredData.Invalid("A thread needs an ID.");
      Anchor = anchor ?? throw StoredData.Invalid($"Thread {id} needs an anchor.");
      State = state;
   }

   /// <summary>The thread's ID, unique among the IDs of the tour's threads, messages and requests.</summary>
   public string Id { get; }

   /// <summary>Where the reviewer asked or wrote the note, or where the session started the thread.</summary>
   public ThreadAnchor Anchor { get; }

   /// <summary>Whether the reviewer considers the thread settled, or the session marked it outdated.</summary>
   public QuestionThreadState State { get; }

   /// <summary>Returns the thread, of the same kind, in the state.</summary>
   internal QuestionThread WithState(QuestionThreadState state) => state == State ? this : Rebuilt(Anchor, state);

   /// <summary>Returns the thread, of the same kind, at the anchor.</summary>
   internal QuestionThread WithAnchor(ThreadAnchor anchor) => Rebuilt(anchor, State);

   private protected abstract QuestionThread Rebuilt(ThreadAnchor anchor, QuestionThreadState state);

   /// <summary>The reviewer's words, which no agent session has received.</summary>
   [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
   internal sealed record Note : QuestionThread
   {
      /// <param name="id">The thread's ID.</param>
      /// <param name="anchor">Where the reviewer wrote the note.</param>
      /// <param name="state">The thread's state.</param>
      /// <param name="text">The reviewer's words, as plain text.</param>
      /// <param name="savedAt">When the server stored the words, at the save or at the last edit that changed them.</param>
      /// <param name="stopId">The stop that the reviewer viewed while writing the note, or null for none.</param>
      [JsonConstructor]
      private Note(string id, ThreadAnchor anchor, QuestionThreadState state, string text, DateTimeOffset savedAt, string? stopId = null)
         : base(id, anchor, state)
      {
         Text = text ?? throw StoredData.Invalid($"Note {id} needs its words.");
         SavedAt = savedAt;
         StopId = stopId;
      }

      /// <summary>The reviewer's words, as plain text.</summary>
      public string Text { get; }

      /// <summary>When the server stored the words, at the save or at the last edit that changed them.</summary>
      public DateTimeOffset SavedAt { get; }

      /// <summary>The stop that the reviewer viewed while writing the note, or null for none. Sending the note gives it to the question. JSON leaves it out when null.</summary>
      [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
      public string? StopId { get; }

      /// <summary>Returns a new open note.</summary>
      internal static Note Saved(string id, ThreadAnchor anchor, string text, DateTimeOffset now, string? stopId) => new(id, anchor, QuestionThreadState.Open, text, now, stopId);

      /// <summary>Returns the note with new words, saved at <paramref name="now"/>.</summary>
      internal Note Edited(string text, DateTimeOffset now) => new(Id, Anchor, State, text, now, StopId);

      private protected override QuestionThread Rebuilt(ThreadAnchor anchor, QuestionThreadState state) => new Note(Id, anchor, state, Text, SavedAt, StopId);
   }

   /// <summary>A thread that reached an agent session, with its messages in the order they were written.</summary>
   /// <remarks>
   /// The constructor checks the thread's timeline. Only the last question may have a pending request. Each reply names a resolved request of an earlier question in the thread, which went to the
   /// reply's session, and each resolved request has one reply.
   /// </remarks>
   internal closed record Sent : QuestionThread
   {
      private Sent(string id, ThreadAnchor anchor, QuestionThreadState state, IReadOnlyList<ThreadMessage> messages)
         : base(id, anchor, state)
      {
         Messages = CheckTimeline(id, messages);
         LastQuestion = Messages.OfType<ThreadMessage.Question>().LastOrDefault();
         Waiting = LastQuestion is { IsWaiting: true } last ? last : null;
      }

      /// <summary>The messages in the order they were written. The first is the reviewer's question or the session's agent message, and each later question is a follow-up.</summary>
      public IReadOnlyList<ThreadMessage> Messages { get; }

      /// <summary>The type of message that each kind of sent thread starts with, which the kind's constructor checks and the UI's contract states.</summary>
      public static IReadOnlyDictionary<Type, Type> FirstMessages { get; } = new Dictionary<Type, Type>
      {
         [typeof(Asked)] = typeof(ThreadMessage.Question),
         [typeof(Decided)] = typeof(ThreadMessage.Question),
         [typeof(Started)] = typeof(ThreadMessage.AgentMessage),
      };

      // Whether the first message has the type that FirstMessages names for the thread's kind.
      private protected bool StartsAsDeclared => FirstMessages[GetType()].IsInstanceOfType(Messages[0]);

      /// <summary>The thread's last question, or null for a started thread that the reviewer has not followed up in.</summary>
      [JsonIgnore]
      public ThreadMessage.Question? LastQuestion { get; }

      /// <summary>The thread's last question while it waits for a reply, or null when no question of the thread waits.</summary>
      /// <remarks>Only the last question can wait, because the constructor refuses a pending request on an earlier question.</remarks>
      [JsonIgnore]
      public ThreadMessage.Question? Waiting { get; }

      /// <summary>Returns the thread, of the same kind, with the state and the messages.</summary>
      internal Sent With(QuestionThreadState state, IReadOnlyList<ThreadMessage> messages) => Rebuilt(Anchor, state, messages);

      private protected sealed override QuestionThread Rebuilt(ThreadAnchor anchor, QuestionThreadState state) => Rebuilt(anchor, state, Messages);

      private protected abstract Sent Rebuilt(ThreadAnchor anchor, QuestionThreadState state, IReadOnlyList<ThreadMessage> messages);

      private static IReadOnlyList<ThreadMessage> CheckTimeline(string id, IReadOnlyList<ThreadMessage> messages)
      {
         if (messages is null || messages.Count == 0 || messages.Any(message => message is null))
         {
            throw StoredData.Invalid($"Thread {id} needs its messages, none of them null.");
         }

         var questions = messages.OfType<ThreadMessage.Question>().ToList();
         if (questions.SkipLast(1).Any(question => question.Requests.Any(request => request.State == RequestState.Pending)))
         {
            throw StoredData.Invalid($"Thread {id} has a pending request on a question before its last one.");
         }

         var resolved = new Dictionary<string, QuestionRequest>(StringComparer.Ordinal);
         var replied = new HashSet<string>(StringComparer.Ordinal);
         foreach (var message in messages)
         {
            switch (message)
            {
               case ThreadMessage.Question question when question.Answered is { } answered:
                  resolved[answered.Id] = answered;
                  break;
               case ThreadMessage.Question.Reply reply:
                  if (!resolved.TryGetValue(reply.RequestId, out var request) || request.SessionId != reply.SessionId || !replied.Add(reply.RequestId))
                  {
                     throw StoredData.Invalid($"Reply {reply.Id} of thread {id} names no resolved request of an earlier question that went to its session, or one that another reply names.");
                  }

                  break;
            }
         }

         return replied.Count == resolved.Count ? messages : throw StoredData.Invalid($"Thread {id} has a resolved request without a reply.");
      }

      /// <summary>A thread that starts with the reviewer's question. Only this kind holds a tour action.</summary>
      [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
      internal sealed record Asked : Sent
      {
         /// <param name="id">The thread's ID.</param>
         /// <param name="anchor">Where the reviewer asked.</param>
         /// <param name="state">The thread's state.</param>
         /// <param name="messages">The messages, the first a question.</param>
         /// <param name="action">The tour action that the thread's first question requests, or null for a question.</param>
         [JsonConstructor]
         private Asked(string id, ThreadAnchor anchor, QuestionThreadState state, IReadOnlyList<ThreadMessage> messages, TourAction? action = null)
            : base(id, anchor, state, messages)
         {
            if (!StartsAsDeclared)
            {
               throw StoredData.Invalid($"Asked thread {id} must start with a question.");
            }

            Action = action is null || !HasNullElement(action) ? action : throw StoredData.Invalid($"The tour action of thread {id} holds a null item.");
         }

         /// <summary>The tour action that the thread's first question requests, or null for a question. A follow-up in the thread is a question about the action. JSON leaves it out when null.</summary>
         [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
         public TourAction? Action { get; }

         /// <summary>Returns a new open thread whose first message is the question.</summary>
         internal static Asked Open(string id, ThreadAnchor anchor, ThreadMessage.Question question, TourAction? action) => new(id, anchor, QuestionThreadState.Open, [question], action);

         /// <summary>Returns the note sent as an open thread with the same ID and anchor, whose first message is the question.</summary>
         internal static Asked From(Note note, ThreadMessage.Question question) => new(note.Id, note.Anchor, QuestionThreadState.Open, [question]);

         private protected override Sent Rebuilt(ThreadAnchor anchor, QuestionThreadState state, IReadOnlyList<ThreadMessage> messages) => new Asked(Id, anchor, state, messages, Action);

         // The serializer refuses a missing or null list, but not a null element in one.
         private static bool HasNullElement(TourAction action) => action switch
         {
            TourAction.Explain or TourAction.Revise or TourAction.Replan or TourAction.Summarize => false,
            TourAction.Reorder reorder => HasNullItem(reorder.StopIds),
            TourAction.FollowUp.DraftWorkItems work => HasNullItem(work.Decisions),
            TourAction.FollowUp.WalkOpenQuestions walk => HasNullItem(walk.OpenQuestions),
            TourAction.FollowUp.DraftComments drafts => HasNullItem(drafts.Concerns) || HasNullItem(drafts.Risks),
         };

         private static bool HasNullItem<TItem>(IReadOnlyList<TItem> items) => items.Any(item => item is null);
      }

      /// <summary>A thread that reports a reviewer decision to the bound session, on the tour anchor, with the reviewer's optional words as its first question.</summary>
      [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
      internal sealed record Decided : Sent
      {
         /// <param name="id">The thread's ID.</param>
         /// <param name="anchor">The tour anchor.</param>
         /// <param name="state">The thread's state.</param>
         /// <param name="messages">The messages, the first a question.</param>
         /// <param name="decision">The decision that the thread's first question reports.</param>
         [JsonConstructor]
         private Decided(string id, ThreadAnchor anchor, QuestionThreadState state, IReadOnlyList<ThreadMessage> messages, ReviewerDecision decision)
            : base(id, anchor, state, messages)
         {
            if (Anchor is not ThreadAnchor.WholeTour || !StartsAsDeclared)
            {
               throw StoredData.Invalid($"Decided thread {id} must be on the tour anchor and start with a question.");
            }

            Decision = decision ?? throw StoredData.Invalid($"Decided thread {id} needs its decision.");
         }

         /// <summary>The decision that the thread's first question reports. A follow-up in the thread is a question about the decision.</summary>
         public ReviewerDecision Decision { get; }

         /// <summary>Returns a new open thread on the tour anchor whose first message is the question.</summary>
         internal static Decided Open(string id, ThreadMessage.Question question, ReviewerDecision decision) => new(id, new ThreadAnchor.WholeTour(), QuestionThreadState.Open, [question], decision);

         private protected override Sent Rebuilt(ThreadAnchor anchor, QuestionThreadState state, IReadOnlyList<ThreadMessage> messages) => new Decided(Id, anchor, state, messages, Decision);
      }

      /// <summary>A thread that starts with the bound session's agent message.</summary>
      [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
      internal sealed record Started : Sent
      {
         /// <param name="id">The thread's ID.</param>
         /// <param name="anchor">Where the session started the thread.</param>
         /// <param name="state">The thread's state.</param>
         /// <param name="messages">The messages, the first an agent message.</param>
         [JsonConstructor]
         private Started(string id, ThreadAnchor anchor, QuestionThreadState state, IReadOnlyList<ThreadMessage> messages)
            : base(id, anchor, state, messages)
         {
            if (!StartsAsDeclared)
            {
               throw StoredData.Invalid($"Started thread {id} must start with an agent message.");
            }
         }

         /// <summary>Returns a new open thread whose first message is the agent message.</summary>
         internal static Started Open(string id, ThreadAnchor anchor, ThreadMessage.AgentMessage message) => new(id, anchor, QuestionThreadState.Open, [message]);

         private protected override Sent Rebuilt(ThreadAnchor anchor, QuestionThreadState state, IReadOnlyList<ThreadMessage> messages) => new Started(Id, anchor, state, messages);
      }
   }
}

/// <summary>Whether the reviewer considers a thread settled, or the bound session marked it outdated.</summary>
/// <remarks>The table of <see cref="TourThreads"/> decides each change of the state. The set may grow, such as to the states of a platform's comment threads.</remarks>
[JsonConverter(typeof(StrictEnumConverter<QuestionThreadState>))]
internal enum QuestionThreadState
{
   /// <summary>A thread that the reviewer has not resolved, or reopened.</summary>
   Open,

   /// <summary>A thread that the reviewer considers settled.</summary>
   Resolved,

   /// <summary>A thread that the bound session marked as no longer current, such as a concern that a revised stop settles, or whose anchor a revision or an iteration update left behind.</summary>
   Outdated,
}

/// <summary>Where in a tour a thread was asked or started. The <c>kind</c> property in JSON names the derived type.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(CodeRange), "code")]
[JsonDerivedType(typeof(StopText), "stopText")]
[JsonDerivedType(typeof(WholeFile), "file")]
[JsonDerivedType(typeof(WholeStop), "stop")]
[JsonDerivedType(typeof(WholeTour), "tour")]
internal closed record ThreadAnchor
{
   private ThreadAnchor()
   {
   }

   /// <summary>Whole lines of one file at one side, with their text as the server read it when the question was asked.</summary>
   /// <param name="Side">The side the lines are on.</param>
   /// <param name="Path">The file's repository path at that side, as <see cref="CheckedPath"/> requires.</param>
   /// <param name="StartLine">The first line, 1-based.</param>
   /// <param name="EndLine">The last line, 1-based and inclusive, never before <see cref="StartLine"/>.</param>
   /// <param name="Commit">The commit that the side named when the question was asked.</param>
   /// <param name="Text">The text of the lines, as <c>FileLines.Text</c> returns it.</param>
   /// <exception cref="ArgumentException">The path is one that <see cref="RepositoryPath.IsValid"/> refuses.</exception>
   internal sealed record CodeRange(FileSide Side, string Path, int StartLine, int EndLine, CommitId Commit, string Text) : ThreadAnchor
   {
      /// <summary>The file's repository path at that side, which a <c>with</c> expression checks as the constructor does.</summary>
      public string Path { get; init => field = CheckedPath(value); } = CheckedPath(Path);
   }

   /// <summary>Text that the reviewer selected in one prose field of one version of a stop's content.</summary>
   /// <param name="StopId">The stop's ID.</param>
   /// <param name="Field">The prose field, as <see cref="ProseField.Path"/> renders it, such as <c>how</c> or <c>checks[&lt;check ID&gt;].question</c>.</param>
   /// <param name="Text">The selected text as the UI sent it. The server does not compare it with the field's Markdown, because the reviewer selects in the rendered text.</param>
   /// <param name="Version">
   /// The number of the stop's version that the text is in. A revision that leaves the field's Markdown as it was moves the anchor to the new version, and any other revision leaves the anchor here.
   /// </param>
   internal sealed record StopText(string StopId, string Field, string Text, int Version) : ThreadAnchor;

   /// <summary>One file at one side.</summary>
   /// <param name="Side">The side the file is on.</param>
   /// <param name="Path">The file's repository path at that side, as <see cref="CheckedPath"/> requires.</param>
   /// <param name="Commit">The commit that the side named when the question was asked.</param>
   /// <exception cref="ArgumentException">The path is one that <see cref="RepositoryPath.IsValid"/> refuses.</exception>
   internal sealed record WholeFile(FileSide Side, string Path, CommitId Commit) : ThreadAnchor
   {
      /// <summary>The file's repository path at that side, which a <c>with</c> expression checks as the constructor does.</summary>
      public string Path { get; init => field = CheckedPath(value); } = CheckedPath(Path);
   }

   /// <summary>One stop of the plan.</summary>
   /// <param name="StopId">The stop's ID.</param>
   internal sealed record WholeStop(string StopId) : ThreadAnchor;

   /// <summary>The tour as a whole.</summary>
   internal sealed record WholeTour : ThreadAnchor;

   /// <summary>Returns the path for a code or file anchor, once <see cref="RepositoryPath.IsValid"/> accepts it.</summary>
   /// <remarks>
   /// The primary constructor and the property's <c>init</c> accessor both run it, because a <c>with</c> expression sets the property without the constructor. So a writer that moves an anchor
   /// to a refused path throws.
   /// </remarks>
   /// <exception cref="ArgumentException">The path is one that <see cref="RepositoryPath.IsValid"/> refuses.</exception>
   private static string CheckedPath(string path) =>
      RepositoryPath.IsValid(path) ? path : throw new ArgumentException($"A code or file anchor's path must be {RepositoryPath.Expected}, and '{path}' is not.", nameof(path));
}

/// <summary>One message of a sent thread. The <c>kind</c> property in JSON names the derived type, and the caller that wrote the message decides the kind.</summary>
/// <remarks>Each kind has a private constructor that checks the kind's invariants, as for <see cref="QuestionThread"/>.</remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Question), "question")]
[JsonDerivedType(typeof(Question.Reply), "reply")]
[JsonDerivedType(typeof(AgentMessage), "agentMessage")]
internal closed record ThreadMessage
{
   private ThreadMessage(string id, DateTimeOffset createdAt)
   {
      Id = id ?? throw StoredData.Invalid("A message needs an ID.");
      CreatedAt = createdAt;
   }

   /// <summary>The message's ID, unique among the IDs of the tour's threads, messages and requests.</summary>
   public string Id { get; }

   /// <summary>When the server stored the message.</summary>
   public DateTimeOffset CreatedAt { get; }

   /// <summary>A question in the reviewer's own words, which the UI sent, or the question of a tour action or a reviewer decision, which may come without words.</summary>
   /// <remarks>
   /// It has at least one request. At most one request is resolved, and none is pending once one is. Only the last request may be pending. Its ID is the questionId of <c>fetch_question</c>.
   /// </remarks>
   [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
   internal sealed record Question : ThreadMessage
   {
      /// <param name="id">The question's ID.</param>
      /// <param name="createdAt">When the server stored the question.</param>
      /// <param name="text">The reviewer's words, as plain text, or empty for a tour action or a reviewer decision without words.</param>
      /// <param name="requests">Each time the question was sent to an agent session, in order.</param>
      /// <param name="stopId">The stop that the reviewer viewed while asking, or null for none.</param>
      [JsonConstructor]
      private Question(string id, DateTimeOffset createdAt, string text, IReadOnlyList<QuestionRequest> requests, string? stopId = null)
         : base(id, createdAt)
      {
         Text = text ?? throw StoredData.Invalid($"Question {id} needs its text.");
         if (requests is null || requests.Count == 0 || requests.Any(request => request is null))
         {
            throw StoredData.Invalid($"Question {id} needs at least one request, none of them null.");
         }

         var resolved = requests.Where(request => request.IsResolved).ToList();
         var pending = requests.Count(request => request.State == RequestState.Pending);
         if (resolved.Count > 1 || (resolved.Count == 1 && pending > 0) || pending > 1 || (pending == 1 && requests[^1].State != RequestState.Pending))
         {
            throw StoredData.Invalid($"Question {id} has more than one resolved request, a pending request beside a resolved one, or a pending request that is not its last.");
         }

         Requests = requests;
         StopId = stopId;
         Answered = resolved.Count == 1 ? resolved[0] : null;
      }

      /// <summary>The reviewer's words, as plain text, or empty for a tour action or a reviewer decision without words.</summary>
      public string Text { get; }

      /// <summary>Each time the question was sent to an agent session, in order.</summary>
      public IReadOnlyList<QuestionRequest> Requests { get; }

      /// <summary>The stop that the reviewer viewed while asking, or null for none. JSON leaves it out when null.</summary>
      [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
      public string? StopId { get; }

      /// <summary>The request that a reply resolved, or null while no reply answered the question.</summary>
      [JsonIgnore]
      public QuestionRequest? Answered { get; }

      /// <summary>Whether the question waits for a reply: no reply answered it and its last request is pending.</summary>
      [JsonIgnore]
      public bool IsWaiting => Answered is null && Requests[^1].State == RequestState.Pending;

      /// <summary>Returns whether a request of the question went to the session and that session has not fetched the question since.</summary>
      public bool IsUnreceivedBy(string sessionId) => Requests.Any(request => request.SessionId == sessionId && request.ReceivedAt is null);

      /// <summary>Returns a new question with one request.</summary>
      internal static Question Asked(string id, DateTimeOffset now, string text, string? stopId, QuestionRequest request) => new(id, now, text, [request], stopId);

      /// <summary>Returns the question with the requests.</summary>
      internal Question WithRequests(IReadOnlyList<QuestionRequest> requests) => new(Id, CreatedAt, Text, requests, StopId);

      /// <summary>An agent session's answer to a question, which resolved one request of the question.</summary>
      /// <remarks>It is nested in <see cref="Question"/>, because <see cref="For"/> builds one only for a question with a resolved request.</remarks>
      [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
      internal sealed record Reply : ThreadMessage
      {
         /// <param name="id">The reply's ID.</param>
         /// <param name="createdAt">When the server stored the reply.</param>
         /// <param name="sessionId">The agent session that wrote the reply.</param>
         /// <param name="requestId">The request that the reply resolved.</param>
         /// <param name="text">The answer as prose, or null for a decline without words.</param>
         [JsonConstructor]
         private Reply(string id, DateTimeOffset createdAt, string sessionId, string requestId, string? text = null)
            : base(id, createdAt)
         {
            SessionId = sessionId ?? throw StoredData.Invalid($"Reply {id} needs its session.");
            RequestId = requestId ?? throw StoredData.Invalid($"Reply {id} needs its request.");
            Text = text;
         }

         /// <summary>The agent session that wrote the reply, as the server derived it from the request's credentials.</summary>
         public string SessionId { get; }

         /// <summary>The request that the reply resolved, whose state holds the resolution.</summary>
         public string RequestId { get; }

         /// <summary>The answer as prose, or null for a decline without words. JSON leaves it out when null.</summary>
         [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
         public string? Text { get; }

         /// <summary>Returns the reply of the session that the answered question's resolved request went to.</summary>
         /// <exception cref="InvalidOperationException">No request of the question is resolved.</exception>
         internal static Reply For(Question answered, string id, DateTimeOffset now, string? text) =>
            answered.Answered is { } request ? new(id, now, request.SessionId, request.Id, text) : throw new InvalidOperationException($"Question {answered.Id} has no resolved request to reply to.");
      }
   }

   /// <summary>A message that the bound session wrote on its own, which answers no question and resolves no request.</summary>
   [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
   internal sealed record AgentMessage : ThreadMessage
   {
      /// <param name="id">The message's ID.</param>
      /// <param name="createdAt">When the server stored the message.</param>
      /// <param name="sessionId">The agent session that wrote the message.</param>
      /// <param name="text">The message as prose.</param>
      [JsonConstructor]
      private AgentMessage(string id, DateTimeOffset createdAt, string sessionId, string text)
         : base(id, createdAt)
      {
         SessionId = sessionId ?? throw StoredData.Invalid($"Agent message {id} needs its session.");
         Text = text ?? throw StoredData.Invalid($"Agent message {id} needs its text.");
      }

      /// <summary>The agent session that wrote the message, as the server derived it from the request's credentials.</summary>
      public string SessionId { get; }

      /// <summary>The message as prose.</summary>
      public string Text { get; }

      /// <summary>Returns a new agent message.</summary>
      internal static AgentMessage Written(string id, DateTimeOffset now, string sessionId, string text) => new(id, now, sessionId, text);
   }
}

/// <summary>One sending of a question to an agent session.</summary>
/// <remarks>A pending request becomes canceled or unanswered when it stops waiting, and a reply resolves one request that is not resolved. A resolved request keeps its state.</remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record QuestionRequest
{
   /// <param name="id">The request's ID.</param>
   /// <param name="state">How far the request has come.</param>
   /// <param name="sessionId">The agent session the request was sent to.</param>
   /// <param name="sentAt">When the server stored the request.</param>
   /// <param name="receivedAt">When the session first fetched the question, or null before it did.</param>
   [JsonConstructor]
   private QuestionRequest(string id, RequestState state, string sessionId, DateTimeOffset sentAt, DateTimeOffset? receivedAt = null)
   {
      Id = id ?? throw StoredData.Invalid("A request needs an ID.");
      State = state;
      SessionId = sessionId ?? throw StoredData.Invalid($"Request {id} needs its session.");
      SentAt = sentAt;
      ReceivedAt = receivedAt;
   }

   /// <summary>The request's ID, unique among the IDs of the tour's threads, messages and requests.</summary>
   public string Id { get; }

   /// <summary>How far the request has come.</summary>
   public RequestState State { get; }

   /// <summary>The agent session the request was sent to: the tour's bound session when the question was stored.</summary>
   public string SessionId { get; }

   /// <summary>When the server stored the request.</summary>
   public DateTimeOffset SentAt { get; }

   /// <summary>When the session that the request went to first fetched the question with <c>fetch_question</c>, or null before it did. JSON leaves it out when null.</summary>
   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
   public DateTimeOffset? ReceivedAt { get; }

   /// <summary>Whether a reply gave the request its state.</summary>
   [JsonIgnore]
   public bool IsResolved => IsResolution(State);

   /// <summary>Returns whether a reply gives a request this state.</summary>
   public static bool IsResolution(RequestState state) => state is RequestState.Done or RequestState.NeedsAnswer or RequestState.Declined;

   /// <summary>Returns a new pending request to the session.</summary>
   internal static QuestionRequest Pending(string id, string sessionId, DateTimeOffset now) => new(id, RequestState.Pending, sessionId, now);

   /// <summary>Returns the pending request canceled.</summary>
   /// <exception cref="InvalidOperationException">The request is not pending.</exception>
   internal QuestionRequest Canceled() => FromPending(RequestState.Canceled);

   /// <summary>Returns the pending request unanswered.</summary>
   /// <exception cref="InvalidOperationException">The request is not pending.</exception>
   internal QuestionRequest Unanswered() => FromPending(RequestState.Unanswered);

   /// <summary>Returns the request with the reply's resolution.</summary>
   /// <exception cref="InvalidOperationException">The request is resolved already, or the state is no resolution.</exception>
   internal QuestionRequest Resolved(RequestState resolution) =>
      !IsResolved && IsResolution(resolution) ? new(Id, resolution, SessionId, SentAt, ReceivedAt) : throw new InvalidOperationException($"Request {Id} cannot take the resolution {resolution}.");

   /// <summary>Returns the request received at <paramref name="now"/>.</summary>
   /// <exception cref="InvalidOperationException">The request is received already.</exception>
   internal QuestionRequest Received(DateTimeOffset now) => ReceivedAt is null ? new(Id, State, SessionId, SentAt, now) : throw new InvalidOperationException($"Request {Id} is received already.");

   private QuestionRequest FromPending(RequestState state) =>
      State == RequestState.Pending ? new(Id, state, SessionId, SentAt, ReceivedAt) : throw new InvalidOperationException($"Request {Id} is {State}, not pending.");
}

/// <summary>How far a request has come.</summary>
/// <remarks>The transitions of <see cref="QuestionRequest"/> keep the states' order, and the table of <see cref="TourThreads"/> decides which one applies.</remarks>
[JsonConverter(typeof(StrictEnumConverter<RequestState>))]
internal enum RequestState
{
   /// <summary>A request that waits for its session's reply.</summary>
   Pending,

   /// <summary>A request whose reply answered the question or did what the reviewer asked.</summary>
   Done,

   /// <summary>A request whose reply asks the reviewer for an answer before the session can continue.</summary>
   NeedsAnswer,

   /// <summary>A request that the session's reply declined.</summary>
   Declined,

   /// <summary>A request that was still pending when its session's binding ended or moved, or when the server started.</summary>
   Unanswered,

   /// <summary>A request that stopped waiting without a reply, because the reviewer canceled it or sent the question again, or because another request of the question got the reply.</summary>
   Canceled,
}

/// <summary>Builds the exception that the constructor of a thread, a message or a request throws for data that breaks its invariant.</summary>
internal static class StoredData
{
   /// <summary>Returns the exception that a constructor throws for data that breaks its type's invariant. The store reads it as a tour that cannot be read.</summary>
   public static JsonException Invalid(string reason) => new(reason);
}
