using System.Text.Json.Serialization;
using Lotsen.Json;

namespace Lotsen.Tours;

/// <summary>A side of a pinned tour's change, which names one of its two commits.</summary>
[JsonConverter(typeof(StrictEnumConverter<FileSide>))]
internal enum FileSide
{
   /// <summary>The side of the merge base, where the change starts.</summary>
   Base,

   /// <summary>The side of the head, where the change ends.</summary>
   Head,
}
