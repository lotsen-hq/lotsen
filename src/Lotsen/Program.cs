using System.Reflection;
using Lotsen.AzureDevOps;
using Lotsen.Cli;
using Lotsen.GitHub;
using Lotsen.Hosting;
using Lotsen.Platforms;

namespace Lotsen;

/// <summary>The entry point of the lotsen executable, which registers the platforms that tours can target.</summary>
internal static class Program
{
   /// <summary>The assemblies that make up the executable, whose module version IDs form its build identity.</summary>
   internal static IReadOnlyList<Assembly> Assemblies { get; } = [typeof(Program).Assembly, typeof(Commands).Assembly, typeof(AzureDevOpsPlatform).Assembly, typeof(GitHubPlatform).Assembly];

   /// <summary>The versions of this executable, which a server and an adapter of the same build agree on.</summary>
   internal static VersionInfo Version { get; } = VersionInfo.FromAssemblies(Assemblies);

   /// <summary>The factories of the platforms that tours can target, in the order that the server tries a pull request URL on them.</summary>
   internal static IReadOnlyList<Func<IServiceProvider, IPullRequestPlatform>> Platforms { get; } = [AzureDevOpsPlatform.Factory(), GitHubPlatform.Factory()];

   public static Task<int> Main(string[] args) => Commands.RunAsync(args, Version, Platforms);
}
