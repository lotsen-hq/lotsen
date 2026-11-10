using System.Text.RegularExpressions;
using Lotsen.Adapter;
using Lotsen.Hosting;
using Lotsen.Server;

namespace Lotsen.Tests;

/// <summary>
/// Covers the messages that tell the user to replace or stop the running server: each names the command <c>restart</c> or <c>stop</c> as its reader can run it, and none a restart of
/// <c>lotsen serve</c>. A message through the adapter names the npx form at the adapter's release, and a message of the CLI names <c>lotsen</c>.
/// </summary>
[Trait("Category", "Versions")]
public sealed partial class ServerMessageTests
{
   private static readonly VersionInfo Adapter = new("1.0.0+abc (adapter)", VersionInfo.CurrentProtocol);

   private static readonly LotsenCommand AdapterCommand = LotsenCommand.Of(Adapter);

   public static TheoryData<string, string> Messages => new()
   {
      { "another build", VersionInfo.MismatchReason(Adapter with { Build = "1.0.0 (server)" }, Adapter) },
      { "an older build", VersionInfo.MismatchReason(null, Adapter) },
      { "another protocol", VersionInfo.MismatchReason(Adapter with { Protocol = Adapter.Protocol + 1 }, Adapter) },
      { "refused credentials", ServerConnection.CredentialsRefusedMessage(AdapterCommand) },
      { "a session that cannot open", ServerConnection.OpenFailedMessage("The port is out of range.", AdapterCommand) },
      { "a listener that does not answer as lotsen", ServerConnection.ForeignMessage(50123, 4242, AdapterCommand) },
      { "a server that does not exit after its grace period", ServerConnection.ExitTooSlowMessage(AdapterCommand) },
      { "another server on the data directory", new ServerAlreadyRunningException(new DataDirectory("/data"), 42).Message },
      { "another server that has not written its state file", new ServerAlreadyRunningException(new DataDirectory("/data"), null).Message },
   };

   [Theory]
   [MemberData(nameof(Messages))]
   public void Message_names_restart_or_stop_and_no_restart_of_lotsen_serve(string situation, string message)
   {
      var named = new[] { "restart", "stop" }.SelectMany(command => new[] { LotsenCommand.Installed.With(command), AdapterCommand.With(command) });
      Assert.True(named.Any(command => message.Contains($"`{command}`", StringComparison.Ordinal)), $"The message for {situation} names neither command: {message}");
      Assert.DoesNotMatch(RestartOfServe(), message);
   }

   [Fact]
   public void Build_mismatch_names_the_restart_of_the_adapters_release_and_both_builds()
   {
      var reason = VersionInfo.MismatchReason(Adapter with { Build = "1.0.0 (server)" }, Adapter);

      Assert.Contains("`npx -y lotsen@1.0.0 restart`", reason, StringComparison.Ordinal);
      Assert.Contains("1.0.0 (server)", reason, StringComparison.Ordinal);
      Assert.Contains(Adapter.Build, reason, StringComparison.Ordinal);
   }

   [Theory]
   [InlineData("0.2.0+e23502b (8549f42b)", "npx -y lotsen@0.2.0 restart")]
   [InlineData("0.2.0-rc.1+e23502b (8549f42b)", "npx -y lotsen@0.2.0-rc.1 restart")]
   [InlineData("0.2.0 (8549f42b)", "npx -y lotsen@0.2.0 restart")]
   [InlineData("0.0.0-dev+e23502b (8549f42b)", "lotsen restart")]
   [InlineData("unknown (8549f42b)", "lotsen restart")]
   public void Command_of_a_build_is_npx_at_its_release_or_lotsen_for_a_development_build(string build, string expected) =>
      Assert.Equal(expected, LotsenCommand.Of(new VersionInfo(build, VersionInfo.CurrentProtocol)).With("restart"));

   /// <summary>The tests run a build that no release made, so its version is the one that <see cref="LotsenCommand.DevelopmentVersion"/> names.</summary>
   [Fact]
   public void Build_of_the_tests_is_a_development_build() => Assert.Same(LotsenCommand.Installed, LotsenCommand.Of(VersionInfo.FromAssemblies(typeof(LotsenCommand).Assembly)));

   [Fact]
   public void Foreign_listener_message_names_its_port_and_process_id()
   {
      var message = ServerConnection.ForeignMessage(50123, 4242, AdapterCommand);

      Assert.Contains("port 50123", message, StringComparison.Ordinal);
      Assert.Contains("PID 4242", message, StringComparison.Ordinal);
   }

   [Theory]
   [InlineData("Restart `lotsen serve` so that both run the same build.")]
   [InlineData("restart lotsen serve, then call the tool again")]
   [InlineData("It restarts the `lotsen serve` process.")]
   [InlineData("Restarting `lotsen serve` replaces the server.")]
   [InlineData("Once you have restarted lotsen serve, continue.")]
   public void Restart_of_lotsen_serve_in_any_inflection_matches(string text) => Assert.Matches(RestartOfServe(), text);

   [Theory]
   [InlineData("Restart the lotsen server, then call the tool again.")]
   [InlineData("Run `npx -y lotsen@0.2.0 restart`, then call the tool again.")]
   [InlineData("Run `lotsen restart` so that the server runs the installed build.")]
   [InlineData("Run `lotsen restart`, or stop `lotsen serve` with Ctrl-C.")]
   [InlineData("Run `lotsen serve` to start it in the foreground.")]
   [InlineData("Restarting the lotsen servers is not needed.")]
   public void Text_without_a_restart_of_lotsen_serve_does_not_match(string text) => Assert.DoesNotMatch(RestartOfServe(), text);

   // Any inflection of "restart", then `lotsen serve` as a whole word, so "the lotsen server" does not count.
   [GeneratedRegex("\\brestart(s|ed|ing)?\\W+(the\\s+)?`?lotsen\\s+serve\\b", RegexOptions.IgnoreCase)]
   internal static partial Regex RestartOfServe();
}
