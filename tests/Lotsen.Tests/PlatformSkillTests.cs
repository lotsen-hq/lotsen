using System.Text.RegularExpressions;
using Lotsen.Hosting;
using Lotsen.Platforms;
using Microsoft.Extensions.DependencyInjection;

namespace Lotsen.Tests;

/// <summary>Checks that the tour skill's "Platforms" section names each platform that the lotsen executable registers, with the pull request URL forms that the platform accepts.</summary>
/// <remarks><c>Lotsen.Core.Tests</c>, which holds the skill's other checks, references no platform library, so this check sits beside the executable that registers the platforms.</remarks>
[Trait("Category", "Skill")]
public sealed partial class PlatformSkillTests : IDisposable
{
   private const string Heading = "## Platforms";

   private readonly DirectoryInfo _dataDirectory = Directory.CreateTempSubdirectory("lotsen-tests-");

   [Fact]
   public async Task Platforms_section_has_a_subsection_for_each_registered_platform_and_names_exactly_their_url_forms()
   {
      await using var services = new ServiceCollection().AddLogging().AddSingleton(TimeProvider.System).AddSingleton(new DataDirectory(_dataDirectory.FullName)).BuildServiceProvider();
      var platforms = Program.Platforms.Select(factory => factory(services)).ToList();
      try
      {
         var section = PlatformsSection();
         var forms = platforms.SelectMany(platform => UrlForm().Matches(platform.ExpectedForms).Select(match => match.Value)).Order(StringComparer.Ordinal);

         Assert.All(platforms, platform => Assert.Matches(UrlForm(), platform.ExpectedForms));
         Assert.All(platforms, platform => Assert.Matches(new Regex($@"^### .+ \(`{Regex.Escape(platform.Id)}`\)$", RegexOptions.Multiline), section));
         Assert.Equal(forms, BacktickedUrl().Matches(section).Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal));
      }
      finally
      {
         foreach (var platform in platforms.OfType<IDisposable>())
         {
            platform.Dispose();
         }
      }
   }

   public void Dispose() => _dataDirectory.Delete(recursive: true);

   private static string PlatformsSection()
   {
      var skill = File.ReadAllText(RepositoryFile.PathOf("plugin/skills/tour/SKILL.md"));
      var start = skill.IndexOf(Heading + "\n", StringComparison.Ordinal);
      Assert.True(start >= 0, $"The skill has no section '{Heading}'.");
      var end = skill.IndexOf("\n## ", start + Heading.Length, StringComparison.Ordinal);
      return skill[start..(end < 0 ? skill.Length : end)];
   }

   // A URL form in a platform's ExpectedForms ends at the space, comma or semicolon that follows it.
   [GeneratedRegex(@"https://[^\s,;]+")]
   private static partial Regex UrlForm();

   [GeneratedRegex(@"`(https://[^`]+)`")]
   private static partial Regex BacktickedUrl();
}
