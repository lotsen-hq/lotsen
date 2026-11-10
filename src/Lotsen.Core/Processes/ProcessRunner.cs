using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Lotsen.Processes;

/// <summary>One run of a process: the executable, its arguments, and the longest the run may take.</summary>
/// <param name="FileName">The executable: a name to look up on <c>PATH</c>, or a path.</param>
/// <param name="Arguments">The arguments, each of which reaches the process as one argument, whatever characters it holds.</param>
/// <param name="Timeout">The longest the run may take, from the start until the process has exited and closed both outputs.</param>
public sealed record ProcessCommand(string FileName, IReadOnlyList<string> Arguments, TimeSpan Timeout)
{
   /// <summary>The directory the process starts in, or null for this process's current directory.</summary>
   public string? WorkingDirectory { get; init; }

   /// <summary>The changes to the environment that the process inherits from this one: a value sets its variable, and null removes the variable.</summary>
   /// <remarks>The runner applies every removal before any value, so on Windows, whose variable names ignore letter case, a removal never undoes a value of the same name.</remarks>
   public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();

   /// <summary>The bytes to write to the standard input, which the runner then closes, or null for an empty input.</summary>
   public ReadOnlyMemory<byte>? Input { get; init; }

   /// <summary>Returns the executable alone, because the arguments, the environment and the input can hold a credential.</summary>
   public override string ToString() => $"{nameof(ProcessCommand)} {{ {nameof(FileName)} = {FileName} }}";
}

/// <summary>Why a process did not start.</summary>
public enum StartFailure
{
   /// <summary>No executable exists at the command's file name, or none on <c>PATH</c> has the name.</summary>
   ExecutableMissing,

   /// <summary>The command's working directory does not exist.</summary>
   WorkingDirectoryMissing,

   /// <summary>Any other refusal of the system, such as an executable without the permission to run.</summary>
   Other,
}

/// <summary>How a run of a process ended.</summary>
public closed record ProcessOutcome
{
   private ProcessOutcome()
   {
   }

   /// <summary>A process that exited before the timeout.</summary>
   /// <param name="Code">The exit code.</param>
   /// <param name="Output">The standard output, as bytes.</param>
   /// <param name="Error">The standard error, decoded as UTF-8.</param>
   public sealed record Exited(int Code, byte[] Output, string Error) : ProcessOutcome
   {
      /// <summary>Returns the exit code alone, because the output can hold a token.</summary>
      public override string ToString() => $"{nameof(Exited)} {{ {nameof(Code)} = {Code} }}";
   }

   /// <summary>A run that outlasted its timeout. The runner killed the whole process tree then.</summary>
   public sealed record TimedOut : ProcessOutcome;

   /// <summary>A run that the caller canceled. The runner killed the whole process tree then.</summary>
   public sealed record Canceled : ProcessOutcome;

   /// <summary>A process that did not start.</summary>
   /// <param name="Failure">Why the process did not start.</param>
   /// <param name="Reason">The system's reason, such as "Permission denied". It never holds the file name or the working directory.</param>
   public sealed record NotStarted(StartFailure Failure, string Reason) : ProcessOutcome;
}

/// <summary>Runs a process to its end, with a timeout, and kills the whole process tree when the timeout or the caller ends the run first.</summary>
/// <remarks>The runner writes no log, so nothing of a command reaches a log line through it.</remarks>
public static class ProcessRunner
{
   // ENOENT on Unix and ERROR_FILE_NOT_FOUND on Windows, as ProcessRunnerTests.Missing_executable_does_not_start pins.
   private const int FileNotFound = 2;

   /// <summary>Runs the command and returns how the run ended.</summary>
   /// <returns>The outcome. A cancellation by the caller returns <see cref="ProcessOutcome.Canceled"/> and does not throw.</returns>
   public static async Task<ProcessOutcome> RunAsync(ProcessCommand command, CancellationToken cancellationToken)
   {
      // A missing working directory makes the start fail like a missing executable.
      if (command.WorkingDirectory is { } directory && !Directory.Exists(directory))
      {
         return new ProcessOutcome.NotStarted(StartFailure.WorkingDirectoryMissing, "The working directory does not exist");
      }

      using var process = new Process { StartInfo = StartInfo(command) };
      try
      {
         process.Start();
      }
      catch (Win32Exception e)
      {
         // The exception's own message holds the executable's path and the working directory, so only the system's reason is used.
         var failure = e.NativeErrorCode == FileNotFound ? StartFailure.ExecutableMissing : StartFailure.Other;
         return new ProcessOutcome.NotStarted(failure, new Win32Exception(e.NativeErrorCode).Message);
      }

      var output = new MemoryStream();
      var outputRead = process.StandardOutput.BaseStream.CopyToAsync(output, CancellationToken.None);
      var errorRead = process.StandardError.ReadToEndAsync(CancellationToken.None);

      using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      timeout.CancelAfter(command.Timeout);
      try
      {
         // The reads run first, so the process never waits on a full output pipe while this process writes its input.
         await WriteInputAsync(process, command.Input, timeout.Token);

         // The outputs can stay open after the process exits if a child of the process holds them, so the timeout covers the reads.
         await Task.WhenAll(outputRead, errorRead).WaitAsync(timeout.Token);
         await process.WaitForExitAsync(timeout.Token);
      }
      catch (OperationCanceledException)
      {
         process.Kill(entireProcessTree: true);
         await process.WaitForExitAsync(CancellationToken.None);
         return cancellationToken.IsCancellationRequested ? new ProcessOutcome.Canceled() : new ProcessOutcome.TimedOut();
      }

      return new ProcessOutcome.Exited(process.ExitCode, output.ToArray(), await errorRead);
   }

   private static async Task WriteInputAsync(Process process, ReadOnlyMemory<byte>? input, CancellationToken cancellationToken)
   {
      try
      {
         if (input is { } bytes)
         {
            await process.StandardInput.BaseStream.WriteAsync(bytes, cancellationToken);
         }

         process.StandardInput.Close();
      }
      catch (IOException)
      {
         // The process closed its input early, as when it exits on an error. Its exit code and error output say why.
      }
   }

   private static ProcessStartInfo StartInfo(ProcessCommand command)
   {
      var startInfo = new ProcessStartInfo(command.FileName)
      {
         WorkingDirectory = command.WorkingDirectory ?? "",
         RedirectStandardInput = true,
         RedirectStandardOutput = true,
         RedirectStandardError = true,
         StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
         StandardErrorEncoding = Encoding.UTF8,
         UseShellExecute = false,
         CreateNoWindow = true,
      };
      foreach (var argument in command.Arguments)
      {
         startInfo.ArgumentList.Add(argument);
      }

      foreach (var (name, _) in command.Environment.Where(variable => variable.Value is null))
      {
         startInfo.Environment.Remove(name);
      }

      foreach (var (name, value) in command.Environment.Where(variable => variable.Value is not null))
      {
         startInfo.Environment[name] = value;
      }

      return startInfo;
   }
}
