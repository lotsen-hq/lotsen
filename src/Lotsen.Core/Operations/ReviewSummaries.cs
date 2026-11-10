using System.ComponentModel;
using System.Text.Json.Serialization;
using Lotsen.Tours;

namespace Lotsen.Operations;

/// <summary>The input of <c>read_summary</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReadSummaryInput(
   [property: Description(OperationInputs.TourIdDescription)]
   TourId TourId);
