using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Lotsen.Tours;

namespace Lotsen.Operations;

/// <summary>What an operation's output becomes for a request that asks for prose as HTML: the output as it is, or the JSON that a renderer returns.</summary>
/// <remarks>
/// Every catalog entry and every event kind declares one, and <see cref="CheckFits"/> refuses <see cref="None"/> on an output that can hold prose and <see cref="Html{TOutput}"/> on one that cannot. An output can hold
/// prose when its JSON type reaches a stop's content, a thread or a review summary.
/// </remarks>
internal abstract class ProseView
{
   private static readonly Type[] ProseTypes = [typeof(StopContent), typeof(QuestionThread), typeof(ReviewSummary)];

   private ProseView()
   {
   }

   /// <summary>The view of an output that holds no prose, which every request receives as it is.</summary>
   public static ProseView None { get; } = new NoneView();

   /// <summary>Returns the view that renders an output of <typeparamref name="TOutput"/> with its prose as HTML.</summary>
   public static ProseView Html<TOutput>(Func<TOutput, JsonNode> render)
      where TOutput : class => new HtmlView<TOutput>(render);

   /// <summary>Returns the output's JSON with its prose as HTML, or null for <see cref="None"/>, whose output is written as it is.</summary>
   /// <param name="output">An output of the type that the view was checked against.</param>
   public abstract JsonNode? Render(object output);

   /// <summary>Checks the view against the output type of its operation, or the payload type of its event kind.</summary>
   /// <param name="owner">What declares the view, as the refusal names it, such as <c>Operation read_state</c>.</param>
   /// <param name="outputType">The JSON type that the view renders.</param>
   /// <exception cref="InvalidOperationException">The view is <see cref="None"/> on an output that can hold prose, or renders another type, or the output cannot hold prose.</exception>
   public void CheckFits(string owner, JsonTypeInfo outputType)
   {
      var holdsProse = HoldsProse(outputType, []);
      var fault = this switch
      {
         NoneView when holdsProse => "has no prose view, but its output can hold prose",
         NoneView => null,
         _ when !holdsProse => "has a prose view, but its output holds no prose",
         _ when RenderedType != outputType.Type => $"has the prose view of {RenderedType!.Name} on an output of {outputType.Type.Name}",
         _ => null,
      };
      if (fault is not null)
      {
         throw new InvalidOperationException($"{owner} {fault}.");
      }
   }

   // The output type that the view renders, or null for None.
   private protected abstract Type? RenderedType { get; }

   private static bool HoldsProse(JsonTypeInfo type, HashSet<Type> seen)
   {
      if (ProseTypes.Contains(type.Type))
      {
         return true;
      }

      if (!seen.Add(type.Type))
      {
         return false;
      }

      IEnumerable<Type> reached =
      [
         .. type.Properties.Select(property => property.PropertyType),
         .. type.ElementType is { } element ? [element] : Type.EmptyTypes,
         .. type.PolymorphismOptions?.DerivedTypes.Select(derived => derived.DerivedType) ?? [],
         .. Nullable.GetUnderlyingType(type.Type) is { } underlying ? [underlying] : Type.EmptyTypes,
      ];
      return reached.Any(next => HoldsProse(type.Options.GetTypeInfo(next), seen));
   }

   private sealed class NoneView : ProseView
   {
      private protected override Type? RenderedType => null;

      public override JsonNode? Render(object output) => null;
   }

   private sealed class HtmlView<TOutput> : ProseView
      where TOutput : class
   {
      private readonly Func<TOutput, JsonNode> _render;

      public HtmlView(Func<TOutput, JsonNode> render)
      {
         _render = render;
      }

      private protected override Type? RenderedType => typeof(TOutput);

      public override JsonNode? Render(object output) => _render((TOutput)output);
   }
}
