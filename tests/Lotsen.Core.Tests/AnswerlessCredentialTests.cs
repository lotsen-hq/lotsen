using Lotsen.Credentials;
using Lotsen.Git;
using Lotsen.Processes;

namespace Lotsen.Tests;

/// <summary>
/// Covers how a git call with an <see cref="AnswerlessGitCredential"/> runs without the user's git configuration and netrc file, so neither makes git or curl send a credential to a
/// platform-free remote that asks for one.
/// </summary>
/// <remarks>
/// The user's home is a fake directory that the runner's process alone gets, in place of the inherited one, so no test reads or writes the real home. The git server accepts the netrc entry's
/// login and challenges with Basic, so curl would send a password that it finds itself, and a request that carries the entry would read the repository.
/// </remarks>
[Trait("Category", "Git")]
public sealed class AnswerlessCredentialTests : IAsyncLifetime
{
   private const string NetrcHeader = "Basic bmV0cmMtdXNlcjpuZXRyYy1wYXNzd29yZA==";

   private readonly GitFixture _git = new();
   private readonly DirectoryInfo _userHome = Directory.CreateTempSubdirectory("lotsen-user-home-");
   private AuthenticatingGitServer _server = null!;

   public AnswerlessCredentialTests()
   {
      _git.Commit("Base", ("a.txt", "one\n"));
   }

   public static TheoryData<string> NetrcEntries => ["default login netrc-user password netrc-password", "machine 127.0.0.1 login netrc-user password netrc-password"];

   private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

   public async ValueTask InitializeAsync()
   {
      _server = await AuthenticatingGitServer.StartAsync(_git.OriginPath, NetrcHeader);
      _server.Challenge("Basic realm=\"git\"");
   }

   public async ValueTask DisposeAsync()
   {
      await _server.DisposeAsync();
      _userHome.Delete(recursive: true);
      _git.Dispose();
   }

   /// <summary>The same call without the isolation sends the entry and reads the repository, so the fake home is the one that git and curl read.</summary>
   [Theory]
   [MemberData(nameof(NetrcEntries))]
   public async Task Netrc_entry_of_the_user_reaches_no_platform_free_remote(string entry)
   {
      await File.WriteAllTextAsync(Path.Combine(_userHome.FullName, ".netrc"), entry + "\n", Cancellation);

      await AssertRefusedWithoutHeaderAsync();

      await PlatformFree(isolated: false).ResolveAsync(_server.RepositoryUrl, null, Cancellation);
      Assert.True(_server.AuthorizedRequests > 0);
   }

   [Fact]
   public async Task Empty_auth_that_the_user_scoped_to_the_remote_sends_no_header()
   {
      await WriteUserConfigAsync($"[http \"{_server.RepositoryUrl.AbsoluteUri}\"]\n\temptyAuth = true\n");

      await AssertRefusedWithoutHeaderAsync();

      await Assert.ThrowsAsync<AccessTokenUnavailableException>(() => PlatformFree(isolated: false).ResolveAsync(_server.RepositoryUrl, null, Cancellation));
      Assert.True(_server.RefusedRequests > 0, "The call without the isolation sent no empty credential, so the test's fake home took no effect.");
   }

   [Fact]
   public async Task Proactive_auth_of_the_user_sends_no_header_and_the_remote_still_answers_first()
   {
      await WriteUserConfigAsync("[http]\n\tproactiveAuth = basic\n");

      await AssertRefusedWithoutHeaderAsync();
   }

   private Task WriteUserConfigAsync(string config) => File.WriteAllTextAsync(Path.Combine(_userHome.FullName, ".gitconfig"), config, Cancellation);

   private async Task AssertRefusedWithoutHeaderAsync()
   {
      var refused = await Assert.ThrowsAsync<AccessTokenUnavailableException>(() => PlatformFree(isolated: true).ResolveAsync(_server.RepositoryUrl, null, Cancellation));

      Assert.Equal(PlatformFreeRepositories.PrivateRefusal, refused.Reason);
      Assert.Equal((0, 0), (_server.AuthorizedRequests, _server.RefusedRequests));
      Assert.True(_server.RequestsWithoutHeader > 0, "Git sent the remote no request.");
   }

   // The runner sets the home variables of an isolated call itself, so the fake home stands only where the process would inherit the real one. Without the isolation, the call keeps the
   // runner's helper and settings, and git reads the fake home as the user's.
   private PlatformFreeRepositories PlatformFree(bool isolated) => new(_git.DataDirectory, new GitRunner(new GitOptions
   {
      IsolatedHome = _git.DataDirectory.IsolatedGitHomePath,
      Run = (command, token) =>
      {
         var environment = new Dictionary<string, string?>(command.Environment);
         if (!isolated)
         {
            foreach (var name in new[] { "GIT_CONFIG_NOSYSTEM", "GIT_CONFIG_GLOBAL", "NETRC", "HOME", "USERPROFILE", "XDG_CONFIG_HOME" })
            {
               environment.Remove(name);
            }
         }

         foreach (var name in new[] { "HOME", "USERPROFILE", "XDG_CONFIG_HOME" })
         {
            environment.TryAdd(name, _userHome.FullName);
         }

         return ProcessRunner.RunAsync(command with { Environment = environment }, token);
      },
   }));
}
