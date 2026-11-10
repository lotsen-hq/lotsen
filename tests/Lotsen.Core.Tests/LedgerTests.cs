using Lotsen.Sessions;
using Lotsen.Tours;

namespace Lotsen.Tests;

/// <summary>Covers the session ledger on its own: one binding per tour, take-over, bindings whose session ended, confirmation, and the ends that wait for settlement.</summary>
[Trait("Category", "Roles")]
public sealed class LedgerTests : IDisposable
{
   private readonly AgentSessions _sessions = new();
   private readonly AgentSessions.Ledger _ledger;
   private readonly TourId _tour = TourId.New();

   public LedgerTests()
   {
      _ledger = _sessions.ClaimLedger();
   }

   public void Dispose() => _sessions.Dispose();

   [Fact]
   public void Second_claim_of_the_ledger_is_refused() => Assert.Throws<InvalidOperationException>(() => _sessions.ClaimLedger());

   [Fact]
   public void Attach_binds_a_tour_that_no_session_holds_and_a_second_attach_changes_nothing()
   {
      var session = Open();

      var first = _ledger.Attach(_tour, session.Recipient, takeOver: false);
      var again = _ledger.Attach(_tour, session.Recipient, takeOver: true);

      Assert.Equal(new AttachOutcome.Bound(Unconfirmed(session), Previous: null), first);
      Assert.Equal(new AttachOutcome.Unchanged(Unconfirmed(session)), again);
      Assert.Same(session.Recipient, _ledger.Holder(_tour));
   }

   [Fact]
   public void Two_attaches_in_sequence_bind_the_first_session_and_refuse_the_second()
   {
      var first = Open();
      var second = Open();

      _ledger.Attach(_tour, first.Recipient, takeOver: false);
      var refused = _ledger.Attach(_tour, second.Recipient, takeOver: false);

      Assert.IsType<AttachOutcome.HeldByAnother>(refused);
      Assert.Same(first.Recipient, _ledger.Holder(_tour));
   }

   [Fact]
   public void Take_over_moves_the_binding_and_names_the_session_that_lost_it()
   {
      var first = Open();
      var second = Open();
      _ledger.Attach(_tour, first.Recipient, takeOver: false);

      var taken = _ledger.Attach(_tour, second.Recipient, takeOver: true);

      Assert.Equal(new AttachOutcome.Bound(Unconfirmed(second), new Released(_tour, first.Recipient, FromEnded: false)), taken);
      Assert.Same(second.Recipient, _ledger.Holder(_tour));
   }

   /// <summary>The session that lost the tour holds no binding when it ends, so its end leaves nothing to settle.</summary>
   [Fact]
   public void Take_over_then_end_leaves_no_end_to_settle()
   {
      var first = Open();
      var second = Open();
      _ledger.Attach(_tour, first.Recipient, takeOver: false);

      _ledger.Attach(_tour, second.Recipient, takeOver: true);
      first.End();

      Assert.Null(_ledger.TakeEnd());
      Assert.Same(second.Recipient, _ledger.Holder(_tour));
   }

   /// <summary>The binding of a session that ended reads as none before its end is settled, and the take-over that replaces it settles it, so the end releases no tour.</summary>
   [Fact]
   public void End_then_take_over_settles_the_dead_binding_once_and_tells_no_ended_session()
   {
      var first = Open();
      var second = Open();
      _ledger.Attach(_tour, first.Recipient, takeOver: false);

      first.End();
      var heldAfterEnd = (_ledger.Holder(_tour), _ledger.BindingOf(_tour), _ledger.Snapshot().Count);
      var taken = _ledger.Attach(_tour, second.Recipient, takeOver: true);

      Assert.Equal(((Recipient?)null, (TourBinding?)null, 0), heldAfterEnd);
      Assert.Equal(new AttachOutcome.Bound(Unconfirmed(second), new Released(_tour, first.Recipient, FromEnded: true)), taken);
      var end = _ledger.TakeEnd();
      Assert.Equal(first.Id, end?.SessionId);
      Assert.Empty(end!.Released);
      Assert.Null(_ledger.TakeEnd());
   }

   [Fact]
   public void End_then_attach_without_take_over_binds_the_tour()
   {
      var first = Open();
      var second = Open();
      _ledger.Attach(_tour, first.Recipient, takeOver: false);

      first.End();
      var attached = _ledger.Attach(_tour, second.Recipient, takeOver: false);

      Assert.Equal(new AttachOutcome.Bound(Unconfirmed(second), new Released(_tour, first.Recipient, FromEnded: true)), attached);
   }

   /// <summary>Another session binds one of the ended session's two tours before the end is settled, so the end releases only the other one.</summary>
   [Fact]
   public void End_releases_only_the_tours_that_its_session_still_holds()
   {
      var first = Open();
      var second = Open();
      var other = TourId.New();
      _ledger.Attach(_tour, first.Recipient, takeOver: false);
      _ledger.Attach(other, first.Recipient, takeOver: false);

      first.End();
      _ledger.Attach(other, second.Recipient, takeOver: false);

      var end = _ledger.TakeEnd();
      Assert.Equal(first.Id, end?.SessionId);
      Assert.Equal([_tour], end!.Released);
      Assert.Null(_ledger.Holder(_tour));
      Assert.Same(second.Recipient, _ledger.Holder(other));
   }

   [Fact]
   public void Ended_session_neither_attaches_nor_binds_a_new_tour()
   {
      var session = Open();
      session.End();

      Assert.IsType<AttachOutcome.SessionEnded>(_ledger.Attach(_tour, session.Recipient, takeOver: true));
      Assert.Null(_ledger.BindNew(_tour, session.Recipient));
      Assert.Null(_ledger.Holder(_tour));
   }

   [Fact]
   public void Forget_drops_the_binding_and_the_end_releases_no_forgotten_tour()
   {
      var session = Open();
      _ledger.BindNew(_tour, session.Recipient);

      _ledger.Forget(_tour);
      session.End();

      Assert.Null(_ledger.Holder(_tour));
      Assert.Null(_ledger.TakeEnd());
   }

   /// <summary>The confirmation and the binds take the registry's lock, so the first ack changes every tour bound before it, and a tour bound after it starts confirmed.</summary>
   [Fact]
   public void Confirm_changes_the_bindings_of_the_session_once_and_a_later_bind_is_confirmed()
   {
      var session = Open();
      var later = TourId.New();
      _ledger.BindNew(_tour, session.Recipient);
      var nonce = Nonce(session);

      var wrong = _ledger.Confirm(session.Recipient, new string('a', 26));
      var first = _ledger.Confirm(session.Recipient, nonce);
      var repeat = _ledger.Confirm(session.Recipient, nonce);
      var bound = _ledger.BindNew(later, session.Recipient);

      var confirmed = new TourBinding(session.Id, AttachmentLevel.Confirmed);
      Assert.IsType<AckOutcome.WrongNonce>(wrong);
      var changed = Assert.IsType<AckOutcome.Confirmed>(first);
      Assert.Equal(session.Id, changed.SessionId);
      Assert.Equal([new BindingChange(_tour, confirmed)], changed.Changed);
      Assert.Empty(Assert.IsType<AckOutcome.Confirmed>(repeat).Changed);
      Assert.Equal(confirmed, bound);
      Assert.Equal(confirmed, _ledger.BindingOf(_tour));
   }

   [Fact]
   public void Confirm_of_an_ended_session_is_refused()
   {
      var session = Open();
      var nonce = Nonce(session);
      session.End();

      Assert.IsType<AckOutcome.SessionEnded>(_ledger.Confirm(session.Recipient, nonce));
   }

   [Fact]
   public void Probe_goes_only_to_a_session_without_a_nonce()
   {
      var session = Open();

      var probed = _ledger.Deliver(session.Recipient.Probe(_tour));
      var again = _ledger.Deliver(session.Recipient.Probe(_tour));

      Assert.True(probed);
      Assert.False(again);
      Assert.True(session.Events.TryRead(out _));
      Assert.False(session.Events.TryRead(out _));
   }

   /// <summary>The end of a session that holds no binding leaves nothing to settle, so only the end of the bound session waits for <see cref="AgentSessions.Ledger.TakeEnd"/>.</summary>
   [Fact]
   public async Task Wait_for_an_end_completes_once_a_session_with_a_binding_ends()
   {
      var unbound = Open();
      var bound = Open();
      _ledger.BindNew(_tour, bound.Recipient);

      var waiting = _ledger.WaitForEndAsync(TestContext.Current.CancellationToken).AsTask();
      unbound.End();
      var queuedByUnbound = _ledger.TakeEnd();
      bound.End();

      Assert.Null(queuedByUnbound);
      Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
      Assert.Equal(bound.Id, _ledger.TakeEnd()?.SessionId);
   }

   [Fact]
   public async Task Wait_for_an_end_returns_false_once_the_registry_is_disposed()
   {
      var waiting = _ledger.WaitForEndAsync(TestContext.Current.CancellationToken).AsTask();

      _sessions.Dispose();

      Assert.False(await waiting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
   }

   private static TourBinding Unconfirmed(AgentSession session) => new(session.Id, AttachmentLevel.Unconfirmed);

   private AgentSession Open() => _sessions.TryOpen()!;

   // Delivers a probe and returns its nonce.
   private string Nonce(AgentSession session)
   {
      Assert.True(_ledger.Deliver(session.Recipient.Probe(_tour)));
      Assert.True(session.Events.TryRead(out var probe));
      return (string)System.Text.Json.Nodes.JsonNode.Parse(probe.Data)!["nonce"]!;
   }
}
