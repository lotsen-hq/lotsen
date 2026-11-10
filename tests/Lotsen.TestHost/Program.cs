using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Lotsen.Git;
using Lotsen.Hosting;
using Lotsen.Platforms;
using Lotsen.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lotsen.TestHost;

/// <summary>
/// Runs the test host: a control API on a port of 127.0.0.1 that starts, seals, copies and stops worlds, each a real <see cref="Server.LotsenServer"/>, for the UI's tests and the demo.
/// </summary>
/// <remarks>
/// <para>
/// The first line on standard output is a JSON object with the control API's <c>url</c> and the bearer <c>token</c> that each control request carries. The host stops every world, removes its
/// directory and exits when standard input closes, so it never outlives the process that started it.
/// </para>
/// <para>
/// The API is <c>POST /control/{verb}</c> with a JSON object, and it answers with a JSON object, or 500 with an <c>error</c>. It covers only what no operation can do, as
/// <see cref="World"/> says, and the real agent sessions of <see cref="ClaudeSession"/>.
/// </para>
/// </remarks>
public static class Program
{
   public static async Task<int> Main()
   {
      var root = Directory.CreateTempSubdirectory("lotsen-host-").FullName;
      await using var host = new Host(root);
      var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
      var builder = WebApplication.CreateSlimBuilder();
      builder.Configuration.Sources.Clear();
      builder.Logging.ClearProviders();
      // Node's fetch refuses the ports that browsers do, so the control API binds as a server does.
      LoopbackListener.ListenOn(builder.WebHost, LoopbackListener.Bind(null));
      var app = builder.Build();
      app.MapPost("/control/{verb}", async (HttpContext context, string verb) =>
      {
         if (context.Request.Headers.Authorization != $"Bearer {token}")
         {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
         }

         var body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted) as JsonObject ?? [];
         try
         {
            return Results.Content((await host.RunAsync(verb, body, context.RequestAborted)).ToJsonString(), "application/json");
         }
         // Only a request that its caller aborted goes unanswered. A cancellation inside the host, such as a world's stop that disposes the client of a session in a call, answers 500.
         catch (Exception exception) when (!context.RequestAborted.IsCancellationRequested)
         {
            return Results.Content(new JsonObject { ["error"] = $"{verb}: {exception.Message}" }.ToJsonString(), "application/json", statusCode: StatusCodes.Status500InternalServerError);
         }
      });
      await app.StartAsync();
      var address = app.Urls.Single();
      Console.Out.WriteLine(new JsonObject { ["url"] = address, ["token"] = token }.ToJsonString());
      Console.Out.Flush();

      using var stopping = new CancellationTokenSource();
      Console.CancelKeyPress += (_, args) =>
      {
         args.Cancel = true;
         stopping.Cancel();
      };
      await Task.WhenAny(Console.In.ReadToEndAsync(stopping.Token), Task.Delay(Timeout.Infinite, stopping.Token)).ContinueWith(_ => { }, TaskScheduler.Default);
      await app.StopAsync(CancellationToken.None);
      await app.DisposeAsync();
      return 0;
   }

   // The worlds and the templates of one run of the host, under one directory that the run removes.
   private sealed class Host : IAsyncDisposable
   {
      private static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(10);

      private readonly string _root;
      private readonly SharedOrigin _origin;
      private readonly ConcurrentDictionary<string, World> _worlds = new(StringComparer.Ordinal);
      private static readonly TimeSpan TemplateWait = TimeSpan.FromMinutes(2);

      private readonly ConcurrentDictionary<string, Template> _templates = new(StringComparer.Ordinal);
      private readonly ConcurrentDictionary<string, TaskCompletionSource> _building = new(StringComparer.Ordinal);

      // The template that each build world builds, by the world's ID, until the world seals or stops.
      private readonly ConcurrentDictionary<string, string> _builds = new(StringComparer.Ordinal);
      private int _nextWorld;

      public Host(string root)
      {
         _root = root;
         _origin = new SharedOrigin(root);
      }

      public async Task<JsonObject> RunAsync(string verb, JsonObject body, CancellationToken cancellationToken)
      {
         switch (verb)
         {
            case "world.start":
               var clockStart = body["clockStart"] is { } start ? DateTimeOffset.Parse((string)start!, System.Globalization.CultureInfo.InvariantCulture) : (DateTimeOffset?)null;
               return Info(await StartAsync((string?)body["template"], clockStart, cancellationToken));
            case "world.seal":
               return await SealAsync(WorldOf(body), TemplateName(body), body["data"]?.DeepClone());
            case "world.stop":
               if (_worlds.TryRemove(Text(body, "world"), out var stopped))
               {
                  if (_builds.TryRemove(stopped.Id, out var unsealed))
                  {
                     Fail(unsealed, $"The build of template '{unsealed}' failed: its world stopped before it sealed. The test that claimed the template first shows why.");
                  }

                  await stopped.DisposeAsync();
                  TestDirectories.Delete(Path.GetDirectoryName(stopped.DataDirectory.Path)!, TimeSpan.FromSeconds(5));
               }

               return [];
            case "world.restart":
               var restarted = WorldOf(body);
               await restarted.StopServerAsync();
               await restarted.StartServerAsync(cancellationToken);
               return Info(restarted);
            case "template.has":
               return Known(TemplateName(body));
            case "template.claim":
               return await ClaimAsync(TemplateName(body), cancellationToken);
            case "repo.commit":
               var files = body["files"]?.AsObject().ToDictionary(file => file.Key, file => (string?)file.Value, StringComparer.Ordinal) ?? [];
               var parent = body["parent"] is { } given ? CommitId.Parse((string)given!) : (CommitId?)null;
               return new JsonObject { ["commit"] = WorldOf(body).Commit(Text(body, "branch"), parent, (string?)body["message"] ?? "A commit", files).Value };
            case "pr.set":
               WorldOf(body).SetPullRequest(
                  CommitId.Parse(Text(body, "source")),
                  CommitId.Parse(Text(body, "target")),
                  (int?)body["iteration"] ?? 1,
                  Enum.Parse<PullRequestState>((string?)body["state"] ?? "active", ignoreCase: true));
               return [];
            case "pr.fail":
               WorldOf(body).FailPullRequest(Text(body, "reason"));
               return [];
            case "pr.hold":
               WorldOf(body).HoldPullRequest();
               return [];
            case "pr.release":
               WorldOf(body).ReleasePullRequest();
               return [];
            case "clock.advance":
               var advanced = WorldOf(body);
               await advanced.Clock.AdvanceAsync(TimeSpan.FromMilliseconds((double)body["ms"]!), () => advanced.IdleAsync().WaitAsync(cancellationToken));
               return [];
            case "jobs.idle":
               await WorldOf(body).IdleAsync().WaitAsync(cancellationToken);
               return [];
            case "jobs.quiet":
               var quiet = WorldOf(body);
               await quiet.QuietAsync().WaitAsync(cancellationToken);
               if ((string?)body["comment"] is { } comment)
               {
                  quiet.Comment(comment);
               }

               return [];
            case "ui.code":
               return new JsonObject { ["code"] = await WorldOf(body).IssueCodeAsync(cancellationToken) };
            case "claude.open":
               var session = await WorldOf(body).OpenSessionAsync((bool?)body["confirm"] ?? true, cancellationToken);
               return new JsonObject { ["session"] = session.Name, ["sessionId"] = session.SessionId };
            case "claude.call":
               var (output, refusal) = await WorldOf(body).Session(Text(body, "session")).CallAsync(Text(body, "operation"), body["input"]?.AsObject() ?? [], cancellationToken);
               return output is not null ? new JsonObject { ["output"] = output } : new JsonObject { ["refusal"] = refusal };
            case "claude.next":
               var wait = body["timeoutMs"] is { } ms ? TimeSpan.FromMilliseconds((double)ms) : DefaultWait;
               var next = await WorldOf(body).Session(Text(body, "session")).NextAsync((string?)body["kind"], wait, cancellationToken);
               return next is null ? [] : new JsonObject { ["type"] = next.Type, ["data"] = JsonNode.Parse(next.Data) };
            case "claude.drop":
               await WorldOf(body).DropSessionAsync(Text(body, "session"));
               return [];
            default:
               throw new InvalidOperationException($"The host has no verb {verb}.");
         }
      }

      public async ValueTask DisposeAsync()
      {
         foreach (var world in _worlds.Values)
         {
            await world.DisposeAsync();
         }

         TestDirectories.Delete(_root, TimeSpan.FromSeconds(5));
      }

      private static string Text(JsonObject body, string name) => (string?)body[name] ?? throw new InvalidOperationException($"The request needs {name}.");

      // A template's name becomes part of a directory's name under the host's root, so it holds no path separator and no "..".
      private static string TemplateName(JsonObject body)
      {
         var name = Text(body, "name");
         return name.Length > 0 && !name.Contains("..", StringComparison.Ordinal) && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            ? name
            : throw new InvalidOperationException($"The template name '{name}' is not a plain name. Use letters, digits, '-', '_' and '.' alone, without '..'.");
      }

      private static JsonObject Info(World world) => new() { ["world"] = world.Id, ["url"] = world.Url, ["uiCredential"] = world.UiCredential };

      private World WorldOf(JsonObject body) =>
         _worlds.TryGetValue(Text(body, "world"), out var world) ? world : throw new InvalidOperationException($"The host has no world {(string?)body["world"]}.");

      private async Task<World> StartAsync(string? template, DateTimeOffset? clockStart, CancellationToken cancellationToken)
      {
         var id = $"w{Interlocked.Increment(ref _nextWorld)}";
         var data = Path.Combine(_root, "worlds", id, "data");
         Template? source = null;
         if (template is not null)
         {
            source = _templates.TryGetValue(template, out var known) ? known : throw new InvalidOperationException($"The host has no template {template}.");
            CopyDirectory(source.Path, data);
         }
         else
         {
            Directory.CreateDirectory(data);
         }

         var world = await World.StartAsync(id, new DataDirectory(data), _origin, clockStart, source?.PullRequest, cancellationToken);
         _worlds[id] = world;
         return world;
      }

      // Stops the world's server and keeps its data directory as the template of the name, which later worlds copy, with what the template's build returned.
      private async Task<JsonObject> SealAsync(World world, string name, JsonNode? data)
      {
         if (_builds.TryGetValue(world.Id, out var claimed) && claimed != name)
         {
            throw new InvalidOperationException($"The world builds template '{claimed}', so it seals only as '{claimed}'.");
         }

         _worlds.TryRemove(world.Id, out _);
         _builds.TryRemove(world.Id, out var built);
         try
         {
            await world.DisposeAsync();
            var template = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "templates")).FullName, $"{name}-{world.Id}");
            TestDirectories.Move(world.DataDirectory.Path, template, TimeSpan.FromSeconds(5));
            _templates[name] = new Template(template, data, world.PullRequestAnswer);
         }
         catch (Exception exception) when (built is not null)
         {
            Fail(built, $"The build of template '{built}' failed: its seal failed with {exception.Message}");
            throw;
         }

         _building.GetOrAdd(name, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
         return new JsonObject { ["template"] = name };
      }

      private JsonObject Known(string name) =>
         _templates.TryGetValue(name, out var known) ? new JsonObject { ["has"] = true, ["data"] = known.Data?.DeepClone() } : new JsonObject { ["has"] = false };

      // The first claim of a name gets the world that builds the template, and every later claim waits for its seal, so test workers that share a name share one build. A build that
      // fails fails every claim of the name for the rest of the host's run.
      private async Task<JsonObject> ClaimAsync(string name, CancellationToken cancellationToken)
      {
         var building = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
         var claimed = _building.GetOrAdd(name, building);
         if (ReferenceEquals(claimed, building) && !_templates.ContainsKey(name))
         {
            World world;
            try
            {
               world = await StartAsync(null, null, cancellationToken);
            }
            catch (Exception exception)
            {
               Fail(name, $"The build of template '{name}' failed: its world did not start, with {exception.Message}");
               throw;
            }

            _builds[world.Id] = name;
            return new JsonObject { ["has"] = false, ["build"] = Info(world) };
         }

         await claimed.Task.WaitAsync(TemplateWait, cancellationToken);
         return Known(name);
      }

      private void Fail(string name, string reason) => _building[name].TrySetException(new InvalidOperationException(reason));

      // A sealed world's data directory, what its build returned, and its pull request answer.
      private sealed record Template(string Path, JsonNode? Data, Func<CancellationToken, Task<ResolvedPullRequest>>? PullRequest);

      private static void CopyDirectory(string source, string target)
      {
         Directory.CreateDirectory(target);
         foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
         {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
         }

         foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
         {
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)));
         }
      }
   }
}
