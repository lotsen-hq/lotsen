using System.Text;
using System.Text.Json.Nodes;
using Lotsen.Operations;
using static Lotsen.Tests.OperationCall;

namespace Lotsen.Tests;

/// <summary>
/// A pinned tour, bound to the harness's agent session, with a plan of an overview stop without content and an inspect stop whose content points at line 2 of <c>src/modified.txt</c> at the
/// head and has one check with a recommendation. The head also adds <see cref="LongPath"/>, whose text is <see cref="LongText"/>. The tests ask questions about it with the UI credential.
/// </summary>
public sealed class QuestionTourFixture : IAsyncLifetime
{
   public const string Headline = "Line two becomes a digit.";

   public const string How = "It replaces `two` with `2`.";

   public const string Check = "Is a digit the right spelling?";

   public const string Recommendation = "Keep the digit, which sorts first.";

   public const string LongPath = "src/long.txt";

   /// <summary>A line of exactly <see cref="ThreadOpener.MaxCodeAnchorLength"/> characters and an empty line, so the text of lines 1-2, with the line break between them, is one character longer.</summary>
   public static readonly string LongText = new string('a', ThreadOpener.MaxCodeAnchorLength) + "\n\n";

   internal PinnedTourFixture Tour { get; private set; } = null!;

   public string OverviewId { get; private set; } = "";

   public string InspectId { get; private set; } = "";

   /// <summary>The ID of the inspect stop's one check.</summary>
   public string CheckId { get; private set; } = "";

   public string UiCredential { get; private set; } = "";

   public async ValueTask InitializeAsync()
   {
      Tour = await PinnedTourFixture.StartAsync((LongPath, LongText));
      var plan = await Tour.Harness.AgentCallAsync(
         On("set_plan", Tour.TourId),
         """
         {
           "chapters": [{ "id": "a", "title": "A" }],
           "stops": [{ "title": "What changes", "kind": "overview", "chapter": "a" }, { "title": "The modified file", "kind": "inspect", "chapter": "a" }]
         }
         """);
      OverviewId = (string)plan["plan"]!["stops"]![0]!["id"]!;
      InspectId = (string)plan["plan"]!["stops"]![1]!["id"]!;
      var inspect = await Tour.Harness.AgentCallAsync(
         On("put_stop", Tour.TourId, stopId: InspectId),
         $$"""
         {
           "headline": "{{Headline}}",
           "how": "{{How}}",
           "checks": [{ "question": "{{Check}}", "recommendation": "{{Recommendation}}", "suggestions": [{ "text": "Keep it.", "type": "decision" }] }],
           "passages": [{ "side": "head", "path": "src/modified.txt", "startLine": 2, "endLine": 2, "role": "primary" }]
         }
         """);
      CheckId = (string)inspect["content"]!["checks"]![0]!["id"]!;
      UiCredential = await Tour.Harness.UiCredentialAsync();
   }

   public async ValueTask DisposeAsync() => await Tour.DisposeAsync();

   /// <summary>Asks a question with the UI credential.</summary>
   public Task<HttpResponseMessage> AskAsync(string body)
   {
      var request = new PreparedCall(On("ask_question", Tour.TourId), UiCredential);
      return Tour.Harness.SendAsync(request, body);
   }

   /// <summary>Asks a question with the UI credential, requires success and returns the new thread.</summary>
   public async Task<JsonNode> AskThreadAsync(string body)
   {
      using var response = await AskAsync(body);
      var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
      Assert.True(response.IsSuccessStatusCode, $"The ask returned {(int)response.StatusCode}: {text}");
      return SuccessBody.Output(text);
   }

   public async Task<JsonArray> ThreadsAsync() => (await Tour.Harness.CallAsync(On("read_state", Tour.TourId)))["threads"]!.AsArray();
}
