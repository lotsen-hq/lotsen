using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Lotsen.Operations;

/// <summary>One kind of event on the event stream: its name, the JSON type of its payload and its prose view, which <see cref="ServerEvents"/> declares once.</summary>
/// <remarks>Only the UI reads the event stream, so each payload is sent as its prose view renders it, with the HTML of its prose.</remarks>
internal abstract class ServerEventKind
{
   private protected ServerEventKind(string name, JsonTypeInfo payload, ProseView prose, bool tourScoped)
   {
      prose.CheckFits($"Event {name}", payload);
      Name = name;
      Payload = payload;
      Prose = prose;
      IsTourScoped = tourScoped;
   }

   /// <summary>The event's type name on the stream, in camelCase.</summary>
   public string Name { get; }

   /// <summary>The JSON type of the payload.</summary>
   public JsonTypeInfo Payload { get; }

   /// <summary>What the payload becomes for the UI.</summary>
   public ProseView Prose { get; }

   /// <summary>Whether each event of the kind is about one tour, so its payload carries the tour's revision as <see cref="ServerEvent.RevisionProperty"/>.</summary>
   public bool IsTourScoped { get; }

   /// <summary>Returns a new JSON Schema of the payload as the UI receives it, with the HTML of its prose and, for a tour-scoped kind, the revision.</summary>
   public JsonNode PayloadSchema() => WithRevision(UiContract.Export(Payload, UiContract.Flow.Received));

   /// <summary>Returns the schema of the payload with the revision that a tour-scoped kind's events add, or the schema itself for any other kind.</summary>
   /// <param name="payload">The payload's schema, inline or a reference to its definition.</param>
   public JsonNode WithRevision(JsonNode payload) => !IsTourScoped
      ? payload
      : new JsonObject
      {
         ["allOf"] = new JsonArray(
            payload,
            new JsonObject
            {
               ["type"] = "object",
               ["properties"] = new JsonObject { [ServerEvent.RevisionProperty] = new JsonObject { ["type"] = "integer", ["description"] = "The tour's revision that the event shows." } },
               ["required"] = new JsonArray(ServerEvent.RevisionProperty),
            }),
      };
}

/// <summary>An event kind whose payload is a <typeparamref name="TPayload"/>, so an event is sent only under the name that declares its payload's type.</summary>
/// <remarks>Only a subclass can build a kind. <see cref="ServerEvents"/> declares each of its kinds through a private subclass and lists every one in <see cref="ServerEvents.All"/>.</remarks>
internal abstract class ServerEventKind<TPayload> : ServerEventKind
   where TPayload : class
{
   private readonly JsonTypeInfo<TPayload> _payload;

   /// <exception cref="InvalidOperationException">The prose view does not fit the payload type.</exception>
   private protected ServerEventKind(string name, JsonTypeInfo<TPayload> payload, ProseView prose, bool tourScoped)
      : base(name, payload, prose, tourScoped)
   {
      _payload = payload;
   }

   /// <summary>Returns the payload's JSON text as its prose view renders it.</summary>
   public string Write(TPayload payload) => Prose.Render(payload)?.ToJsonString() ?? JsonSerializer.Serialize(payload, _payload);
}
