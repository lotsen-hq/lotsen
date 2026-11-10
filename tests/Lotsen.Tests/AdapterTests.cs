using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Adapter;
using Lotsen.Hosting;
using Lotsen.Operations;
using Microsoft.Extensions.DependencyInjection;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>Covers <c>lotsen mcp</c> on the wire: the handshake Claude Code runs, the tools, and how calls reach the server.</summary>
[Trait("Category", "Adapter")]
public sealed class AdapterTests : IDisposable
{
   private const string PrUrl = TestPlatform.PrUrl;

   private const string CreateTourArguments = $$"""{ "prUrl": "{{PrUrl}}", "familiarity": "some" }""";

   // The server that lotsen serve starts has the Azure DevOps platform, not the test platform.
   private const string ServedPrUrl = "https://dev.azure.com/contoso/shop/_git/web/pullrequest/42";

   // Claude Code cuts server instructions and the description of a tool that loads upfront at this length: https://code.claude.com/docs/en/mcp
   private const int ClaudeCodeLimit = 4096;

   private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(2);

   private readonly string _root = Directory.CreateTempSubdirectory("lotsen-tests-").FullName;

   /// <summary>The discover request carries the per-request <c>_meta</c> of protocol revision 2026-07-28, which the SDK requires before the adapter's filter sees the request.</summary>
   [Fact]
   public async Task Discover_is_answered_as_unknown_and_initialize_declares_the_channel()
   {
      using var adapter = AdapterProcess.Start(EmptyDataDirectory());

      var discover = await adapter.RequestAsync(0, McpAdapter.DiscoverMethod, new JsonObject
      {
         ["_meta"] = new JsonObject
         {
            ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
            ["io.modelcontextprotocol/clientInfo"] = new JsonObject { ["name"] = "claude-code", ["version"] = "2.1.304" },
            ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject(),
         },
      });
      var initialize = await adapter.InitializeAsync();

      Assert.Equal(-32601, (int)discover["error"]!["code"]!);
      Assert.Equal("2025-11-25", (string)initialize["protocolVersion"]!);
      var experimental = initialize["capabilities"]!["experimental"]!.AsObject();
      Assert.Equal([McpAdapter.ChannelCapability], experimental.Select(capability => capability.Key));
      Assert.NotNull(initialize["capabilities"]!["tools"]);
      Assert.Equal(McpAdapter.Instructions, (string)initialize["instructions"]!);
      Assert.Contains("tourId", McpAdapter.Instructions);
   }

   /// <summary>The SDK refuses a discover without the per-request <c>_meta</c> before the adapter's filter sees it.</summary>
   [Fact]
   public async Task Discover_without_per_request_meta_gets_the_sdks_error()
   {
      using var adapter = AdapterProcess.Start(EmptyDataDirectory());

      var discover = await adapter.RequestAsync(0, McpAdapter.DiscoverMethod, new JsonObject());
      var initialize = await adapter.InitializeAsync();

      Assert.Equal(-32602, (int)discover["error"]!["code"]!);
      Assert.Equal("2025-11-25", (string)initialize["protocolVersion"]!);
   }

   [Fact]
   public async Task Tools_are_the_operations_with_their_exported_input_schemas()
   {
      using var adapter = AdapterProcess.Start(EmptyDataDirectory());
      await adapter.InitializeAsync();

      var tools = (await adapter.RequestAsync(2, "tools/list"))["result"]!["tools"]!.AsArray();

      Assert.Equal(["create_tour", "attach_tour", "set_plan", "put_stop", "read_state", "read_summary", "write_synthesis", "fetch_answers", "fetch_question", "reply", "start_thread", "add_thread_message", "outdate_thread", "delete_thread", "ack", "pin_tour", "find_tours", "open_ui"], tools.Select(tool => (string)tool!["name"]!));
      Assert.All(tools, tool =>
      {
         var operation = OperationCatalog.Get((string)tool!["name"]!);
         Assert.Equal(operation.Description, (string)tool["description"]!);
         Assert.True(JsonNode.DeepEquals(operation.InputSchema(), tool["inputSchema"]), $"{operation.Name}: the tool's input schema differs from the exported one: {tool["inputSchema"]}");
      });
   }

   [Fact]
   public async Task Operation_that_is_no_agent_tool_cannot_be_called()
   {
      await using var server = await StartServerAsync();
      using var adapter = AdapterProcess.Start(server.DataDirectory.Path);
      await adapter.InitializeAsync();

      var response = await adapter.RequestAsync(2, "tools/call", new JsonObject
      {
         ["name"] = "list_tours",
         ["arguments"] = new JsonObject(),
      });

      Assert.Equal(-32602, (int)response["error"]!["code"]!);
   }

   [Fact]
   public async Task Tool_calls_reach_the_running_server()
   {
      await using var server = await StartServerAsync();
      using var adapter = AdapterProcess.Start(server.DataDirectory.Path);
      await adapter.InitializeAsync();

      var created = await adapter.CallToolAsync(2, "create_tour", CreateTourArguments);
      var id = (string)JsonNode.Parse(created.Text)!["id"]!;
      var planned = await adapter.CallToolAsync(3, "set_plan", $$"""
         {
           "tourId": "{{id}}",
           "chapters": [{ "id": "orientation", "title": "Orientation" }],
           "stops": [{ "title": "What this PR changes", "kind": "overview", "chapter": "orientation" }],
           "fileMarks": [{ "path": "package-lock.json", "mark": "generated", "reason": "npm writes the lock file." }]
         }
         """);
      var read = await adapter.CallToolAsync(4, "read_state", $$"""{ "tourId": "{{id}}" }""");

      Assert.False(created.IsError, created.Text);
      Assert.False(planned.IsError, planned.Text);
      Assert.False(read.IsError, read.Text);
      var stored = await server.CallAsync(On("read_state", id));
      Assert.Equal(PrUrl, (string)stored["target"]!["url"]!);
      Assert.Equal("What this PR changes", (string)stored["plan"]!["stops"]![0]!["title"]!);
      Assert.Equal("generated", (string)stored["plan"]!["fileMarks"]![0]!["mark"]!);
      Assert.True(JsonNode.DeepEquals(stored, JsonNode.Parse(read.Text)), $"read_state differs from the API: {read.Text}");
      Assert.DoesNotContain(server.State.Token, adapter.Output);
   }

   /// <summary>The second adapter's first attach is refused, so only its take-over makes the server send the first adapter's session an event.</summary>
   [Fact]
   public async Task Take_over_event_becomes_a_channel_notification_to_the_old_session()
   {
      await using var server = await StartServerAsync();
      using var first = AdapterProcess.Start(server.DataDirectory.Path);
      await first.InitializeAsync();
      var id = (string)JsonNode.Parse((await first.CallToolAsync(2, "create_tour", CreateTourArguments)).Text)!["id"]!;
      using var second = AdapterProcess.Start(server.DataDirectory.Path);
      await second.InitializeAsync();

      var refused = await second.CallToolAsync(2, "attach_tour", $$"""{ "tourId": "{{id}}" }""");
      var takenOver = await second.CallToolAsync(3, "attach_tour", $$"""{ "tourId": "{{id}}", "takeOver": true }""");
      var firstProbe = await NextChannelNotificationAsync(first);
      var notification = await NextChannelNotificationAsync(first);
      var secondProbe = await NextChannelNotificationAsync(second);
      var oldWrite = await first.CallToolAsync(3, "set_plan", $$"""{ "tourId": "{{id}}", "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "title": "S", "kind": "overview", "chapter": "a" }] }""");

      Assert.True(refused.IsError);
      Assert.StartsWith($"tourId: tour {id} is attached to another session.", refused.Text, StringComparison.Ordinal);
      Assert.False(takenOver.IsError, takenOver.Text);
      Assert.NotNull(notification);
      var parameters = notification["params"]!.AsObject();
      Assert.Equal(["content", "meta"], parameters.Select(parameter => parameter.Key).Order());
      Assert.Contains($"took over tour {id}", (string)parameters["content"]!, StringComparison.Ordinal);
      Assert.Contains("attach_tour", (string)parameters["content"]!, StringComparison.Ordinal);
      Assert.True(JsonNode.DeepEquals(new JsonObject { ["event"] = "tourTakenOver", ["tourId"] = id }, parameters["meta"]), $"meta: {parameters["meta"]}");
      Assert.Equal(("probe", "probe"), ((string?)firstProbe?["params"]?["meta"]?["event"], (string?)secondProbe?["params"]?["meta"]?["event"]));
      Assert.Null(await NextChannelNotificationAsync(second, TimeSpan.FromMilliseconds(500)));
      Assert.True(oldWrite.IsError);
      Assert.Contains("attached to another session", oldWrite.Text, StringComparison.Ordinal);
   }

   /// <summary>The reviewer's question reaches the bound session's adapter as the words and IDs, and the adapter's fetch_question returns the stop that the notification leaves out.</summary>
   [Fact]
   public async Task Question_event_becomes_a_channel_notification_with_the_words_and_ids_and_no_code()
   {
      await using var server = await StartServerAsync();
      using var adapter = AdapterProcess.Start(server.DataDirectory.Path);
      await adapter.InitializeAsync();
      var id = (string)JsonNode.Parse((await adapter.CallToolAsync(2, "create_tour", CreateTourArguments)).Text)!["id"]!;
      var probe = await NextChannelNotificationAsync(adapter);
      var planned = await adapter.CallToolAsync(3, "set_plan", $$"""{ "tourId": "{{id}}", "chapters": [{ "id": "a", "title": "A" }], "stops": [{ "title": "The secret title", "kind": "overview", "chapter": "a" }] }""");
      var stopId = (string)JsonNode.Parse(planned.Text)!["plan"]!["stops"]![0]!["id"]!;
      var ask = new PreparedCall(On("ask_question", id), await server.UiCredentialAsync());
      using var asked = await server.SendAsync(ask, new JsonObject { ["text"] = "Why start here?", ["anchor"] = new JsonObject { ["kind"] = "stop", ["stopId"] = stopId }, ["stopId"] = stopId }.ToJsonString());
      var thread = SuccessBody.Output(await asked.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

      var notification = await NextChannelNotificationAsync(adapter);

      Assert.Equal(HttpStatusCode.OK, asked.StatusCode);
      Assert.NotNull(notification);
      var parameters = notification["params"]!.AsObject();
      var questionId = (string)thread["messages"]![0]!["id"]!;
      Assert.Equal("Why start here?", (string)parameters["content"]!);
      var meta = parameters["meta"]!.AsObject();
      Assert.Equal((string?)probe?["params"]?["meta"]?["nonce"], (string?)meta["nonce"]);
      meta.Remove("nonce");
      Assert.True(
         JsonNode.DeepEquals(
            new JsonObject
            {
               ["event"] = "question",
               ["tourId"] = id,
               ["threadId"] = (string)thread["id"]!,
               ["questionId"] = questionId,
               ["requestId"] = (string)thread["messages"]![0]!["requests"]![0]!["id"]!,
               ["stopId"] = stopId,
            },
            meta),
         $"meta: {meta}");
      Assert.DoesNotContain("The secret title", notification.ToJsonString(), StringComparison.Ordinal);
      var fetched = await adapter.CallToolAsync(4, "fetch_question", $$"""{ "tourId": "{{id}}", "questionId": "{{questionId}}" }""");
      Assert.False(fetched.IsError, fetched.Text);
      Assert.Equal("The secret title", (string)JsonNode.Parse(fetched.Text)!["stop"]!["title"]!);
   }

   /// <summary>
   /// The reviewer's batch of answers reaches the bound session's adapter as IDs alone, and the adapter's fetch_answers returns the answers. The store writes the stop's content, because the
   /// harness's tour is not pinned, so put_stop refuses it.
   /// </summary>
   [Fact]
   public async Task Answers_event_becomes_a_channel_notification_with_the_ids_and_no_answer_text()
   {
      await using var server = await StartServerAsync();
      using var adapter = AdapterProcess.Start(server.DataDirectory.Path);
      await adapter.InitializeAsync();
      var id = (string)JsonNode.Parse((await adapter.CallToolAsync(2, "create_tour", CreateTourArguments)).Text)!["id"]!;
      var probe = await NextChannelNotificationAsync(adapter);
      Assert.True(Lotsen.Tours.TourId.TryParse(id, out var tourId));
      var content = new Lotsen.Tours.StopContent("Keep it?", [], Checks: [new Lotsen.Tours.StopCheck("somechck", "Keep the digit?")]);
      var stop = new Lotsen.Tours.Stop.Written("somestop", "Keep it?", Lotsen.Tours.StopKind.Overview, "a", content, 1, []);
      server.ChangeStored(tourId, tour => tour with { Plan = new Lotsen.Tours.Plan([new Lotsen.Tours.Chapter("a", "A")], [stop], []) });
      var credential = await server.UiCredentialAsync();
      var answer = new PreparedCall(On("answer_check", id, stopId: "somestop", checkId: "somechck"), credential);
      using var answered = await server.SendAsync(answer, """{ "text": "The secret answer." }""");
      var send = new PreparedCall(On("send_answers", id), credential);
      using var sent = await server.SendAsync(send, "{}");
      var batchId = (string)SuccessBody.Output(await sent.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))["batchId"]!;

      var notification = await NextChannelNotificationAsync(adapter);

      Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (answered.StatusCode, sent.StatusCode));
      Assert.NotNull(notification);
      var parameters = notification["params"]!.AsObject();
      Assert.Contains("fetch_answers", (string)parameters["content"]!, StringComparison.Ordinal);
      Assert.True(
         JsonNode.DeepEquals(
            new JsonObject { ["event"] = "answers", ["tourId"] = id, ["batchId"] = batchId, ["nonce"] = (string?)probe?["params"]?["meta"]?["nonce"] },
            parameters["meta"]),
         $"meta: {parameters["meta"]}");
      Assert.DoesNotContain("The secret answer.", notification.ToJsonString(), StringComparison.Ordinal);
      var fetched = await adapter.CallToolAsync(3, "fetch_answers", $$"""{ "tourId": "{{id}}", "batchId": "{{batchId}}" }""");
      Assert.False(fetched.IsError, fetched.Text);
      var fetchedAnswer = JsonNode.Parse(fetched.Text)!["answers"]![0]!;
      Assert.Equal(("Keep the digit?", "The secret answer.", "decision"), ((string)fetchedAnswer["question"]!, (string)fetchedAnswer["text"]!, (string)fetchedAnswer["type"]!));
   }

   [Fact]
   public async Task Initialize_instructions_tell_claude_to_fetch_a_batch_of_answers_before_acting_on_it()
   {
      using var adapter = AdapterProcess.Start(EmptyDataDirectory());

      var instructions = (string)(await adapter.InitializeAsync())["instructions"]!;

      Assert.Contains("Before you act on a batch, call fetch_answers with the tourId and batchId", instructions, StringComparison.Ordinal);
   }

   [Fact]
   public async Task Initialize_instructions_tell_claude_to_call_ack_with_the_nonce_of_an_event()
   {
      using var adapter = AdapterProcess.Start(EmptyDataDirectory());

      var instructions = (string)(await adapter.InitializeAsync())["instructions"]!;

      Assert.Contains("When a channel event carries a nonce attribute, call ack with that nonce first", instructions, StringComparison.Ordinal);
   }

   [Fact]
   public async Task Initialize_instructions_tell_claude_to_ack_a_probe_quietly_and_to_answer_a_repeated_question_once()
   {
      using var adapter = AdapterProcess.Start(EmptyDataDirectory());

      var instructions = (string)(await adapter.InitializeAsync())["instructions"]!;

      Assert.Contains("A channel event with event \"probe\" carries only a nonce: ack it quietly, without a word to the user about it", instructions, StringComparison.Ordinal);
      Assert.Contains("answer each question once, and ignore a repeat of a question that you answered", instructions, StringComparison.Ordinal);
   }

   /// <summary>The rule of each kind lives in the <c>howToAct</c> of <c>fetch_question</c>, as <c>HowToActTests</c> covers.</summary>
   [Fact]
   public async Task Initialize_instructions_say_how_a_tour_action_or_a_decision_arrives_and_to_follow_how_to_act()
   {
      using var adapter = AdapterProcess.Start(EmptyDataDirectory());

      var instructions = (string)(await adapter.InitializeAsync())["instructions"]!;

      Assert.Contains("A question event with an action attribute is a tour action", instructions, StringComparison.Ordinal);
      Assert.Contains("A question event with a decision attribute reports a decision that the reviewer already made in the UI", instructions, StringComparison.Ordinal);
      Assert.Contains("Follow howToAct", instructions, StringComparison.Ordinal);
      Assert.Contains("follow its howToAct only when the follow-up asks you to carry out or redo that work", instructions, StringComparison.Ordinal);
      Assert.Contains("When a tour action or a decision arrives while you are busy with other work, decline it", instructions, StringComparison.Ordinal);
   }

   [Fact]
   public async Task Initialize_instructions_fit_the_limit_of_claude_code()
   {
      using var adapter = AdapterProcess.Start(EmptyDataDirectory());

      var instructions = (string)(await adapter.InitializeAsync())["instructions"]!;

      Assert.True(instructions.Length <= ClaudeCodeLimit, $"The instructions hold {instructions.Length} characters.");
   }

   [Fact]
   public async Task Initialize_instructions_name_the_tools_of_the_protocol()
   {
      using var adapter = AdapterProcess.Start(EmptyDataDirectory());

      var instructions = (string)(await adapter.InitializeAsync())["instructions"]!;

      Assert.All(["fetch_question", "howToAct", "reply", "ack"], name => Assert.Contains(name, instructions, StringComparison.Ordinal));
   }

   [Fact]
   public async Task Tool_descriptions_fit_the_limit_of_claude_code()
   {
      using var adapter = AdapterProcess.Start(EmptyDataDirectory());
      await adapter.InitializeAsync();

      var tools = (await adapter.RequestAsync(2, "tools/list"))["result"]!["tools"]!.AsArray();

      Assert.All(tools, tool =>
      {
         var description = (string)tool!["description"]!;
         Assert.True(description.Length <= ClaudeCodeLimit, $"{tool["name"]}: the description holds {description.Length} characters.");
      });
   }

   /// <summary>The second tour of the session gets no probe of its own, because the nonce belongs to the session and not to a tour.</summary>
   [Fact]
   public async Task Probe_becomes_a_channel_notification_with_the_nonce_and_ack_with_it_confirms_the_session()
   {
      await using var server = await StartServerAsync();
      using var adapter = AdapterProcess.Start(server.DataDirectory.Path);
      await adapter.InitializeAsync();
      var first = (string)JsonNode.Parse((await adapter.CallToolAsync(2, "create_tour", CreateTourArguments)).Text)!["id"]!;
      var probe = await NextChannelNotificationAsync(adapter);
      var second = (string)JsonNode.Parse((await adapter.CallToolAsync(3, "create_tour", CreateTourArguments)).Text)!["id"]!;
      var levelBefore = (string)(await server.BindingOfAsync(second))!["level"]!;
      var nonce = (string)probe!["params"]!["meta"]!["nonce"]!;

      var wrong = await adapter.CallToolAsync(4, "ack", """{ "nonce": "aaaaaaaaaaaaaaaaaaaaaaaaaa" }""");
      var acked = await adapter.CallToolAsync(5, "ack", $$"""{ "nonce": "{{nonce}}" }""");

      var parameters = probe["params"]!.AsObject();
      Assert.Equal("lotsen checks that its questions reach this session.", (string)parameters["content"]!);
      Assert.True(JsonNode.DeepEquals(new JsonObject { ["event"] = "probe", ["nonce"] = nonce }, parameters["meta"]), $"meta: {parameters["meta"]}");
      Assert.True(wrong.IsError, wrong.Text);
      Assert.Contains("this is not the nonce that this session received", wrong.Text, StringComparison.Ordinal);
      Assert.Equal("unconfirmed", levelBefore);
      Assert.False(acked.IsError, acked.Text);
      Assert.Equal("confirmed", (string)JsonNode.Parse(acked.Text)!["level"]!);
      Assert.Equal("confirmed", (string)(await server.BindingOfAsync(first))!["level"]!);
      Assert.Equal("confirmed", (string)(await server.BindingOfAsync(second))!["level"]!);
      Assert.Null(await NextChannelNotificationAsync(adapter, TimeSpan.FromMilliseconds(500)));
   }

   [Fact]
   public async Task Refused_operation_is_a_tool_error_with_the_servers_reason()
   {
      const string arguments = """{ "prUrl": "https://example.com/not-a-pull-request", "familiarity": "some" }""";
      await using var server = await StartServerAsync();
      using var adapter = AdapterProcess.Start(server.DataDirectory.Path);
      await adapter.InitializeAsync();

      var result = await adapter.CallToolAsync(2, "create_tour", arguments);

      using var direct = await server.AgentSendJsonAsync("create_tour", arguments);
      var error = JsonNode.Parse(await direct.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
      Assert.Equal(HttpStatusCode.BadRequest, direct.StatusCode);
      Assert.True(result.IsError);
      Assert.Equal((string)error["reason"]!, result.Text);
   }

   /// <summary>The adapter passes the arguments to the server as they are, so the server refuses each value that names no tour, a path or an empty one included.</summary>
   [Theory]
   [InlineData("aaaaaaaaaaaaaaaa")]
   [InlineData("../../api/tours")]
   [InlineData("")]
   [InlineData(".")]
   [InlineData("..")]
   public async Task Unknown_tour_is_a_tool_error_with_the_servers_reason(string tourId)
   {
      await using var server = await StartServerAsync();
      using var adapter = AdapterProcess.Start(server.DataDirectory.Path);
      await adapter.InitializeAsync();

      var result = await adapter.CallToolAsync(2, "read_state", new JsonObject { ["tourId"] = tourId }.ToJsonString());

      Assert.True(result.IsError);
      Assert.Equal($"tourId: no tour has the ID '{tourId}'.", result.Text);
   }

   /// <summary>The server in the test process runs at other versions than the adapter's executable, so the result holds the text that Claude sees for a server of another build.</summary>
   [Theory]
   [Trait("Category", "Versions")]
   [InlineData(false)]
   [InlineData(true)]
   public async Task Server_of_another_build_refuses_the_call_with_its_mismatch_reason(bool protocolDiffers)
   {
      var current = Program.Version;
      var other = protocolDiffers ? current with { Protocol = current.Protocol + 1 } : current with { Build = "0.0.0 (another build)" };
      await using var server = await ServerHarness.StartAsync(version: other);
      using var adapter = AdapterProcess.Start(server.DataDirectory.Path);
      await adapter.InitializeAsync();

      var result = await adapter.CallToolAsync(2, "create_tour", CreateTourArguments);

      Assert.True(result.IsError);
      Assert.Equal(VersionInfo.MismatchReason(other, current), result.Text);
      Assert.Empty((await server.CallAsync("list_tours"))["tours"]!.AsArray());
      Assert.Empty(server.Sessions.Current);
   }

   [Fact]
   public async Task Adapter_opens_a_session_at_start_and_a_new_one_after_the_server_restarts()
   {
      await using var first = await StartServerAsync();
      using var adapter = AdapterProcess.Start(first.DataDirectory.Path);
      await adapter.InitializeAsync();
      var before = await WaitForSessionAsync(first, adapter);

      await first.StopServerAsync();
      await using var second = await ServerHarness.StartAsync(version: Program.Version, dataDirectory: first.DataDirectory);
      var after = await WaitForSessionAsync(second, adapter);
      var created = await adapter.CallToolAsync(2, "create_tour", CreateTourArguments);

      Assert.NotEqual(before.Id, after.Id);
      Assert.False(created.IsError, created.Text);
      Assert.Equal(after.Id, Assert.Single(second.Sessions.Current).Id);
   }

   [Fact]
   public async Task Tool_calls_are_made_as_the_adapters_session()
   {
      var serverLog = new RecordingLoggerProvider();
      await using var server = await ServerHarness.StartAsync(version: Program.Version, configure: options => options with { LoggerProvider = serverLog });
      using var adapter = AdapterProcess.Start(server.DataDirectory.Path);
      await adapter.InitializeAsync();
      var session = await WaitForSessionAsync(server, adapter);

      var created = await adapter.CallToolAsync(2, "create_tour", CreateTourArguments);
      var id = (string)JsonNode.Parse(created.Text)!["id"]!;
      var read = await adapter.CallToolAsync(3, "read_state", $$"""{ "tourId": "{{id}}" }""");

      Assert.False(read.IsError, read.Text);
      Assert.Contains(serverLog.Lines, line => line.Contains("POST /api/ops/create_tour", StringComparison.Ordinal) && line.Contains($"agent session {session.Id}", StringComparison.Ordinal));
      Assert.Contains(serverLog.Lines, line => line.Contains("POST /api/ops/read_state", StringComparison.Ordinal) && line.Contains($"agent session {session.Id}", StringComparison.Ordinal));
      Assert.DoesNotContain(serverLog.Lines, line => line.Contains("/api/ops/", StringComparison.Ordinal) && line.Contains(" from the CLI", StringComparison.Ordinal));
   }

   /// <summary>The server ends the session while the adapter still holds it, so the call either meets the 401 for the ended session or waits for the next one.</summary>
   [Fact]
   public async Task Tool_call_after_the_server_ended_the_session_is_made_as_a_new_session()
   {
      var serverLog = new RecordingLoggerProvider();
      await using var server = await ServerHarness.StartAsync(version: Program.Version, configure: options => options with { LoggerProvider = serverLog });
      using var adapter = AdapterProcess.Start(server.DataDirectory.Path);
      await adapter.InitializeAsync();
      var ended = await WaitForSessionAsync(server, adapter);

      ended.End();
      var created = await adapter.CallToolAsync(2, "create_tour", CreateTourArguments);
      var current = await WaitForSessionAsync(server, adapter);

      Assert.False(created.IsError, created.Text);
      Assert.NotEqual(ended.Id, current.Id);
      Assert.Contains(serverLog.Lines, line => line.Contains("POST /api/ops/create_tour", StringComparison.Ordinal) && line.Contains($"agent session {current.Id}", StringComparison.Ordinal));
      Assert.Single((await server.CallAsync("list_tours"))["tours"]!.AsArray());
   }

   /// <summary>The server ends the session it registered for the adapter once the adapter has exited and its connection has dropped.</summary>
   [Fact]
   public async Task Adapter_with_an_open_session_exits_within_two_seconds_of_stdin_closing()
   {
      await using var server = await StartServerAsync();
      using var adapter = AdapterProcess.Start(server.DataDirectory.Path);
      await adapter.InitializeAsync();
      var session = await WaitForSessionAsync(server, adapter);

      var stopwatch = Stopwatch.StartNew();
      adapter.CloseInput();
      var exited = await adapter.WaitForExitAsync(ExitTimeout);

      Assert.True(exited, $"The adapter still runs {stopwatch.Elapsed.TotalSeconds:F2} s after stdin closed:\n{adapter.Output}");
      Assert.Equal(0, adapter.ExitCode);
      await Poll.EndedAsync(session, TimeSpan.FromSeconds(10), "The server kept the session after the adapter exited.");
   }

   /// <summary>The adapter held a session on the server it started, so it starts no other one, and the next call finds the server that the user started.</summary>
   [Fact]
   public async Task Server_started_after_the_adapters_server_was_killed_is_found()
   {
      var dataDirectory = EmptyDataDirectory();
      using var adapter = AdapterProcess.Start(dataDirectory);
      await adapter.InitializeAsync();
      const string arguments = $$"""{ "prUrl": "{{ServedPrUrl}}", "familiarity": "some" }""";
      var before = await adapter.CallToolAsync(2, "open_ui");
      var background = StateFile.TryRead(new DataDirectory(dataDirectory))!;
      using (var killed = ProcessLiveness.TryGet(background.Pid)!)
      {
         killed.Kill();
         await killed.WaitForExitAsync(TestContext.Current.CancellationToken);
      }

      using var serve = LotsenProcess.Start(dataDirectory, null, "serve");
      await WaitForStateFileAsync(new DataDirectory(dataDirectory), serve);
      var after = await adapter.CallToolAsync(3, "create_tour", arguments);

      Assert.False(before.IsError, before.Text);
      Assert.False(after.IsError, after.Text);
      Assert.Equal(ServedPrUrl, (string)JsonNode.Parse(after.Text)!["target"]!["url"]!);
      Assert.Equal(serve.Id, StateFile.TryRead(new DataDirectory(dataDirectory))!.Pid);
   }

   [Fact]
   public async Task Adapter_exits_within_two_seconds_of_stdin_closing()
   {
      using var adapter = AdapterProcess.Start(EmptyDataDirectory());
      await adapter.InitializeAsync();

      var stopwatch = Stopwatch.StartNew();
      adapter.CloseInput();
      var exited = await adapter.WaitForExitAsync(ExitTimeout);

      Assert.True(exited, $"The adapter still runs {stopwatch.Elapsed.TotalSeconds:F2} s after stdin closed:\n{adapter.Output}");
      Assert.Equal(0, adapter.ExitCode);
   }

   [Fact]
   public async Task Adapter_exits_cleanly_on_sigterm()
   {
      Assert.SkipWhen(OperatingSystem.IsWindows(), "The test sends SIGTERM.");
      using var adapter = AdapterProcess.Start(EmptyDataDirectory());
      await adapter.InitializeAsync();

      adapter.Terminate();

      Assert.True(await adapter.WaitForExitAsync(ExitTimeout), $"The adapter ignored SIGTERM:\n{adapter.Output}");
      Assert.Equal(0, adapter.ExitCode);
   }

   [Fact]
   public async Task Version_endpoint_needs_the_token_and_reports_the_servers_versions()
   {
      await using var server = await StartServerAsync();

      using var anonymous = await server.SendAsync(server.Request(HttpMethod.Get, "api/version"));
      using var versionResponse = await server.SendAsync(server.Request(HttpMethod.Get, "api/version", withToken: true));
      var version = JsonNode.Parse(await versionResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;

      Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
      Assert.Equal(Program.Version.Build, (string)version["build"]!);
      Assert.Equal(Program.Version.Protocol, (int)version["protocol"]!);
   }

   public void Dispose()
   {
      BackgroundServerProcesses.KillAll(new DataDirectory(EmptyDataDirectory()));
      try
      {
         TestDirectories.Delete(_root, BackgroundServerProcesses.CleanupWait);
      }
      catch (IOException)
      {
      }
   }

   // The adapter process refuses a server whose versions differ from those of the lotsen executable.
   private static Task<ServerHarness> StartServerAsync() => ServerHarness.StartAsync(version: Program.Version);

   private string EmptyDataDirectory() => Path.Combine(_root, "data");

   // Skips any other notification, such as one the SDK sends by itself.
   private static async Task<JsonObject?> NextChannelNotificationAsync(AdapterProcess adapter, TimeSpan? timeout = null)
   {
      while (await adapter.NextNotificationAsync(timeout ?? TimeSpan.FromSeconds(15)) is { } notification)
      {
         if ((string?)notification["method"] == ChannelNotifications.Method)
         {
            return notification;
         }
      }

      return null;
   }

   private static Task<Lotsen.Sessions.AgentSession> WaitForSessionAsync(ServerHarness server, AdapterProcess adapter) =>
      Poll.UntilAsync(() => server.Sessions.Current.SingleOrDefault(), TimeSpan.FromSeconds(15), () => $"The adapter opened no session:\n{adapter.Output}");

   private static async Task WaitForStateFileAsync(DataDirectory dataDirectory, LotsenProcess serve)
   {
      var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
      while (StateFile.TryRead(dataDirectory) is not { } state || state.Pid != serve.Id)
      {
         Assert.False(serve.HasExited, $"lotsen serve exited early:\n{serve.Output}");
         Assert.True(DateTime.UtcNow < deadline, "lotsen serve wrote no state file.");
         await Task.Delay(50, TestContext.Current.CancellationToken);
      }
   }
}
