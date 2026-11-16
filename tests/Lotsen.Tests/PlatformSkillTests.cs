using System.Text.RegularExpressions;
using Lotsen.Hosting;
using Lotsen.Platforms;
using Microsoft.Extensions.DependencyInjection;

namespace Lotsen.Tests;

/// <summary>
/// Checks that the tour skill's "Platforms" section names each platform that the lotsen executable registers, with the pull request URL forms that the platform accepts, and that the codebase
/// skill's names each platform with repositories, with the repository URL forms that the platform accepts.
/// </summary>
/// <remarks><c>Lotsen.Core.Tests</c>, which holds the skill's other checks, references no platform library, so this check sits beside the executable that registers the platforms.</remarks>
[Trait("Category", "Skill")]
public sealed partial class PlatformSkillTests : IDisposable
{
   private const string Heading = "## Platforms";

   private readonly DirectoryInfo _dataDirectory = Directory.CreateTempSubdirectory("lotsen-tests-");

   [Fact]
   public async Task Platforms_section_has_a_subsection_for_each_registered_platform_and_names_exactly_their_url_forms()
   {
      await using var services = Services();
      var platforms = Program.Platforms.Select(factory => factory(services)).ToList();
      try
      {
         AssertNamesExactly(PlatformsSection("tour"), platforms.Select(platform => (platform.Id, platform.ExpectedForms)));
      }
      finally
      {
         Dispose(platforms);
      }
   }

   [Fact]
   public async Task Codebase_platforms_section_has_a_subsection_for_each_platform_with_repositories_and_names_exactly_their_repository_url_forms()
   {
      await using var services = Services();
      var platforms = Program.Platforms.Select(factory => factory(services)).ToList();
      try
      {
         var repositories = platforms.OfType<IRepositoryPlatform>().ToList();

         Assert.NotEmpty(repositories);
         AssertNamesExactly(PlatformsSection("codebase"), repositories.Select(platform => (platform.Id, platform.ExpectedRepositoryForms)));
      }
      finally
      {
         Dispose(platforms);
      }
   }

   public void Dispose() => _dataDirectory.Delete(recursive: true);

   private static void AssertNamesExactly(string section, IEnumerable<(string Id, string ExpectedForms)> platforms)
   {
      var forms = platforms.SelectMany(platform => UrlForm().Matches(platform.ExpectedForms).Select(match => match.Value)).Order(StringComparer.Ordinal);

      Assert.All(platforms, platform => Assert.Matches(UrlForm(), platform.ExpectedForms));
      Assert.All(platforms, platform => Assert.Matches(new Regex($@"^### .+ \(`{Regex.Escape(platform.Id)}`\)$", RegexOptions.Multiline), section));
      Assert.Equal(forms, BacktickedUrl().Matches(section).Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal));
   }

   private static void Dispose(IEnumerable<IPullRequestPlatform> platforms)
   {
      foreach (var platform in platforms.OfType<IDisposable>())
      {
         platform.Dispose();
      }
   }

   private ServiceProvider Services() =>
      new ServiceCollection().AddLogging().AddSingleton(TimeProvider.System).AddSingleton(new DataDirectory(_dataDirectory.FullName)).BuildServiceProvider();

   private static string PlatformsSection(string skillName)
   {
      var skill = File.ReadAllText(RepositoryFile.PathOf($"plugin/skills/{skillName}/SKILL.md"));
      var start = skill.IndexOf(Heading + "\n", StringComparison.Ordinal);
      Assert.True(start >= 0, $"The skill has no section '{Heading}'.");
      var end = skill.IndexOf("\n## ", start + Heading.Length, StringComparison.Ordinal);
      return skill[start..(end < 0 ? skill.Length : end)];
   }

   // A URL form in a platform's ExpectedForms, an https or ssh URL or git's scp-like form, ends at the space, comma or semicolon that follows it.
   [GeneratedRegex(@"(?:https://|ssh://|git@)[^\s,;]+")]
   private static partial Regex UrlForm();

   [GeneratedRegex(@"`((?:https://|ssh://|git@)[^`]+)`")]
   private static partial Regex BacktickedUrl();
}
