using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Lotsen.Tests;

/// <summary>Runs <c>lotsen mcp</c> as a child process and speaks JSON-RPC to it, one message per line, over stdio.</summary>
internal sealed class AdapterProcess : IDisposable
{
   private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(30);

   private readonly Process _process;
   private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
   private readonly Channel<JsonObject> _notifications = Channel.CreateUnbounded<JsonObject>();
   private readonly StringBuilder _output = new();
   private readonly StringBuilder _errors = new();

   private AdapterProcess(Process process)
   {
      _process = process;
      process.OutputDataReceived += (_, e) =>
      {
         if (e.Data is null)
         {
            _lines.Writer.TryComplete();
            _notifications.Writer.TryComplete();
            return;
         }

         Append(_output, e.Data);
         if (TryParseNotification(e.Data) is { } notification)
         {
            _notifications.Writer.TryWrite(notification);
            return;
         }

         _lines.Writer.TryWrite(e.Data);
      };
      process.ErrorDataReceived += (_, e) => Append(_errors, e.Data);
      process.BeginOutputReadLine();
      process.BeginErrorReadLine();
   }

   public int Id => _process.Id;

   public bool HasExited => _process.HasExited;

   public int ExitCode => _process.ExitCode;

   /// <summary>Everything the adapter wrote to standard output and standard error so far.</summary>
   public string Output
   {
      get
      {
         lock (_output)
         {
            lock (_errors)
            {
               return _output.ToString() + _errors;
            }
         }
      }
   }

   /// <summary>Everything the adapter wrote to standard error so far.</summary>
   public string Errors
   {
      get
      {
         lock (_errors)
         {
            return _errors.ToString();
         }
      }
   }

   /// <summary>The grace period of the background servers that adapters start in tests, unless a test sets its own, so a server that a test leaves exits soon.</summary>
   public static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(2);

   /// <summary>Starts the adapter on a data directory, with a no-op browser, as the test process's own child, so that a signal to <see cref="Id"/> reaches the adapter.</summary>
   /// <param name="environment">Variables to set beside the data directory, the browser and <see cref="GracePeriod"/>, which they may override.</param>
   /// <param name="ownProcessGroup">Whether the adapter runs in a session and a process group of its own, as <c>setsid</c> makes it, so a signal to that group reaches the adapter alone.</param>
   public static AdapterProcess Start(string dataDirectory, IReadOnlyDictionary<string, string>? environment = null, bool ownProcessGroup = false) =>
      new(Process.Start(AdapterStartInfo(dataDirectory, environment, ownProcessGroup)) ?? throw new InvalidOperationException("dotnet did not start."));

   /// <summary>Returns the variables that set a background server's grace period for a test.</summary>
   public static Dictionary<string, string> WithGracePeriod(TimeSpan gracePeriod) =>
      new() { [Hosting.BackgroundServer.TestGracePeriodVariable] = ((int)gracePeriod.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture) };

   /// <summary>Returns the variables that set how long the adapter waits for a background server that it started to answer, for a test.</summary>
   public static Dictionary<string, string> WithStartTimeout(TimeSpan startTimeout) =>
      new() { [Hosting.BackgroundServer.TestStartTimeoutVariable] = ((int)startTimeout.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture) };

   private static ProcessStartInfo AdapterStartInfo(string dataDirectory, IReadOnlyDictionary<string, string>? environment, bool ownProcessGroup)
   {
      var variables = WithGracePeriod(GracePeriod);
      foreach (var (name, value) in environment ?? new Dictionary<string, string>())
      {
         variables[name] = value;
      }

      var startInfo = LotsenProcess.StartInfo(dataDirectory, variables, "mcp");
      startInfo.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
      if (ownProcessGroup)
      {
         // setsid runs the program in its own process when, as here, the caller leads no process group, so Id stays the adapter's.
         startInfo.ArgumentList.Insert(0, startInfo.FileName);
         startInfo.FileName = "setsid";
      }

      return startInfo;
   }

   /// <summary>Writes one JSON-RPC message as a line.</summary>
   public async Task SendAsync(JsonNode message)
   {
      await _process.StandardInput.WriteLineAsync(message.ToJsonString());
      await _process.StandardInput.FlushAsync(TestContext.Current.CancellationToken);
   }

   /// <summary>Sends a request and returns the response with its ID, skipping any other message.</summary>
   public async Task<JsonObject> RequestAsync(int id, string method, JsonObject? parameters = null)
   {
      var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
      if (parameters is not null)
      {
         request["params"] = parameters;
      }

      await SendAsync(request);
      using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
      timeout.CancelAfter(ReplyTimeout);
      try
      {
         await foreach (var line in _lines.Reader.ReadAllAsync(timeout.Token))
         {
            if (JsonNode.Parse(line) is JsonObject message && message["id"] is { } messageId && (int)messageId == id)
            {
               return message;
            }
         }
      }
      catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
      {
      }

      throw new InvalidOperationException($"The adapter sent no response to {method}:\n{Output}");
   }

   /// <summary>Runs the initialize handshake that Claude Code runs, and returns the initialize result.</summary>
   public async Task<JsonObject> InitializeAsync()
   {
      var response = await RequestAsync(1, "initialize", new JsonObject
      {
         ["protocolVersion"] = "2025-11-25",
         ["capabilities"] = new JsonObject { ["roots"] = new JsonObject(), ["elicitation"] = new JsonObject() },
         ["clientInfo"] = new JsonObject { ["name"] = "claude-code", ["version"] = "2.1.304" },
      });
      await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
      return response["result"]?.AsObject() ?? throw new InvalidOperationException($"initialize failed: {response}");
   }

   /// <summary>Calls a tool and returns its result.</summary>
   public async Task<ToolResult> CallToolAsync(int id, string name, string argumentsJson = "{}")
   {
      var response = await RequestAsync(id, "tools/call", new JsonObject
      {
         ["name"] = name,
         ["arguments"] = JsonNode.Parse(argumentsJson),
      });
      var result = response["result"] ?? throw new InvalidOperationException($"tools/call {name} failed: {response}");
      var text = Assert.Single(result["content"]!.AsArray())!;
      Assert.Equal("text", (string)text["type"]!);
      return new ToolResult((string)text["text"]!, (bool?)result["isError"] ?? false);
   }

   /// <summary>Calls <c>open_ui</c>, which reaches the server, and fails the test when the call returns an error.</summary>
   public async Task ReachServerAsync(int id)
   {
      var result = await CallToolAsync(id, "open_ui");
      Assert.False(result.IsError, $"{result.Text}\n{Output}");
   }

   /// <summary>Returns the next notification that the adapter sent, a message with a method and no ID, or null when none arrives within the timeout.</summary>
   public async Task<JsonObject?> NextNotificationAsync(TimeSpan timeout)
   {
      using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
      cts.CancelAfter(timeout);
      try
      {
         return await _notifications.Reader.ReadAsync(cts.Token);
      }
      catch (Exception exception) when (exception is ChannelClosedException || (exception is OperationCanceledException && !TestContext.Current.CancellationToken.IsCancellationRequested))
      {
         return null;
      }
   }

   /// <summary>Closes the adapter's standard input, as Claude Code does when it ends the session.</summary>
   public void CloseInput() => _process.StandardInput.Close();

   /// <summary>Sends SIGTERM, the signal a service manager or <c>kill</c> sends.</summary>
   public void Terminate()
   {
      using var kill = Process.Start("kill", ["-TERM", _process.Id.ToString()]);
      kill.WaitForExit();
   }

   /// <summary>Sends SIGINT to the adapter's process group, as a Ctrl-C in its terminal does.</summary>
   public void InterruptProcessGroup()
   {
      using var kill = Process.Start("kill", ["-INT", "--", $"-{_process.Id}"]);
      kill.WaitForExit();
   }

   /// <summary>Returns whether the adapter exited within the timeout.</summary>
   public async Task<bool> WaitForExitAsync(TimeSpan timeout)
   {
      using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
      cts.CancelAfter(timeout);
      try
      {
         await _process.WaitForExitAsync(cts.Token);
      }
      catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
      {
         return false;
      }

      // The parameterless wait also drains the redirected output.
      _process.WaitForExit();
      return true;
   }

   public void Dispose()
   {
      if (!_process.HasExited)
      {
         _process.Kill(entireProcessTree: true);
         _process.WaitForExit();
      }

      _process.Dispose();
   }

   private static JsonObject? TryParseNotification(string line)
   {
      try
      {
         return JsonNode.Parse(line) is JsonObject { } message && message.ContainsKey("method") && !message.ContainsKey("id") ? message : null;
      }
      catch (System.Text.Json.JsonException)
      {
         return null;
      }
   }

   private static void Append(StringBuilder builder, string? line)
   {
      if (line is null)
      {
         return;
      }

      lock (builder)
      {
         builder.AppendLine(line);
      }
   }
}

/// <summary>The text of a tool result and whether the result is an error.</summary>
internal sealed record ToolResult(string Text, bool IsError);
