using Microsoft.Extensions.Logging;

namespace Lotsen.Tests;

/// <summary>Keeps every log line, formatted with its exception, and its level.</summary>
internal sealed class RecordingLoggerProvider : ILoggerProvider, ILogger
{
   private readonly List<(LogLevel Level, string Line)> _entries = [];

   public IReadOnlyList<string> Lines => [.. Entries.Select(entry => entry.Line)];

   public IReadOnlyList<(LogLevel Level, string Line)> Entries
   {
      get
      {
         lock (_entries)
         {
            return [.. _entries];
         }
      }
   }

   public ILogger CreateLogger(string categoryName) => this;

   /// <summary>Returns a logger that sends every level to this provider.</summary>
   public ILogger<T> CreateLogger<T>() => LoggerFactory.Create(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(this)).CreateLogger<T>();

   public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

   public bool IsEnabled(LogLevel logLevel) => true;

   public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
   {
      lock (_entries)
      {
         _entries.Add((logLevel, $"{formatter(state, exception)} {exception}"));
      }
   }

   public void Dispose()
   {
   }
}
