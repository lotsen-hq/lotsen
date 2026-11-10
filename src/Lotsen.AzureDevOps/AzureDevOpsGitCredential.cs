using Lotsen.Credentials;

namespace Lotsen.AzureDevOps;

/// <summary>Presents the token provider's Azure DevOps token to git, in the bearer scheme that the REST client sends too.</summary>
/// <remarks>A refused answer rejects its token at the provider, so the next answer after a new sign-in carries the new token.</remarks>
internal sealed class AzureDevOpsGitCredential : IGitCredential
{
   private readonly IAccessTokenProvider _tokens;

   public AzureDevOpsGitCredential(IAccessTokenProvider tokens)
   {
      _tokens = tokens;
   }

   public string FixHint => "Run 'az login' with an account that can read the repository, then try again.";

   public async ValueTask<GitCredentialAnswer> AnswerAsync(TimeSpan minimumValidity, CancellationToken cancellationToken)
   {
      var token = await _tokens.GetTokenAsync(minimumValidity, cancellationToken);
      return new GitCredentialAnswer("Bearer", token.Value, () => _tokens.Reject(token));
   }
}
