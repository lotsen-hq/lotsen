using System.Text.Json.Nodes;

namespace Lotsen.Tests;

/// <summary>Checks that the third-party notices name every NuGet package that the executable's restore resolves, because the build generates only the UI packages' notices.</summary>
[Trait("Category", "Licenses")]
public sealed class ThirdPartyNoticesTests
{
   [Fact]
   public void The_notices_name_every_package_of_the_executable()
   {
      var notices = File.ReadAllText(RepositoryFile.PathOf("ui/third-party/notices.txt"));
      var assets = JsonNode.Parse(File.ReadAllText(RepositoryFile.PathOf("src/Lotsen/obj/project.assets.json")))!;

      var packages = assets["libraries"]!.AsObject()
         .Where(library => (string)library.Value!["type"]! == "package")
         .Select(library => library.Key.Split('/')[0])
         .Where(package => !package.StartsWith("runtime.", StringComparison.Ordinal))
         .ToList();

      var missing = packages.Where(package => !notices.Contains(package, StringComparison.Ordinal)).ToList();

      Assert.NotEmpty(packages);
      Assert.Empty(missing);
   }
}
