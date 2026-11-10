using Lotsen.Sessions;
using Lotsen.Tours;

namespace Lotsen.Operations;

/// <summary>Whether each control of the UI can act on a tour now, at one revision of the tour: the tour's offers with the binding refusals of their operations.</summary>
/// <remarks>
/// <para>
/// The UI shows a control as these say, and derives no rule of its own. A control that the tour's state closes stays closed with that refusal. A control that the state leaves open closes when
/// its operation's <see cref="Access.RefusesReviewer"/> refuses the tour's binding, so the reviewer reads the state's reason before the binding's.
/// </para>
/// <para>
/// So a control is open exactly when its operation takes a call with valid input. When the state and the binding both refuse, the reasons differ: the control shows the state's, and the
/// operation answers with the binding's, because the tour writer checks the binding first.
/// </para>
/// </remarks>
/// <param name="TourId">The tour.</param>
/// <param name="Revision">The tour's revision that the offers come from.</param>
/// <param name="Offers">The offers, with the binding refusals.</param>
internal sealed record TourControls(TourId TourId, long Revision, TourOffers Offers)
{
   private static readonly Access AskAccess = OperationCatalog.Get("ask_question").Access;
   private static readonly Access AnswersAccess = OperationCatalog.Get("send_answers").Access;
   private static readonly Access LevelAccess = OperationCatalog.Get("change_familiarity").Access;
   private static readonly Access ApprovalAccess = OperationCatalog.Get("approve_plan").Access;
   private static readonly Access UpdateAccess = OperationCatalog.Get("update_iteration").Access;
   private static readonly Access ActionAccess = OperationCatalog.Get("request_tour_action").Access;
   private static readonly Access ResendAccess = OperationCatalog.Get("resend_question").Access;
   private static readonly Access FollowUpAccess = OperationCatalog.Get("ask_follow_up").Access;
   private static readonly Access CancelAccess = OperationCatalog.Get("cancel_question").Access;
   private static readonly Access SendNotesAccess = OperationCatalog.Get("send_notes").Access;

   /// <summary>Returns the controls of the tour as stored, for the UI.</summary>
   /// <param name="tour">The stored tour.</param>
   /// <param name="holder">The tour's bound session, or null when no session holds the tour.</param>
   public static TourControls Of(Tour tour, Recipient? holder)
   {
      var offers = TourOffers.Of(tour, holder?.Id);
      Offer Bound(Offer offer, Access access) => offer is Offer.Open ? Offer.Of(access.RefusesReviewer(tour.Id, holder)) : offer;
      return new TourControls(tour.Id, tour.Revision, offers with
      {
         Ask = Bound(offers.Ask, AskAccess),
         SendAnswers = Bound(offers.SendAnswers, AnswersAccess),
         Level = offers.Level with { Change = Bound(offers.Level.Change, LevelAccess) },
         ApprovePlan = Bound(offers.ApprovePlan, ApprovalAccess),
         UpdateIteration = Bound(offers.UpdateIteration, UpdateAccess),
         Replan = Bound(offers.Replan, ActionAccess),
         Reorder = Bound(offers.Reorder, ActionAccess),
         Stops = offers.Stops.ToDictionary(entry => entry.Key, entry => new StopOffers(Bound(entry.Value.Explain, ActionAccess), Bound(entry.Value.Revise, ActionAccess)), StringComparer.Ordinal),
         Summarize = Bound(offers.Summarize, ActionAccess),
         FollowUps = offers.FollowUps.ToDictionary(entry => entry.Key, entry => Bound(entry.Value, ActionAccess), StringComparer.Ordinal),
         Threads = offers.Threads.ToDictionary(
            entry => entry.Key,
            entry => new ThreadOffers(
               Bound(entry.Value.Resend, ResendAccess), Bound(entry.Value.FollowUp, FollowUpAccess), Bound(entry.Value.Cancel, CancelAccess), Bound(entry.Value.SendNote, SendNotesAccess)),
            StringComparer.Ordinal),
      });
   }
}
