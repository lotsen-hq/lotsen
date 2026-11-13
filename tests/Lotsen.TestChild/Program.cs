using System.Diagnostics;
using System.Globalization;

namespace Lotsen.TestChild;

/// <summary>
/// Runs one behavior that a test asks of a child process, named by the first argument. Run as an executable named <c>az</c> or <c>gh</c>, it stands in for the Azure CLI or the GitHub CLI
/// instead, as <see cref="FakeCli(string[])"/> says.
/// </summary>
/// <remarks>
/// The behaviors are <c>print &lt;code&gt; &lt;output&gt; &lt;error&gt;</c>, <c>flood &lt;bytes&gt;</c>, which writes that many bytes to each output, <c>echo</c>, which copies the input to the
/// output, <c>env &lt;name&gt;</c>, which prints the variable's value or exits with 3 when it is unset, <c>cwd</c>, which prints the current directory, <c>sleep</c>, and
/// <c>spawn &lt;file&gt;</c>, which starts a sleeping child of its own, writes its own process ID and then the child's to the file, one per line, and sleeps. With an <c>http://</c> link as its
/// only argument, it exits with 0 and opens nothing, as a browser launcher that accepted the link does.
/// </remarks>
public static class Program
{
   /// <summary>How long a sleep lasts, so a child that a test fails to kill still exits.</summary>
   private static readonly TimeSpan SleepLength = TimeSpan.FromSeconds(60);

   public static int Main(string[] args)
   {
      if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) is "az" or "gh")
      {
         return FakeCli(args);
      }

      switch (args)
      {
         case ["print", var code, var output, var error]:
            Console.Out.Write(output);
            Console.Error.Write(error);
            return int.Parse(code, CultureInfo.InvariantCulture);
         case ["flood", var count]:
            Flood(int.Parse(count, CultureInfo.InvariantCulture));
            return 0;
         case ["echo"]:
            using (var input = Console.OpenStandardInput())
            using (var output = Console.OpenStandardOutput())
            {
               input.CopyTo(output);
            }

            return 0;
         case ["env", var name]:
            if (Environment.GetEnvironmentVariable(name) is not { } value)
            {
               return 3;
            }

            Console.Out.Write(value);
            return 0;
         case ["cwd"]:
            Console.Out.Write(Environment.CurrentDirectory);
            return 0;
         case ["sleep"]:
            Thread.Sleep(SleepLength);
            return 0;
         case ["spawn", var pidFile]:
            using (var child = Process.Start(SelfStartInfo("sleep")) ?? throw new InvalidOperationException("The child did not start."))
            {
               // The rename makes the file appear whole, so a test that waits for it never reads half of it.
               File.WriteAllText(pidFile + ".tmp", string.Create(CultureInfo.InvariantCulture, $"{Environment.ProcessId}\n{child.Id}\n"));
               File.Move(pidFile + ".tmp", pidFile);
            }

            Thread.Sleep(SleepLength);
            return 0;
         case [var link] when link.StartsWith("http://", StringComparison.Ordinal):
            return 0;
         default:
            Console.Error.WriteLine($"Unknown arguments: {string.Join(' ', args)}");
            return 64;
      }
   }

   /// <summary>Writes the count of bytes to each output in turns of 4 KiB, so a parent that reads one output only after the other keeps this process blocked on a full pipe.</summary>
   private static void Flood(int count)
   {
      using var output = Console.OpenStandardOutput();
      using var error = Console.OpenStandardError();
      var outputChunk = Enumerable.Repeat((byte)'o', 4096).ToArray();
      var errorChunk = Enumerable.Repeat((byte)'e', 4096).ToArray();
      for (var written = 0; written < count; written += outputChunk.Length)
      {
         var length = Math.Min(outputChunk.Length, count - written);
         output.Write(outputChunk, 0, length);
         error.Write(errorChunk, 0, length);
      }
   }

   /// <summary>Records the arguments in <c>arguments.log</c>, then sleeps if <c>hang</c> exists, or prints <c>stdout</c> and <c>stderr</c> and exits with the code in <c>exit-code</c>.</summary>
   /// <remarks>The files sit beside the executable, in the directory that the test copied this program to.</remarks>
   private static int FakeCli(string[] args)
   {
      var directory = AppContext.BaseDirectory;
      File.AppendAllText(Path.Combine(directory, "arguments.log"), string.Join(' ', args) + "\n");
      if (File.Exists(Path.Combine(directory, "hang")))
      {
         Thread.Sleep(SleepLength);
         return 0;
      }

      using (var output = Console.OpenStandardOutput())
      {
         output.Write(File.ReadAllBytes(Path.Combine(directory, "stdout")));
      }

      using (var error = Console.OpenStandardError())
      {
         error.Write(File.ReadAllBytes(Path.Combine(directory, "stderr")));
      }

      return int.Parse(File.ReadAllText(Path.Combine(directory, "exit-code")).Trim(), CultureInfo.InvariantCulture);
   }

   /// <summary>Returns the start of this program again with the arguments, whether it runs as an executable of its own or under <c>dotnet</c>.</summary>
   private static ProcessStartInfo SelfStartInfo(params string[] args)
   {
      var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("The path of this process is unknown.");
      var startInfo = new ProcessStartInfo(processPath) { UseShellExecute = false };
      if (Path.GetFileNameWithoutExtension(processPath) == "dotnet")
      {
         startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Lotsen.TestChild.dll"));
      }

      foreach (var arg in args)
      {
         startInfo.ArgumentList.Add(arg);
      }

      return startInfo;
   }
}
