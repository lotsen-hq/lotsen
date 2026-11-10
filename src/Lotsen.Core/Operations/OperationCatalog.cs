using System.Collections.Frozen;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using Lotsen.Git;
using Lotsen.Json;
using Lotsen.Prose;
using Lotsen.Server;
using Lotsen.Sessions;
using Lotsen.Tours;

namespace Lotsen.Operations;

/// <summary>One operation as callers see it: its tool name, description, access declaration, prose view and the JSON types of its input and output.</summary>
/// <remarks>
/// <para>
/// The input type is the operation's whole input, the arguments an agent tool passes. Over HTTP, the input is the JSON body of the operation's request, as <see cref="OperationWire"/> builds it,
/// and the response body is the output, or an <see cref="OperationError"/>.
/// </para>
/// <para>
/// The schemas and the <see cref="Decoder"/> come from the same source-generated <see cref="JsonTypeInfo"/>, so the server checks an input against the schema that the agent was given.
/// </para>
/// </remarks>
internal abstract class Operation
{
   private protected Operation(string name, string description, JsonTypeInfo inputType, JsonTypeInfo outputType, ProseView prose, bool carriesControls)
   {
      CarriesControls = carriesControls;
      Name = name;
      Description = description;
      InputType = inputType;
      OutputType = outputType;
      Prose = prose;
      prose.CheckFits($"Operation {name}", outputType);
      Decoder = InputDecoder.For(inputType);
      NamesTour = inputType.Properties.Any(property => property.Name == TourIdProperty);
   }

   /// <summary>The name of the input property that names an operation's tour.</summary>
   public const string TourIdProperty = "tourId";

   /// <summary>The operation's name in snake_case, which is also its agent tool name and the last segment of its request's path.</summary>
   public string Name { get; }

   /// <summary>What the operation does, written for an agent choosing a tool.</summary>
   public string Description { get; }

   /// <summary>Who may call the operation, which the endpoint admits the caller against before it reads the input.</summary>
   public abstract Access Access { get; }

   /// <summary>Whether an agent adapter offers the operation as a tool: whether <see cref="Access"/> admits agent sessions.</summary>
   public bool IsAgentTool => Access.Admits.HasFlag(CallerKinds.Agent);

   /// <summary>Whether the input names a tour by its <see cref="TourIdProperty"/>.</summary>
   public bool NamesTour { get; }

   /// <summary>Whether a successful response to the UI carries the controls of the tour that the input names.</summary>
   /// <remarks>
   /// Every operation that names a tour carries them, except the file reads of <see cref="DefineFileRead"/>. A file read neither changes the tour nor shows its state, so its response skips
   /// the writer's lock and the second read of the tour that the controls take.
   /// </remarks>
   public bool CarriesControls { get; }

   public JsonTypeInfo InputType { get; }

   public JsonTypeInfo OutputType { get; }

   /// <summary>Reads the input from a request's body, checked against <see cref="InputSchema"/>.</summary>
   public InputDecoder Decoder { get; }

   /// <summary>What the output becomes for the UI, which asks for its prose as HTML.</summary>
   public ProseView Prose { get; }

   /// <summary>Returns a new JSON Schema of the operation's whole input.</summary>
   public JsonNode InputSchema() => OperationSchemas.Export(InputType);

   /// <summary>Returns a new JSON Schema of the operation's output.</summary>
   public JsonNode OutputSchema() => OperationSchemas.Export(OutputType);

   /// <summary>Returns an operation whose code receives the pass that <paramref name="access"/> issues to an admitted caller.</summary>
   /// <param name="name">The operation's name in snake_case.</param>
   /// <param name="description">What the operation does, for an agent.</param>
   /// <param name="inputType">The JSON type of the whole input.</param>
   /// <param name="outputType">The JSON type of the output.</param>
   /// <param name="access">Who may call the operation.</param>
   /// <param name="prose">What the output becomes for the UI: <see cref="ProseView.None"/>, or the HTML view of an output that can hold prose.</param>
   /// <param name="invoke">Runs the operation for an admitted caller's pass, and returns the output or the refusal.</param>
   /// <exception cref="InvalidOperationException">The prose view does not fit the output type.</exception>
   /// <exception cref="NotSupportedException">The input schema holds a form that <see cref="InputDecoder"/> cannot check.</exception>
   public static Operation<TInput, TOutput> Define<TInput, TOutput, TPass>(
      string name,
      string description,
      JsonTypeInfo<TInput> inputType,
      JsonTypeInfo<TOutput> outputType,
      Access.For<TPass> access,
      ProseView prose,
      Func<IServiceProvider, TPass, TInput, CancellationToken, Task<OperationResult<TOutput>>> invoke)
      where TInput : class
      where TOutput : class
      where TPass : Access.Pass => new Operation<TInput, TOutput, TPass>(name, description, inputType, outputType, access, prose, invoke, carriesControls: true);

   /// <summary>Returns an operation that reads a tour's files and nothing else of the tour, whose response to the UI carries no controls, as <see cref="CarriesControls"/> says.</summary>
   /// <inheritdoc cref="Define" path="/param"/>
   public static Operation<TInput, TOutput> DefineFileRead<TInput, TOutput, TPass>(
      string name,
      string description,
      JsonTypeInfo<TInput> inputType,
      JsonTypeInfo<TOutput> outputType,
      Access.For<TPass> access,
      ProseView prose,
      Func<IServiceProvider, TPass, TInput, CancellationToken, Task<OperationResult<TOutput>>> invoke)
      where TInput : class
      where TOutput : class
      where TPass : Access.Pass => new Operation<TInput, TOutput, TPass>(name, description, inputType, outputType, access, prose, invoke, carriesControls: false);

   /// <summary>Admits the caller against <see cref="Access"/>, and returns the call that runs the operation for it.</summary>
   /// <remarks>It is the only way to run the operation, so no caller reaches the operation's code without admission.</remarks>
   /// <param name="caller">Who made the request, as the request guards derived it from the credentials. No input property can name it.</param>
   /// <returns>The call, or the declaration's <see cref="OperationErrorCode.CallerNotAllowed"/> refusal.</returns>
   public abstract OperationResult<AdmittedCall> Admit(Caller caller);
}

/// <summary>An operation with typed input and output, as C# code that calls it by name sees it, such as <see cref="OperationCatalog.OpenUi"/>.</summary>
internal abstract class Operation<TInput, TOutput> : Operation
   where TInput : class
   where TOutput : class
{
   private protected Operation(string name, string description, JsonTypeInfo<TInput> inputType, JsonTypeInfo<TOutput> outputType, ProseView prose, bool carriesControls)
      : base(name, description, inputType, outputType, prose, carriesControls)
   {
      TypedInput = inputType;
      TypedOutput = outputType;
   }

   public JsonTypeInfo<TInput> TypedInput { get; }

   public JsonTypeInfo<TOutput> TypedOutput { get; }
}

/// <summary>One call of an operation by a caller that the operation's access declaration admitted. Only <see cref="Operation.Admit"/> returns one.</summary>
internal abstract class AdmittedCall
{
   private protected AdmittedCall()
   {
   }

   /// <summary>Runs the operation on an input of the operation's <see cref="Operation.InputType"/>.</summary>
   /// <param name="services">The server's services.</param>
   /// <param name="input">The input, of <see cref="Operation.InputType"/>.</param>
   /// <param name="cancellationToken">Cancels the operation.</param>
   /// <returns>The output, of <see cref="Operation.OutputType"/>, or the error that refused the input.</returns>
   public abstract Task<(object? Output, OperationError? Error)> InvokeAsync(IServiceProvider services, object input, CancellationToken cancellationToken);

   /// <summary>The revision of the tour that the output shows, as the call's pass took it from the call's last write, or null when the call wrote no tour.</summary>
   public abstract long? Revision { get; }
}

/// <summary>An operation with typed input and output, whose code receives a <typeparamref name="TPass"/>.</summary>
internal sealed class Operation<TInput, TOutput, TPass> : Operation<TInput, TOutput>
   where TInput : class
   where TOutput : class
   where TPass : Access.Pass
{
   private readonly Func<IServiceProvider, TPass, TInput, CancellationToken, Task<OperationResult<TOutput>>> _invoke;

   public Operation(
      string name,
      string description,
      JsonTypeInfo<TInput> inputType,
      JsonTypeInfo<TOutput> outputType,
      Access.For<TPass> access,
      ProseView prose,
      Func<IServiceProvider, TPass, TInput, CancellationToken, Task<OperationResult<TOutput>>> invoke,
      bool carriesControls)
      : base(name, description, inputType, outputType, prose, carriesControls)
   {
      Access = access;
      _invoke = invoke;
   }

   public override Access.For<TPass> Access { get; }

   public override OperationResult<AdmittedCall> Admit(Caller caller)
   {
      var admitted = Access.Admit(caller, this);
      return admitted.Value is { } pass ? new Call(this, pass) : admitted.Error!;
   }

   private sealed class Call : AdmittedCall
   {
      private readonly Operation<TInput, TOutput, TPass> _operation;
      private readonly TPass _pass;

      public Call(Operation<TInput, TOutput, TPass> operation, TPass pass)
      {
         _operation = operation;
         _pass = pass;
      }

      public override long? Revision => _pass.Revision;

      public override async Task<(object? Output, OperationError? Error)> InvokeAsync(IServiceProvider services, object input, CancellationToken cancellationToken)
      {
         var result = await _operation._invoke(services, _pass, (TInput)input, cancellationToken);
         return (result.Value, result.Error);
      }
   }
}

/// <summary>Every operation that the HTTP API offers, in one list. An agent adapter builds its tools from <see cref="AgentTools"/>.</summary>
/// <remarks>Each operation declares its <see cref="Operation.Access"/> here, with the wording of its refusal, and no operation's code checks the caller.</remarks>
internal static class OperationCatalog
{
   // The refusals' second sentences that more than one operation shares.
   private const string QuestionWords = "A question holds the reviewer's own words.";
   private const string NoteWords = "A note holds the reviewer's own words.";
   private const string AnswerWords = "An answer holds the reviewer's own words.";
   private const string ReviewMarkWhy = "A review mark is the reviewer's reading state.";

   // The declarations that more than one operation shares. They come before All, which the static initializers build in text order.
   private static readonly Access.BoundSession WritesPlanAndStops = new("write its plan and stops");
   private static readonly Access.BoundSession WritesThreads = new("write its threads");

   /// <summary>The operation that opens the UI, which <see cref="Client.ServerClient.OpenUiAsync"/> calls by name.</summary>
   public static Operation<OpenUiInput, OpenUiResult> OpenUi { get; } = Operation.Define(
      "open_ui",
      "Opens the lotsen UI in the user's browser. With a tourId, the page opens with that tour selected. A tourId that names no tour is refused, and no browser opens. When a browser launched, launched is true and the result has no url. When no browser could be launched, launched is false and url is a link that the user must open by hand. If the user reports that no browser window opened after a launch, ask them to run 'lotsen open --print-link' in a terminal and to open the link it prints. Do not run that command yourself.",
      LotsenJsonContext.Default.OpenUiInput,
      LotsenJsonContext.Default.OpenUiResult,
      new Access.Open(CallerKinds.Agent | CallerKinds.Cli, "open the UI"),
      ProseView.None,
      (services, _, input, cancellationToken) => services.GetRequiredService<UiLauncher>().OpenAsync(input, cancellationToken));

   /// <summary>The operation that creates a link to the UI and launches no browser, which <see cref="Client.ServerClient.CreateUiLinkAsync"/> calls by name.</summary>
   public static Operation<NoInput, UiLinkResult> CreateUiLink { get; } = Operation.Define(
      "create_ui_link",
      "Creates a fresh link to the lotsen UI and opens no browser. url is a link that the user opens by hand. It works once, for a short time.",
      LotsenJsonContext.Default.NoInput,
      LotsenJsonContext.Default.UiLinkResult,
      new Access.Open(CallerKinds.Cli, "create a link to the UI"),
      ProseView.None,
      (services, _, _, _) => Task.FromResult(OperationResult<UiLinkResult>.Of(services.GetRequiredService<UiLauncher>().CreateLink())));

   public static IReadOnlyList<Operation> All { get; } =
   [
      Operation.Define(
         "create_tour",
         "Creates a tour of a pull request and returns it with its new tourId, attached to this session. The tour starts without a plan, and with pin state pinning: the server pins the tour to the pull request's merge base and head in the background, which can take minutes for a large repository. read_state shows the pin when it is done. With planFirst, the tour's planApproval is awaited: once set_plan sets the plan, put_stop is refused until the reviewer approves the plan in the UI, which reports a planApproved decision to this session.",
         LotsenJsonContext.Default.CreateTourInput,
         LotsenJsonContext.Default.Tour,
         new Access.AnySession("create a tour"),
         ProseView.Html<Tour>(ProseHtml.Of),
         (services, pass, input, _) => Task.FromResult(From<TourPins>(services).CreateTour(pass, input))),
      Operation.Define(
         "attach_tour",
         "Attaches an existing tour to this session and returns the tour. A tour is attached to at most one session, and only that session can call set_plan and put_stop on it. create_tour attaches its new tour, so call attach_tour to work on a tour from an earlier session, or after the lotsen server restarted. A tour attached to another session is refused unless takeOver is true, which moves the tour to this session and tells the other session. The questions that the other session has not replied to become unanswered, and the reviewer can send them again. Attaching a tour that this session holds changes nothing.",
         LotsenJsonContext.Default.AttachTourInput,
         LotsenJsonContext.Default.Tour,
         new Access.AnySession("attach a tour"),
         ProseView.Html<Tour>(ProseHtml.Of),
         (services, pass, input, _) => Task.FromResult(From<SessionOperations>(services).AttachTour(pass, input))),
      Operation.Define(
         "set_plan",
         "Sets the tour's plan, replacing any earlier plan, and returns the tour. Only the session that the tour is attached to can set it. Give chapters and stops in tour order. The server assigns each new stop an ID that stays stable; to keep a stop from the current plan, pass its ID. A kept stop keeps its status, version and content, and a stop that the plan leaves out loses its content, its earlier versions and the reviewer's skip of it. A kept stop with content keeps its kind, because put_stop checked the content against that kind, so change its kind with put_stop. It also gets no anchorHint, because its passages say where it points. A plan-first tour waits for the reviewer's approval once set_plan sets its plan: stop, tell the user that the plan waits for the reviewer's approval in the browser, and wait for the planApproved decision. Meanwhile set_plan still revises the waiting plan, as a replan asks, and you still reply to the action that asked for the revision. fileMarks marks changed files as mechanical or generated, each with a one-line reason. The server does not check that the pull request changes a marked file, and the UI ignores a mark on a file it does not change.",
         LotsenJsonContext.Default.SetPlanInput,
         LotsenJsonContext.Default.Tour,
         WritesPlanAndStops,
         ProseView.Html<Tour>(ProseHtml.Of),
         (services, pass, input, _) => Task.FromResult(From<PlanOperations>(services).SetPlan(pass, input))),
      Operation.Define(
         "put_stop",
         "Writes the content of a stop that the plan announced, chosen by its stopId, and returns the stored stop, without its earlier versions, as read_state shows it. Only the session that the tour is attached to can write it. The first content is version 1, with status ready. Writing the stop again stores the next version, with status revised, and the server keeps the earlier version for the reviewer. A write clears the stop's staleOn. New stops come only from set_plan. The server checks every passage against the tour's pinned commits: the path is a text file on that side, the lines are in bounds, and expectedFirstLine matches the start line when given. Each Markdown field may link to code as [label](ref:<side>/<path>#L<start>-<end>), with the path percent-encoded, and the server checks every such link the same way, except that a link may point at unchanged code. It stores the text of every passage, and the stop's passages replace its anchorHint. The kind rules: an overview is the first stop and needs no passages, every other kind needs at least one passage, a risk stop needs a scenario, and a question stop needs at least one check. Each check is one decision for the reviewer: a question, an optional recommendation, and suggested answers, each plain text with its type. The server gives each new check an ID that is unique within the tour; a check written with the id of one of the stop's current checks keeps it, and any other id is refused. In a revision, keep a check's id when it asks the same decision, and its answers stay with it. A check left out or written without its id leaves its answers outdated on the earlier version, with outdatedOn, and they are never sent again. A thread on the stop's text moves to the new version when its field's Markdown is unchanged, and otherwise stays on the earlier version and becomes outdated when it is open. Empty optional fields are dropped. A title, kind or chapter in the input changes the plan, and the plan's rules are checked again. A failure names the field and stores nothing. A tour that is not pinned is refused with its pin. A tour whose planApproval is awaited, after set_plan set its plan, is refused until the reviewer approves the plan.",
         LotsenJsonContext.Default.PutStopInput,
         LotsenJsonContext.Default.Stop,
         WritesPlanAndStops,
         ProseView.Html<Stop>(ProseHtml.Of),
         (services, pass, input, cancellationToken) => From<PlanOperations>(services).PutStopAsync(pass, input, cancellationToken)),
      Operation.Define(
         "read_state",
         "Returns the tour's current state: its target, familiarity, pin and plan, with each stop's ID, stage, status and, once put_stop wrote it, content and version, from 1. A stop's stage is pending until put_stop writes it, with its anchorHint, and written after, with its content, version, earlier and staleOn. A stop with staleOn has content that the reviewer's update to iteration staleOn made stale: its passages and code references point at the iteration before, until put_stop writes the stop again against the tour's pin. A stop with status revised has earlier versions, which this read leaves out, so earlier is empty. The pin's state is pinning, pinned with the merge base and head commits, or failed with a reason for the user. The tour shows no attachment: a successful attach_tour or create_tour attaches it to this session, ack confirms this session, and a write that is refused says when the tour is attached to another session or to none. threads holds the reviewer's questions and the threads that a session started, each thread with its anchor, its state, its action when it requests a tour action, its decision when it reports what the reviewer already decided in the UI, and its messages in the order they were written: each question, the first or a follow-up, with the state of each of its requests, the replies, each with the requestId it resolved, and the messages that a session added on its own, without a requestId. A request has receivedAt once the session it went to fetched its question. A thread's state is open, resolved when the reviewer considers it settled, or outdated when a session marked it with outdate_thread, a revision changed the stop text it points into, or the reviewer's update to a new iteration could not move the code or file it points at. A stopText anchor names its field, a check's as checks[<id>].question or checks[<id>].recommendation, and the stop's version that the text is in. The reviewer's notes, threads not sent yet, are left out for this session until the reviewer sends them. answers holds the reviewer's answers to checks that the reviewer sent, each with its stopId, checkId, text, type, state and batch: sent, or received once the session the batch went to fetched it with fetch_answers while the tour was attached to it. An answer with outdatedOn answers a check that a revision dropped or gave a new id, and outdatedOn is the number of the stop's version that it stays on. The reviewer's draft answers are left out until the reviewer sends them. A stop with skipped true is one that the reviewer skipped: the reviewer's reading state, which reaches the session as no event. A skipped stop stays in the plan, and a re-plan may take the skips into account. progress, when present, holds lastStop, the stop that the reviewer opened last, absent when the summary page was last, and finished, true once the reviewer opened the summary page. archived is true while the reviewer keeps the tour archived. Only the UI writes progress and archived, and no event tells the session of them. pullRequestState, when present, is completed or abandoned: the pull request's state when lotsen last checked the pull request for a new iteration. It is absent while the pull request is active as far as lotsen knows, and no event tells the session of it. planApproval is awaited while the plan of a plan-first tour waits for the reviewer's approval, so put_stop is refused, and given once the reviewer approved it. It is absent for a tour whose plan does not wait. synthesis, when present, is the synthesis that write_synthesis stored last, with its text, its writtenAt, the pin its ref: links read at, and basedOn, the summaryFingerprint that write_synthesis took, an opaque value. availableIteration, when present, is a newer iteration of the pull request that the tour is not updated to yet, with its pin and the IDs of the stops it would make stale. The tour, its plan and its files stay on the tour's pin until the reviewer updates it.",
         LotsenJsonContext.Default.ReadStateInput,
         LotsenJsonContext.Default.Tour,
         new Access.Open(CallerKinds.Ui | CallerKinds.Agent, "read a tour"),
         ProseView.Html<Tour>(ProseHtml.Of),
         (services, pass, input, _) => Task.FromResult(From<TourPins>(services).ReadState(pass.Viewer, input))),
      Operation.Define(
         "read_summary",
         "Returns the tour's review summary: what the review has produced so far, built from the tour's state alone, with the tour's updatedAt. answers groups the reviewer's answers by their type, into decisions, concerns and openQuestions, each with its stopId and the stop's title, its checkId and the check's question, the reviewer's text, its type and its state: sent, or received once you fetched its batch. answers.outdated lists apart the answers with outdatedOn, the number of the stop's version that each stays on. An answer's question comes from the stop's current content, or for an outdated answer from the version that it stays on, and is absent when that content does not hold the check. stopTitle is absent when the plan no longer holds the stop. risks lists every risk stop of the plan as a concern, in plan order, with its scenario once put_stop wrote it. threads lists the threads by state, as open, resolved and outdated, each with its id, its anchor, the stopId and stopTitle of the stop it is about, if any, the kind of its tour action as action, if it requests one, and the kind of its reviewer decision as decision, if it reports one. skippedStops lists the stops that the reviewer skipped, and staleStops the stops with staleOn, both in plan order. summaryFingerprint identifies this summary as you read it, at the tour's pin: pass it to write_synthesis as basedOn. synthesis, when present, is the synthesis that write_synthesis stored last, with its text, its writtenAt, the pin its ref: links read at, and predatesSummary, true when the summary changed since the one that the synthesis is based on: an answer sent or changed, a thread started or in another state, a skip, a stale stop, a risk stop or its scenario, a stop title or check question that the summary names and that set_plan or put_stop changed, or another merge base or head. The reviewer's draft answers and notes are left out until the reviewer sends them. A tour without a plan, answers or threads has an empty summary.",
         LotsenJsonContext.Default.ReadSummaryInput,
         LotsenJsonContext.Default.ReviewSummary,
         new Access.Open(CallerKinds.Ui | CallerKinds.Agent, "read a review summary"),
         ProseView.Html<ReviewSummary>(ProseHtml.Of),
         (services, pass, input, _) => Task.FromResult(From<ReadingOperations>(services).ReadSummary(pass.Viewer, input))),
      Operation.Define(
         "write_synthesis",
         "Stores your synthesis of the review on the tour, in place of the last one, and returns when it was written with the tour's update time. Only the session that the tour is attached to can write it, and only on a pinned tour. The summary page shows it beside the review summary, which read_summary returns with it. Write what the review decided, what worries the reviewer and what stays open, and pass as basedOn the summaryFingerprint of the review summary that you read before you wrote it. The text is Markdown, as in reply, and the server checks its ref: links against the tour's pinned commits as reply does. A failure names the field and stores nothing.",
         LotsenJsonContext.Default.WriteSynthesisInput,
         LotsenJsonContext.Default.WrittenSynthesis,
         new Access.BoundSession("write its synthesis"),
         ProseView.None,
         (services, pass, input, cancellationToken) => From<ReadingOperations>(services).WriteSynthesisAsync(pass, input, cancellationToken)),
      Operation.Define(
         "ask_question",
         "Asks the tour's bound session a question in the reviewer's words and returns the new thread. The anchor says where the reviewer asked: whole lines at one side, which the server checks against the tour's pinned commits as it checks a stop's passage, except that unchanged code is allowed, and whose text it stores; text selected in a prose field of a stop; a file at one side; a stop; or the tour. The thread holds the question with a pending request for the session that the tour is attached to, which receives the words and IDs only, whether or not the session has confirmed with ack. A tour attached to no session is refused, and nothing is stored. A reply answers the question, and a request still pending when its session detaches becomes unanswered.",
         LotsenJsonContext.Default.AskQuestionInput,
         LotsenJsonContext.Default.QuestionThread,
         new Access.ReviewerToSession("ask a question", QuestionWords, "the question"),
         ProseView.Html<QuestionThread>(ProseHtml.Of),
         (services, pass, input, cancellationToken) => From<QuestionOperations>(services).AskQuestionAsync(pass, input, cancellationToken)),
      Operation.Define(
         "request_tour_action",
         "Requests a tour action of the tour's bound session in the reviewer's name and returns the new thread. An action asks Claude to act on the tour: explain to explain a stop or text in it more deeply, or revise to revise the stop, which Claude folds into the stop, replan to change the plan as the reviewer's words say, reorder to put the plan's stops in the reviewer's proposed order, which Claude carries out with set_plan, summarize to write a synthesis of the review, which Claude stores with write_synthesis, or a follow-up action that hands Claude a group of the review summary: draftWorkItems the sent decisions, for Claude to propose work items, walkOpenQuestions the sent open questions, for Claude to start a thread on each, or draftComments the sent concerns and the risk stops that Claude wrote and the reviewer did not skip, for Claude to draft comments for the author. The server collects a follow-up action's items from the summary that Claude reads when it stores the request, so they hold no draft answer. The thread holds one question with the action, the reviewer's optional words and a pending request for the session that the tour is attached to, which receives the words, the IDs and the action's kind. Explain and revise take a target: a stop that has content, or text selected in a prose field of a stop, and a version that is not the stop's current one is refused. Replan, reorder and summarize apply to the tour, which needs a plan, and so do the follow-up actions. A revise and a replan need words, and a reorder names every stop of the current plan once. A summarize or a follow-up action is refused while another of its kind waits, a replan or a reorder while a replan, a reorder or a decision of the reviewer waits, and a follow-up action is refused when its group holds nothing that the reviewer sent. The action holds only its kind and, for explain, revise and reorder, its target or order: a decision of the reviewer, a follow-up action's items or an anchor fail to read. A tour attached to no session is refused. Nothing is stored then. A reply resolves the action, as it answers a question, and the reviewer can follow up, send it again or cancel it as a question.",
         LotsenJsonContext.Default.RequestTourActionInput,
         LotsenJsonContext.Default.QuestionThread,
         new Access.ReviewerToSession("request a tour action", "A tour action holds the reviewer's own request.", "the tour action"),
         ProseView.Html<QuestionThread>(ProseHtml.Of),
         (services, pass, input, _) => Task.FromResult(From<QuestionOperations>(services).RequestTourAction(pass, input))),
      Operation.Define(
         "ask_follow_up",
         "Adds the reviewer's follow-up to an existing thread as a new question, sends it to the tour's bound session and returns the thread. The follow-up has its own questionId and a pending request for the session that the tour is attached to, which receives the words and IDs only, with the threadId, whether or not the session has confirmed with ack. The earlier questions of the thread keep their requests and answers, so a reply that needs an answer is answered with a follow-up. A tour attached to no session is refused, and so is a thread whose last question still waits for its answer, a note that is not sent yet, or the thread of a summarize, a follow-up action or a reviewer decision while the last question of another thread of its kind waits. A familiarity change and the plan approval count as one kind here. Nothing is stored then.",
         LotsenJsonContext.Default.AskFollowUpInput,
         LotsenJsonContext.Default.QuestionThread,
         new Access.ReviewerToSession("follow up in a thread", "A follow-up holds the reviewer's own words.", "the question"),
         ProseView.Html<QuestionThread>(ProseHtml.Of),
         (services, pass, input, _) => Task.FromResult(From<QuestionOperations>(services).AskFollowUp(pass, input))),
      Operation.Define(
         "resend_question",
         "Sends a question that has no answer yet again to the session that the tour is attached to now, as a new pending request for the same question, and returns the thread. The question's last request may be pending, canceled or unanswered, and a pending one becomes canceled. A session that an earlier request went to can still answer, and the first answer wins. A tour attached to no session is refused, and so is a question that has its answer or a follow-up in its thread, and a tour action that the tour moved past since the request: an explain or revise of a stop that Claude revised since or that the plan dropped, one of text in a stop whose field a revision changed, and a reorder of stops that the plan no longer has, or that leaves out one it has now. The refusal tells the reviewer what changed and what to do in the UI. A reviewer decision goes again only while the tour still holds what it reports: a familiarity change never, because the reviewer asks again for it with change_familiarity at the same level, a plan approval only while no stop of the plan has content and the tour's familiarity level, skipped stops and stops are the approval's, and an iteration update only while the tour stays on its iteration and one of its stops is still stale. A question in the thread of a summarize, a follow-up action or a reviewer decision, the opening question or a follow-up in its thread, goes again only while the last question of no other thread of its kind waits, where a familiarity change and the plan approval count as one kind. A replan or a reorder goes again only while no other replan, reorder or reviewer decision waits, as a new one does, and a follow-up in its thread goes again whatever waits. Nothing is stored then. A follow-up action goes again with the items that the server stored at its request.",
         LotsenJsonContext.Default.ResendQuestionInput,
         LotsenJsonContext.Default.QuestionThread,
         new Access.ReviewerToSession("send a question again", QuestionWords, "the question"),
         ProseView.Html<QuestionThread>(ProseHtml.Of),
         (services, pass, input, _) => Task.FromResult(From<QuestionOperations>(services).ResendQuestion(pass, input))),
      Operation.Define(
         "cancel_question",
         "Stops a question waiting for its answer, and returns the thread: the question's pending request becomes canceled. Nothing reaches the session, which can still answer the canceled request, and the first answer wins. A question whose last request is not pending is refused, and so is a question that has its answer. Nothing is stored then.",
         LotsenJsonContext.Default.CancelQuestionInput,
         LotsenJsonContext.Default.QuestionThread,
         new Access.Reviewer("cancel a question"),
         ProseView.Html<QuestionThread>(ProseHtml.Of),
         (services, pass, input, _) => Task.FromResult(From<QuestionOperations>(services).CancelQuestion(pass, input))),
      Operation.Define(
         "set_thread_state",
         "Resolves a thread or reopens it, and returns the thread with the tour's update time after the change. The tour needs no attached session, and the reviewer may resolve or reopen an outdated thread. Nothing reaches the session: the state is the reviewer's record of which threads are settled, and read_state and fetch_question show it. The thread's questions and requests stay as they are, so a waiting question still waits, and a reply leaves the state as it is. A question that the reviewer sends in a resolved thread reopens it. Setting the state a thread has changes nothing.",
         LotsenJsonContext.Default.SetThreadStateInput,
         LotsenJsonContext.Default.UpdatedThread,
         new Access.Reviewer("resolve or reopen a thread", "A thread's state is the reviewer's."),
         ProseView.Html<UpdatedThread>(ProseHtml.Of),
         (services, pass, input, _) => Task.FromResult(From<ThreadOperations>(services).SetThreadState(pass, input))),
      Operation.Define(
         "remove_thread",
         "Deletes a thread with every message in it, for good, and returns the thread's ID with the tour's update time after the delete. The tour needs no attached session. A question in the thread that still waits for its answer is canceled first, as cancel_question cancels it. When a session is attached to the tour, it receives one event that names the deleted thread, and the canceled question when one waited. A note sends nothing, because no session saw it. A thread that is gone is refused.",
         LotsenJsonContext.Default.RemoveThreadInput,
         LotsenJsonContext.Default.DeletedThread,
         new Access.Reviewer("delete a thread"),
         ProseView.None,
         (services, pass, input, _) => Task.FromResult(From<ThreadOperations>(services).RemoveThread(pass, input))),
      Operation.Define(
         "save_note",
         "Stores the reviewer's words as a new thread that is a note, sent to no session, and returns the thread with the tour's update time after the save. The tour needs no attached session. The thread has no messages and a note with the words, the time the server saved them and the stop that the reviewer viewed. The words and the anchor pass the checks of ask_question. The note stays a draft until send_notes sends it, and edit_note and discard_note change or remove it until then. Agent sessions do not see the note before it is sent.",
         LotsenJsonContext.Default.AskQuestionInput,
         LotsenJsonContext.Default.UpdatedThread,
         new Access.Reviewer("save a note", NoteWords),
         ProseView.Html<UpdatedThread>(ProseHtml.Of),
         (services, pass, input, cancellationToken) => From<NoteOperations>(services).SaveNoteAsync(pass, input, cancellationToken)),
      Operation.Define(
         "edit_note",
         "Replaces the words of a note that is not sent yet, and returns the thread with the tour's update time after the edit. The tour needs no attached session. A thread that is not a note, because it was sent or asked at once, is refused. Words that equal the note's words change nothing.",
         LotsenJsonContext.Default.EditNoteInput,
         LotsenJsonContext.Default.UpdatedThread,
         new Access.Reviewer("edit a note", NoteWords),
         ProseView.Html<UpdatedThread>(ProseHtml.Of),
         (services, pass, input, _) => Task.FromResult(From<NoteOperations>(services).EditNote(pass, input))),
      Operation.Define(
         "discard_note",
         "Removes a note that is not sent yet, with its thread, and returns the thread's ID with the tour's update time after the removal. The tour needs no attached session. A thread that is not a note, because it was sent or asked at once, is refused.",
         LotsenJsonContext.Default.DiscardNoteInput,
         LotsenJsonContext.Default.DeletedThread,
         new Access.Reviewer("discard a note", NoteWords),
         ProseView.None,
         (services, pass, input, _) => Task.FromResult(From<NoteOperations>(services).DiscardNote(pass, input))),
      Operation.Define(
         "send_notes",
         $"Sends one note or a batch of notes to the session that the tour is attached to, and returns their threads with the tour's update time after the send. Each note becomes its thread's first question, with its own questionId and a pending request for the session, which receives each question as its own event, as ask_question sends one. A sent thread is open. A batch holds at most {NoteOperations.MaxNoteBatch} notes, each once. The batch is sent whole or not at all: a tour attached to no session is refused, and so is a batch that names a thread that is gone or is not a note any more. Nothing is stored then.",
         LotsenJsonContext.Default.SendNotesInput,
         LotsenJsonContext.Default.SentNotes,
         new Access.ReviewerToSession("send a note", NoteWords, "the question"),
         ProseView.Html<SentNotes>(ProseHtml.Of),
         (services, pass, input, _) => Task.FromResult(From<NoteOperations>(services).SendNotes(pass, input))),
      Operation.Define(
         "answer_check",
         "Stores the reviewer's answer to a check of a stop as a draft, in the place of the check's earlier answer, and returns it with the tour's update time after the write. The tour needs no attached session. An answer that the reviewer picked from the check's suggestions takes that suggestion's type. The reviewer's own words keep the type of the check's earlier answer, or start as a decision when the check has no answer. A new text or type makes the answer a draft again, also a sent or received one, which the next send_answers sends, and the same text and type change nothing. The words pass the checks of a question's. A stop that the plan lacks is refused, and so is a check that the stop's current content lacks, or a suggestion that the check lacks. Nothing is stored then. Agent sessions do not see a draft answer.",
         LotsenJsonContext.Default.AnswerCheckInput,
         LotsenJsonContext.Default.UpdatedAnswer,
         new Access.Reviewer("answer a check", AnswerWords),
         ProseView.None,
         (services, pass, input, _) => Task.FromResult(From<AnswerOperations>(services).AnswerCheck(pass, input))),
      Operation.Define(
         "set_answer_type",
         "Sets the type of the reviewer's answer to a check, and returns the answer with the tour's update time after the write. The tour needs no attached session. A new type makes the answer a draft again, and the same type changes nothing. A check that the stop's current content lacks is refused, and so is a check without an answer.",
         LotsenJsonContext.Default.SetAnswerTypeInput,
         LotsenJsonContext.Default.UpdatedAnswer,
         new Access.Reviewer("set an answer's type", "Only the reviewer types an answer."),
         ProseView.None,
         (services, pass, input, _) => Task.FromResult(From<AnswerOperations>(services).SetAnswerType(pass, input))),
      Operation.Define(
         "clear_answer",
         "Removes the reviewer's answer to a check, whether it is a draft, sent or received, and returns the check's IDs with the tour's update time after the removal. The tour needs no attached session. A cleared answer leaves the batch it was sent in, and nothing reaches the session. A check that the stop's current content lacks is refused, and so is a check without an answer.",
         LotsenJsonContext.Default.ClearAnswerInput,
         LotsenJsonContext.Default.ClearedAnswer,
         new Access.Reviewer("clear an answer", AnswerWords),
         ProseView.None,
         (services, pass, input, _) => Task.FromResult(From<AnswerOperations>(services).ClearAnswer(pass, input))),
      Operation.Define(
         "send_answers",
         "Sends the reviewer's answers to checks as one batch to the session that the tour is attached to, and returns the batch's answers with the tour's update time after the send. The batch holds every draft answer to a check of a stop's current content, and every sent answer whose session lost the tour or ended before it received the answer. Each becomes sent, with the batch's ID and the session's ID. The session receives one event with the tourId and batchId only, whether or not it has confirmed with ack, and fetches the answers with fetch_answers. The batch is sent whole or not at all: a tour attached to no session is refused, and so is a tour with no answer to send. Nothing is stored then.",
         LotsenJsonContext.Default.SendAnswersInput,
         LotsenJsonContext.Default.UpdatedBatch,
         new Access.ReviewerToSession("send answers", AnswerWords, "the answers"),
         ProseView.None,
         (services, pass, input, _) => Task.FromResult(From<AnswerOperations>(services).SendAnswers(pass, input))),
      Operation.Define(
         "fetch_answers",
         "Returns a batch of the reviewer's answers to checks, by the batchId of its channel event: each answer with its stopId and checkId, the check's question, the reviewer's text and the answer's type. The type is decision, concern or openQuestion, and the reviewer set it. An answer whose check a revision dropped or gave a new id has outdatedOn, the number of the stop's version that it stays on, and the question from that version. The first fetch by this session, while the tour is attached to it, marks each answer of the batch as received, with receivedAt, which shows the reviewer that you have the answers; a later fetch changes nothing. After another session took the tour over, a fetch only reads, and the answers go to that session in a later batch. An answer that the reviewer changed or cleared after the send is not in the batch any more, and a changed answer comes in a later batch. A batch holds only the answers sent since the last one, so read_state shows every answer sent so far. Raise a concern that needs the reviewer's attention as a thread with start_thread, anchored as stopText on the check's question, with the answer's stopId, field checks[<checkId>].question and the question as text. Call it before you act on a batch.",
         LotsenJsonContext.Default.FetchAnswersInput,
         LotsenJsonContext.Default.FetchedAnswers,
         new Access.AnySession("fetch a batch of answers"),
         ProseView.None,
         (services, pass, input, _) => Task.FromResult(From<AnswerOperations>(services).FetchAnswers(pass, input))),
      Operation.Define(
         "fetch_question",
         "Returns a question that the reviewer asked, by the questionId of its channel event: its thread, with the anchor, the thread's state (open, resolved when the reviewer considers it settled, or outdated) and every message so far, which holds the earlier questions and replies when the question is a follow-up, and the stop it is about with its current content. A question with an action requests a tour action, whose kind the action names: explain, revise, replan, reorder with the proposed order of the plan's stops in stopIds, summarize, or a follow-up action with the items that the server collected from the review summary when the reviewer requested it: draftWorkItems with the sent decisions in decisions, walkOpenQuestions with the sent open questions in openQuestions, or draftComments with the sent concerns in concerns and, in risks, the risk stops that you wrote and the reviewer did not skip. The thread of a question with a decision reports what the reviewer already decided in the UI, whose kind the decision names: familiarityChanged with the tour's earlier and new familiarity level in from and to and the stops to rewrite at the new level in stopIds, planApproved with the reviewer's proposed order of the plan's stops in stopIds, the skipped stops in skippedStops and the level to write at in familiarity, or iterationUpdated with the iteration in iteration and the stale stops in stopIds. A question in the thread of a tour action or a decision has howToAct, which says what its kind asks of you and how to carry it out: follow it, in a follow-up only when the follow-up asks you to carry out or redo that work. An answer item names its stopId and stopTitle, its checkId and question, the reviewer's text and its type, and a risk its stopId, title and scenario. Its text holds the reviewer's optional words, empty without them. The anchor says where the reviewer asked: code (whole lines at one side, with their text as stored when the question was asked), stopText (text the reviewer selected in a prose field of the stop's version that the anchor's version names, which may be older than the stop's current version), file, stop, or tour. The stop is the anchor's stop for a stop or stopText anchor, and otherwise the stop the reviewer viewed; it is absent when there is none. The first fetch by this session marks each request of the question that went to this session as received, with receivedAt, which shows the reviewer that you have the question; a later fetch changes nothing. Call it before you answer a question with reply.",
         LotsenJsonContext.Default.FetchQuestionInput,
         LotsenJsonContext.Default.FetchedQuestion,
         new Access.AnySession("fetch a question"),
         ProseView.Html<FetchedQuestion>(ProseHtml.Of),
         (services, pass, input, _) => Task.FromResult(From<QuestionOperations>(services).FetchQuestion(pass, input))),
      Operation.Define(
         "reply",
         "Replies to a question that the reviewer asked, by the questionId of its channel event, and resolves the question's request that reached this session: done, needsAnswer or declined. Returns the thread with the reply. Only a session that the question reached can reply, also after the tour moved to another session. A question is answered once: the first reply wins, and a later reply is refused. The answer is Markdown, as in put_stop, and may link to code as [label](ref:<side>/<path>#L<start>-<end>), with the path percent-encoded. The server checks every such link against the tour's pinned commits as put_stop does, so a reply with links needs a pinned tour. A failure names the field and stores nothing.",
         LotsenJsonContext.Default.ReplyInput,
         LotsenJsonContext.Default.QuestionThread,
         // The table of TourThreads refuses a session that an unanswered question did not reach, with QuestionNotSentToSession, because the question's requests decide it.
         new Access.AnySession("reply to a question"),
         ProseView.Html<QuestionThread>(ProseHtml.Of),
         (services, pass, input, cancellationToken) => From<ReplyOperations>(services).ReplyAsync(pass, input, cancellationToken)),
      Operation.Define(
         "start_thread",
         "Starts a thread with a message of yours, and returns the thread with the tour's update time. Only the session that the tour is attached to can start one. Use it to raise something with the reviewer that no question asked about, such as a concern drawn from an answer or an item noticed while planning. The anchor says where the thread belongs: whole lines at one side, which the server checks against the tour's pinned commits as it checks a stop's passage, except that unchanged code is allowed, and whose text it stores; text in a prose field of a stop; a file at one side; a stop; or the tour. The text is Markdown, as in reply, and the server checks its ref: links as reply does. The thread is open and holds no question, so nothing waits for you. The reviewer can follow up in it, and a follow-up reaches you as a question. A failure names the field and stores nothing.",
         LotsenJsonContext.Default.StartThreadInput,
         LotsenJsonContext.Default.UpdatedThread,
         WritesThreads,
         ProseView.Html<UpdatedThread>(ProseHtml.Of),
         (services, pass, input, cancellationToken) => From<ThreadOperations>(services).StartThreadAsync(pass, input, cancellationToken)),
      Operation.Define(
         "add_thread_message",
         "Adds a message of yours to the end of a thread, and returns the thread with the tour's update time. Only the session that the tour is attached to can add one. Use it to update a thread, such as to say that a revised stop now covers its point. The earlier messages stay as they are. The message answers no question and resolves no request, so answer a question with reply. A thread whose last question still waits for your reply is refused, and so is a thread that read_state does not show. The text is Markdown, as in reply, and the server checks its ref: links as reply does. The message opens an outdated thread and leaves an open or resolved thread as it is, but a thread on stop text that a revision changed stays outdated. A failure names the field and stores nothing.",
         LotsenJsonContext.Default.AddThreadMessageInput,
         LotsenJsonContext.Default.UpdatedThread,
         WritesThreads,
         ProseView.Html<UpdatedThread>(ProseHtml.Of),
         (services, pass, input, cancellationToken) => From<ThreadOperations>(services).AddThreadMessageAsync(pass, input, cancellationToken)),
      Operation.Define(
         "outdate_thread",
         "Marks an open thread outdated, and returns the thread with the tour's update time. Only the session that the tour is attached to can mark it. Use it for a thread that no longer applies but that the reviewer may still want to read, such as a concern that a revised stop settles. The UI hides outdated threads until the reviewer shows them. A thread whose last question still waits for your reply is refused, and so is a thread that read_state does not show. The reviewer can open or resolve the thread again, and a follow-up or a message of yours opens it, unless it is on stop text that a revision changed. A resolved thread stays resolved, because the reviewer settled it, and an outdated one stays as it is: the call then changes nothing.",
         LotsenJsonContext.Default.OutdateThreadInput,
         LotsenJsonContext.Default.UpdatedThread,
         WritesThreads,
         ProseView.Html<UpdatedThread>(ProseHtml.Of),
         (services, pass, input, _) => Task.FromResult(From<ThreadOperations>(services).OutdateThread(pass, input))),
      Operation.Define(
         "delete_thread",
         "Deletes a thread with every message in it, for good, and returns the thread's ID with the tour's update time. Only the session that the tour is attached to can delete it. Use it for a thread that the reviewer has no reason to read any more, such as one whose whole point the tour now holds. Call outdate_thread instead when the reviewer may still want to read it. A thread whose last question still waits for your reply is refused, and so is a thread that read_state does not show.",
         LotsenJsonContext.Default.DeleteThreadInput,
         LotsenJsonContext.Default.DeletedThread,
         WritesThreads,
         ProseView.None,
         (services, pass, input, _) => Task.FromResult(From<ThreadOperations>(services).DeleteThread(pass, input))),
      Operation.Define(
         "ack",
         "Confirms to lotsen that its channel events reach this session, which lets the reviewer see that questions asked in the UI reach Claude. Call it with the nonce attribute of a channel event that carries one, before you handle that event. A nonce that this session did not receive is refused.",
         LotsenJsonContext.Default.AckInput,
         LotsenJsonContext.Default.AckResult,
         new Access.AnySession("confirm its channel with ack"),
         ProseView.None,
         (services, pass, input, _) => Task.FromResult(From<SessionOperations>(services).Ack(pass, input))),
      Operation.Define(
         "pin_tour",
         "Pins a tour again and returns it with pin state pinning: a tour whose pin failed, such as after the user signed in again, or a tour whose pin stopped without a result. The pin runs in the background; read_state shows when it is done. A tour whose pin is still running is returned unchanged, so calling it for a pinning tour is safe. A pinned tour is refused.",
         LotsenJsonContext.Default.PinTourInput,
         LotsenJsonContext.Default.Tour,
         new Access.AnySession("pin a tour"),
         ProseView.Html<Tour>(ProseHtml.Of),
         (services, pass, input, _) => Task.FromResult(From<TourPins>(services).PinTour(pass, input))),
      Operation.Define(
         "find_tours",
         "Finds saved tours and returns them newest first, each with its id, target, label, familiarity, createdAt, stopCount, pin, state, pullRequestState and attachment, and without its plan. With prUrl, it returns only the tours of that pull request, whatever URL form names it. Without prUrl, it returns every tour. state is generating, ready, inProgress, finished or archived, and only the reviewer unarchives a tour, in the UI. pullRequestState is active, completed or abandoned, as lotsen last checked it. attachment is thisSession for a tour attached to this session, otherSession for a tour attached to another session, and none for a tour attached to no session. Call attach_tour to work on a tour that it finds.",
         LotsenJsonContext.Default.FindToursInput,
         LotsenJsonContext.Default.FoundTours,
         new Access.AnySession("find tours"),
         ProseView.None,
         (services, pass, input, _) => Task.FromResult(From<TourPins>(services).FindTours(pass, input))),
      Operation.Define(
         "list_tours",
         "Lists every tour, oldest first, with its pin, its state, which is generating, ready, inProgress, finished or archived, and its pullRequestState, which is active, completed or abandoned as lotsen last checked it, and without its plan.",
         LotsenJsonContext.Default.NoInput,
         LotsenJsonContext.Default.TourList,
         new Access.Open(CallerKinds.Ui, "list the tours"),
         ProseView.None,
         (services, _, _, _) => Task.FromResult(From<TourPins>(services).ListTours())),
      Operation.DefineFileRead(
         "list_changed_files",
         $"Lists the files that differ between a pinned tour's merge base and head, each with its path and whether it was added, modified, deleted or renamed. A renamed file is one that git's rename detection finds moved, with or without edits. Its path is its path at the head, and its oldPath is its path at the merge base. A file that git treats as binary on either side has binary true. A file larger than {FileOperations.LargeFileSize} bytes on either side has large, with the size of its larger side. A modified or renamed text file whose only change is its line endings, such as LF to CRLF, has lineEndingsOnly true. Each file but a deleted one has headObjectId, the object ID of its entry at the head. reviewedPaths lists the files whose review mark counts: a mark set with the file's current headObjectId, or with none on a deleted file. A tour that is not pinned is refused with its pin.",
         LotsenJsonContext.Default.ListChangedFilesInput,
         LotsenJsonContext.Default.ChangedFileList,
         new Access.Open(CallerKinds.Ui, "list a tour's changed files"),
         ProseView.None,
         (services, _, input, cancellationToken) => FileOperationsFrom(services).ListChangedFilesAsync(input, cancellationToken)),
      Operation.DefineFileRead(
         "read_file_text",
         $"Returns a file's text at a pinned tour's base or head side, with the commit that the side names, as content text. A file that git treats as binary comes as content binary, without its content. A file larger than {FileOperations.LargeFileSize} bytes comes as content large, with its size, unless large is include. Include returns files up to {FileOperations.MaxFileSize} bytes and refuses larger ones. A path that names no file on that side is not found. A tour that is not pinned is refused with its pin.",
         LotsenJsonContext.Default.ReadFileTextInput,
         LotsenJsonContext.Default.FileText,
         new Access.Open(CallerKinds.Ui, "read a file of a tour"),
         ProseView.None,
         (services, _, input, cancellationToken) => FileOperationsFrom(services).ReadFileTextAsync(input, cancellationToken)),
      Operation.DefineFileRead(
         "diff_file",
         $"Returns a file's text at a pinned tour's base and head sides, null on a side without the file, and the changed regions as git finds them, in UTF-16 offsets, as content text. For a renamed file, give its oldPath too: the diff then compares the old path at the base with the path at the head, so it shows only the edits. With whitespace ignore, a line that differs only in whitespace or in its line ending counts as unchanged. A file that git treats as binary on either side comes as content binary, without its content. A file larger than {FileOperations.LargeFileSize} bytes on either side comes as content large, with the size of its larger side, unless large is include. Include returns files up to {FileOperations.MaxFileSize} bytes and refuses larger ones. A path that names a file on neither side is not found. With oldPath, the diff is not found unless oldPath is a file at the base and path is a file at the head. A tour that is not pinned is refused with its pin.",
         LotsenJsonContext.Default.DiffFileInput,
         LotsenJsonContext.Default.FileDiff,
         new Access.Open(CallerKinds.Ui, "diff a file of a tour"),
         ProseView.None,
         (services, _, input, cancellationToken) => FileOperationsFrom(services).DiffFileAsync(input, cancellationToken)),
      Operation.DefineFileRead(
         "list_directory",
         "Lists the entries of one directory at a pinned tour's base or head side, with the commit that the side names. Each entry has its repository path and its type: file, directory, symlink or submodule. Omit path for the repository's root directory. The listing holds the directory's own entries, not those of its subdirectories, and gives no sizes, so read_file_text tells whether a file is binary or large. A path that names no directory on that side is not found. A tour that is not pinned is refused with its pin.",
         LotsenJsonContext.Default.ListDirectoryInput,
         LotsenJsonContext.Default.DirectoryListing,
         new Access.Open(CallerKinds.Ui, "list a directory of a tour"),
         ProseView.None,
         (services, _, input, cancellationToken) => FileOperationsFrom(services).ListDirectoryAsync(input, cancellationToken)),
      Operation.Define(
         "mark_file_reviewed",
         "Marks a changed file as reviewed by the reviewer, with the version of the file the reviewer saw, and returns every review mark of the tour. A mark on the same path is replaced. The mark counts in list_changed_files only while the file at the pinned head has that version, so a new iteration that changes the file clears it. Setting the same mark again changes nothing.",
         LotsenJsonContext.Default.MarkFileReviewedInput,
         LotsenJsonContext.Default.ReviewMarkList,
         new Access.Reviewer("mark a file reviewed", ReviewMarkWhy),
         ProseView.None,
         (services, pass, input, _) => Task.FromResult(FileOperationsFrom(services).MarkFileReviewed(pass, input))),
      Operation.Define(
         "clear_file_reviewed",
         "Clears the reviewer's review mark on a file and returns every review mark of the tour. Clearing a file without a mark changes nothing.",
         LotsenJsonContext.Default.ClearFileReviewedInput,
         LotsenJsonContext.Default.ReviewMarkList,
         new Access.Reviewer("clear a file's review mark", ReviewMarkWhy),
         ProseView.None,
         (services, pass, input, _) => Task.FromResult(FileOperationsFrom(services).ClearFileReviewed(pass, input))),
      Operation.Define(
         "set_stop_skipped",
         "Skips a stop of the plan or takes its skip back, and returns the IDs of every skipped stop of the tour, in plan order, with the tour's update time after the write. The tour needs no attached session. A skip is the reviewer's reading state: the UI passes over a skipped stop, and the reviewer can still open it. Nothing reaches the session, but a skip is not private: read_state shows the skipped stops. A pending stop can be skipped too, and a stop that set_plan leaves out loses its skip. A stop that the plan lacks is refused. Setting the state a stop has changes nothing.",
         LotsenJsonContext.Default.SetStopSkippedInput,
         LotsenJsonContext.Default.UpdatedSkips,
         new Access.Reviewer("skip a stop", "A skip is the reviewer's reading state."),
         ProseView.None,
         (services, pass, input, _) => Task.FromResult(From<AnswerOperations>(services).SetStopSkipped(pass, input))),
      Operation.Define(
         "record_reading_progress",
         "Records the stop or the summary page that the reviewer opened last, and returns the tour's reading progress, as lastStop and finished, with the tour's state and update time after the write. Give stopId for a stop of the plan, or summary true for the summary page. Give version with a stop whose current version the reviewer sees: the newest version given is stored as the stop's seenVersion, an earlier one or one that the stop lacks changes nothing, and a change publishes seenVersionUpdated. The tour needs no attached session, and nothing reaches the session. The state is generating while the tour has no plan or a pending stop, ready once every stop has content, inProgress once the reviewer opened a stop, finished once the reviewer opened the summary page, which stays finished, and archived while the tour is archived. A stop that the plan lacks is refused. Recording the place that the progress holds already, with no newer version, changes nothing.",
         LotsenJsonContext.Default.RecordReadingProgressInput,
         LotsenJsonContext.Default.RecordedProgress,
         new Access.Reviewer("record reading progress", "Reading progress is the reviewer's reading state."),
         ProseView.None,
         (services, pass, input, _) => Task.FromResult(From<ReadingOperations>(services).RecordReadingProgress(pass, input))),
      Operation.Define(
         "set_tour_archived",
         "Archives a tour or unarchives it, and returns the tour's state with its update time after the write. The tour needs no attached session, and nothing reaches the session. An archived tour has the state archived, and the UI's tour list hides it by default. An unarchived tour has the state that its plan and reading progress give. Setting the state the tour has changes nothing.",
         LotsenJsonContext.Default.SetTourArchivedInput,
         LotsenJsonContext.Default.UpdatedTourState,
         new Access.Reviewer("archive a tour", "The archive is the reviewer's own list of tours set aside."),
         ProseView.None,
         (services, pass, input, _) => Task.FromResult(From<ReadingOperations>(services).SetTourArchived(pass, input))),
      Operation.Define(
         "delete_tour",
         "Deletes a tour for good, with its plan, stops, threads, answers and review marks, and returns its tourId. The tour needs no attached session. The session that the tour was attached to gets no event, and its later calls on the tour are refused as not found. Once the tour is deleted, each repository clone that its pin or available iteration names is removed when no other tour refers to it and no pin, fetch, file read or the clones' maintenance uses it. While another stored tour cannot be read, no clone is removed. A tour deleted while it was still pinning refers to no clone yet, so prune_clones removes its clone.",
         LotsenJsonContext.Default.DeleteTourInput,
         LotsenJsonContext.Default.DeletedTour,
         new Access.Reviewer("delete a tour", "Deleting a tour is the reviewer's decision."),
         ProseView.None,
         (services, pass, input, _) => Task.FromResult(From<TourDeletion>(services).DeleteTour(pass, input))),
      Operation.Define(
         "prune_clones",
         "Removes every repository clone that no tour refers to, and every temporary clone that an interrupted clone left, and returns how many it removed. A clone that a pin, fetch, file read or the clones' maintenance uses stays. A tour that another lotsen version stored in another format refers to no clone. While any other stored tour cannot be read, the prune removes nothing and is refused.",
         LotsenJsonContext.Default.NoInput,
         LotsenJsonContext.Default.PrunedClones,
         new Access.Reviewer("prune the clones"),
         ProseView.None,
         (services, _, _, cancellationToken) => From<TourDeletion>(services).PruneClonesAsync(cancellationToken)),
      Operation.Define(
         "change_familiarity",
         "Changes the tour's familiarity level in the reviewer's name, reports the change to the session that the tour is attached to as a familiarityChanged decision in a new thread, and returns the new level with the thread. The level only guides Claude, so a tour attached to no session is refused, and nothing changes then. The decision names the earlier level in from, the new one in to, and in stopIds the stops for Claude to rewrite: the stops after the one the reviewer views, or every stop when none is open, that have content and are not skipped. The thread's question holds the reviewer's optional words. A change while an earlier change or the plan approval still waits for Claude is refused, and so is a change on a plan-first tour whose plan is not approved yet, because approve_plan carries its level. The tour's current level asks Claude again for the latest change's rewrite when the last question in that change's thread, the change or a follow-up about it, was declined, canceled or unanswered: the new decision keeps that change's from, picks its stopIds from the stop the reviewer views now, and leaves the level as it is. The current level is refused otherwise. A reply resolves the thread's question, and the reviewer can follow up or cancel it as a question. A familiarity change cannot be sent again with resend_question, because the reviewer asks again with this operation.",
         LotsenJsonContext.Default.ChangeFamiliarityInput,
         LotsenJsonContext.Default.ChangedFamiliarity,
         new Access.ReviewerToSession("change the familiarity level", "The level is the reviewer's own.", "the familiarity change"),
         ProseView.Html<ChangedFamiliarity>(ProseHtml.Of),
         (services, pass, input, _) => Task.FromResult(From<ReviewerDecisions>(services).ChangeFamiliarity(pass, input))),
      Operation.Define(
         "approve_plan",
         "Approves the plan of a plan-first tour in the reviewer's name, reports the approval to the session that the tour is attached to as a planApproved decision in a new thread, and returns the tour's approval, level and skipped stops with the thread. One write stores the reviewer's skipped stops and familiarity level, ends the plan's wait, so put_stop accepts stops, and stores the thread, whose decision carries the reviewer's proposed order of the plan's stops in stopIds, the skipped stops and the level, and whose question holds the reviewer's optional words. The approval needs a tour attached to a session, a plan that waits for approval, an order that names every stop of the current plan once, and skips of stops that the plan has. Nothing is stored otherwise. A reply resolves the thread's question, and the reviewer can follow up, cancel it, or send it again while no stop has content. The plan stays approved whatever happens to the thread.",
         LotsenJsonContext.Default.ApprovePlanInput,
         LotsenJsonContext.Default.ApprovedPlan,
         new Access.ReviewerToSession("approve a plan", "The approval is the reviewer's own.", "the plan approval"),
         ProseView.Html<ApprovedPlan>(ProseHtml.Of),
         (services, pass, input, _) => Task.FromResult(From<ReviewerDecisions>(services).ApprovePlan(pass, input))),
      Operation.Define(
         "check_iteration",
         "Checks the pull request of a pinned tour for a new iteration now, a push to its source branch, and returns the tour's available iteration with its pin and the IDs of the stops that an update to it would mark stale, or null when the pull request's latest head is the tour's head. It asks the pull request's platform even when a check ran moments ago, and runs after a check of the tour that is still running. A move of the target branch alone is no new iteration. The tour stays on its pin. When the check cannot finish, failure says why, and the available iteration is the one an earlier check found. A tour that is not pinned is refused.",
         LotsenJsonContext.Default.CheckIterationInput,
         LotsenJsonContext.Default.CheckedIteration,
         new Access.Reviewer("check a tour for a new iteration", "The server checks on its own while the reviewer has the tour open."),
         ProseView.None,
         (services, _, input, cancellationToken) => From<IterationChecks>(services).CheckIterationAsync(input, cancellationToken)),
      Operation.Define(
         "update_iteration",
         "Updates a pinned tour to its available iteration in the reviewer's name, reports the update to the session that the tour is attached to as an iterationUpdated decision in a new thread when a stop goes stale, and returns the tour's new pin, the IDs of its stale stops and the thread. The update moves the tour onto the available iteration whose pin it names, the one that the notice shows, without asking the pull request's platform, and maps the tour as it is at the update. When the pull request has moved on since, the next check shows the newer iteration. An update is refused when the tour's available iteration is another one than its pin names, whether a check found that one before the update or during it. One write pins the tour to the iteration's merge base and head, moves the passages and code references of each stop whose every passage and code reference the iteration leaves as they were, moves each code or file anchor whose lines or file the iteration leaves as they were, marks each other stop with content as stale with staleOn, the iteration's number, and outdates each open thread whose anchor cannot move and whose last question does not wait. A stale stop keeps all its content, every passage and code reference included, on the pin before the update until Claude writes it again with put_stop, and a stop that an earlier update left stale stays as it is. The decision names the iteration and every stale stop, and an update that leaves no stop stale sends none. The plan's approval stays as it is. A tour attached to no session is refused, and so is a tour without an available iteration and an update while the thread of an earlier update still waits. An update during which the tour keeps changing is refused too, and can be sent again, and so is one whose iteration lotsen cannot read from the repository.",
         LotsenJsonContext.Default.UpdateIterationInput,
         LotsenJsonContext.Default.UpdatedIteration,
         new Access.ReviewerToSession("update a tour to a new iteration", "The update is the reviewer's own.", "the move to the new update"),
         ProseView.Html<UpdatedIteration>(ProseHtml.Of),
         (services, pass, input, cancellationToken) => From<ReviewerDecisions>(services).UpdateIterationAsync(pass, input, cancellationToken)),
      OpenUi,
      CreateUiLink,
   ];

   /// <summary>The operations that an agent adapter offers as tools, in catalog order.</summary>
   public static IReadOnlyList<Operation> AgentTools { get; } = [.. All.Where(operation => operation.IsAgentTool)];

   /// <summary>Every operation by its name, which matches in exact letter case.</summary>
   public static FrozenDictionary<string, Operation> ByName { get; } = All.ToFrozenDictionary(operation => operation.Name, StringComparer.Ordinal);

   /// <summary>Returns the operation with this name.</summary>
   /// <exception cref="KeyNotFoundException">No operation has the name.</exception>
   public static Operation Get(string name) => ByName.TryGetValue(name, out var operation) ? operation : throw new KeyNotFoundException($"No operation is named {name}.");

   private static T From<T>(IServiceProvider services)
      where T : notnull => services.GetRequiredService<T>();

   private static FileOperations FileOperationsFrom(IServiceProvider services) => services.GetRequiredService<FileOperations>();
}
