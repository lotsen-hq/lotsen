namespace Lotsen.Hosting;

/// <summary>How a message names a lotsen command for its reader to run, such as <c>lotsen restart</c> or <c>npx -y lotsen@0.2.0 restart</c>.</summary>
/// <remarks>
/// The reader of a CLI message just ran lotsen, so the message names <see cref="Installed"/>. The plugin starts the adapter through npx, so its user may have no lotsen on PATH, and a message
/// that reaches the user through the adapter names <see cref="Of"/> the adapter's build.
/// </remarks>
internal sealed record LotsenCommand
{
   /// <summary>The version of a build that no release made, which <c>src/Directory.Build.props</c> sets and a release build overrides.</summary>
   public const string DevelopmentVersion = "0.0.0-dev";

   private LotsenCommand(string program)
   {
      Program = program;
   }

   /// <summary>The command of the lotsen on PATH.</summary>
   public static LotsenCommand Installed { get; } = new("lotsen");

   /// <summary>The program and the arguments that come before the command's own, such as <c>lotsen</c> or <c>npx -y lotsen@0.2.0</c>.</summary>
   public string Program { get; }

   /// <summary>Returns the command that runs this build of lotsen.</summary>
   /// <returns>npx with the build's release version, or <see cref="Installed"/> for a development build, which npm does not hold.</returns>
   public static LotsenCommand Of(VersionInfo build)
   {
      // A build identity starts with the informational version, such as 0.2.0+e23502b… (8549f42b…), whose part before the + is the npm version.
      var end = build.Build.IndexOfAny(['+', ' ']);
      var version = end < 0 ? build.Build : build.Build[..end];
      return version is DevelopmentVersion or "" or "unknown" ? Installed : new LotsenCommand($"npx -y lotsen@{version}");
   }

   /// <summary>Returns the command line that runs lotsen with these arguments, such as <c>npx -y lotsen@0.2.0 restart</c> for <c>restart</c>.</summary>
   public string With(string arguments) => $"{Program} {arguments}";
}
