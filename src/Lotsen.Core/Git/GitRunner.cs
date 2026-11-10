using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Lotsen.Credentials;
using Lotsen.Platforms;
using Lotsen.Processes;

namespace Lotsen.Git;

/// <summary>The settings for running git.</summary>
internal sealed record GitOptions
{
   /// <summary>The git executable: a name to look up on <c>PATH</c>, or a path.</summary>
   public string Executable { get; init; } = "git";

   /// <summary>The longest one git run of a clone or a fetch may take, as <see cref="GitCall.Timeout"/> says.</summary>
   public TimeSpan TransferTimeout { get; init; } = TimeSpan.FromMinutes(10);

   /// <summary>The longest one git run of a query may take, including the download of the file contents it reads, as <see cref="GitCall.Timeout"/> says.</summary>
   public TimeSpan QueryTimeout { get; init; } = TimeSpan.FromMinutes(2);

   /// <summary>Runs one git process. <see cref="GitRunner"/> starts every run of git with it, so a test that replaces it sees, holds or fails each run without a process of its own.</summary>
   internal Func<ProcessCommand, CancellationToken, Task<ProcessOutcome>> Run { get; init; } = ProcessRunner.RunAsync;
}

/// <summary>The exception for a git call that failed or did not finish in time.</summary>
internal class GitException : ExternalFailureException
{
   /// <param name="message">What git could not do and git's reason. It never holds git's command line or a credential.</param>
   public GitException(string message)
      : base(message, null)
   {
   }
}

/// <summary>The exception for a git call whose remote refused the credential that git sent.</summary>
/// <remarks><see cref="GitRunner"/> throws it only when the remote refused an answer of the call's credential, never for another failure.</remarks>
internal sealed class GitAuthException : GitException
{
   /// <param name="message">What git could not do, that the remote refused the credential, and the credential's fix hint.</param>
   public GitAuthException(string message)
      : base(message)
   {
   }
}

/// <summary>
/// One run of git.
/// </summary>
/// <param name="Failure">What git could not do, as the start of the error message, such as "Git could not clone X".</param>
/// <param name="Arguments">The arguments after the executable. They must not hold a credential.</param>
/// <param name="Timeout">
/// The longest each git run of the call may take. A call that authenticates runs git twice and asks the credential between the runs, as <see cref="GitRunner"/> says. The whole call can then take
/// twice this timeout plus the time that the credential takes.
/// </param>
internal sealed record GitCall(string Failure, IReadOnlyList<string> Arguments, TimeSpan Timeout)
{
   /// <summary>The repository the call works on, or null for a call outside a repository, such as a clone.</summary>
   public string? GitDirectory { get; init; }

   /// <summary>The directory git starts in.</summary>
   public required string WorkingDirectory { get; init; }

   /// <summary>The remote whose credential git presents when that remote asks for authentication, or null for a call that presents none.</summary>
   /// <remarks>Git presents the credential to no other address, not even one that the remote redirects to, as <see cref="GitRunner"/> says.</remarks>
   public GitRemote? Remote { get; init; }

   /// <summary>The text to write to git's standard input, or null to give git an empty input.</summary>
   public string? Input { get; init; }

   /// <summary>The exit codes that mean success. Any other fails the call.</summary>
   public IReadOnlyList<int> SuccessExitCodes { get; init; } = [0];

   /// <summary>Whether git may download an object that a partial clone lacks from the remote, as it does for a file's content.</summary>
   /// <remarks>
   /// A call that checks whether the clone holds an object turns it off, so git reports the object missing instead of downloading it.
   /// <c>CloneCacheTests.Required_commit_that_no_branch_holds_is_fetched_by_its_id_and_kept_under_a_ref</c> pins that.
   /// </remarks>
   public bool LazyFetch { get; init; } = true;
}

/// <summary>What a successful git call printed and the code it exited with.</summary>
/// <param name="ExitCode">One of the call's <see cref="GitCall.SuccessExitCodes"/>.</param>
/// <param name="Output">The standard output, as bytes.</param>
internal sealed record GitOutput(int ExitCode, byte[] Output)
{
   /// <summary>The standard output decoded as UTF-8, with invalid bytes replaced.</summary>
   public string Text => Encoding.UTF8.GetString(Output);
}

/// <summary>Runs the real git CLI with a timeout, a controlled environment and a credential that git asks for only when a remote needs it.</summary>
/// <remarks>
/// <para>
/// A call with a credential runs git with a credential helper of the runner's own, which git runs only when a remote asks for authentication. The first run gives the helper no answer, so a command
/// that needs no network asks the credential for nothing. When git asks the helper, the helper prints <see cref="WantedMarker"/> and tells git to quit. The runner then asks the credential and runs
/// git again with the answer. When the remote refuses the answer, git runs the helper with <c>erase</c>, the helper prints <see cref="RefusedMarker"/>, and the call throws
/// <see cref="GitAuthException"/>. <c>GitAuthTests</c> pins each of these.
/// </para>
/// <para>
/// The helper is the remote's alone, as <c>credential.&lt;remote URL&gt;.helper</c>. Git runs it only for an address with the remote's scheme, host and port, whose path starts with the remote's
/// path at a <c>/</c> (https://git-scm.com/docs/gitcredentials, "Credential contexts"). So when the remote redirects git to another address, git has no answer to send there and fails, because it
/// cannot prompt. <c>GitAuthTests</c> pins each of these boundaries.
/// </para>
/// <para>
/// The helper and its answer travel as configuration in <c>GIT_CONFIG_COUNT</c>, <c>GIT_CONFIG_KEY_n</c> and <c>GIT_CONFIG_VALUE_n</c>, and the helper reads the answer from its own environment, so
/// the credential never appears in a process argument list or in a file. Git's child processes, such as the lazy fetch of a missing file content, inherit the variables. Before its first call the
/// runner checks that git reads these variables and that its helper protocol has the <c>authtype</c> capability, which git 2.46 added.
/// </para>
/// </remarks>
internal sealed class GitRunner
{
   private const string ProbeKey = "lotsen.probe";

   /// <summary>The start of the line that the helper prints to git's error output when git asks it for a credential that the run has not given it.</summary>
   private const string WantedMarker = "lotsen: credential wanted";

   /// <summary>The start of the line that the helper prints to git's error output when the remote refused its answer.</summary>
   private const string RefusedMarker = "lotsen: credential refused";

   /// <summary>The variable that holds the run's nonce, which the helper prints after each marker.</summary>
   /// <remarks>Git prints a remote's error text in its error output too, so only the nonce, which the remote cannot know, tells the helper's line from a remote's copy of it.</remarks>
   private const string NonceVariable = "LOTSEN_CREDENTIAL_NONCE";

   // The configuration entry whose value is the helper's answer. It is the first entry, so the helper reads GIT_CONFIG_VALUE_0.
   private const string AnswerKey = "lotsen.credentialAnswer";

   private readonly Lock _probeLock = new();
   private Task? _probe;

   public GitRunner(GitOptions options)
   {
      Options = options;
   }

   /// <summary>The settings this runner runs git with.</summary>
   public GitOptions Options { get; }

   /// <summary>Runs the call and returns what git printed.</summary>
   /// <exception cref="GitAuthException">The remote refused the answer of the call's credential. The answer's source was told then.</exception>
   /// <exception cref="GitException">Git could not be started, exited with a code outside the call's success codes, or did not finish within the call's timeout. Git was stopped then.</exception>
   /// <exception cref="AccessTokenUnavailableException">A remote asked for authentication, and the call's credential has none.</exception>
   public async Task<GitOutput> RunAsync(GitCall call, CancellationToken cancellationToken)
   {
      await EnsureProbedAsync(call.WorkingDirectory).WaitAsync(cancellationToken);
      var run = await RunCoreAsync(call, [], answer: null, cancellationToken);
      if (call.Remote?.Credential is { } credential && run.Wanted)
      {
         // Git sends the answer with each request of the second run, so the answer must stay valid for as long as that run may take.
         var answer = await credential.AnswerAsync(call.Timeout, cancellationToken);
         run = await RunCoreAsync(call, [], answer, cancellationToken);
         if (run.Refused)
         {
            answer.Refuse();
            throw new GitAuthException($"{call.Failure}: the remote refused lotsen's credential. {credential.FixHint}");
         }
      }

      return Succeeded(call, run);
   }

   private Task EnsureProbedAsync(string workingDirectory)
   {
      lock (_probeLock)
      {
         if (_probe is null || _probe.IsFaulted || _probe.IsCanceled)
         {
            _probe = ProbeAsync(workingDirectory);
         }

         return _probe;
      }
   }

   private async Task ProbeAsync(string workingDirectory)
   {
      const string tooOld = "lotsen needs git 2.46 or newer, to pass credentials to git safely. Install a newer git.";
      var configCall = new GitCall("Git could not be checked", ["config", "--get", ProbeKey], Options.QueryTimeout)
      {
         WorkingDirectory = workingDirectory,
         SuccessExitCodes = [0, 1],
      };
      var config = Succeeded(configCall, await RunCoreAsync(configCall, [(ProbeKey, "yes")], answer: null, CancellationToken.None));
      if (config.Text.Trim() != "yes")
      {
         throw new GitException(tooOld);
      }

      // A git without the subcommand exits with an error, which the documentation says means no capabilities (https://git-scm.com/docs/git-credential).
      var capabilityCall = new GitCall("Git could not be checked", ["credential", "capability"], Options.QueryTimeout) { WorkingDirectory = workingDirectory };
      var capabilities = await RunCoreAsync(capabilityCall, [], answer: null, CancellationToken.None);
      if (capabilities.ExitCode != 0 || !Encoding.UTF8.GetString(capabilities.Output).Split('\n').Contains("capability authtype"))
      {
         throw new GitException(tooOld);
      }
   }

   private static GitOutput Succeeded(GitCall call, GitRun run) =>
      call.SuccessExitCodes.Contains(run.ExitCode) ? new GitOutput(run.ExitCode, run.Output) : throw new GitException($"{call.Failure}: {Reason(run.Error)}");

   private async Task<GitRun> RunCoreAsync(GitCall call, IReadOnlyList<(string Key, string Value)> extraConfig, GitCredentialAnswer? answer, CancellationToken cancellationToken)
   {
      var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
      var (wanted, refused) = ($"{WantedMarker} {nonce}", $"{RefusedMarker} {nonce}");
      var exited = await Options.Run(Command(call, extraConfig, answer, nonce), cancellationToken) switch
      {
         ProcessOutcome.Exited run => run,
         ProcessOutcome.TimedOut => throw new GitException($"{call.Failure}: git did not finish within {Describe(call.Timeout)}."),
         ProcessOutcome.Canceled => throw new OperationCanceledException(cancellationToken),
         ProcessOutcome.NotStarted { Failure: StartFailure.ExecutableMissing } => throw new GitException($"{call.Failure}: git was not found. Install git and put it on PATH."),
         ProcessOutcome.NotStarted { Failure: StartFailure.WorkingDirectoryMissing } => throw new GitException($"{call.Failure}: the directory {call.WorkingDirectory} does not exist."),
         ProcessOutcome.NotStarted notStarted => throw new GitException($"{call.Failure}: git could not be started: {notStarted.Reason}."),
      };

      // Git has no reason to print the credential, but an error message must not carry it even if git does.
      var error = answer is null ? exited.Error : exited.Error.Replace(answer.Credential, "[redacted]", StringComparison.Ordinal);
      var lines = error.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
      return new GitRun(
         exited.Code,
         exited.Output,
         lines.Contains(wanted),
         lines.Contains(refused),
         string.Join('\n', lines.Where(line => line != wanted && line != refused)));
   }

   private ProcessCommand Command(GitCall call, IReadOnlyList<(string Key, string Value)> extraConfig, GitCredentialAnswer? answer, string nonce)
   {
      // Inherited git variables could point git at another repository, or add traces and configuration that this runner does not control.
      var environment = Environment.GetEnvironmentVariables().Keys
         .Cast<string>()
         .Where(name => name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase))
         .ToDictionary(name => name, string? (_) => null, StringComparer.Ordinal);
      environment["SSH_ASKPASS"] = null;
      environment[NonceVariable] = nonce;
      environment["GIT_TERMINAL_PROMPT"] = "0";
      environment["GIT_LITERAL_PATHSPECS"] = "1";

      // Git's reasons become part of lotsen's English messages, so git must not translate them.
      environment["LC_ALL"] = "C";
      if (call.GitDirectory is { } gitDirectory)
      {
         environment["GIT_DIR"] = gitDirectory;
      }

      if (!call.LazyFetch)
      {
         environment["GIT_NO_LAZY_FETCH"] = "1";
      }

      // The user's and the system's attribute files could change what counts as binary and how a diff reads (pinned by GitQueryTests.Git_reads_neither_the_system_nor_the_global_attribute_file).
      environment["GIT_ATTR_NOSYSTEM"] = "1";

      List<(string Key, string Value)> config =
      [
         // Git ignores this key. The helper reads its value from GIT_CONFIG_VALUE_0.
         (AnswerKey, answer is null ? "" : $"capability[]=authtype\nauthtype={answer.Scheme}\ncredential={answer.Credential}"),

         // Empty values clear what the user's configuration sets, so git runs no credential helper but the runner's own or askpass program, sends no extra header, and reads no global
         // attribute file.
         ("credential.helper", ""),
         ("core.askPass", ""),
         ("http.extraHeader", ""),
         ("core.attributesFile", ""),

         // Git starts no maintenance of its own, so only CloneCache.MaintainAsync maintains a clone, and only while no clone use holds it.
         ("gc.auto", "0"),
         ("maintenance.auto", "false"),

         // A maintenance run otherwise continues in the background after the call returns and its timeout ends.
         ("gc.autoDetach", "false"),
         ("maintenance.autoDetach", "false"),

         // Maintenance's gc packs no refs (https://github.com/git/git/blob/v2.53.0/builtin/gc.c#L848-L855), because a kill during that step leaves packed-refs.lock, and every later gc then
         // fails at that step (https://github.com/git/git/blob/v2.53.0/refs/files-backend.c#L1462) until CloneCache deletes the lock before a fetch. A fetch writes each ref that it changes as
         // its own file, so a clone holds at most one file per ref.
         ("gc.packRefs", "false"),
         .. extraConfig,
      ];
      if (call.Remote is { Credential: not null } remote)
      {
         config.Add(($"credential.{HelperScope(remote.Url)}.helper", Helper));
      }

      environment["GIT_CONFIG_COUNT"] = config.Count.ToString(CultureInfo.InvariantCulture);
      for (var i = 0; i < config.Count; i++)
      {
         environment[$"GIT_CONFIG_KEY_{i}"] = config[i].Key;
         environment[$"GIT_CONFIG_VALUE_{i}"] = config[i].Value;
      }

      return new ProcessCommand(Options.Executable, call.Arguments, call.Timeout)
      {
         WorkingDirectory = call.WorkingDirectory,
         Environment = environment,
         Input = call.Input is null ? null : (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(call.Input),
      };
   }

   /// <summary>The runner's credential helper, as a shell command that git runs with the action, <c>get</c> or <c>erase</c>, as its argument.</summary>
   /// <remarks>
   /// It reads git's request first, so git never writes to a closed pipe. Its answer to <c>get</c> is the protocol's fields from <c>GIT_CONFIG_VALUE_0</c>, or <c>quit=1</c> without them, after
   /// which git fails without asking another helper or the user (https://git-scm.com/docs/gitcredentials). Its markers, each followed by the run's nonce, go to standard error, which git passes through.
   /// </remarks>
   private static readonly string Helper =
      $"!f() {{ cat >/dev/null; case \"$1\" in get) if [ -n \"$GIT_CONFIG_VALUE_0\" ]; then printf '%s\\n' \"$GIT_CONFIG_VALUE_0\"; "
      + $"else echo '{WantedMarker}' \"${NonceVariable}\" >&2; echo quit=1; fi ;; erase) echo '{RefusedMarker}' \"${NonceVariable}\" >&2 ;; esac; }}; f";

   /// <summary>Returns the remote's URL in the form that git asks a credential for, so a configuration key scoped to it matches the remote's own requests.</summary>
   /// <remarks>
   /// Git decodes the path, trims its slashes and percent-encodes every byte but the unreserved characters and <c>/</c> (https://github.com/git/git/blob/v2.53.0/credential.c#L674-L683,
   /// https://github.com/git/git/blob/v2.53.0/strbuf.c#L490-L506). It keeps a raw <c>(</c> or <c>&amp;</c> in a key's URL as given (https://github.com/git/git/blob/v2.53.0/urlmatch.c#L59-L65), so the
   /// key holds the encoded form. <c>GitAuthTests</c> pins remotes with such characters, raw and encoded.
   /// </remarks>
   private static string HelperScope(Uri url)
   {
      var path = Uri.UnescapeDataString(url.AbsolutePath).Trim('/');
      return $"{url.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped)}/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}";
   }

   /// <summary>Returns git's reason from its error output: its error lines without their prefixes, or its last line.</summary>
   private static string Reason(string error)
   {
      var lines = error
         .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
         .Where(line => !line.StartsWith("hint:", StringComparison.Ordinal))
         .ToList();
      List<string> reasons =
      [
         .. lines
            .Where(line => line.StartsWith("fatal:", StringComparison.Ordinal) || line.StartsWith("error:", StringComparison.Ordinal))
            .Select(line => line[(line.IndexOf(':') + 1)..].Trim()),
      ];
      if (reasons.Count == 0 && lines.Count > 0)
      {
         reasons.Add(lines[^1]);
      }

      return reasons.Count == 0 ? "git gave no reason." : string.Join(' ', reasons.Select(line => line.EndsWith('.') ? line : line + "."));
   }

   private static string Describe(TimeSpan timeout) =>
      timeout.TotalSeconds < 120
         ? string.Create(CultureInfo.InvariantCulture, $"{timeout.TotalSeconds:0.#} seconds")
         : string.Create(CultureInfo.InvariantCulture, $"{timeout.TotalMinutes:0.#} minutes");

   /// <summary>One run of git: its exit code, its output, whether the helper printed each marker, and the rest of its error output, with the credential redacted.</summary>
   private sealed record GitRun(int ExitCode, byte[] Output, bool Wanted, bool Refused, string Error);
}
