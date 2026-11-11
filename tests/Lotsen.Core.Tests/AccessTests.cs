using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Operations;
using Lotsen.Server;
using Lotsen.Sessions;
using Lotsen.Tours;
using Microsoft.Extensions.DependencyInjection;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>
/// Covers the access declarations of the catalog's operations: which callers each operation admits, the refusal of every other caller before the input is read, the binding check of a bound
/// pass, the callers that the credentials name, and what a read shows each viewer.
/// </summary>
[Trait("Category", "Roles")]
public sealed class AccessTests : IClassFixture<AccessTests.Server>
{
   private readonly Server _server;

   public AccessTests(Server server)
   {
      _server = server;
   }

   public static TheoryData<string, string> EveryOperationAndCallerKind() =>
      [.. OperationCatalog.All.SelectMany(operation => new[] { "ui", "agent", "cli" }.Select(kind => (operation.Name, kind)))];

   /// <summary>The reviewed record of who may call each operation. A change to an operation's access changes a line here.</summary>
   [Fact]
   public void Catalog_declares_each_operation_s_access()
   {
      Assert.Equal(
         [
            ("create_tour", "AnySession", "Agent"),
            ("attach_tour", "AnySession", "Agent"),
            ("set_plan", "BoundSession", "Agent"),
            ("put_stop", "BoundSession", "Agent"),
            ("read_state", "Open", "Ui, Agent"),
            ("read_summary", "Open", "Ui, Agent"),
            ("write_synthesis", "BoundSession", "Agent"),
            ("ask_question", "ReviewerToSession", "Ui"),
            ("request_tour_action", "ReviewerToSession", "Ui"),
            ("ask_follow_up", "ReviewerToSession", "Ui"),
            ("resend_question", "ReviewerToSession", "Ui"),
            ("cancel_question", "Reviewer", "Ui"),
            ("set_thread_state", "Reviewer", "Ui"),
            ("remove_thread", "Reviewer", "Ui"),
            ("save_note", "Reviewer", "Ui"),
            ("edit_note", "Reviewer", "Ui"),
            ("discard_note", "Reviewer", "Ui"),
            ("send_notes", "ReviewerToSession", "Ui"),
            ("answer_check", "Reviewer", "Ui"),
            ("set_answer_type", "Reviewer", "Ui"),
            ("clear_answer", "Reviewer", "Ui"),
            ("send_answers", "ReviewerToSession", "Ui"),
            ("fetch_answers", "AnySession", "Agent"),
            ("fetch_question", "AnySession", "Agent"),
            ("reply", "AnySession", "Agent"),
            ("start_thread", "BoundSession", "Agent"),
            ("add_thread_message", "BoundSession", "Agent"),
            ("outdate_thread", "BoundSession", "Agent"),
            ("delete_thread", "BoundSession", "Agent"),
            ("ack", "AnySession", "Agent"),
            ("pin_tour", "AnySession", "Agent"),
            ("find_tours", "AnySession", "Agent"),
            ("list_tours", "Open", "Ui"),
            ("list_changed_files", "Open", "Ui"),
            ("read_file_text", "Open", "Ui"),
            ("diff_file", "Open", "Ui"),
            ("list_directory", "Open", "Ui"),
            ("mark_file_reviewed", "Reviewer", "Ui"),
            ("clear_file_reviewed", "Reviewer", "Ui"),
            ("set_stop_skipped", "Reviewer", "Ui"),
            ("record_reading_progress", "Reviewer", "Ui"),
            ("set_tour_archived", "Reviewer", "Ui"),
            ("delete_tour", "Reviewer", "Ui"),
            ("prune_clones", "Reviewer", "Ui"),
            ("change_familiarity", "ReviewerToSession", "Ui"),
            ("approve_plan", "ReviewerToSession", "Ui"),
            ("check_iteration", "Reviewer", "Ui"),
            ("update_iteration", "ReviewerToSession", "Ui"),
            ("read_preferences", "Open", "Ui"),
            ("write_preference", "Reviewer", "Ui"),
            ("open_ui", "Open", "Agent, Cli"),
            ("create_ui_link", "Open", "Cli"),
         ],
         OperationCatalog.All.Select(operation => (operation.Name, operation.Access.GetType().Name, operation.Access.Admits.ToString())));
   }

   [Fact]
   public void Agent_tools_are_the_operations_that_admit_an_agent_session()
   {
      Assert.Equal(
         ["create_tour", "attach_tour", "set_plan", "put_stop", "read_state", "read_summary", "write_synthesis", "fetch_answers", "fetch_question", "reply", "start_thread",
          "add_thread_message", "outdate_thread", "delete_thread", "ack", "pin_tour", "find_tours", "open_ui"],
         OperationCatalog.AgentTools.Select(operation => operation.Name));
   }

   /// <summary>
   /// Each request's input names a malformed tour when the input has a tourId, and holds a property that no input declares, so an operation that read its input or its tour before it admitted
   /// the caller would answer 400 or 404 instead of 403.
   /// </summary>
   [Theory]
   [MemberData(nameof(EveryOperationAndCallerKind))]
   public async Task Every_operation_refuses_each_caller_kind_it_does_not_admit_before_reading_its_input(string name, string kind)
   {
      var operation = OperationCatalog.Get(name);
      var caller = _server.CallerOf(kind);
      using var subscription = _server.Harness.Server.Services.GetRequiredService<EventBroadcaster>().Subscribe();

      using var response = await _server.Harness.SendAsync(_server.Request(kind, operation));
      var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

      var refusal = operation.Access.Refusal(caller);
      if (operation.Access.Admits.HasFlag(Access.KindOf(caller)))
      {
         Assert.False(body.Contains(refusal, StringComparison.Ordinal), $"{name} admits {kind}, but refused it: {body}");
         return;
      }

      Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{name} answered {kind} with {(int)response.StatusCode}: {body}");
      var error = JsonNode.Parse(body)!;
      Assert.Equal("callerNotAllowed", (string?)error["code"]);
      Assert.Equal(refusal, (string?)error["problem"]);
      Assert.Equal(operation.InputSchema()["properties"]?["tourId"] is null ? null : "tourId", (string?)error["field"]);
      Assert.False(subscription.Reader.TryRead(out var published), $"{name} refused {kind} but published {published?.Type}.");
   }

   /// <summary>The session holds the tour and sends valid input, so only the access of these reviewer operations can refuse it.</summary>
   [Theory]
   [InlineData("mark_file_reviewed", """{ "path": "src/a.txt", "headObjectId": null }""")]
   [InlineData("clear_file_reviewed", """{ "path": "src/a.txt" }""")]
   [InlineData("check_iteration", "{}")]
   public async Task Review_marks_and_iteration_checks_refuse_an_agent_session(string operation, string body)
   {
      var harness = _server.Harness;
      await using var session = await AgentSessionConnection.OpenAsync(harness);
      using var created = await harness.SendAsync(session.Request(harness, "create_tour"), $$"""{ "prUrl": "{{TestPlatform.PrUrl}}", "familiarity": "some" }""");
      var tourId = (string)SuccessBody.Output(await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))["id"]!;

      using var response = await harness.SendAsync(session.Request(harness, On(operation, tourId)), body);

      var error = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
      Assert.Equal((HttpStatusCode.Forbidden, "callerNotAllowed"), (response.StatusCode, (string?)error["code"]));
      Assert.Contains("only the UI can", (string)error["reason"]!, StringComparison.Ordinal);
      Assert.Empty(harness.StoredReviewMarks(TourId.TryParse(tourId, out var id) ? id : throw new InvalidOperationException("The tour ID does not parse."))!);
      Assert.Null(harness.StoredTour(id)!.AvailableIteration);
   }

   [Fact]
   public void Refusals_name_what_the_operation_does_and_the_caller_it_refuses()
   {
      var agent = _server.CallerOf("agent");

      Assert.Equal($"only the UI can skip a stop, not {agent}. A skip is the reviewer's reading state.", OperationCatalog.Get("set_stop_skipped").Access.Refusal(agent));
      Assert.Equal("only an agent session can attach a tour, not the UI.", OperationCatalog.Get("attach_tour").Access.Refusal(_server.CallerOf("ui")));
      Assert.Equal("only the agent session that a tour is attached to can write its plan and stops, not the CLI.", OperationCatalog.Get("put_stop").Access.Refusal(_server.CallerOf("cli")));
      Assert.Equal("only the UI or an agent session can read a tour, not the CLI.", OperationCatalog.Get("read_state").Access.Refusal(_server.CallerOf("cli")));
      Assert.Equal("only an agent session or the CLI can open the UI, not the UI.", OperationCatalog.Get("open_ui").Access.Refusal(_server.CallerOf("ui")));
   }

   [Fact]
   public void Bound_session_pass_binds_only_the_session_that_holds_the_tour()
   {
      using var sessions = new AgentSessions();
      var credentials = ServerCredentials.CreateRandom();
      var session = sessions.TryOpen()!;
      var other = sessions.TryOpen()!;
      var pass = Passes.Admit<Access.BoundSession.Pass>("put_stop", Passes.Agent(credentials, sessions, session));
      var tour = TourValues.Id;

      Assert.Equal(OperationErrorCode.NoSessionAttached, pass.Bind(tour, null).Error?.Code);
      Assert.Equal(OperationErrorCode.AttachedToAnotherSession, pass.Bind(tour, other.Recipient).Error?.Code);
      Assert.Same(session.Recipient, pass.Bind(tour, session.Recipient).Value);
      Assert.Contains("so this session cannot write its plan and stops", pass.Bind(tour, other.Recipient).Error!.Problem, StringComparison.Ordinal);
   }

   [Fact]
   public void Reviewer_to_session_pass_binds_whichever_session_holds_the_tour()
   {
      using var sessions = new AgentSessions();
      var credentials = ServerCredentials.CreateRandom();
      var session = sessions.TryOpen()!;
      var pass = Passes.Admit<Access.ReviewerToSession.Pass>("send_answers", Passes.Ui(credentials, sessions));
      var tour = TourValues.Id;

      var unbound = pass.Bind(tour, null).Error;
      Assert.Equal(OperationErrorCode.NoSessionAttached, unbound?.Code);
      Assert.Contains("no session can receive the answers", unbound!.Problem, StringComparison.Ordinal);
      Assert.Same(session.Recipient, pass.Bind(tour, session.Recipient).Value);
   }

   [Fact]
   public void Identify_names_each_caller_from_its_credentials_alone()
   {
      using var sessions = new AgentSessions();
      var credentials = ServerCredentials.CreateRandom();
      var session = sessions.TryOpen()!;
      var token = credentials.ServerToken.Value;

      Assert.Equal(("cli", IdentifyFailure.None), Identified(credentials, sessions, token));
      Assert.Equal(("agent", IdentifyFailure.None), Identified(credentials, sessions, token, session.Credential));
      Assert.Equal(("ui", IdentifyFailure.None), Identified(credentials, sessions, credentials.Ui.Value));
      Assert.Equal((null, IdentifyFailure.NoCredential), Identified(credentials, sessions, "not-a-credential"));
      Assert.Equal((null, IdentifyFailure.NoCredential), Identified(credentials, sessions, ""));
      Assert.Equal((null, IdentifyFailure.SessionUnknown), Identified(credentials, sessions, token, $"{session.Id}.wrong-key"));
      Assert.Equal((null, IdentifyFailure.SessionUnknown), Identified(credentials, sessions, token, session.Credential, session.Credential));
      Assert.Equal((null, IdentifyFailure.UiAsSession), Identified(credentials, sessions, credentials.Ui.Value, session.Credential));
      Assert.Equal((null, IdentifyFailure.NoCredential), Identified(credentials, sessions, credentials.Ui.Value + "x"));
      Assert.Equal((null, IdentifyFailure.NoCredential), Identified(credentials, sessions, token + "x", session.Credential));
      Assert.Equal((null, IdentifyFailure.NoCredential), Identified(credentials, sessions, "", session.Credential));

      session.End();
      Assert.Equal((null, IdentifyFailure.SessionUnknown), Identified(credentials, sessions, token, session.Credential));
   }

   [Fact]
   public void Agent_caller_carries_the_session_that_its_credential_names()
   {
      using var sessions = new AgentSessions();
      var credentials = ServerCredentials.CreateRandom();
      var session = sessions.TryOpen()!;

      var caller = Assert.IsType<Caller.Agent>(Passes.Agent(credentials, sessions, session));

      Assert.Same(session.Recipient, caller.Recipient);
   }

   /// <summary>Each case calls the factory directly, so an agent needs both credentials even from code that skips <see cref="Caller.Identify"/>.</summary>
   [Fact]
   public void Agent_comes_only_from_the_server_token_with_a_session_credential()
   {
      using var sessions = new AgentSessions();
      var credentials = ServerCredentials.CreateRandom();
      var session = sessions.TryOpen()!;
      var token = credentials.ServerToken.Value;

      Assert.Same(session.Recipient, Caller.Agent.Presenting(credentials, sessions, token, session.Credential)?.Recipient);
      Assert.Null(Caller.Agent.Presenting(credentials, sessions, token, $"{session.Id}.wrong-key"));
      Assert.Null(Caller.Agent.Presenting(credentials, sessions, token + "x", session.Credential));
      Assert.Null(Caller.Agent.Presenting(credentials, sessions, credentials.Ui.Value, session.Credential));
      Assert.Null(Caller.Agent.Presenting(credentials, sessions, "", session.Credential));
   }

   [Fact]
   public void Only_the_reviewer_sees_draft_answers()
   {
      using var sessions = new AgentSessions();
      var session = sessions.TryOpen()!;
      var tour = TourValues.Tour() with { Answers = [new CheckAnswer("s1", "c1", "Mine.", AnswerType.Decision, AnswerState.Draft, TourValues.Now)] };

      Assert.Same(tour, AgentView.For(new Viewer.Reviewer(), tour));
      Assert.Empty(AgentView.For(new Viewer.Session(session.Recipient), tour).Answers);
      Assert.Empty(AgentView.For(new Viewer.Cli(), tour).Answers);
   }

   [Fact]
   public void Open_pass_shows_each_caller_its_own_view()
   {
      using var sessions = new AgentSessions();
      var credentials = ServerCredentials.CreateRandom();
      var session = sessions.TryOpen()!;

      Assert.IsType<Viewer.Reviewer>(Passes.Admit<Access.Open.Pass>("read_state", Passes.Ui(credentials, sessions)).Viewer);
      Assert.Same(session.Recipient, Assert.IsType<Viewer.Session>(Passes.Admit<Access.Open.Pass>("read_state", Passes.Agent(credentials, sessions, session)).Viewer).Recipient);
      Assert.IsType<Viewer.Cli>(Passes.Admit<Access.Open.Pass>("open_ui", Caller.Cli.Instance).Viewer);
   }

   private static (string? Kind, IdentifyFailure Failure) Identified(ServerCredentials credentials, AgentSessions sessions, string bearer, params string[] sessionCredential)
   {
      var caller = Caller.Identify(credentials, sessions, bearer, new(sessionCredential), out var failure);
      return (caller?.Kind, failure);
   }

   /// <summary>One server for every case of the theory, with one agent session that holds no tour.</summary>
   public sealed class Server : IAsyncLifetime
   {
      private AgentSessionConnection _agent = null!;

      internal ServerHarness Harness { get; private set; } = null!;

      public async ValueTask InitializeAsync()
      {
         Harness = await ServerHarness.StartAsync();
         _agent = await AgentSessionConnection.OpenAsync(Harness);
      }

      public async ValueTask DisposeAsync()
      {
         await _agent.DisposeAsync();
         await Harness.DisposeAsync();
      }

      internal Caller CallerOf(string kind) => kind switch
      {
         "ui" => Harness.UiCaller,
         "agent" => Passes.Identify(Harness.Server.Services.GetRequiredService<ServerCredentials>(), Harness.Sessions, Harness.State.Token, _agent.Credential),
         _ => Caller.Cli.Instance,
      };

      // Names a tour by a placeholder that is no tour ID, when the input has a tourId, and adds a property that no input declares.
      internal HttpRequestMessage Request(string kind, Operation operation)
      {
         var input = operation.InputSchema()["properties"]?["tourId"] is null ? """{ "notAnInput": true }""" : """{ "tourId": "placeholder", "notAnInput": true }""";
         return kind switch
         {
            "ui" => ServerHarness.Request(operation.Name, Harness.UiCredential, json: input),
            "agent" => _agent.Request(Harness, operation.Name).Request(input),
            _ => Harness.Request(operation.Name, withToken: true, json: input),
         };
      }
   }
}
