using Lotsen.Git;
using Lotsen.Platforms;

namespace Lotsen.Tests;

/// <summary>Covers the promises of <see cref="IPullRequestPlatform"/> that every adapter keeps, with one subclass per adapter that supplies the platform and arranges its pull request.</summary>
/// <remarks>A resolve reads no git, so the commits are any valid IDs and the remote is any URL.</remarks>
public abstract class PlatformContractTests
{
   /// <summary>How the subclass makes the next resolve fail.</summary>
   public enum ResolveFailure
   {
      /// <summary>A platform that has no such pull request.</summary>
      MissingPullRequest,

      /// <summary>A platform that refuses the user's token.</summary>
      RefusedToken,

      /// <summary>A token provider that has no token for the user.</summary>
      NoToken,

      /// <summary>A platform that nothing answers at.</summary>
      UnreachableHost,
   }

   private static readonly CommitId First = CommitId.Parse(new string('1', 40));
   private static readonly CommitId Second = CommitId.Parse(new string('2', 40));
   private static readonly CommitId Third = CommitId.Parse(new string('3', 40));
   private static readonly CommitId Target = CommitId.Parse(new string('a', 40));
   private static readonly CommitId MovedTarget = CommitId.Parse(new string('b', 40));

   /// <summary>The adapter under test.</summary>
   protected abstract IPullRequestPlatform Platform { get; }

   /// <summary>Texts that name the subclass's one pull request in different forms. The first is the normalized form.</summary>
   protected abstract IReadOnlyList<string> PullRequestForms { get; }

   /// <summary>Texts that name the subclass's one pull request in forms that <see cref="IPullRequestPlatform.Recognize"/> normalizes to URLs other than the normalized form.</summary>
   protected abstract IReadOnlyList<string> SamePullRequestInOtherForms { get; }

   /// <summary>Texts that name other pull requests: another number in the same repository, and the same number in another repository and in each other scope that the platform has.</summary>
   protected abstract IReadOnlyList<string> OtherPullRequests { get; }

   /// <summary>Adds an iteration with this source head to the subclass's pull request, which then reports this state.</summary>
   protected abstract void Push(CommitId source, CommitId target, PullRequestState state = PullRequestState.Active);

   /// <summary>Makes every later resolve fail in this way.</summary>
   protected abstract void Fail(ResolveFailure failure);

   private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

   private Uri PullRequestUrl => Platform.Recognize(PullRequestForms[0])!;

   [Fact]
   public void Every_form_of_the_pull_request_is_recognized_as_its_normalized_url()
   {
      var normalized = Platform.Recognize(PullRequestForms[0]);

      Assert.NotNull(normalized);
      Assert.Equal(PullRequestForms[0], normalized.AbsoluteUri);
      Assert.All(PullRequestForms, form => Assert.Equal(normalized, Platform.Recognize(form)));
   }

   [Fact]
   public void Recognized_url_is_recognized_again_as_itself()
   {
      Assert.All(PullRequestForms, form =>
      {
         var recognized = Platform.Recognize(form)!;
         Assert.Equal(recognized, Platform.Recognize(recognized.AbsoluteUri));
      });
   }

   [Fact]
   public void Every_recognized_url_has_a_label()
   {
      Assert.All(PullRequestForms, form => Assert.False(string.IsNullOrWhiteSpace(Platform.Label(Platform.Recognize(form)!)), $"'{form}' has no label."));
   }

   [Fact]
   public void Url_of_another_host_is_neither_recognized_nor_labeled()
   {
      const string Other = "https://example.invalid/shop/pull/42";

      Assert.Null(Platform.Recognize(Other));
      Assert.Null(Platform.Label(new Uri(Other)));
   }

   [Fact]
   public void Every_form_of_the_pull_request_names_the_same_pull_request()
   {
      Assert.All(PullRequestForms.Concat(SamePullRequestInOtherForms), form =>
      {
         var url = Platform.Recognize(form)!;
         Assert.True(Platform.IsSamePullRequest(PullRequestUrl, url), $"'{form}' does not name the same pull request as {PullRequestUrl}.");
         Assert.True(Platform.IsSamePullRequest(url, PullRequestUrl), $"{PullRequestUrl} does not name the same pull request as '{form}'.");
      });
   }

   [Fact]
   public void Other_forms_normalize_to_other_urls()
   {
      Assert.NotEmpty(SamePullRequestInOtherForms);
      Assert.All(SamePullRequestInOtherForms, form => Assert.NotEqual(PullRequestUrl, Platform.Recognize(form)));
   }

   [Fact]
   public void Other_pull_requests_are_not_the_same_pull_request()
   {
      Assert.NotEmpty(OtherPullRequests);
      Assert.All(OtherPullRequests, text =>
      {
         var other = Platform.Recognize(text);
         Assert.NotNull(other);
         Assert.False(Platform.IsSamePullRequest(PullRequestUrl, other), $"'{text}' names the same pull request as {PullRequestUrl}.");
         Assert.False(Platform.IsSamePullRequest(other, PullRequestUrl), $"{PullRequestUrl} names the same pull request as '{text}'.");
      });
   }

   [Theory]
   [InlineData(ResolveFailure.MissingPullRequest)]
   [InlineData(ResolveFailure.RefusedToken)]
   [InlineData(ResolveFailure.NoToken)]
   [InlineData(ResolveFailure.UnreachableHost)]
   public async Task Resolve_that_fails_throws_an_external_failure_with_a_reason(ResolveFailure failure)
   {
      Push(First, Target);
      Fail(failure);

      var thrown = await Assert.ThrowsAnyAsync<ExternalFailureException>(() => Platform.ResolveAsync(PullRequestUrl, Cancellation));

      Assert.False(string.IsNullOrWhiteSpace(thrown.Reason));
   }

   [Theory]
   [InlineData(PullRequestState.Active)]
   [InlineData(PullRequestState.Completed)]
   [InlineData(PullRequestState.Abandoned)]
   public async Task Resolve_reports_the_pull_requests_state(PullRequestState state)
   {
      Push(First, Target, state);

      var resolved = await Platform.ResolveAsync(PullRequestUrl, Cancellation);

      Assert.Equal(state, resolved.State);
   }

   [Fact]
   public async Task Iteration_number_starts_at_one_or_more_and_rises_with_each_new_source_head()
   {
      Push(First, Target);
      var first = await Platform.ResolveAsync(PullRequestUrl, Cancellation);
      Push(Second, Target);
      var second = await Platform.ResolveAsync(PullRequestUrl, Cancellation);
      Push(Third, MovedTarget);
      var third = await Platform.ResolveAsync(PullRequestUrl, Cancellation);

      Assert.Equal((First, Target), (first.SourceCommit, first.TargetCommit));
      Assert.Equal((Third, MovedTarget), (third.SourceCommit, third.TargetCommit));
      Assert.True(first.Iteration >= 1, $"The first iteration is numbered {first.Iteration}.");
      Assert.True(second.Iteration > first.Iteration, $"Iteration {second.Iteration} follows iteration {first.Iteration}.");
      Assert.True(third.Iteration > second.Iteration, $"Iteration {third.Iteration} follows iteration {second.Iteration}.");
   }

   [Fact]
   public async Task Iteration_number_rises_again_when_the_source_head_returns_to_an_earlier_head()
   {
      Push(First, Target);
      var first = await Platform.ResolveAsync(PullRequestUrl, Cancellation);
      Push(Second, Target);
      var second = await Platform.ResolveAsync(PullRequestUrl, Cancellation);
      Push(First, Target);
      var back = await Platform.ResolveAsync(PullRequestUrl, Cancellation);

      Assert.Equal(First, back.SourceCommit);
      Assert.True(second.Iteration > first.Iteration, $"Iteration {second.Iteration} follows iteration {first.Iteration}.");
      Assert.True(back.Iteration > second.Iteration, $"Iteration {back.Iteration} follows iteration {second.Iteration}, though its head is the first iteration's.");
   }

   [Fact]
   public void Git_credential_names_the_fix_for_a_refusal()
   {
      Assert.False(string.IsNullOrWhiteSpace(Platform.GitCredential.FixHint));
   }
}
