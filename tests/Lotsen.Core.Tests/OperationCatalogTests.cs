using System.Text.Json.Nodes;
using Lotsen.Json;
using Lotsen.Operations;
using Lotsen.Prose;
using Lotsen.Tours;

namespace Lotsen.Tests;

/// <summary>Covers the operation catalog and the JSON Schemas it exports for the future MCP tools, and the event kinds listed beside it.</summary>
[Trait("Category", "Api")]
public sealed class OperationCatalogTests
{
   [Fact]
   public void Catalog_names_each_operation_and_its_path()
   {
      Assert.Equal(
         [
            "create_tour",
            "attach_tour",
            "set_plan",
            "put_stop",
            "read_state",
            "read_summary",
            "write_synthesis",
            "ask_question",
            "request_tour_action",
            "ask_follow_up",
            "resend_question",
            "cancel_question",
            "set_thread_state",
            "remove_thread",
            "save_note",
            "edit_note",
            "discard_note",
            "send_notes",
            "answer_check",
            "set_answer_type",
            "clear_answer",
            "send_answers",
            "fetch_answers",
            "fetch_question",
            "reply",
            "start_thread",
            "add_thread_message",
            "outdate_thread",
            "delete_thread",
            "ack",
            "pin_tour",
            "find_tours",
            "list_tours",
            "list_changed_files",
            "read_file_text",
            "diff_file",
            "list_directory",
            "mark_file_reviewed",
            "clear_file_reviewed",
            "set_stop_skipped",
            "set_stop_done",
            "record_reading_progress",
            "set_tour_archived",
            "delete_tour",
            "prune_clones",
            "change_familiarity",
            "approve_plan",
            "check_iteration",
            "update_iteration",
            "read_preferences",
            "write_preference",
            "open_ui",
            "create_ui_link",
         ],
         OperationCatalog.All.Select(operation => operation.Name));
      Assert.All(OperationCatalog.All, operation => Assert.False(string.IsNullOrWhiteSpace(operation.Description)));
      Assert.All(OperationCatalog.All, operation => Assert.Equal($"/api/ops/{operation.Name}", OperationWire.PathOf(operation)));
      Assert.All(OperationCatalog.All, operation => Assert.Same(operation, OperationCatalog.ByName[operation.Name]));
      Assert.False(OperationCatalog.ByName.ContainsKey("Read_State"));
      Assert.Same(OperationCatalog.OpenUi, OperationCatalog.Get("open_ui"));
   }

   [Fact]
   public void Only_the_file_reads_answer_the_ui_without_the_tours_controls()
   {
      Assert.Equal(["list_changed_files", "read_file_text", "diff_file", "list_directory"], OperationCatalog.All.Where(operation => !operation.CarriesControls).Select(operation => operation.Name));
   }

   /// <summary>The reviewed record of which outputs the UI receives with their prose as HTML. An output that holds a stop, a thread or a review summary must render it.</summary>
   [Fact]
   public void Catalog_renders_html_for_each_output_that_can_hold_prose()
   {
      Assert.Equal(
         ["create_tour", "attach_tour", "set_plan", "put_stop", "read_state", "read_summary", "ask_question", "request_tour_action", "ask_follow_up", "resend_question", "cancel_question",
          "set_thread_state", "save_note", "edit_note", "send_notes", "fetch_question", "reply", "start_thread", "add_thread_message", "outdate_thread", "pin_tour", "change_familiarity",
          "approve_plan", "update_iteration"],
         OperationCatalog.All.Where(operation => operation.Prose != ProseView.None).Select(operation => operation.Name));
   }

   [Fact]
   public void Prose_view_that_does_not_fit_the_output_fails_the_definition()
   {
      var none = Assert.Throws<InvalidOperationException>(() => Define(LotsenJsonContext.Default.Tour, ProseView.None));
      var holdsNoProse = Assert.Throws<InvalidOperationException>(() => Define(LotsenJsonContext.Default.DeletedTour, ProseView.Html<DeletedTour>((_, _) => new JsonObject())));
      var otherType = Assert.Throws<InvalidOperationException>(() => Define(LotsenJsonContext.Default.Tour, ProseView.Html<Stop>(ProseHtml.Of)));

      Assert.Contains("has no prose view, but its output can hold prose", none.Message, StringComparison.Ordinal);
      Assert.Contains("has a prose view, but its output holds no prose", holdsNoProse.Message, StringComparison.Ordinal);
      Assert.Contains("has the prose view of Stop on an output of Tour", otherType.Message, StringComparison.Ordinal);
   }

   [Fact]
   public void Event_whose_payload_can_hold_prose_is_refused_without_the_mention_links_of_its_tour()
   {
      var payload = new StopPutEvent(TourValues.Id, TourValues.Now, TourValues.Pending("s1"));

      var refused = Assert.Throws<InvalidOperationException>(() => ServerEvent.Of(ServerEvents.StopPut, payload));

      Assert.Contains("stopPut", refused.Message, StringComparison.Ordinal);
   }

   /// <summary>The exporter gives a defaulted enum property no schema of its own, so the export describes it as it does a nullable one.</summary>
   [Fact]
   public void Exported_schemas_type_a_defaulted_enum_with_its_values_and_description()
   {
      var diff = OperationCatalog.Get("diff_file").InputSchema();
      var text = OperationCatalog.Get("read_file_text").InputSchema();

      Assert.Equal(["withhold", "include"], Property(diff, "large")["enum"]!.AsArray().Select(n => (string)n!));
      Assert.Equal(["compare", "ignore"], Property(diff, "whitespace")["enum"]!.AsArray().Select(n => (string)n!));
      Assert.Equal(("string", "withhold"), ((string)Property(text, "large")["type"]!, (string)Property(text, "large")["default"]!));
      Assert.False(string.IsNullOrWhiteSpace((string?)Property(text, "large")["description"]));
   }

   [Fact]
   public void Exported_schemas_type_integers_as_integer_only()
   {
      var integerTypes = OperationCatalog.All
         .SelectMany(operation => new[] { operation.InputSchema(), operation.OutputSchema() })
         .SelectMany(Descendants)
         .Select(node => node["type"])
         .Where(type => type is not null && type.ToJsonString().Contains("\"integer\"", StringComparison.Ordinal))
         .ToList();

      Assert.NotEmpty(integerTypes);
      Assert.All(integerTypes, type => Assert.DoesNotContain("\"string\"", type!.ToJsonString(), StringComparison.Ordinal));
      var line = Property(Items(Property(OperationCatalog.Get("set_plan").InputSchema(), "stops")), "anchorHint");
      Assert.Equal("""["integer","null"]""", Property(OneOfObject(line), "line")["type"]!.ToJsonString());
   }

   [Fact]
   public void Exported_input_schemas_mark_required_properties_and_refuse_others()
   {
      var createTour = OperationCatalog.Get("create_tour").InputSchema();
      var setPlan = OperationCatalog.Get("set_plan").InputSchema();
      var stop = Items(Property(setPlan, "stops"));

      Assert.Equal(["prUrl", "familiarity"], Required(createTour));
      Assert.Equal(["tourId", "chapters", "stops"], Required(setPlan));
      Assert.Equal(["id", "title"], Required(Items(Property(setPlan, "chapters"))));
      Assert.Equal(["title", "kind", "chapter"], Required(stop));
      Assert.Equal(["tourId"], Required(OperationCatalog.Get("read_state").InputSchema()));
      Assert.Equal(["tourId"], Required(OperationCatalog.Get("read_summary").InputSchema()));
      Assert.Equal(["tourId", "text", "basedOn"], Required(OperationCatalog.Get("write_synthesis").InputSchema()));
      Assert.Equal(["tourId"], Required(OperationCatalog.Get("pin_tour").InputSchema()));
      var fileMark = Items(Property(setPlan, "fileMarks"));
      Assert.Equal(["path", "mark", "reason"], Required(fileMark));
      Assert.All([createTour, setPlan, stop, fileMark], schema => Assert.False((bool)schema["additionalProperties"]!));

      var putStop = OperationCatalog.Get("put_stop").InputSchema();
      var passage = Items(Property(putStop, "passages"));
      Assert.Equal(["tourId", "stopId", "headline"], Required(putStop));
      Assert.Equal(["side", "path", "startLine", "endLine", "role"], Required(passage));
      Assert.All([putStop, passage], schema => Assert.False((bool)schema["additionalProperties"]!));
   }

   [Fact]
   public void Exported_schema_of_open_ui_offers_its_tour_id_as_an_optional_string_with_no_default()
   {
      var schema = OperationCatalog.Get("open_ui").InputSchema();
      var tourId = Property(schema, "tourId").AsObject();

      Assert.Equal("string", tourId["type"]!.GetValue<string>());
      Assert.False(tourId.ContainsKey("default"), tourId.ToJsonString());
      Assert.Empty(Required(schema));
   }

   [Fact]
   public void Exported_schemas_list_enum_names_and_the_tour_id_format()
   {
      var setPlan = OperationCatalog.Get("set_plan").InputSchema();
      var kind = Property(Items(Property(setPlan, "stops")), "kind");

      Assert.Equal(["familiar", "some", "new"], Property(OperationCatalog.Get("create_tour").InputSchema(), "familiarity")["enum"]!.AsArray().Select(n => (string)n!));
      Assert.Equal(["overview", "context", "inspect", "risk", "question"], kind["enum"]!.AsArray().Select(n => (string)n!));
      Assert.False(string.IsNullOrWhiteSpace((string?)kind["description"]));
      var putStop = OperationCatalog.Get("put_stop").InputSchema();
      Assert.Equal(["overview", "context", "inspect", "risk", "question", null], Property(putStop, "kind")["enum"]!.AsArray().Select(n => (string?)n));
      Assert.Equal(["inline", "side", null], Property(putStop, "placement")["enum"]!.AsArray().Select(n => (string?)n));
      Assert.Equal("""["string","null"]""", Property(putStop, "kind")["type"]!.ToJsonString());
      Assert.False(string.IsNullOrWhiteSpace((string?)Property(putStop, "kind")["description"]));
      Assert.Equal(["primary", "related"], Property(Items(Property(putStop, "passages")), "role")["enum"]!.AsArray().Select(n => (string)n!));
      var mark = Property(Items(Property(setPlan, "fileMarks")), "mark");
      Assert.Equal(["mechanical", "generated"], mark["enum"]!.AsArray().Select(n => (string)n!));
      Assert.False(string.IsNullOrWhiteSpace((string?)mark["description"]));
      Assert.Equal("string", (string)Property(setPlan, "tourId")["type"]!);
      Assert.Equal("^[a-z2-7]{16}$", (string)Property(setPlan, "tourId")["pattern"]!);
      Assert.Matches((string)Property(setPlan, "tourId")["pattern"]!, Tours.TourId.New().Value);
      Assert.False(string.IsNullOrWhiteSpace((string?)Property(setPlan, "tourId")["description"]));
   }

   [Fact]
   public async Task Catalog_over_http_matches_the_in_process_export()
   {
      await using var harness = await ServerHarness.StartAsync();

      var listed = await OperationsAsync(harness);

      Assert.Equal(OperationCatalog.All.Count, listed.Count);
      foreach (var (operation, entry) in OperationCatalog.All.Zip(listed))
      {
         Assert.Equal(operation.Name, (string)entry!["name"]!);
         Assert.Equal(operation.Description, (string)entry["description"]!);
         Assert.Equal(["name", "description", "inputSchema", "outputSchema"], entry.AsObject().Select(property => property.Key));
         Assert.True(JsonNode.DeepEquals(operation.InputSchema(), entry["inputSchema"]));
         Assert.True(JsonNode.DeepEquals(operation.OutputSchema(), entry["outputSchema"]));
      }
   }

   [Fact]
   public async Task Catalog_over_http_lists_each_event_kind_with_its_payload_schema()
   {
      await using var harness = await ServerHarness.StartAsync();

      var listed = await ListingAsync(harness, "events");

      Assert.Equal(ServerEvents.All.Select(kind => kind.Name), listed.Select(entry => (string)entry!["name"]!));
      Assert.All(ServerEvents.All.Zip(listed), pair => Assert.True(JsonNode.DeepEquals(pair.First.PayloadSchema(), pair.Second!["payloadSchema"])));
   }

   [Fact]
   public void Payload_schema_requires_the_html_of_a_written_stop()
   {
      var stop = Property(ServerEvents.StopPut.PayloadSchema()["allOf"]![0]!, "stop");
      var written = stop["anyOf"]!.AsArray().Single(variant => (string?)variant!["properties"]!["stage"]!["const"] == "written")!;

      Assert.Contains("html", Required(Property(written, "content")));
      Assert.Contains("html", Property(written, "content")["properties"]!.AsObject().Select(property => property.Key));
   }

   [Fact]
   public void Event_kind_whose_payload_can_hold_prose_needs_its_html_view()
   {
      var none = Assert.Throws<InvalidOperationException>(() => new UnviewedKind());

      Assert.Equal("Event test has no prose view, but its output can hold prose.", none.Message);
   }

   private static Task<JsonArray> OperationsAsync(ServerHarness harness) => ListingAsync(harness, "operations");

   private static async Task<JsonArray> ListingAsync(ServerHarness harness, string list)
   {
      using var response = await harness.SendAsync(ServerHarness.Request(HttpMethod.Get, "api/operations", harness.UiCredential));
      response.EnsureSuccessStatusCode();
      return JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))![list]!.AsArray();
   }

   private static Operation Define<TOutput>(System.Text.Json.Serialization.Metadata.JsonTypeInfo<TOutput> output, ProseView prose)
      where TOutput : class =>
      Operation.Define("test", "A test.", LotsenJsonContext.Default.NoInput, output, new Access.Reviewer("test"), prose, (_, _, _, _) => Task.FromResult<OperationResult<TOutput>>(null!));

   private static JsonNode Property(JsonNode schema, string name) => schema["properties"]?[name] ?? throw new InvalidOperationException($"No property {name} in {schema.ToJsonString()}");

   private static JsonNode Items(JsonNode schema) => schema["items"] ?? throw new InvalidOperationException($"No items in {schema.ToJsonString()}");

   private static JsonNode OneOfObject(JsonNode schema) => schema["properties"] is not null ? schema : schema["anyOf"]!.AsArray().First(option => option!["properties"] is not null)!;

   private static IEnumerable<string> Required(JsonNode schema) => schema["required"]?.AsArray().Select(name => (string)name!) ?? [];

   private static IEnumerable<JsonObject> Descendants(JsonNode? node)
   {
      switch (node)
      {
         case JsonObject obj:
            yield return obj;
            foreach (var child in obj.SelectMany(pair => Descendants(pair.Value)))
            {
               yield return child;
            }

            break;
         case JsonArray array:
            foreach (var child in array.SelectMany(Descendants))
            {
               yield return child;
            }

            break;
      }
   }

   // A kind of a payload that can hold prose, without the payload's HTML view.
   private sealed class UnviewedKind : ServerEventKind<PlanSetEvent>
   {
      public UnviewedKind()
         : base("test", LotsenJsonContext.Default.PlanSetEvent, ProseView.None, tourScoped: true)
      {
      }
   }
}
