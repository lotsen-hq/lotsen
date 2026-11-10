using System.Text.Json.Nodes;

namespace Lotsen.Tests;

/// <summary>Reads parts of a tour's JSON as the API serves it.</summary>
internal static class TourJson
{
   /// <summary>Returns the IDs of the plan's stops that the reviewer skipped, in plan order.</summary>
   public static string[] SkippedIn(JsonNode tour) =>
      [.. (tour["plan"]?["stops"]?.AsArray() ?? []).Where(stop => (bool?)stop!["skipped"] == true).Select(stop => (string)stop!["id"]!)];

   /// <summary>Returns the earlier versions of the plan's stop with this ID.</summary>
   public static JsonArray EarlierOf(JsonNode tour, string stopId) =>
      tour["plan"]!["stops"]!.AsArray().Single(stop => (string)stop!["id"]! == stopId)!["earlier"]!.AsArray();
}
