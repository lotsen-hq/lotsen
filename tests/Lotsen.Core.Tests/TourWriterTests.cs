using Lotsen.Git;
using Lotsen.Hosting;
using Lotsen.Json;
using Lotsen.Operations;
using Lotsen.Platforms;
using Lotsen.Server;
using Lotsen.Sessions;
using Lotsen.Tours;
using Microsoft.Extensions.Logging;

namespace Lotsen.Tests;

/// <summary>
/// Covers how <see cref="TourWriter"/> runs a write: its access checks, its checks on the latest tour, the order of its events, the delivery of its session events under the lock, the settlement of
/// session ends and its hook point.
/// </summary>
[Trait("Category", "Writer")]
public sealed class TourWriterTests : IDisposable
{
   private static readonly PullRequestTarget PullRequest = new("test", new Uri("https://platform.test/shop/pull/42"));

   private static readonly TourPin.Pinned FirstPin = new(CommitId.Parse(new string('a', 40)), CommitId.Parse(new string('b', 40)), 1, new Uri("https://platform.test/shop.git"));

   private static readonly OperationError Refused = OperationError.Invalid("text", "the test refuses this write.");

   private readonly string _root = Directory.CreateTempSubdirectory("lotsen-tests-").FullName;
   private readonly FileTourStore _store;
   private readonly EventBroadcaster _events = new();
   private readonly AgentSessions _sessions = new();
   private readonly ServerCredentials _credentials = ServerCredentials.CreateRandom();
   private readonly RecordingLoggerProvider _logs = new();
   private readonly TourWriter _writer;

   public TourWriterTests()
   {
      var dataDirectory = new DataDirectory(Path.Combine(_root, "data"));
      _store = new FileTourStore(dataDirectory);
      _writer = new TourWriter(dataDirectory, _events, new PullRequestPlatforms([new TestPlatform()]), _sessions, TimeProvider.System, _logs.CreateLogger<TourWriter>());
   }

   public void Dispose()
   {
      _sessions.Dispose();
      Directory.Delete(_root, recursive: true);
   }

   [Fact]
   public void Refused_write_stores_nothing_and_publishes_nothing()
   {
      var tour = Create();
      var stored = File.ReadAllText(_store.PathOf(tour.Id));
      using var subscription = _events.Subscribe();

      var result = _writer.Write<Tour>(Unbound, tour.Id, _ => Refused);

      Assert.Same(Refused, result.Error);
      Assert.Equal(stored, File.ReadAllText(_store.PathOf(tour.Id)));
      Assert.False(subscription.Reader.TryRead(out _));
   }

   [Fact]
   public void Events_go_out_in_store_order_and_each_write_that_changes_the_tour_state_publishes_it()
   {
      var tour = Create();
      using var subscription = _events.Subscribe();

      Publishing(tour.Id, "first", latest => latest with { Archived = true });
      Publishing(tour.Id, "second", latest => latest with { Familiarity = Familiarity.New });
      Publishing(tour.Id, "third", latest => latest with { Archived = false });

      Assert.Equal(["first", ServerEvents.TourStateChanged.Name, "second", "third", ServerEvents.TourStateChanged.Name], DrainBesidesControls(subscription).Select(received => received.Type));
   }

   [Fact]
   public void Session_write_without_a_bound_session_is_refused_before_its_change_runs()
   {
      var tour = Create();
      var ran = false;

      var result = _writer.Write(ToSession, tour.Id, edit =>
      {
         ran = true;
         return edit.Store(edit.Latest, stored => stored.Tour);
      });

      Assert.Equal(OperationErrorCode.NoSessionAttached, result.Error?.Code);
      Assert.Contains("no session can receive the answers", result.Error!.Reason, StringComparison.Ordinal);
      Assert.False(ran);
   }

   [Fact]
   public void Session_write_gets_the_bound_session_as_its_recipient()
   {
      var tour = Create();
      var session = Bind(tour.Id);
      Recipient? recipient = null;

      var result = _writer.Write(ToSession, tour.Id, edit =>
      {
         recipient = edit.Recipient;
         return edit.Store(edit.Latest, stored => stored.Tour);
      });

      Assert.Null(result.Error);
      Assert.Same(session.Recipient, recipient);
   }

   /// <summary>
   /// The probe meets a full queue, so its delivery ends the session in the write's own hold. The end only records itself, and <see cref="TourWriter.SettleEnds"/> publishes the release.
   /// </summary>
   [Fact]
   public void Send_to_a_full_queue_ends_its_session_and_the_release_waits_for_the_settlement()
   {
      var tour = Create();
      var session = Bind(tour.Id);
      Fill(session, tour.Id);
      using var subscription = _events.Subscribe();

      var result = _writer.Write(ToSession, tour.Id, edit => edit.Store(edit.Latest with { Familiarity = Familiarity.New }, stored =>
      {
         stored.Publish(Marker("written", stored.Tour.Id));
         stored.Send(edit.Recipient.Probe(stored.Tour.Id));
         return stored.Tour;
      }));
      var (endedAtOnce, holderAtOnce, publishedAtOnce) = (session.IsEnded, Holder(tour.Id), DrainBesidesControls(subscription));
      var settled = _writer.SettleEnds();

      Assert.Null(result.Error);
      Assert.True(endedAtOnce);
      Assert.Null(holderAtOnce);
      Assert.Equal(["written"], publishedAtOnce.Select(received => received.Type));
      Assert.Equal(1, settled);
      Assert.Equal([ServerEvents.TourBindingChanged.Name], DrainBesidesControls(subscription).Select(received => received.Type));
      Assert.Equal(0, _writer.SettleEnds());
   }

   /// <summary>The question meets a full queue, so the ask stores its thread, and the settlement of the end marks the request unanswered.</summary>
   [Fact]
   public void Question_to_a_full_queue_is_stored_and_the_settlement_of_the_end_marks_it_unanswered()
   {
      var tour = Create();
      var session = Bind(tour.Id);
      Fill(session, tour.Id);
      using var subscription = _events.Subscribe();

      var asked = _writer.Write(ToSession, tour.Id, edit => edit.Store(edit.Threads.Ask(new ThreadAnchor.WholeTour(), "Why?", null), (_, written) => written.Thread!));
      var stored = _store.Get(tour.Id)!.Threads.Find(asked.Value!.Id)!;
      Drain(subscription);
      _writer.SettleEnds();
      var settled = DrainBesidesControls(subscription);

      Assert.True(session.IsEnded);
      Assert.Equal([RequestState.Pending], RequestStates(stored));
      Assert.Equal([ServerEvents.TourBindingChanged.Name, ServerEvents.ThreadUpdated.Name], settled.Select(received => received.Type));
      Assert.Equal([RequestState.Unanswered], RequestStates(_store.Get(tour.Id)!.Threads.Find(asked.Value.Id)!));
   }

   /// <summary>The thread's agent message holds a mention, and the follow-up waits on the session when it ends.</summary>
   [Fact]
   public void Thread_events_of_a_write_and_of_the_settlement_of_an_end_link_the_mentions_through_the_tours_platform()
   {
      var tour = Create();
      var session = Bind(tour.Id);
      using var subscription = _events.Subscribe();

      var threadId = Threaded(tour.Id, edit => edit.Threads.Apply(new ThreadCommand.Start(new ThreadAnchor.WholeTour(), "See [#7](workitem:7).", session.Recipient)));
      var created = DrainBesidesControls(subscription).Single(received => received.Type == ServerEvents.ThreadCreated.Name);
      _writer.Write(ToSession, tour.Id, edit => edit.Store(edit.Threads.FollowUp(threadId, "Why?", null), (_, written) => written.Thread!));
      Drain(subscription);
      session.End();
      _writer.SettleEnds();
      var settled = DrainBesidesControls(subscription).Single(received => received.Type == ServerEvents.ThreadUpdated.Name);

      Assert.All([created, settled], received => Assert.Contains(
         $"<a href=\"https://{TestPlatform.DefaultHost}/shop/workitems/7\"", (string)System.Text.Json.Nodes.JsonNode.Parse(received.Data)!["thread"]!["messages"]![0]!["html"]!, StringComparison.Ordinal));
   }

   /// <summary>The ended session held two tours, and one settlement releases both of them.</summary>
   [Fact]
   public void Settlement_of_an_end_releases_every_tour_that_the_session_still_held()
   {
      var first = Create();
      var second = Create();
      var session = Bind(first.Id);
      _writer.Exclusive(locked => locked.BindNew(second.Id, session.Recipient));
      using var subscription = _events.Subscribe();

      session.End();
      var settled = _writer.SettleEnds();

      Assert.Equal(1, settled);
      var released = DrainBesidesControls(subscription);
      Assert.All(released, received => Assert.Equal(ServerEvents.TourBindingChanged.Name, received.Type));
      Assert.Equal(new[] { first.Id.Value, second.Id.Value }.Order(), released.Select(received => (string)System.Text.Json.Nodes.JsonNode.Parse(received.Data)!["tourId"]!).Order());
      Assert.Null(Holder(first.Id));
      Assert.Null(Holder(second.Id));
   }

   /// <summary>The probe of the bind and the question of a later write reach the session's queue in store order, and the question carries the probe's nonce.</summary>
   [Fact]
   public void Session_queue_holds_the_probe_and_then_the_question_in_store_order()
   {
      var tour = Create();
      var session = _sessions.TryOpen()!;
      _writer.Exclusive(locked =>
      {
         locked.BindNew(tour.Id, session.Recipient);
         locked.Send(session.Recipient.Probe(tour.Id));
      });

      _writer.Write(ToSession, tour.Id, edit => edit.Store(edit.Threads.Ask(new ThreadAnchor.WholeTour(), "Why?", null), (_, written) => written.Thread!));

      Assert.True(session.Events.TryRead(out var probe));
      Assert.True(session.Events.TryRead(out var question));
      Assert.False(session.Events.TryRead(out _));
      var nonce = (string)System.Text.Json.Nodes.JsonNode.Parse(probe.Data)!["nonce"]!;
      Assert.Equal((SessionEvents.Probe, SessionEvents.Question, nonce), (probe.Type, question.Type, (string?)System.Text.Json.Nodes.JsonNode.Parse(question.Data)!["nonce"]));
   }

   /// <summary>Another thread holds the writer's lock while the session ends, so an end that took the writer's lock would not return before the lock is released.</summary>
   [Fact]
   public async Task End_takes_no_writer_lock_and_runs_no_writer_code()
   {
      var tour = Create();
      var session = Bind(tour.Id);
      using var inside = new ManualResetEventSlim();
      using var release = new ManualResetEventSlim();
      using var subscription = _events.Subscribe();
      var holder = Task.Run(() => _writer.Exclusive(_ =>
      {
         inside.Set();
         release.Wait(TestContext.Current.CancellationToken);
      }), TestContext.Current.CancellationToken);
      Assert.True(inside.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "The holder did not take the lock.");

      var ending = Task.Run(session.End, TestContext.Current.CancellationToken);
      var endedWhileHeld = await Task.WhenAny(ending, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)) == ending;
      var published = Drain(subscription);
      release.Set();
      await holder;

      Assert.True(endedWhileHeld, "The end waited for the writer's lock.");
      Assert.True(session.WhenEnded.IsCompletedSuccessfully);
      Assert.Empty(published);
   }

   /// <summary>
   /// The writer logs the probe's delivery on the delivering thread, and the log asks another thread to take the writer's lock. That thread cannot take it while the delivery runs under the lock.
   /// </summary>
   [Fact]
   public void Session_events_are_delivered_before_the_lock_is_released()
   {
      using var sessions = new AgentSessions();
      TourWriter? writer = null;
      bool? lockFreeAtDelivery = null;
      var logs = new DeliveryProbe(() => lockFreeAtDelivery ??= Task.Run(() => writer!.Exclusive(_ => true)).Wait(TimeSpan.FromMilliseconds(500)));
      writer = new TourWriter(new DataDirectory(Path.Combine(_root, "probe")), _events, new PullRequestPlatforms([new TestPlatform()]), sessions, TimeProvider.System, logs);
      var tour = writer.Exclusive(locked => locked.Create(PullRequest, Familiarity.Some, PlanApproval.NotNeeded));
      var session = sessions.TryOpen()!;

      writer.Exclusive(locked =>
      {
         locked.BindNew(tour.Id, session.Recipient);
         locked.Send(session.Recipient.Probe(tour.Id));
      });

      Assert.False(lockFreeAtDelivery, "The probe was delivered after the lock was released.");
   }

   /// <summary>Every write runs under the writer's lock, since the store takes none, so writes from many threads to one tour all apply.</summary>
   [Fact]
   public void Concurrent_writes_to_one_tour_are_all_applied()
   {
      const int writers = 64;
      var tour = Create();

      Parallel.For(0, writers, new ParallelOptions { MaxDegreeOfParallelism = 16 }, index =>
         _writer.WriteMarks(Unbound, tour.Id, marks => [.. marks, new ReviewMark($"f{index}.txt", null)]));

      Assert.Equal(Enumerable.Range(0, writers).Select(index => $"f{index}.txt").Order(), _store.GetReviewMarks(tour.Id)!.Select(mark => mark.Path).Order());
   }

   [Fact]
   public async Task Git_write_whose_pin_moves_during_the_git_phase_is_refused_and_stores_nothing()
   {
      var tour = Pinned(Create());
      using var subscription = _events.Subscribe();

      var result = await _writer.WriteAfterGitAsync<string, Tour>(
         Unbound,
         tour.Id,
         _ => null,
         (_, git, _) =>
         {
            Assert.Equal(FirstPin, git.Pin("text").Value);
            Publishing(tour.Id, "moved", latest => latest with { Pin = FirstPin with { Iteration = 2 } });
            return Task.FromResult<OperationResult<Gathered<string>>>(Gathered.Of("read"));
         },
         (edit, _) => edit.Store(edit.Latest with { Familiarity = Familiarity.New }, stored => stored.Tour),
         TestContext.Current.CancellationToken);

      Assert.Equal(("text", OperationErrorCode.InvalidInput), (result.Error?.Field, result.Error?.Code));
      Assert.Contains("pin changed", result.Error!.Reason, StringComparison.Ordinal);
      Assert.Equal(Familiarity.Some, _store.Get(tour.Id)!.Familiarity);
      Assert.Equal(["moved"], Drain(subscription).Select(received => received.Type));
   }

   [Fact]
   public async Task Git_write_whose_basis_no_longer_holds_on_the_latest_tour_is_refused()
   {
      var tour = Create();

      var result = await _writer.WriteAfterGitAsync<string, Tour>(
         Unbound,
         tour.Id,
         _ => null,
         (read, _, _) =>
         {
            var level = read.Familiarity;
            Publishing(tour.Id, "changed", latest => latest with { Familiarity = Familiarity.Familiar });
            return Task.FromResult<OperationResult<Gathered<string>>>(Gathered.Of("read").Unless(latest => latest.Familiarity != level, Refused));
         },
         (edit, _) => edit.Store(edit.Latest with { Archived = true }, stored => stored.Tour),
         TestContext.Current.CancellationToken);

      Assert.Same(Refused, result.Error);
      Assert.False(_store.Get(tour.Id)!.Archived);
   }

   [Fact]
   public async Task Git_write_whose_check_passes_on_the_read_tour_and_fails_on_the_latest_one_is_refused()
   {
      var tour = Create();
      var checks = 0;

      var result = await _writer.WriteAfterGitAsync<string, Tour>(
         Unbound,
         tour.Id,
         checking =>
         {
            checks++;
            return checking.Archived ? Refused : null;
         },
         (_, _, _) =>
         {
            Publishing(tour.Id, "archived", latest => latest with { Archived = true });
            return Task.FromResult<OperationResult<Gathered<string>>>(Gathered.Of("read"));
         },
         (edit, _) => edit.Store(edit.Latest with { Familiarity = Familiarity.New }, stored => stored.Tour),
         TestContext.Current.CancellationToken);

      Assert.Same(Refused, result.Error);
      Assert.Equal(2, checks);
      Assert.Equal(Familiarity.Some, _store.Get(tour.Id)!.Familiarity);
   }

   [Fact]
   public async Task Hook_runs_once_after_the_git_phase_with_the_operations_name_and_not_for_a_write_without_one()
   {
      var tour = Create();
      List<WritePoint> points = [];
      _writer.Hook = point =>
      {
         points.Add(point);
         return ValueTask.CompletedTask;
      };

      await _writer.WriteAfterGitAsync<string, Tour>(
         Unbound,
         tour.Id,
         _ => null,
         (_, _, _) => Task.FromResult<OperationResult<Gathered<string>>>(Gathered.Of("read")),
         (edit, _) => edit.Store(edit.Latest with { Familiarity = Familiarity.New }, stored => stored.Tour),
         TestContext.Current.CancellationToken);
      _writer.Write(Unbound, tour.Id, edit => edit.Store(edit.Latest with { Archived = true }, stored => stored.Tour));

      Assert.Equal([new WritePoint("set_tour_archived", tour.Id)], points);
   }

   [Fact]
   public void Thread_write_publishes_after_the_features_own_events_in_change_order()
   {
      var tour = Create();
      Bind(tour.Id);
      var first = Threaded(tour.Id, edit => edit.Threads.Apply(new ThreadCommand.SaveNote(new ThreadAnchor.WholeTour(), "First.", null)));
      var second = Threaded(tour.Id, edit => edit.Threads.Apply(new ThreadCommand.SaveNote(new ThreadAnchor.WholeTour(), "Second.", null)));
      using var subscription = _events.Subscribe();

      var result = _writer.Write(ToSession, tour.Id, edit => edit.Store(edit.Threads.SendNotes([second, first]), (stored, _) =>
      {
         stored.Publish(Marker("feature", stored.Tour.Id));
         return stored.Tour;
      }));

      Assert.Null(result.Error);
      var received = DrainBesidesControls(subscription);
      Assert.Equal(["feature", ServerEvents.ThreadUpdated.Name, ServerEvents.ThreadUpdated.Name], received.Select(serverEvent => serverEvent.Type));
      Assert.Equal([second, first], received.Skip(1).Select(serverEvent => (string)System.Text.Json.Nodes.JsonNode.Parse(serverEvent.Data)!["thread"]!["id"]!));
   }

   [Fact]
   public void Thread_write_of_a_carried_fact_publishes_no_thread_event()
   {
      var tour = Create();
      var session = Bind(tour.Id);
      var replaced = new Stop.Written("s1", "Rounding", StopKind.Overview, "c1", new StopContent("Old.", [], Why: "Old why."), 1, []);
      var written = replaced with { Content = new StopContent("New.", [], Why: "New why."), Version = 2, Earlier = [new StopVersion("Rounding", StopKind.Overview, replaced.Content, DateTimeOffset.UnixEpoch)] };
      Publishing(tour.Id, "planned", latest => latest with { Plan = new Plan([new Chapter("c1", "Pricing")], [replaced], []) });
      var threadId = Threaded(tour.Id, edit => edit.Threads.Apply(new ThreadCommand.Start(new ThreadAnchor.StopText("s1", "why", "Old", 1), "Look here.", session.Recipient)));
      using var subscription = _events.Subscribe();

      var result = _writer.Write(Unbound, tour.Id, edit => edit.Store(TourThreads.Settle(edit.Latest, new ThreadFact.StopRevised(replaced, written)), (stored, settled) =>
      {
         stored.Publish(Marker("revised", stored.Tour.Id));
         return settled;
      }));

      Assert.True(result.Value!.IsCarried);
      Assert.Equal(["revised"], Drain(subscription).Select(serverEvent => serverEvent.Type));
      Assert.Equal(QuestionThreadState.Outdated, _store.Get(tour.Id)!.Threads.Find(threadId)!.State);
   }

   [Fact]
   public void Thread_write_that_changes_nothing_stores_nothing_and_publishes_nothing()
   {
      var tour = Create();
      var noteId = Threaded(tour.Id, edit => edit.Threads.Apply(new ThreadCommand.SaveNote(new ThreadAnchor.WholeTour(), "Kept.", null)));
      var stored = File.ReadAllText(_store.PathOf(tour.Id));
      using var subscription = _events.Subscribe();

      var result = _writer.Write(Unbound, tour.Id, edit => edit.Store(edit.Threads.Apply(new ThreadCommand.EditNote(noteId, "Kept.")), (written, _) => written.Tour));

      Assert.Null(result.Error);
      Assert.Equal(stored, File.ReadAllText(_store.PathOf(tour.Id)));
      Assert.Empty(Drain(subscription));
   }

   [Fact]
   public void Thread_change_through_the_plain_store_or_from_other_threads_than_the_latest_is_refused()
   {
      var tour = Create();
      Threaded(tour.Id, edit => edit.Threads.Apply(new ThreadCommand.SaveNote(new ThreadAnchor.WholeTour(), "Mine.", null)));
      var stale = _store.Get(tour.Id)!;
      Threaded(tour.Id, edit => edit.Threads.Apply(new ThreadCommand.SaveNote(new ThreadAnchor.WholeTour(), "Later.", null)));

      Assert.Throws<InvalidOperationException>(() => _writer.Write(Unbound, tour.Id, edit => edit.Store(edit.Latest with { Threads = edit.Latest.Threads.WithoutNotes() }, written => written.Tour)));
      Assert.Throws<InvalidOperationException>(() => _writer.Write(Unbound, tour.Id, edit =>
         edit.Store(TourThreads.Settle(stale, new ThreadFact.Released()), (written, _) => written.Tour)));
      Assert.Equal(2, _store.Get(tour.Id)!.Threads.Count);
   }

   /// <summary>The change builds a plan with two stops of one ID, a bug that the store would otherwise keep as a tour that never loads again.</summary>
   [Fact]
   public void Write_that_breaks_a_rule_is_refused_as_a_rule_broken_stores_nothing_publishes_nothing_and_logs_one_error()
   {
      var tour = Create();
      var stored = File.ReadAllText(_store.PathOf(tour.Id));
      using var subscription = _events.Subscribe();
      var then = false;

      var result = _writer.Write(Unbound, tour.Id, edit => edit.Store(edit.Latest with { Plan = TourValues.Plan(TourValues.Pending("s1"), TourValues.Pending("s1")) }, written =>
      {
         then = true;
         return written.Tour;
      }));

      Assert.Equal(OperationErrorCode.TourRuleBroken, result.Error?.Code);
      Assert.Equal(
         $"set_tour_archived would have stored tour {tour.Id} with a broken rule, so lotsen stored nothing. plan.stops[1].id: another stop already has the ID 's1'. This is a fault in lotsen: report it with the server log.",
         result.Error!.Reason);
      Assert.Null(result.Error.Field);
      Assert.False(then);
      Assert.Equal(stored, File.ReadAllText(_store.PathOf(tour.Id)));
      Assert.False(subscription.Reader.TryRead(out _));
      Assert.Single(_logs.Entries, entry => entry.Level == LogLevel.Error && entry.Line.Contains("plan.stops[1].id", StringComparison.Ordinal));
   }

   [Fact]
   public void Tour_stays_readable_and_writable_after_a_write_that_breaks_a_rule()
   {
      var tour = Create();
      _writer.Write(Unbound, tour.Id, edit => edit.Store(edit.Latest with { Plan = TourValues.Plan(TourValues.Pending("")) }, written => written.Tour));

      var read = _writer.Read(tour.Id);
      var written = Publishing(tour.Id, "planned", latest => latest with { Plan = TourValues.Plan() });

      Assert.Equivalent(tour, read.Value, strict: true);
      Assert.Equal(["s1"], written.Plan!.Stops.Select(stop => stop.Id));
      Assert.Equal(["s1"], _writer.Read(tour.Id).Value!.Plan!.Stops.Select(stop => stop.Id));
   }

   [Theory]
   [InlineData("id", "id")]
   [InlineData("createdAt", "createdAt")]
   public void Write_that_changes_the_tours_id_or_creation_time_is_refused_as_a_rule_broken(string changed, string path)
   {
      var tour = Create();
      var other = TourId.TryParse("zzzzzzzzzzzzzzzz", out var id) ? id : throw new InvalidOperationException("The ID does not parse.");

      var result = _writer.Write(Unbound, tour.Id, edit => edit.Store(
         changed == "id" ? edit.Latest with { Id = other } : edit.Latest with { CreatedAt = edit.Latest.CreatedAt.AddDays(-1) },
         written => written.Tour));

      Assert.Equal(OperationErrorCode.TourRuleBroken, result.Error?.Code);
      Assert.Contains($" {path}: ", result.Error!.Reason, StringComparison.Ordinal);
      Assert.Equivalent(tour, _writer.Read(tour.Id).Value, strict: true);
      Assert.False(File.Exists(_store.PathOf(other)));
   }

   [Fact]
   public void Stored_file_that_breaks_a_rule_reads_as_unreadable_and_the_log_names_the_rule()
   {
      var tour = Create();
      var path = _store.PathOf(tour.Id);
      File.WriteAllText(path, File.ReadAllText(path).Replace("\"plan\":null", "\"plan\":{\"chapters\":[],\"stops\":[],\"fileMarks\":[]}", StringComparison.Ordinal));

      Assert.Equal(OperationErrorCode.TourUnreadable, _writer.Read(tour.Id).Error?.Code);
      Assert.Equal([tour.Id], _writer.List().Unreadable);
      Assert.Contains(_logs.Lines, line => line.Contains("tour.plan.chapters: a plan needs at least one chapter.", StringComparison.Ordinal));
   }

   // The passes of the UI, from the catalog's declarations, for writes that need no bound session and for writes to the bound session.
   private Access.Reviewer.Pass Unbound => Passes.Admit<Access.Reviewer.Pass>("set_tour_archived", Passes.Ui(_credentials, _sessions));

   private Access.ReviewerToSession.Pass ToSession => Passes.Admit<Access.ReviewerToSession.Pass>("send_answers", Passes.Ui(_credentials, _sessions));

   private static List<ServerEvent> Drain(EventBroadcaster.Subscription subscription)
   {
      List<ServerEvent> received = [];
      while (subscription.Reader.TryRead(out var next))
      {
         received.Add(next);
      }

      return received;
   }

   // For a test about the other events of a write, which a controlsChanged event follows whenever the write changes the tour's controls.
   private static List<ServerEvent> DrainBesidesControls(EventBroadcaster.Subscription subscription) =>
      [.. Drain(subscription).Where(received => received.Type != ServerEvents.ControlsChanged.Name)];

   private Tour Create() => _writer.Exclusive(locked => locked.Create(PullRequest, Familiarity.Some, PlanApproval.NotNeeded));

   private Tour Pinned(Tour tour) => Publishing(tour.Id, "pinned", latest => latest with { Pin = FirstPin });

   private static IEnumerable<RequestState> RequestStates(QuestionThread thread) =>
      ((QuestionThread.Sent)thread).Messages.OfType<ThreadMessage.Question>().SelectMany(question => question.Requests).Select(request => request.State);

   // Opens a session and binds it to the tour.
   private AgentSession Bind(TourId id)
   {
      var session = _sessions.TryOpen()!;
      _writer.Exclusive(locked => locked.BindNew(id, session.Recipient));
      return session;
   }

   // Fills the session's queue with notices that carry no nonce, so the session's next event finds the queue full.
   private void Fill(AgentSession session, TourId id) => _writer.Exclusive(locked =>
   {
      for (var i = 0; i < AgentSessions.EventCapacity; i++)
      {
         locked.Send(session.Recipient.TakenOver(id));
      }
   });

   private Recipient? Holder(TourId id) => _writer.Exclusive(locked => locked.Holder(id));

   // Stores the thread write and returns the ID of the thread it names.
   private string Threaded(TourId id, Func<TourEdit, OperationResult<ThreadWrite>> write) =>
      _writer.Write(Unbound, id, edit => edit.Store(write(edit), (_, written) => written.Thread!)).Value!.Id;

   // Returns an event that its type alone names, so a test sees its place among the events that the writer publishes itself.
   private static ServerEvent Marker(string type, TourId id) => ServerEvent.Of(new MarkerKind(type), new DeletedTour(id));

   // Writes the change and publishes an event of the given type with it.
   private Tour Publishing(TourId id, string type, Func<Tour, Tour> change) =>
      _writer.Write(Unbound, id, edit => edit.Store(change(edit.Latest), stored =>
      {
         stored.Publish(Marker(type, stored.Tour.Id));
         return stored.Tour;
      })).Value!;

   private sealed class MarkerKind : ServerEventKind<DeletedTour>
   {
      public MarkerKind(string type)
         : base(type, LotsenJsonContext.Default.DeletedTour, ProseView.None, tourScoped: false)
      {
      }
   }
}

/// <summary>A logger that runs a check when the writer logs a delivered probe, on the thread that delivered it.</summary>
internal sealed class DeliveryProbe : ILogger<TourWriter>
{
   private readonly Action _check;

   public DeliveryProbe(Action check)
   {
      _check = check;
   }

   public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

   public bool IsEnabled(LogLevel logLevel) => true;

   public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
   {
      if (formatter(state, exception).Contains("a probe", StringComparison.Ordinal))
      {
         _check();
      }
   }
}
