using System.Text;
using Lotsen.Credentials;

namespace Lotsen.GitHub;

/// <summary>Presents the token provider's GitHub token to git, as the password of the user name <c>x-access-token</c> in the basic scheme.</summary>
/// <remarks>A refused answer rejects its token at the provider, so the next answer after a new sign-in carries the new token.</remarks>
internal sealed class GitHubGitCredential : IGitCredential
{
   private readonly IAccessTokenProvider _tokens;

   public GitHubGitCredential(IAccessTokenProvider tokens)
   {
      _tokens = tokens;
   }

   public string FixHint => "Run 'gh auth login' with an account that can read the repository, then try again.";

   public async ValueTask<GitCredentialAnswer> AnswerAsync(TimeSpan minimumValidity, CancellationToken cancellationToken)
   {
      var token = await _tokens.GetTokenAsync(minimumValidity, cancellationToken);
      var credential = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token.Value}"));
      return new GitCredentialAnswer("Basic", credential, () => _tokens.Reject(token));
   }
}
