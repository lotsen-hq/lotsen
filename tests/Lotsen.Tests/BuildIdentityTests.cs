using System.Reflection;
using Lotsen.AzureDevOps;
using Lotsen.Cli;
using Lotsen.GitHub;
using Lotsen.Hosting;

namespace Lotsen.Tests;

/// <summary>Covers the build identity of the <c>lotsen</c> executable, which the adapter compares with the server's.</summary>
[Trait("Category", "Adapter")]
public sealed class BuildIdentityTests
{
   private static readonly Assembly[] Assemblies = [typeof(Program).Assembly, typeof(Commands).Assembly, typeof(AzureDevOpsPlatform).Assembly, typeof(GitHubPlatform).Assembly];

   [Fact]
   public void Build_identity_comes_from_the_executable_the_core_and_the_platform_libraries()
   {
      Assert.Equal(VersionInfo.FromModuleVersionIds(Assemblies.Select(assembly => assembly.ManifestModule.ModuleVersionId)), Program.Version);
   }

   [Theory]
   [InlineData("lotsen")]
   [InlineData("Lotsen.Core")]
   [InlineData("Lotsen.AzureDevOps")]
   [InlineData("Lotsen.GitHub")]
   public void Change_to_one_assembly_alone_changes_the_build_identity(string changed)
   {
      Assert.Contains(Assemblies, assembly => assembly.GetName().Name == changed);

      var ids = Assemblies.Select(assembly => assembly.GetName().Name == changed ? Guid.NewGuid() : assembly.ManifestModule.ModuleVersionId);

      Assert.NotEqual(Program.Version.Build, VersionInfo.FromModuleVersionIds(ids).Build);
   }
}
