using System.Text.Json;
using System.Text.Json.Nodes;
using Lotsen.Json;
using Lotsen.Operations;
using Lotsen.Sessions;
using Lotsen.Tours;

namespace Lotsen.Tests;

/// <summary>Covers the invariants of a tour's threads through seeded random walks of commands and facts on <see cref="TourThreads"/>.</summary>
/// <remarks>
/// After each step the walk checks that the IDs are unique, that only a thread's last question waits, that only an asked thread holds an action, that every question has a request, that a
/// resolved request never changes, that a refused step builds no tour, and that the threads load again from their JSON.
/// </remarks>
[Trait("Category", "Threads")]
public sealed class ThreadWalkTests : IDisposable
{
   private const int Steps = 400;

   private static readonly ThreadAnchor[] Anchors =
   [
      new ThreadAnchor.WholeTour(),
      new ThreadAnchor.WholeStop("s1"),
      new ThreadAnchor.StopText("s1", "how", "Kept", 1),
      new ThreadAnchor.StopText("s1", "why", "Old", 1),
   ];

   private static readonly Stop.Written Replaced = new("s1", "Rounding", StopKind.Inspect, "c1", new StopContent("Old.", [], How: "Kept.", Why: "Old why."), 1, []);
   private static readonly Stop.Written Written = Replaced with
   {
      Content = new StopContent("New.", [], How: "Kept.", Why: "New why."),
      Version = 2,
      Earlier = [new StopVersion("Rounding", StopKind.Inspect, Replaced.Content, ThreadFixtures.At)],
   };

   private readonly AgentSessions _sessions = new();

   public void Dispose() => _sessions.Dispose();

   [Theory]
   [InlineData(1)]
   [InlineData(2)]
   [InlineData(3)]
   [InlineData(4)]
   [InlineData(5)]
   public void Thread_walk_keeps_every_invariant(int seed)
   {
      var random = new Random(seed);
      Recipient[] sessions = [_sessions.TryOpen()!.Recipient, _sessions.TryOpen()!.Recipient];
      var tour = SummaryTours.Empty() with { Plan = new Plan([new Chapter("c1", "Pricing")], [Written], []) };
      var resolved = new Dictionary<string, QuestionRequest>(StringComparer.Ordinal);
      var applied = 0;
      for (var step = 0; step < Steps; step++)
      {
         var now = ThreadFixtures.At.AddMinutes(step);
         var next = random.Next(10) == 0 ? TourThreads.Settle(tour, Fact(random)) : TourThreads.Apply(tour, Command(random, tour, sessions), now, TourIds.Random);
         if (next.Value is not { } write)
         {
            Assert.NotNull(next.Error);
            continue;
         }

         applied++;
         Assert.Same(tour.Threads, write.From.Threads);
         tour = write.Next;
         CheckInvariants(tour.Threads, resolved);
      }

      Assert.True(applied > Steps / 4, $"Only {applied} of {Steps} steps applied, so the walk covers too little.");
   }

   private static void CheckInvariants(TourThreads threads, Dictionary<string, QuestionRequest> resolved)
   {
      var ids = threads.SelectMany(IdsOf).ToList();
      Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());

      foreach (var sent in threads.OfType<QuestionThread.Sent>())
      {
         var questions = sent.Messages.OfType<ThreadMessage.Question>().ToList();
         Assert.All(questions, question => Assert.NotEmpty(question.Requests));
         Assert.All(questions.SkipLast(1), question => Assert.False(question.IsWaiting, $"Question {question.Id} of thread {sent.Id} waits, but it is not the last."));
         Assert.Equal(questions.LastOrDefault() is { IsWaiting: true } last ? last : null, sent.Waiting);
         foreach (var request in questions.SelectMany(question => question.Requests))
         {
            if (resolved.TryGetValue(request.Id, out var earlier))
            {
               Assert.Equal((earlier.State, earlier.SessionId, earlier.SentAt), (request.State, request.SessionId, request.SentAt));
            }
            else if (request.IsResolved)
            {
               resolved[request.Id] = request;
            }
         }
      }

      var json = JsonSerializer.SerializeToNode(threads, LotsenJsonContext.Default.TourThreads)!.AsArray();
      Assert.All(json, thread => Assert.True((string)thread!["kind"]! == "asked" || !thread.AsObject().ContainsKey("action"), $"A thread that is not asked holds an action: {thread}"));
      var loaded = JsonSerializer.Deserialize(json.ToJsonString(), LotsenJsonContext.Default.TourThreads)!;
      Assert.True(JsonNode.DeepEquals(json, JsonSerializer.SerializeToNode(loaded, LotsenJsonContext.Default.TourThreads)));
   }

   private static IEnumerable<string> IdsOf(QuestionThread thread)
   {
      yield return thread.Id;
      foreach (var message in (thread as QuestionThread.Sent)?.Messages ?? [])
      {
         yield return message.Id;
         foreach (var request in (message as ThreadMessage.Question)?.Requests ?? [])
         {
            yield return request.Id;
         }
      }
   }

   private static ThreadCommand Command(Random random, Tour tour, Recipient[] sessions)
   {
      var threadId = Pick(random, [.. tour.Threads.Select(thread => thread.Id), "missing"]);
      var questionId = Pick(random, [.. tour.Threads.OfType<QuestionThread.Sent>().SelectMany(thread => thread.Messages.OfType<ThreadMessage.Question>()).Select(question => question.Id), "missing"]);
      var session = Pick(random, sessions);
      var anchor = Pick(random, Anchors);
      return random.Next(15) switch
      {
         0 => random.Next(3) switch
         {
            0 when ActionRules.Admit(tour, new ActionRequest.Summarize(), "").Value is { } admitted => new ThreadCommand.Request(admitted, "Why?", null, session),
            1 => new ThreadCommand.Report(ReviewerDecision.IterationUpdated.For(2, ["s1"]), "", null, session, "tourId"),
            _ => new ThreadCommand.Ask(anchor, "Why?", null, session),
         },
         1 => new ThreadCommand.Start(anchor, "Look here.", session),
         2 => new ThreadCommand.SaveNote(anchor, "My words.", null),
         3 => new ThreadCommand.EditNote(threadId, random.Next(2) == 0 ? "My words." : "Other words."),
         4 => new ThreadCommand.DiscardNote(threadId),
         5 => new ThreadCommand.SendNotes([.. tour.Threads.OfType<QuestionThread.Note>().Select(note => note.Id).Where(_ => random.Next(2) == 0).DefaultIfEmpty(threadId)], session),
         6 => new ThreadCommand.FollowUp(threadId, "And?", null, session),
         7 => new ThreadCommand.Resend(questionId, session),
         8 => new ThreadCommand.Cancel(questionId),
         9 => new ThreadCommand.SetState(threadId, random.Next(2) == 0 ? ReviewerThreadState.Open : ReviewerThreadState.Resolved),
         10 => new ThreadCommand.AgentWrote(threadId, "More.", session),
         11 => new ThreadCommand.Reply(questionId, Pick(random, [ReplyResolution.Done, ReplyResolution.NeedsAnswer, ReplyResolution.Declined]), "Here.", session),
         12 => new ThreadCommand.Outdate(threadId, session),
         13 => random.Next(4) switch
         {
            0 => new ThreadCommand.Delete(threadId, session),
            1 => new ThreadCommand.Remove(threadId),
            _ => new ThreadCommand.MarkReceived(questionId, session),
         },
         _ => new ThreadCommand.MarkReceived(questionId, session),
      };
   }

   private static ThreadFact Fact(Random random) => random.Next(2) == 0 ? new ThreadFact.Released() : new ThreadFact.StopRevised(Replaced, Written);

   private static T Pick<T>(Random random, IReadOnlyList<T> items) => items[random.Next(items.Count)];
}
