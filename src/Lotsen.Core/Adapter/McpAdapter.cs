using System.Text.Json;
using System.Text.Json.Nodes;
using Lotsen.Hosting;
using Lotsen.Operations;
using Lotsen.Sessions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Lotsen.Adapter;

/// <summary>Runs <c>lotsen mcp</c>: the stdio MCP server that Claude Code launches as a channel, with one tool per agent operation.</summary>
/// <remarks>
/// <para>
/// The adapter has no logic of its own. Each tool is an entry of <see cref="OperationCatalog.AgentTools"/>, with that operation's description and input schema, and a call sends the arguments to
/// the running server as the operation's input, unchanged, so the server alone checks them.
/// </para>
/// <para>
/// While it runs, the adapter holds an agent session on the server through <see cref="ServerConnection"/>, and it makes every call as that session.
/// </para>
/// <para>
/// When no server is running, the adapter starts a background server with <see cref="BackgroundServer.StartAsync"/>. The adapter serves MCP while the server starts, and even when the start fails,
/// because Claude Code shows a failed MCP server poorly. Each tool call then returns the reason as a tool error, and the adapter logs it to standard error. Standard output carries the protocol alone.
/// </para>
/// </remarks>
internal static class McpAdapter
{
   /// <summary>The experimental capability through which Claude Code registers the adapter as a channel.</summary>
   public const string ChannelCapability = "claude/channel";

   /// <summary>The method that Claude Code's MCP runtime sends before <c>initialize</c>, answered as unknown.</summary>
   public const string DiscoverMethod = "server/discover";

   /// <summary>The instructions that <c>initialize</c> gives Claude: the protocol that holds for every event.</summary>
   /// <remarks>Claude Code cuts these at 4,096 characters: https://code.claude.com/docs/en/mcp. So <see cref="Tours.ActionRules.HowToAct"/> and <see cref="Tours.DecisionRules.HowToAct"/> hold the rule of each kind.</remarks>
   public const string Instructions =
      "lotsen shows the user a guided tour of a pull request in a local web UI. You write the tour with these tools. The tools only read and write tour data, and every tool that acts on a tour takes its tourId. The user moves through the tour in the UI; you never move the user's view. A tour is attached to one Claude Code session at a time, and lotsen tells you in a channel event when another session takes over a tour that you write. When a channel event carries a nonce attribute, call ack with that nonce first, before you handle the event: it confirms to lotsen that its events reach this session. A channel event with event \"probe\" carries only a nonce: ack it quietly, without a word to the user about it, and carry on with your work. A channel event with event \"question\" holds the reviewer's question about a tour attached to this session, asked in the UI: its content is the reviewer's words, and its attributes name the tourId, threadId, questionId, requestId and, when the reviewer had a stop open, stopId. Before you answer, call fetch_question with the tourId and questionId: it returns the question's thread with every message so far, the anchor and the stop, and shows the reviewer that you received the question. Several questions can arrive at once, as when the reviewer sends a batch of notes: answer each with its own reply, in its own thread. A question can be a follow-up in a thread that holds earlier questions and replies, so read the whole thread. Then answer with reply, which resolves the request as done, needsAnswer or declined. Only the reviewer resolves or reopens a thread, and a question in a resolved thread still needs your reply. The reviewer can send a question again, so the same questionId can arrive more than once: answer each question once, and ignore a repeat of a question that you answered. A repeat of a tour action or a decision reaches you only while the tour still fits it: lotsen refuses to send again an explain or revise of a stop that you revised since the reviewer asked, and a reorder once the plan's stops changed. A question event with an action attribute is a tour action: the reviewer asks you to act on the tour, and its content holds the reviewer's optional words. A question event with a decision attribute reports a decision that the reviewer already made in the UI, which lotsen stored before it told you: the reviewer decided, and you act on the decision. Its content holds the reviewer's optional words. Fetch a tour action or a decision with fetch_question as a question: the thread holds the action under action, or the decision under decision, and howToAct says what its kind asks of you and how to carry it out. Follow howToAct. When a tour action or a decision arrives while you are busy with other work, decline it with a reply that says so, and tell the user in the terminal: a declined decision stays made, and the reviewer can ask you again later. A follow-up in the thread of a tour action or a decision is a question about it: follow its howToAct only when the follow-up asks you to carry out or redo that work. A channel event with event \"answers\" says that the reviewer sent a batch of answers to the checks of a tour attached to this session: its attributes name the tourId and batchId, and it holds none of the answers. Before you act on a batch, call fetch_answers with the tourId and batchId.";

   /// <summary>Serves MCP over standard input and output until standard input closes or the token is canceled.</summary>
   /// <param name="version">The versions of this executable, which a server must match before a call reaches it.</param>
   public static async Task RunAsync(DataDirectory dataDirectory, VersionInfo version, CancellationToken cancellationToken)
   {
      using var loggerFactory = LoggerFactory.Create(logging => logging
         .SetMinimumLevel(LogLevel.Warning)
         .AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace));
      var logger = loggerFactory.CreateLogger("lotsen.mcp");
      McpServer? server = null;
      var connection = new ServerConnection(
         dataDirectory, version, logger, (sessionEvent, token) => NotifyAsync(server!, sessionEvent, token), startServer: token => BackgroundServer.StartAsync(dataDirectory, LotsenCommand.Of(version), token));
      var tools = OperationCatalog.AgentTools.Select(ToTool).ToList();

      var options = new McpServerOptions
      {
         ServerInfo = new Implementation { Name = "lotsen", Version = VersionInfo.InformationalVersion },
         ServerInstructions = Instructions,
         Capabilities = new ServerCapabilities
         {
            Experimental = new Dictionary<string, object> { [ChannelCapability] = new JsonObject() },
         },
         Handlers = new McpServerHandlers
         {
            ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = tools }),
            CallToolHandler = (request, token) => CallToolAsync(connection, logger, request.Params, token),
         },
      };
      options.Filters.Message.IncomingFilters.Add(RefuseDiscover);

      await using var transport = new StdioServerTransport(options, loggerFactory);
      await using var created = McpServer.Create(transport, options, loggerFactory);
      server = created;
      using var stopped = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      var session = connection.RunAsync(stopped.Token);
      try
      {
         await created.RunAsync(cancellationToken);
      }
      finally
      {
         // A server that does not answer must not hold the exit once standard input has closed.
         await stopped.CancelAsync();
         await session;
      }
   }

   // Claude Code registers a channel only after an initialize handshake, which it sends when discover is unknown. The SDK checks the _meta of a discover before this filter runs, as
   // Discover_without_per_request_meta_gets_the_sdks_error shows.
   private static McpMessageHandler RefuseDiscover(McpMessageHandler next) => (context, cancellationToken) =>
      context.JsonRpcMessage is JsonRpcRequest { Method: DiscoverMethod }
         ? throw new McpProtocolException($"Method '{DiscoverMethod}' is not available.", McpErrorCode.MethodNotFound)
         : next(context, cancellationToken);

   // The connection starts after the server exists, so every event finds it.
   private static async Task NotifyAsync(McpServer server, SessionEvent sessionEvent, CancellationToken cancellationToken)
   {
      if (ChannelNotifications.ParamsFor(sessionEvent) is { } parameters)
      {
         await server.SendMessageAsync(new JsonRpcNotification { Method = ChannelNotifications.Method, Params = parameters }, cancellationToken);
      }
   }

   private static Tool ToTool(Operation operation) => new()
   {
      Name = operation.Name,
      Description = operation.Description,
      InputSchema = JsonElement.Parse(operation.InputSchema().ToJsonString()),
   };

   private static async ValueTask<CallToolResult> CallToolAsync(ServerConnection connection, ILogger logger, CallToolRequestParams? request, CancellationToken cancellationToken)
   {
      var operation = OperationCatalog.AgentTools.FirstOrDefault(operation => operation.Name == request?.Name)
         ?? throw new McpProtocolException($"No tool is named '{request?.Name}'.", McpErrorCode.InvalidParams);
      return await connection.InvokeAsync(operation, request!.Arguments ?? new Dictionary<string, JsonElement>(), cancellationToken) switch
      {
         ToolOutcome.Output output => Result(output.Json, isError: false),
         ToolOutcome.Refused refused => Result(refused.Error.Reason, isError: true),
         ToolOutcome.Unavailable unavailable => Unavailable(logger, operation, unavailable.Reason),
      };
   }

   private static CallToolResult Unavailable(ILogger logger, Operation operation, string reason)
   {
      logger.LogWarning("{Tool}: {Message}", operation.Name, reason);
      return Result(reason, isError: true);
   }

   private static CallToolResult Result(string text, bool isError) => new()
   {
      Content = [new TextContentBlock { Text = text }],
      IsError = isError,
   };
}
