using System.Text.Json.Nodes;

namespace Lotsen.Tests;

/// <summary>Builds valid inputs from an exported input schema, and mutations of them that each break one rule of the schema.</summary>
/// <remarks>A sample holds every declared property, so a mutation reaches each of them, and it takes the first branch of each union but the one that a variant varies.</remarks>
internal static class SchemaSamples
{
   private const string TourIdPattern = "^[a-z2-7]{16}$";

   private const string CommitIdPattern = "^([0-9a-f]{40}|[0-9a-f]{64})$";

   /// <summary>Returns the samples of a schema: one with the first branch of every union, and one for each other branch of each union.</summary>
   public static IEnumerable<JsonNode> Of(JsonNode schema)
   {
      var unions = new List<string>();
      Collect(schema, "$", unions);
      yield return Sample(schema, "$", new Dictionary<string, int>());
      foreach (var union in unions.Distinct())
      {
         var branches = BranchCount(schema, union);
         for (var branch = 1; branch < branches; branch++)
         {
            yield return Sample(schema, "$", new Dictionary<string, int> { [union] = branch });
         }
      }
   }

   /// <summary>Returns each mutation of the sample, with the rule that it breaks.</summary>
   public static IEnumerable<(string Rule, JsonNode Mutated)> Mutations(JsonNode schema, JsonNode sample)
   {
      var found = new List<(string, JsonNode)>();
      Mutate(schema, sample, sample, [], found);
      return found;
   }

   private static void Mutate(JsonNode schema, JsonNode root, JsonNode? value, List<object> path, List<(string, JsonNode)> found)
   {
      var where = string.Concat(path.Select(step => step is int index ? $"[{index}]" : $".{step}"));
      if (schema["anyOf"] is JsonArray branches && value is JsonObject union)
      {
         var discriminator = (string)schema["required"]![0]!;
         found.Add(($"{where} names no kind", Changed(root, path, node => ((JsonObject)node!).Remove(discriminator))));
         var kind = (string)union[discriminator]!;
         var branch = branches.First(each => (string?)each!["properties"]![discriminator]!["const"] == kind)!;
         MutateObject(branch, root, union, path, found, skip: discriminator);
         return;
      }

      switch (TypeOf(schema).Type)
      {
         case "object" when value is JsonObject obj:
            MutateObject(schema, root, obj, path, found, skip: null);
            break;
         case "array" when value is JsonArray { Count: > 0 }:
            Mutate(schema["items"]!, root, value[0], [.. path, 0], found);
            break;
      }
   }

   private static void MutateObject(JsonNode schema, JsonNode root, JsonObject value, List<object> path, List<(string, JsonNode)> found, string? skip)
   {
      var where = string.Concat(path.Select(step => step is int index ? $"[{index}]" : $".{step}"));
      if (schema["additionalProperties"] is JsonValue closed && !(bool)closed)
      {
         found.Add(($"{where} holds an unknown property", Changed(root, path, node => node!["notAProperty"] = 1)));
      }

      var required = schema["required"]?.AsArray().Select(name => (string)name!).ToHashSet() ?? [];
      foreach (var (name, property) in schema["properties"]?.AsObject() ?? [])
      {
         if (name == skip || property is null)
         {
            continue;
         }

         List<object> at = [.. path, name];
         var (type, nullable) = TypeOf(property);
         found.Add(($"{where}.{name} has the wrong JSON type", Changed(root, at, _ => { }, WrongType(property.AsObject().ContainsKey("anyOf") ? "object" : type))));
         if (!nullable)
         {
            found.Add(($"{where}.{name} is null", Changed(root, at, _ => { }, null, setNull: true)));
         }

         if (required.Contains(name))
         {
            found.Add(($"{where}.{name} is missing", Changed(root, path, node => ((JsonObject)node!).Remove(name))));
         }

         Mutate(property, root, value[name], at, found);
      }
   }

   // Returns a copy of the root in which the node at the path is changed, or replaced by the value.
   private static JsonNode Changed(JsonNode root, List<object> path, Action<JsonNode?> change, JsonNode? replacement = null, bool setNull = false)
   {
      var copy = root.DeepClone();
      if (replacement is null && !setNull)
      {
         change(At(copy, path));
         return copy;
      }

      var parent = At(copy, path[..^1]);
      switch (path[^1])
      {
         case int index:
            parent![index] = replacement;
            break;
         case string name:
            parent![name] = replacement;
            break;
      }

      return copy;
   }

   private static JsonNode? At(JsonNode node, IEnumerable<object> path) => path.Aggregate<object, JsonNode?>(node, (current, step) => step is int index ? current![index] : current![(string)step]);

   private static JsonNode WrongType(string type) => type switch
   {
      "string" => 12345,
      "array" => new JsonObject(),
      _ => "wrong",
   };

   private static JsonNode Sample(JsonNode schema, string where, Dictionary<string, int> choices)
   {
      if (schema["anyOf"] is JsonArray branches)
      {
         var discriminator = (string)schema["required"]![0]!;
         var branch = branches[choices.GetValueOrDefault(where)]!;
         var sample = SampleObject(branch, where, choices);
         sample[discriminator] = (string)branch["properties"]![discriminator]!["const"]!;
         return sample;
      }

      var type = TypeOf(schema).Type;
      return type switch
      {
         "object" => SampleObject(schema, where, choices),
         "array" => new JsonArray(Sample(schema["items"]!, $"{where}[]", choices)),
         "string" when (string?)schema["pattern"] == TourIdPattern => "aaaaaaaaaaaaaaaa",
         "string" when (string?)schema["pattern"] == CommitIdPattern => new string('a', 40),
         "string" when (string?)schema["format"] == "uri" => "https://example.test/repo.git",
         "string" when schema["enum"] is JsonArray values => (string)values.First(value => value is not null)!,
         "string" => "s",
         "integer" => 1,
         "number" => 1.5,
         "boolean" => true,
         _ => throw new InvalidOperationException($"No sample of the type {type} at {where}."),
      };
   }

   private static JsonObject SampleObject(JsonNode schema, string where, Dictionary<string, int> choices)
   {
      var sample = new JsonObject();
      foreach (var (name, property) in schema["properties"]?.AsObject() ?? [])
      {
         if (property?["const"] is null)
         {
            sample[name] = Sample(property!, $"{where}.{name}", choices);
         }
      }

      return sample;
   }

   private static void Collect(JsonNode schema, string where, List<string> unions)
   {
      if (schema["anyOf"] is JsonArray branches)
      {
         unions.Add(where);
         foreach (var branch in branches)
         {
            foreach (var (name, property) in branch!["properties"]?.AsObject() ?? [])
            {
               if (property?["const"] is null)
               {
                  Collect(property!, $"{where}.{name}", unions);
               }
            }
         }

         return;
      }

      foreach (var (name, property) in schema["properties"]?.AsObject() ?? [])
      {
         Collect(property!, $"{where}.{name}", unions);
      }

      if (schema["items"] is { } items)
      {
         Collect(items, $"{where}[]", unions);
      }
   }

   private static int BranchCount(JsonNode schema, string where)
   {
      foreach (var (path, node) in Nodes(schema, "$"))
      {
         if (path == where)
         {
            return node["anyOf"]!.AsArray().Count;
         }
      }

      throw new InvalidOperationException($"No union at {where}.");
   }

   private static IEnumerable<(string Path, JsonNode Node)> Nodes(JsonNode schema, string where)
   {
      yield return (where, schema);
      IEnumerable<JsonNode> branches = schema["anyOf"] is JsonArray anyOf ? [.. anyOf.Select(branch => branch!)] : [schema];
      foreach (var branch in branches)
      {
         foreach (var (name, property) in branch["properties"]?.AsObject() ?? [])
         {
            if (property?["const"] is null)
            {
               foreach (var nested in Nodes(property!, $"{where}.{name}"))
               {
                  yield return nested;
               }
            }
         }
      }

      if (schema["items"] is { } items)
      {
         foreach (var nested in Nodes(items, $"{where}[]"))
         {
            yield return nested;
         }
      }
   }

   private static (string Type, bool Nullable) TypeOf(JsonNode schema) => schema["type"] switch
   {
      JsonArray types => ((string)types[0]!, true),
      JsonValue type => ((string)type!, false),
      _ => ("object", false),
   };
}
