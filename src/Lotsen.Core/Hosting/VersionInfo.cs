using System.Reflection;
using System.Security.Cryptography;

namespace Lotsen.Hosting;

/// <summary>
/// The versions that the adapter and the server compare before the adapter forwards a call.
/// </summary>
/// <param name="Build">The build of the executable: its informational version and a digest of the module version IDs of its assemblies, as <c>0.1.0+e23502b… (8549f42b…)</c>.</param>
/// <param name="Protocol">The version of the HTTP API contract between the adapter and the server.</param>
public sealed record VersionInfo(string Build, int Protocol)
{
   /// <summary>The API contract version. Change it when the adapter and the server must agree on a change.</summary>
   public const int CurrentProtocol = 2;

   /// <summary>The informational version of lotsen, such as <c>0.1.0+e23502b…</c> with the commit it was built from.</summary>
   public static string InformationalVersion { get; } = typeof(VersionInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

   /// <summary>Returns the refusal of an adapter whose versions differ from the server's. It names the fix, with the command that runs the adapter's build.</summary>
   /// <param name="server">The server's versions, or null for a server that cannot tell them, which is a build older than the adapter.</param>
   /// <param name="adapter">The adapter's versions.</param>
   internal static string MismatchReason(VersionInfo? server, VersionInfo adapter)
   {
      var serverVersions = server is null ? "an older build" : $"build {server.Build}, protocol {server.Protocol}";
      return $"The running lotsen server is another build than this adapter (server: {serverVersions}; adapter: build {adapter.Build}, protocol {adapter.Protocol}). Run `{LotsenCommand.Of(adapter).With("restart")}` so that the server runs this adapter's build, or reconnect lotsen in Claude Code's /mcp if this adapter is the older one. Then call the tool again.";
   }

   /// <summary>Returns the versions of a build made of these assemblies.</summary>
   /// <remarks>
   /// The compiler builds deterministically, so a module version ID is the same for the same compiled assembly and differs when the code differs, even between local builds of one commit.
   /// </remarks>
   public static VersionInfo FromAssemblies(params IEnumerable<Assembly> assemblies) => FromModuleVersionIds(assemblies.Select(assembly => assembly.ManifestModule.ModuleVersionId));

   /// <summary>Returns the versions of a build whose assemblies have these module version IDs, in this order.</summary>
   /// <remarks>The build's digest is the first 16 bytes of the SHA-256 of the IDs, so a change to any one ID changes it.</remarks>
   public static VersionInfo FromModuleVersionIds(IEnumerable<Guid> moduleVersionIds)
   {
      var digest = SHA256.HashData([.. moduleVersionIds.SelectMany(id => id.ToByteArray())]);
      return new VersionInfo($"{InformationalVersion} ({Convert.ToHexStringLower(digest, 0, 16)})", CurrentProtocol);
   }
}
