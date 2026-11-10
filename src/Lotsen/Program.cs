using System.Reflection;
using Lotsen.AzureDevOps;
using Lotsen.Cli;
using Lotsen.Hosting;

namespace Lotsen;

/// <summary>The entry point of the lotsen executable, which registers the platforms that tours can target.</summary>
internal static class Program
{
   /// <summary>The assemblies that make up the executable, whose module version IDs form its build identity.</summary>
   internal static IReadOnlyList<Assembly> Assemblies { get; } = [typeof(Program).Assembly, typeof(Commands).Assembly, typeof(AzureDevOpsPlatform).Assembly];

   /// <summary>The versions of this executable, which a server and an adapter of the same build agree on.</summary>
   internal static VersionInfo Version { get; } = VersionInfo.FromAssemblies(Assemblies);

   public static Task<int> Main(string[] args) => Commands.RunAsync(args, Version, [AzureDevOpsPlatform.Factory()]);
}
