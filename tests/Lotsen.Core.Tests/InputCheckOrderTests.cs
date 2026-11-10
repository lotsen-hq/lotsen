using System.Net;
using static Lotsen.Tests.OperationCall;
using static Lotsen.Tests.ThreadCalls;

namespace Lotsen.Tests;

/// <summary>Covers the order of the refusals of a UI-only write whose input is invalid on its own: the caller's role first, then the input, then the tour and its binding.</summary>
[Trait("Category", "Roles")]
public sealed class InputCheckOrderTests : IAsyncLifetime
{
   private const string MissingTour = "aaaaaaaaaaaaaaaa";

   private ServerHarness _harness = null!;
   private ThreadCalls _calls = null!;

   public async ValueTask InitializeAsync()
   {
      _harness = await ServerHarness.StartAsync();
      _calls = new ThreadCalls(_harness);
   }

   public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

   /// <summary>The tour has no bound session and no plan, so the binding check and the stop lookup would each refuse the write too.</summary>
   [Theory]
   [InlineData("send_notes", """{ "threadIds": [] }""", "threadIds")]
   [InlineData("answer_check", """{ "text": "   " }""", "text")]
   [InlineData("edit_note", """{ "text": "   " }""", "text")]
   [InlineData("record_reading_progress", """{ "stopId": "s1", "summary": true }""", "stopId")]
   public async Task Invalid_input_from_the_ui_is_refused_before_the_tour_and_its_binding_are_checked(string operation, string body, string field)
   {
      using var response = await _calls.SendAsync(await _calls.UiAsync(Call(operation)), body);

      var error = await ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal(field, (string)error["field"]!);
   }

   [Theory]
   [InlineData("send_notes", """{ "threadIds": [] }""")]
   [InlineData("answer_check", """{ "text": "   " }""")]
   [InlineData("edit_note", """{ "text": "   " }""")]
   [InlineData("record_reading_progress", """{ "stopId": "s1", "summary": true }""")]
   public async Task Invalid_input_from_the_cli_is_refused_for_the_callers_role_first(string operation, string body)
   {
      using var response = await _harness.CliSendJsonAsync(Call(operation), body);

      await ErrorAsync(response, HttpStatusCode.Forbidden, "callerNotAllowed");
   }

   // Returns the operation's call on a tour that the refusal under test needs: an unbound tour without a plan, or a missing tour for edit_note and record_reading_progress, so the input check must win over the lookup.
   private OperationCall Call(string operation)
   {
      var unbound = (string)_harness.CreateUnboundTour(CreateBody)["id"]!;
      return operation switch
      {
         "send_notes" => On("send_notes", unbound),
         "answer_check" => On("answer_check", unbound, stopId: "s1", checkId: "c1"),
         "edit_note" => On("edit_note", MissingTour, threadId: "t1"),
         _ => On("record_reading_progress", MissingTour),
      };
   }
}
