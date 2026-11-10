using System.ComponentModel;
using System.Text.Json.Serialization;
using Lotsen.Hosting;
using Lotsen.Json;
using Lotsen.Pinning;
using Lotsen.Platforms;
using Lotsen.Sessions;
using Lotsen.Tours;

namespace Lotsen.Operations;

/// <summary>The input of <c>create_tour</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateTourInput(
   [property: Description("The web URL of the pull request to tour, as the browser shows it on the pull request's page.")]
   string PrUrl,
   [property: Description("How well the reviewer knows the code: familiar, some or new. It guides how much each stop explains.")]
   Familiarity Familiarity,
   [property: Description("true when the reviewer asks for plan first: the plan then waits for the reviewer's approval, put_stop is refused until the reviewer approves it in the UI, and set_plan still revises it. Omit it otherwise, and the plan does not wait.")]
   bool PlanFirst = false);

/// <summary>The input of <c>read_state</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReadStateInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId);

/// <summary>The input of <c>pin_tour</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PinTourInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId);

/// <summary>A tour as the list shows it, without its plan.</summary>
/// <param name="Id">The tour's ID.</param>
/// <param name="Target">What the tour reviews.</param>
/// <param name="Familiarity">How well the reviewer knows the code.</param>
/// <param name="CreatedAt">When the tour was created.</param>
/// <param name="UpdatedAt">When the tour was last stored.</param>
/// <param name="Pin">The tour's pin state.</param>
/// <param name="StopCount">The number of stops in the plan, 0 until a plan is set.</param>
/// <param name="Label">The target's short name, as <see cref="PullRequestPlatforms.Label"/> returns it.</param>
/// <param name="State">The tour's state, as <see cref="TourStates.Of"/> derives it.</param>
/// <param name="PullRequestState">The pull request's state as the first pin or the last iteration check learned it, which flags the tour when completed or abandoned.</param>
/// <param name="Revision">The tour's revision that the summary shows.</param>
/// <param name="Binding">The agent session bound to the tour, or null when none is. JSON leaves it out when null.</param>
/// <param name="PlanApproval">Whether the plan waits for the reviewer's approval, as <see cref="Tour.PlanApproval"/> says. JSON leaves it out when not needed.</param>
internal sealed record TourSummary(
   TourId Id,
   TourTarget Target,
   Familiarity Familiarity,
   DateTimeOffset CreatedAt,
   DateTimeOffset UpdatedAt,
   TourPin Pin,
   int StopCount,
   string Label,
   TourState State,
   PullRequestState PullRequestState,
   long Revision,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TourBinding? Binding = null,
   [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] PlanApproval PlanApproval = PlanApproval.NotNeeded)
{
   /// <summary>Returns the summary of the tour, with the binding that the session ledger holds for it.</summary>
   public static TourSummary Of(Tour tour, PullRequestPlatforms platforms, TourBinding? binding) =>
      new(tour.Id, tour.Target, tour.Familiarity, tour.CreatedAt, tour.UpdatedAt, tour.Pin, tour.Plan?.Stops.Count ?? 0, platforms.Label(tour.Target), TourStates.Of(tour), tour.PullRequestState,
         tour.Revision, binding, tour.PlanApproval);
}

/// <summary>The output of <c>list_tours</c>.</summary>
/// <param name="Tours">Every readable tour, oldest first.</param>
internal sealed record TourList(IReadOnlyList<TourSummary> Tours);

/// <summary>The input of <c>find_tours</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record FindToursInput(
   [property: Description("The web URL of a pull request, in any form that create_tour accepts, to find only that pull request's tours. Omit it to find every tour.")]
   string? PrUrl = null);

/// <summary>The output of <c>find_tours</c>.</summary>
/// <param name="Tours">The readable tours that the input selects, newest first.</param>
internal sealed record FoundTours(IReadOnlyList<FoundTour> Tours);

/// <summary>A tour as <c>find_tours</c> shows it to an agent session, without its plan.</summary>
/// <param name="Id">The tour's ID.</param>
/// <param name="Target">What the tour reviews.</param>
/// <param name="Label">The target's short name, as <see cref="PullRequestPlatforms.Label"/> returns it.</param>
/// <param name="Familiarity">How well the reviewer knows the code.</param>
/// <param name="CreatedAt">When the tour was created.</param>
/// <param name="StopCount">The number of stops in the plan, 0 until a plan is set.</param>
/// <param name="Pin">The tour's pin state.</param>
/// <param name="State">The tour's state, as <see cref="TourStates.Of"/> derives it.</param>
/// <param name="PullRequestState">The pull request's state as the first pin or the last iteration check learned it.</param>
/// <param name="Attachment">Which session the tour is attached to, as the calling session sees it.</param>
internal sealed record FoundTour(
   TourId Id,
   TourTarget Target,
   string Label,
   Familiarity Familiarity,
   DateTimeOffset CreatedAt,
   int StopCount,
   TourPin Pin,
   TourState State,
   PullRequestState PullRequestState,
   TourAttachment Attachment)
{
   /// <summary>Returns the row of a tour for the calling session, with the binding that the session ledger holds for the tour.</summary>
   public static FoundTour Of(Tour tour, PullRequestPlatforms platforms, TourBinding? binding, Recipient caller)
   {
      var attachment = binding is null ? TourAttachment.None : binding.SessionId == caller.Id ? TourAttachment.ThisSession : TourAttachment.OtherSession;
      return new(tour.Id, tour.Target, platforms.Label(tour.Target), tour.Familiarity, tour.CreatedAt, tour.Plan?.Stops.Count ?? 0, tour.Pin, TourStates.Of(tour), tour.PullRequestState,
         attachment);
   }
}

/// <summary>Which session a tour is attached to, as one agent session sees it, without the other session's ID.</summary>
[JsonConverter(typeof(StrictEnumConverter<TourAttachment>))]
internal enum TourAttachment
{
   /// <summary>A tour attached to the calling session.</summary>
   ThisSession,

   /// <summary>A tour attached to another session, which <c>attach_tour</c> moves to the calling session only with <c>takeOver</c>.</summary>
   OtherSession,

   /// <summary>A tour attached to no session.</summary>
   None,
}

/// <summary>Creates, pins, lists, finds and reads tours: <c>create_tour</c>, <c>pin_tour</c>, <c>list_tours</c>, <c>find_tours</c> and <c>read_state</c>, with the pin job and its resume on the server's start.</summary>
/// <remarks>
/// A tour's pin runs as a background job, which stores the pin when it ends. A job that the server's stop cancels stores nothing, so its tour stays pinning until <see cref="ResumePins"/> starts the
/// pin again on the next start. A job whose pin could not be stored leaves its tour pinning without a job, and <c>pin_tour</c> starts such a pin again.
/// </remarks>
internal sealed class TourPins
{
   private const string UnexpectedPinFailure = "lotsen could not pin the tour because of an unexpected error, which the server's log describes. Pin it again.";

   // The running pin job of each tour, which only the writer's write and Exclusive bodies read and change, so a pinning tour without a job can be pinned again. A job releases only its own
   // entry, so a job that ends late cannot release a newer job's entry.
   private readonly Dictionary<TourId, object> _runningPins = [];

   private readonly TourWriter _writer;
   private readonly IterationLifecycle _lifecycle;
   private readonly BackgroundJobs _jobs;
   private readonly IterationScheduler _scheduler;
   private readonly PullRequestPlatforms _platforms;
   private readonly ILogger<TourPins> _logger;

   public TourPins(TourWriter writer, IterationLifecycle lifecycle, BackgroundJobs jobs, IterationScheduler scheduler, PullRequestPlatforms platforms, ILogger<TourPins> logger)
   {
      _writer = writer;
      _lifecycle = lifecycle;
      _jobs = jobs;
      _scheduler = scheduler;
      _platforms = platforms;
      _logger = logger;
   }

   /// <summary>A test hook that each pin job awaits after the pin and before it stores the result, or null for none.</summary>
   internal Func<TourId, Task>? BeforePinStored { get; set; }

   /// <summary>A test hook that each pin job awaits once it has stored its result or failed to, or null for none.</summary>
   internal Func<TourId, Task>? AfterPinStored { get; set; }

   /// <summary>A test hook that each pin job calls as its last step, or null for none.</summary>
   internal Action<TourId>? AfterPinJob { get; set; }

   /// <summary>Creates a tour and returns it, bound to the calling session unless that session has ended.</summary>
   /// <remarks>The bound session receives <see cref="SessionEvents.Probe"/>, as on <c>attach_tour</c>.</remarks>
   public OperationResult<Tour> CreateTour(Access.AnySession.Pass pass, CreateTourInput input)
   {
      var recognized = Recognize(input.PrUrl);
      if (recognized.Value is not { } target)
      {
         return recognized.Error!;
      }

      return _writer.Exclusive(pass, locked =>
      {
         var tour = locked.Create(target, input.Familiarity, input.PlanFirst ? PlanApproval.Awaited : PlanApproval.NotNeeded);
         var binding = locked.BindNew(tour.Id, pass.Recipient);
         if (binding is not null)
         {
            locked.Send(pass.Recipient.Probe(tour.Id));
         }

         locked.Publish(ServerEvents.ForTourCreated(TourSummary.Of(tour, _platforms, binding)).At(tour.Revision));
         StartPin(tour);
         return AgentView.Of(tour);
      });
   }

   /// <summary>Starts the pin of a tour whose pin failed, or that pins without a job, and returns the tour.</summary>
   public OperationResult<Tour> PinTour(Access.AnySession.Pass pass, PinTourInput input) =>
      _writer.Write(pass, input.TourId, edit =>
      {
         var tour = edit.Latest;
         switch (tour.Pin)
         {
            case TourPin.Failed:
               return edit.Store(tour with { Pin = new TourPin.Pinning() }, stored =>
               {
                  stored.Publish(ServerEvents.ForTourPinned(stored.Tour));
                  StartPin(stored.Tour);
                  return AgentView.Of(stored.Tour);
               });
            case TourPin.Pinned pinned:
               return OperationError.Invalid(
                  "tourId",
                  $"tour {tour.Id} is already pinned to head {pinned.Head} of iteration {pinned.Iteration}. Only a tour whose pin failed, or stopped without a result, can be pinned again.");
            default:
               var orphaned = !_runningPins.ContainsKey(tour.Id);
               return edit.Store(tour, stored =>
               {
                  if (orphaned)
                  {
                     StartPin(stored.Tour);
                  }

                  return AgentView.Of(stored.Tour);
               });
         }
      });

   /// <summary>Starts the pin of every stored tour whose pin is in progress, as one left by a stopped server.</summary>
   /// <remarks>Call it once when the server starts, before any client can reach it, so no tour gets two pins.</remarks>
   public void ResumePins()
   {
      TourListing listing;
      try
      {
         listing = _writer.List();
      }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
      {
         // A failed listing must not stop the server from starting.
         _logger.LogWarning("The tours could not be listed, so no pin starts again: {Reason}", exception.Message);
         return;
      }

      _writer.Exclusive(_ =>
      {
         foreach (var tour in listing.Tours.Where(tour => tour.Pin is TourPin.Pinning))
         {
            _logger.LogInformation("Tour {TourId} was pinning when the server stopped. Its pin starts again.", tour.Id.Value);
            StartPin(tour);
         }
      });
   }

   /// <summary>Returns the tour as <see cref="AgentView.For"/> shows it to the viewer.</summary>
   /// <remarks>A read by the reviewer opens the tour for the reviewer, so it nudges an iteration check of a pinned tour, which it does not wait for.</remarks>
   public OperationResult<Tour> ReadState(Viewer viewer, ReadStateInput input)
   {
      var result = _writer.Read(input.TourId);
      if (result.Value is not { } tour)
      {
         return result;
      }

      if (viewer is Viewer.Reviewer && tour.Pin is TourPin.Pinned)
      {
         _scheduler.Nudge(tour.Id, Nudge.ReviewerRead);
      }

      return AgentView.For(viewer, tour);
   }

   public OperationResult<TourList> ListTours()
   {
      var tours = ReadableTours();
      return _writer.Exclusive(locked =>
      {
         var bindings = locked.Snapshot();
         return new TourList([.. tours.Select(tour => TourSummary.Of(tour, _platforms, bindings.GetValueOrDefault(tour.Id)))]);
      });
   }

   /// <summary>Returns the readable tours, newest first, each with its attachment as the calling session sees it.</summary>
   /// <returns>Every tour, or only the tours of the input's pull request by <see cref="PullRequestPlatforms.IsSamePullRequest"/>. A pull request URL that no platform recognizes is refused.</returns>
   public OperationResult<FoundTours> FindTours(Access.AnySession.Pass pass, FindToursInput input)
   {
      PullRequestTarget? target = null;
      if (input.PrUrl is { } prUrl)
      {
         var recognized = Recognize(prUrl);
         if (recognized.Error is { } error)
         {
            return error;
         }

         target = recognized.Value;
      }

      var tours = ReadableTours();
      return _writer.Exclusive(pass, locked =>
      {
         var bindings = locked.Snapshot();
         return new FoundTours([.. tours.Reverse()
            .Where(tour => target is null || _platforms.IsSamePullRequest(tour.Target, target))
            .Select(tour => FoundTour.Of(tour, _platforms, bindings.GetValueOrDefault(tour.Id), pass.Recipient))]);
      });
   }

   // Returns the target of an input's prUrl, or the refusal that names the URL forms that the platforms accept.
   private OperationResult<PullRequestTarget> Recognize(string prUrl) =>
      _platforms.Recognize(prUrl) is { } target ? target : OperationError.Invalid("prUrl", $"'{prUrl}' is not a pull request URL that lotsen recognizes. Expected {_platforms.ExpectedForms}.");

   // Returns the stored tours that could be read, oldest first, and logs each tour that could not be read.
   private IReadOnlyList<Tour> ReadableTours()
   {
      var listing = _writer.List();
      foreach (var id in listing.Unreadable.Concat(listing.OtherSchema))
      {
         _logger.LogWarning("Tour {TourId} could not be read and is left out of the tours that lotsen lists.", id.Value);
      }

      return listing.Tours;
   }

   // Call it in a body of the writer's write or Exclusive.
   private void StartPin(Tour tour)
   {
      var job = new object();
      _runningPins[tour.Id] = job;
      _jobs.Run(async cancellationToken =>
      {
         var storing = false;
         async Task StoreAsync(TourPin pin, PullRequestState? state)
         {
            storing = true;
            if (BeforePinStored is { } beforePinStored)
            {
               await beforePinStored(tour.Id);
            }

            StorePin(tour.Id, pin, state, job);
            if (AfterPinStored is { } afterPinStored)
            {
               await afterPinStored(tour.Id);
            }
         }

         try
         {
            // The lifecycle keeps the pin's clone in use until the store returns, so a prune between the two keeps the clone.
            await _lifecycle.PinAsync(tour.Id, tour.Target, StoreAsync, cancellationToken);
         }
         catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
         {
            throw;
         }
         catch (Exception exception) when (!storing)
         {
            _logger.LogError(exception, "Tour {TourId} could not be pinned.", tour.Id.Value);
            await StoreAsync(new TourPin.Failed(UnexpectedPinFailure), null);
         }
         finally
         {
            // A job that ends before StorePin, as when the server stops, releases its entry here.
            _writer.Exclusive(_ => ReleasePin(tour.Id, job));
            AfterPinJob?.Invoke(tour.Id);
         }
      });
   }

   // Stores the pin, with the pull request's state when the platform answered, only over a pin in progress, so a pin can never replace the result of another. The job's entry is released in the
   // same Exclusive body, so a pin_tour that follows sees the stored result and the released entry together.
   private void StorePin(TourId id, TourPin pin, PullRequestState? state, object job) => _writer.Exclusive(locked =>
   {
      ReleasePin(id, job);
      OperationResult<Tour> stored;
      try
      {
         stored = locked.Write(id, edit => edit.Latest.Pin is TourPin.Pinning
            ? edit.Store(edit.Latest with { Pin = pin, PullRequestState = state ?? edit.Latest.PullRequestState }, written =>
            {
               written.Publish(ServerEvents.ForTourPinned(written.Tour));
               return written.Tour;
            })
            : edit.Store(edit.Latest, written => written.Tour));
      }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
      {
         _logger.LogWarning("The pin of tour {TourId} could not be stored, so the tour stays pinning until it is pinned again: {Reason}", id.Value, exception.Message);
         return;
      }

      if (stored.Error?.Code == OperationErrorCode.TourUnreadable)
      {
         _logger.LogWarning("Tour {TourId} could not be read, so its pin was not stored.", id.Value);
      }
   });

   // Call it in a body of the writer's Exclusive.
   private void ReleasePin(TourId id, object job)
   {
      if (_runningPins.TryGetValue(id, out var running) && ReferenceEquals(running, job))
      {
         _runningPins.Remove(id);
      }
   }
}
