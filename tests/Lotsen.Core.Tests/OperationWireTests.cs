using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lotsen.Client;
using Lotsen.Hosting;
using Lotsen.Operations;
using Lotsen.Server;
using Lotsen.Tours;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lotsen.Tests;

/// <summary>Covers the wire of every operation: the request that the wire builds, the endpoint's answer to a request it cannot route, and the status of each error code.</summary>
[Trait("Category", "Api")]
public sealed class OperationWireTests
{
   public static TheoryData<string> Operations => [.. OperationCatalog.All.Select(operation => operation.Name)];

   public static TheoryData<string> ErrorCodes => [.. OperationErrorCode.All.Select(code => code.Name)];

   /// <summary>The reviewed record of each error code's status. A new code gets its kind where it is declared, and its line here.</summary>
   [Fact]
   public void Each_error_code_has_the_status_of_its_kind()
   {
      Assert.Equal(
         [
            ("invalidInput", 400), ("tourNotFound", 404), ("stopNotFound", 404), ("tourUnreadable", 500), ("tourNotPinned", 409), ("fileNotFound", 404),
            ("repositoryUnavailable", 502), ("callerNotAllowed", 403), ("attachedToAnotherSession", 409), ("noSessionAttached", 409), ("questionNotFound", 404),
            ("threadNotFound", 404), ("requestNotPending", 409), ("questionAnswered", 409), ("questionWaiting", 409), ("questionFollowedUp", 409), ("wrongNonce", 409),
            ("noteSent", 409), ("threadIsNote", 409), ("checkNotFound", 404), ("answerNotFound", 404), ("batchNotFound", 404), ("planAwaitsApproval", 409),
            ("noAnswersToSend", 409), ("iterationNotAvailable", 409), ("tourChanged", 409), ("tourRuleBroken", 500), ("operationNotFound", 404),
            ("buildMismatch", 409), ("serverExiting", 503), ("sessionUnknown", 401),
         ],
         OperationErrorCode.All.Select(code => (code.Name, (int)OperationWire.StatusOf(code.Kind))));
   }

   [Fact]
   public void Error_code_is_written_and_read_by_its_name_and_any_other_value_is_refused()
   {
      var error = OperationError.Invalid("text", "the text is empty.");
      var json = JsonSerializer.Serialize(error, Json.LotsenJsonContext.Default.OperationError);

      Assert.Contains("\"code\":\"invalidInput\"", json, StringComparison.Ordinal);
      Assert.Same(OperationErrorCode.InvalidInput, JsonSerializer.Deserialize(json, Json.LotsenJsonContext.Default.OperationError)!.Code);
      Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json.Replace("invalidInput", "InvalidInput", StringComparison.Ordinal), Json.LotsenJsonContext.Default.OperationError));
   }

   [Theory]
   [MemberData(nameof(Operations))]
   public async Task Request_posts_the_input_to_the_operations_path_and_decodes_to_the_same_input(string name)
   {
      var operation = OperationCatalog.Get(name);

      foreach (var sample in SchemaSamples.Of(operation.InputSchema()))
      {
         using var request = OperationWire.Request(operation, JsonSerializer.SerializeToUtf8Bytes(sample, Json.LotsenJsonContext.Default.JsonNode));
         var body = await request.Content!.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
         var decoded = operation.Decoder.Decode(body, NullLogger.Instance);

         Assert.Equal((HttpMethod.Post, $"/api/ops/{name}", "application/json", "utf-8"), (request.Method, request.RequestUri!.OriginalString, request.Content.Headers.ContentType!.MediaType, request.Content.Headers.ContentType.CharSet));
         Assert.True(decoded.Value is not null, decoded.Error?.Reason);
         var again = JsonSerializer.SerializeToNode(decoded.Value, operation.InputType);
         Assert.True(JsonNode.DeepEquals(sample, again), $"{sample.ToJsonString()} decoded to {again?.ToJsonString()}");
      }
   }

   [Fact]
   public async Task Typed_request_carries_the_input_of_its_type()
   {
      using var request = OperationWire.Request(OperationCatalog.OpenUi, new OpenUiInput());

      Assert.Equal("{}", await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
   }

   [Theory]
   [InlineData("no_such_operation")]
   [InlineData("Read_State")]
   public async Task Request_that_names_no_operation_is_not_found(string name)
   {
      await using var harness = await ServerHarness.StartAsync();
      var request = ServerHarness.Request(HttpMethod.Post, $"api/ops/{name}", harness.UiCredential);
      request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

      using var response = await harness.SendAsync(request);

      var error = await ThreadCalls.ErrorAsync(response, HttpStatusCode.NotFound, "operationNotFound");
      Assert.Contains($"'{name}'", (string)error["reason"]!, StringComparison.Ordinal);
   }

   [Theory]
   [InlineData("")]
   [InlineData("null")]
   [InlineData("\"tourId\"")]
   [InlineData("{ \"tourId\": ")]
   public async Task Body_that_is_not_one_json_object_is_invalid_input(string body)
   {
      await using var harness = await ServerHarness.StartAsync();

      using var response = await harness.SendJsonAsync("list_tours", body);

      var error = await ThreadCalls.ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Null(error["field"]);
   }

   [Fact]
   public async Task Get_of_an_operations_path_is_not_an_operation_call()
   {
      await using var harness = await ServerHarness.StartAsync();

      using var response = await harness.SendAsync(ServerHarness.Request(HttpMethod.Get, "api/ops/list_tours", harness.UiCredential));

      Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
   }

   [Fact]
   public async Task Server_client_posts_the_arguments_to_the_operations_path()
   {
      await using var server = new RecordingHttpServer(answer: _ => new HttpAnswer(200, """{ "output": { "text": "new\n" } }"""));
      using var client = ClientOf(server, out var dataDirectory);
      using var input = JsonDocument.Parse("""{ "tourId": "aaaaaaaaaaaaaaaa", "side": "head", "path": "src/added.txt" }""");

      var response = await client.InvokeAsync(OperationCatalog.Get("read_file_text"), Properties(input), TestContext.Current.CancellationToken);

      Assert.IsType<WireAnswer<string>.Output>(response);
      Assert.Contains(server.Requests, request => request.StartsWith("POST /api/ops/read_file_text ", StringComparison.Ordinal));
      DirectoryTree.Delete(dataDirectory.Path);
   }

   [Theory]
   [MemberData(nameof(ErrorCodes))]
   public async Task Server_client_reads_the_refusal_of_each_error_code(string name)
   {
      var code = OperationErrorCode.All.Single(code => code.Name == name);
      var refusal = JsonSerializer.Serialize(new OperationError(code, "the problem."), Json.LotsenJsonContext.Default.OperationError);
      await using var server = new RecordingHttpServer(answer: _ => new HttpAnswer((int)OperationWire.StatusOf(code.Kind), refusal));
      using var client = ClientOf(server, out var dataDirectory);
      using var input = JsonDocument.Parse("""{ "tourId": "aaaaaaaaaaaaaaaa" }""");

      var response = await client.InvokeAsync(OperationCatalog.Get("read_state"), Properties(input), TestContext.Current.CancellationToken);

      Assert.Same(code, JsonSerializer.Deserialize(refusal, Json.LotsenJsonContext.Default.OperationError)!.Code);
      Assert.Equal("the problem.", Assert.IsType<WireAnswer<string>.Refused>(response).Error.Reason);
      DirectoryTree.Delete(dataDirectory.Path);
   }

   [Theory]
   [InlineData(404, true)]
   [InlineData(409, false)]
   [InlineData(500, false)]
   public async Task Server_client_reads_a_refusal_only_when_its_status_is_its_codes(int status, bool fromLotsen)
   {
      const string refusal = """{ "code": "tourNotFound", "problem": "no tour has the ID 'aaaaaaaaaaaaaaaa'.", "field": "tourId" }""";
      await using var server = new RecordingHttpServer(answer: _ => new HttpAnswer(status, refusal));
      using var client = ClientOf(server, out var dataDirectory);
      using var input = JsonDocument.Parse("""{ "tourId": "aaaaaaaaaaaaaaaa" }""");

      var response = await client.InvokeAsync(OperationCatalog.Get("read_state"), Properties(input), TestContext.Current.CancellationToken);

      if (fromLotsen)
      {
         Assert.Equal("tourId: no tour has the ID 'aaaaaaaaaaaaaaaa'.", Assert.IsType<WireAnswer<string>.Refused>(response).Error.Reason);
      }
      else
      {
         Assert.StartsWith($"The lotsen server answered read_state with {status} ", Assert.IsType<WireAnswer<string>.Unexpected>(response).Description, StringComparison.Ordinal);
      }
      DirectoryTree.Delete(dataDirectory.Path);
   }

   private static ServerClient ClientOf(RecordingHttpServer server, out DataDirectory dataDirectory)
   {
      dataDirectory = new DataDirectory(Directory.CreateTempSubdirectory("lotsen-tests-").FullName);
      var state = new ServerState(Environment.ProcessId, "127.0.0.1", server.BaseUrl.Port, "token");
      StateFile.Write(dataDirectory, state);
      return new ServerClient(state);
   }

   private static IEnumerable<KeyValuePair<string, JsonElement>> Properties(JsonDocument input) =>
      input.RootElement.EnumerateObject().Select(property => KeyValuePair.Create(property.Name, property.Value));
}
