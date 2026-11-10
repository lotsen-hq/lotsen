namespace Lotsen.Operations;

/// <summary>Registers the tour operations with the server's services.</summary>
internal static class TourOperationServices
{
   /// <summary>Adds the tour writer with its sweep of session ends, and every feature class of the tour operations, each as a singleton.</summary>
   public static IServiceCollection AddTourOperations(this IServiceCollection services) => services
      .AddSingleton<TourWriter>()
      .AddHostedService<SessionEndSweep>()
      .AddSingleton<IterationChecks>()
      .AddSingleton<TourPins>()
      .AddSingleton<SessionOperations>()
      .AddSingleton<PlanOperations>()
      .AddSingleton<QuestionOperations>()
      .AddSingleton<ReplyOperations>()
      .AddSingleton<NoteOperations>()
      .AddSingleton<ThreadOperations>()
      .AddSingleton<AnswerOperations>()
      .AddSingleton<ReadingOperations>()
      .AddSingleton<ReviewerDecisions>()
      .AddSingleton<TourDeletion>();
}

/// <summary>Runs the tour writer's sweep of session ends for the server's lifetime.</summary>
/// <remarks>It runs outside <c>BackgroundJobs</c>, so <c>BackgroundJobs.WhenIdleAsync</c> does not wait for it while a session is bound.</remarks>
internal sealed class SessionEndSweep : BackgroundService
{
   private readonly TourWriter _writer;

   public SessionEndSweep(TourWriter writer)
   {
      _writer = writer;
   }

   protected override Task ExecuteAsync(CancellationToken stoppingToken) => _writer.RunEndSweepAsync(stoppingToken);
}
