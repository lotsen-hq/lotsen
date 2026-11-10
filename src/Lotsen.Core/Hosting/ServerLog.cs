using System.Text;

namespace Lotsen.Hosting;

/// <summary>
/// The background server's log: <see cref="DataDirectory.LogFilePath"/>, user-only, which receives the log lines that the console receives for <c>lotsen serve</c>.
/// </summary>
/// <remarks>
/// The log holds no credential, as the console holds none, which <c>ServerLogSecretsTests</c> pins. A server empties the file when it opens it, so the file holds one run's lines.
/// </remarks>
internal sealed class ServerLog : ILoggerProvider
{
   private readonly Lock _lock = new();
   private readonly StreamWriter _writer;
   private bool _disposed;

   private ServerLog(StreamWriter writer)
   {
      _writer = writer;
   }

   /// <summary>Empties the log file, creating it when it is missing, and returns the log that writes to it.</summary>
   /// <remarks>Call it only while holding the claim lock, so a start that another server wins leaves that server's log as it is.</remarks>
   public static ServerLog Open(DataDirectory dataDirectory) => new(OpenWriter(dataDirectory, FileMode.Create));

   /// <summary>Adds a line to the end of the log file, for a background server that stops on an error before or without its log.</summary>
   /// <remarks>A failure to write is ignored, because the line only tells the user why the server is gone.</remarks>
   public static void Append(DataDirectory dataDirectory, string line)
   {
      try
      {
         using var writer = OpenWriter(dataDirectory, FileMode.Append);
         writer.WriteLine(line);
      }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
      {
      }
   }

   /// <summary>Returns the last line of the log file that holds text.</summary>
   /// <param name="dataDirectory">The data directory that holds the log file.</param>
   /// <param name="since">The UTC time at which the caller started the server. A file last written before then holds only an earlier run's lines.</param>
   /// <returns>The line, or null when there is none, the file was last written before <paramref name="since"/>, or the file cannot be read.</returns>
   public static string? TryReadLastLine(DataDirectory dataDirectory, DateTime since)
   {
      try
      {
         using var stream = new FileStream(dataDirectory.LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
         if (File.GetLastWriteTimeUtc(stream.SafeFileHandle) < since)
         {
            return null;
         }

         using var reader = new StreamReader(stream, Encoding.UTF8);
         return reader.ReadToEnd().Split('\n').Select(line => line.Trim()).LastOrDefault(line => line.Length > 0);
      }
      catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
      {
         return null;
      }
   }

   public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

   public void Dispose()
   {
      lock (_lock)
      {
         if (!_disposed)
         {
            _disposed = true;
            _writer.Dispose();
         }
      }
   }

   private static StreamWriter OpenWriter(DataDirectory dataDirectory, FileMode mode)
   {
      dataDirectory.EnsureExists();

      // Another process may append while the server writes, and a reader may read the file meanwhile.
      var options = new FileStreamOptions { Mode = mode, Access = FileAccess.Write, Share = FileShare.ReadWrite | FileShare.Delete };
      if (!OperatingSystem.IsWindows())
      {
         options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
      }

      var stream = new FileStream(dataDirectory.LogFilePath, options);
      try
      {
         // A file of an earlier run keeps the mode it was created with.
         if (!OperatingSystem.IsWindows())
         {
            File.SetUnixFileMode(stream.SafeFileHandle, UnixFileMode.UserRead | UnixFileMode.UserWrite);
         }

         return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
      }
      catch
      {
         stream.Dispose();
         throw;
      }
   }

   private void Write(LogLevel level, string category, EventId eventId, string message, Exception? exception)
   {
      var line = new StringBuilder()
         .Append(DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture))
         .Append(' ').Append(LevelName(level)).Append(": ").Append(category).Append('[').Append(eventId.Id).Append("] ").Append(message);
      if (exception is not null)
      {
         line.AppendLine().Append(exception);
      }

      lock (_lock)
      {
         if (!_disposed)
         {
            _writer.WriteLine(line);
         }
      }
   }

   // The console's abbreviations, so the two logs read alike.
   private static string LevelName(LogLevel level) => level switch
   {
      LogLevel.Trace => "trce",
      LogLevel.Debug => "dbug",
      LogLevel.Information => "info",
      LogLevel.Warning => "warn",
      LogLevel.Error => "fail",
      LogLevel.Critical => "crit",
      _ => level.ToString(),
   };

   private sealed class Logger : ILogger
   {
      private readonly ServerLog _log;
      private readonly string _category;

      public Logger(ServerLog log, string category)
      {
         _log = log;
         _category = category;
      }

      public IDisposable? BeginScope<TState>(TState state)
         where TState : notnull => null;

      // The logging framework applies the minimum level before a line reaches the provider.
      public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

      public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
      {
         if (IsEnabled(logLevel))
         {
            _log.Write(logLevel, _category, eventId, formatter(state, exception), exception);
         }
      }
   }
}
