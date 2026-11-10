using Lotsen.Hosting;

namespace Lotsen.Tests;

/// <summary>Covers which line of the log file a start quotes as the reason that its background server failed.</summary>
[Trait("Category", "Lifecycle")]
public sealed class ServerLogTests : IDisposable
{
   private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

   private readonly DataDirectory _dataDirectory = new(Directory.CreateTempSubdirectory("lotsen-tests-").FullName);

   /// <summary>The file times are whole seconds, so a file system that keeps coarse times stores them as set.</summary>
   [Theory]
   [InlineData(-1, null)]
   [InlineData(0, "the last line")]
   [InlineData(1, "the last line")]
   public void Last_line_counts_only_when_the_log_was_written_since_the_start(int secondsAfterTheStart, string? expected)
   {
      File.WriteAllText(_dataDirectory.LogFilePath, "an earlier line\nthe last line\n\n");
      File.SetLastWriteTimeUtc(_dataDirectory.LogFilePath, Start.AddSeconds(secondsAfterTheStart));

      Assert.Equal(expected, ServerLog.TryReadLastLine(_dataDirectory, Start));
   }

   [Fact]
   public void Missing_log_has_no_last_line()
   {
      Assert.Null(ServerLog.TryReadLastLine(_dataDirectory, Start));
   }

   public void Dispose()
   {
      try
      {
         Directory.Delete(_dataDirectory.Path, recursive: true);
      }
      catch (IOException)
      {
      }
   }
}
