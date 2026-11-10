using Lotsen.Sessions;
using Lotsen.Tours;

namespace Lotsen.Operations;

/// <summary>The kinds of caller that an <see cref="Access"/> declaration admits, as flags.</summary>
[Flags]
internal enum CallerKinds
{
   /// <summary>No kind.</summary>
   None = 0,

   /// <summary>The UI, a <see cref="Caller.Ui"/>.</summary>
   Ui = 1,

   /// <summary>Any agent session, a <see cref="Caller.Agent"/>.</summary>
   Agent = 2,

   /// <summary>The CLI, a <see cref="Caller.Cli"/>.</summary>
   Cli = 4,
}

/// <summary>Who may call an operation: the one declaration on each catalog entry, against which the endpoint admits the caller before it reads the input.</summary>
/// <remarks>
/// <para>
/// Admission turns the caller into a pass of the declaration's variant, and the operation's code receives the pass, never the caller. Each pass has a private constructor, and its one factory
/// takes the caller kind that the variant admits. A <see cref="Caller.Ui"/> or a <see cref="Caller.Agent"/> comes only from a check of its credential, so no code makes one of those passes
/// without the credential.
/// </para>
/// <para>
/// A <see cref="BoundPass"/> reaches only the writer overloads that check the tour's binding, and no <see cref="BoundPass"/> is an <see cref="UnboundPass"/>. So who may call an operation and
/// whether its writes need the tour's bound session come from one declaration.
/// </para>
/// </remarks>
internal abstract record Access
{
   private Access()
   {
   }

   /// <summary>The kinds of caller that the declaration admits.</summary>
   public abstract CallerKinds Admits { get; }

   /// <summary>Returns the kind of the caller, or <see cref="CallerKinds.None"/> for a kind that no declaration admits.</summary>
   public static CallerKinds KindOf(Caller caller) => caller switch
   {
      Caller.Ui => CallerKinds.Ui,
      Caller.Agent => CallerKinds.Agent,
      Caller.Cli => CallerKinds.Cli,
      _ => CallerKinds.None,
   };

   /// <summary>Returns the reason of the refusal of a caller that the declaration does not admit, as the refusal serves it.</summary>
   public abstract string Refusal(Caller caller);

   /// <summary>Returns why a write of the UI with this declaration's pass would be refused for the tour's binding, or null when it would not.</summary>
   /// <param name="tour">The tour that the write writes.</param>
   /// <param name="holder">The tour's bound session, or null when no session holds the tour.</param>
   public virtual OperationError? RefusesReviewer(TourId tour, Recipient? holder) => null;

   // Names the kinds as a refusal says them, such as "the UI or an agent session".
   private static string Named(CallerKinds kinds) => string.Join(" or ", new[]
   {
      (CallerKinds.Ui, "the UI"),
      (CallerKinds.Agent, "an agent session"),
      (CallerKinds.Cli, "the CLI"),
   }.Where(kind => kinds.HasFlag(kind.Item1)).Select(kind => kind.Item2));

   private static string Refused(CallerKinds admitted, string doing, Caller caller, string? why = null) => $"only {Named(admitted)} can {doing}, not {caller}.{(why is null ? "" : $" {why}")}";

   /// <summary>A declaration whose admission yields a <typeparamref name="TPass"/>.</summary>
   /// <typeparam name="TPass">The pass that the operation's code receives.</typeparam>
   public abstract record For<TPass> : Access
      where TPass : Pass
   {
      private protected For()
      {
      }

      /// <summary>Returns the pass of a caller that the declaration admits.</summary>
      /// <param name="caller">Who made the request.</param>
      /// <param name="operation">The operation that the declaration is on, whose name the pass carries.</param>
      /// <returns>
      /// The pass, or <see cref="OperationErrorCode.CallerNotAllowed"/> with <see cref="Refusal"/> for a caller outside <see cref="Admits"/>. The refusal names the <c>tourId</c> field when
      /// the operation's input has one.
      /// </returns>
      public OperationResult<TPass> Admit(Caller caller, Operation operation)
      {
         if ((Admits & KindOf(caller)) == CallerKinds.None)
         {
            return OperationError.CallerNotAllowed(Refusal(caller), operation.NamesTour ? (FieldPath?)Operation.TourIdProperty : null);
         }

         // Admits and Issue live on the same variant, so a variant whose Issue refuses a kind that it admits is a fault of the server.
         return OperationResult<TPass>.Of(Issue(caller, operation.Name) ?? throw new InvalidOperationException($"{GetType().Name} admits {caller} but issues it no pass."));
      }

      // Returns the variant's pass of an admitted caller, or null for a caller that the pass cannot carry.
      private protected abstract TPass? Issue(Caller admitted, string operation);
   }

   /// <summary>The UI alone, which speaks for the reviewer.</summary>
   /// <param name="Doing">What only the UI may do, as the refusal says it after "can", such as "cancel a question".</param>
   /// <param name="Why">The refusal's second sentence, which says why, or null for none.</param>
   public sealed record Reviewer(string Doing, string? Why = null) : For<Reviewer.Pass>
   {
      public override CallerKinds Admits => CallerKinds.Ui;

      public override string Refusal(Caller caller) => Refused(Admits, Doing, caller, Why);

      private protected override Pass? Issue(Caller admitted, string operation) => admitted is Caller.Ui ui ? Pass.Admit(ui, operation) : null;

      /// <summary>The pass of the UI.</summary>
      public new sealed class Pass : UnboundPass
      {
         private Pass(string operation)
            : base(operation)
         {
         }

         internal static Pass Admit(Caller.Ui ui, string operation)
         {
            ArgumentNullException.ThrowIfNull(ui);
            return new(operation);
         }
      }
   }

   /// <summary>The UI alone, and its writes need a tour with a bound session, which the write sends something to.</summary>
   /// <param name="Doing">What only the UI may do, as for <see cref="Reviewer"/>.</param>
   /// <param name="Why">The refusal's second sentence, or null for none.</param>
   /// <param name="Sent">What the bound session would receive, as <see cref="OperationError.NoSessionAttached"/> names it.</param>
   public sealed record ReviewerToSession(string Doing, string? Why, string Sent) : For<ReviewerToSession.Pass>
   {
      public override CallerKinds Admits => CallerKinds.Ui;

      public override string Refusal(Caller caller) => Refused(Admits, Doing, caller, Why);

      private protected override Pass? Issue(Caller admitted, string operation) => admitted is Caller.Ui ui ? Pass.Admit(ui, operation, Sent) : null;

      /// <summary>Returns the refusal that the pass's <see cref="Pass.Bind"/> gives while no session holds the tour, or null when one does.</summary>
      public override OperationError? RefusesReviewer(TourId tour, Recipient? holder) => holder is null ? OperationError.NoSessionAttached(tour, Sent) : null;

      /// <summary>The pass of the UI, whose writes name the tour's bound session.</summary>
      public new sealed class Pass : BoundPass
      {
         private readonly string _sent;

         private Pass(string operation, string sent)
            : base(operation)
         {
            _sent = sent;
         }

         internal static Pass Admit(Caller.Ui ui, string operation, string sent)
         {
            ArgumentNullException.ThrowIfNull(ui);
            return new(operation, sent);
         }

         internal override OperationResult<Recipient> Bind(TourId tour, Recipient? holder) => holder is null ? OperationError.NoSessionAttached(tour, _sent) : holder;
      }
   }

   /// <summary>Any agent session, whatever tour it holds. The operation decides what the session may act on, as a reply does per request.</summary>
   /// <param name="Doing">What only an agent session may do, as the refusal says it after "can", such as "attach a tour".</param>
   public sealed record AnySession(string Doing) : For<AnySession.Pass>
   {
      public override CallerKinds Admits => CallerKinds.Agent;

      public override string Refusal(Caller caller) => Refused(Admits, Doing, caller);

      private protected override Pass? Issue(Caller admitted, string operation) => admitted is Caller.Agent agent ? Pass.Admit(agent, operation) : null;

      /// <summary>The pass of one agent session.</summary>
      public new sealed class Pass : UnboundPass
      {
         private Pass(string operation, Recipient recipient)
            : base(operation)
         {
            Recipient = recipient;
         }

         /// <summary>The session that made the request. It may have ended since, as <see cref="Caller.Agent.Recipient"/> says.</summary>
         public Recipient Recipient { get; }

         internal static Pass Admit(Caller.Agent agent, string operation) => new(operation, agent.Recipient);
      }
   }

   /// <summary>The tour's bound session alone: any agent session is admitted, and its writes need it to hold the tour.</summary>
   /// <param name="Action">What only the bound session may do, as the refusals say it after "can", such as "write its plan and stops".</param>
   public sealed record BoundSession(string Action) : For<BoundSession.Pass>
   {
      public override CallerKinds Admits => CallerKinds.Agent;

      public override string Refusal(Caller caller) => $"only the agent session that a tour is attached to can {Action}, not {caller}.";

      private protected override Pass? Issue(Caller admitted, string operation) => admitted is Caller.Agent agent ? Pass.Admit(agent, operation, Action) : null;

      /// <summary>The pass of one agent session, whose writes need that session to be the tour's bound session.</summary>
      public new sealed class Pass : BoundPass
      {
         private readonly string _action;

         private Pass(string operation, Recipient recipient, string action)
            : base(operation)
         {
            Recipient = recipient;
            _action = action;
         }

         /// <summary>The session that made the request, which a check before the writer's lock may name. Under the lock, the edit's recipient is the one that <see cref="Bind"/> found.</summary>
         public Recipient Recipient { get; }

         internal static Pass Admit(Caller.Agent agent, string operation, string action) => new(operation, agent.Recipient, action);

         internal override OperationResult<Recipient> Bind(TourId tour, Recipient? holder) => holder switch
         {
            null => OperationError.AttachedToNoSession(tour, _action),
            _ when holder == Recipient => holder,
            _ => OperationError.AttachedToAnotherSession(tour, _action),
         };
      }
   }

   /// <summary>The kinds named, for an operation whose code needs no more than what the read shows each of them.</summary>
   /// <param name="Callers">The kinds that the declaration admits.</param>
   /// <param name="Doing">What only those kinds may do, as the refusal says it after "can", such as "read a tour".</param>
   public sealed record Open(CallerKinds Callers, string Doing) : For<Open.Pass>
   {
      public override CallerKinds Admits => Callers;

      public override string Refusal(Caller caller) => Refused(Admits, Doing, caller);

      private protected override Pass? Issue(Caller admitted, string operation) => Pass.Admit(admitted, operation);

      /// <summary>The pass of an admitted caller, which carries what a read shows it.</summary>
      public new sealed class Pass : UnboundPass
      {
         private Pass(string operation, Viewer viewer)
            : base(operation)
         {
            Viewer = viewer;
         }

         /// <summary>Who reads, which decides what a read shows.</summary>
         public Viewer Viewer { get; }

         internal static Pass? Admit(Caller caller, string operation) => caller switch
         {
            Caller.Ui => new(operation, new Viewer.Reviewer()),
            Caller.Agent agent => new(operation, new Viewer.Session(agent.Recipient)),
            Caller.Cli => new(operation, new Viewer.Cli()),
            _ => null,
         };
      }
   }

   /// <summary>The proof that the endpoint admitted a caller to one operation, which the operation's code receives in place of the caller.</summary>
   public abstract class Pass
   {
      private protected Pass(string operation)
      {
         Operation = operation;
      }

      /// <summary>The name of the operation that admitted the caller, which the writer's refusals of a broken rule and its hook point name.</summary>
      public string Operation { get; }

      /// <summary>The revision of the tour as the call's last write stored it, or as it found it when the write stored nothing, or null before the call wrote.</summary>
      /// <remarks>The operation's output shows the tour at this revision, so the wire sends it beside the output.</remarks>
      public long? Revision { get; private set; }

      /// <summary>Takes the revision of the tour that the call's write showed. Only the tour writer calls it, under its lock.</summary>
      internal void Showed(long revision) => Revision = revision;
   }

   /// <summary>A pass whose writes need no bound session, so the writer hands their changes a <see cref="TourEdit"/>.</summary>
   public abstract class UnboundPass : Pass
   {
      private protected UnboundPass(string operation)
         : base(operation)
      {
      }
   }

   /// <summary>A pass whose writes name the tour's bound session, so the writer hands their changes a <see cref="SessionEdit"/>.</summary>
   public abstract class BoundPass : Pass
   {
      private protected BoundPass(string operation)
         : base(operation)
      {
      }

      /// <summary>Returns the session that a write with this pass names.</summary>
      /// <remarks>The writer calls it under its lock, inside the store's update, with the holder that the session ledger gives, so a binding that moves before the store refuses the write.</remarks>
      /// <param name="tour">The tour that the write writes.</param>
      /// <param name="holder">The tour's bound session, or null when no session holds the tour.</param>
      /// <returns>The session, or <see cref="OperationErrorCode.NoSessionAttached"/> or <see cref="OperationErrorCode.AttachedToAnotherSession"/>.</returns>
      internal abstract OperationResult<Recipient> Bind(TourId tour, Recipient? holder);
   }
}
