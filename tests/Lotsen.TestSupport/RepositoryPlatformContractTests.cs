using Lotsen.Git;
using Lotsen.Platforms;

namespace Lotsen.Tests;

/// <summary>Covers the promises of <see cref="IRepositoryPlatform"/> that every adapter keeps, with one subclass per adapter that supplies the platform and arranges its one repository.</summary>
/// <remarks>A resolve reads no git, so the commits are any valid IDs.</remarks>
public abstract class RepositoryPlatformContractTests
{
   /// <summary>How the subclass makes the next resolve fail.</summary>
   public enum ResolveFailure
   {
      /// <summary>A platform that has no such repository.</summary>
      MissingRepository,

      /// <summary>A platform that refuses the user's token.</summary>
      RefusedToken,

      /// <summary>A token provider that has no token for the user.</summary>
      NoToken,

      /// <summary>A platform that nothing answers at.</summary>
      UnreachableHost,
   }

   private const string DefaultBranch = "trunk";

   private static readonly CommitId TrunkHead = CommitId.Parse(new string('1', 40));
   private static readonly CommitId FeatureHead = CommitId.Parse(new string('2', 40));
   private static readonly CommitId Release = CommitId.Parse(new string('3', 40));
   private static readonly CommitId Older = CommitId.Parse(new string('4', 40));
   private static readonly CommitId SharedTag = CommitId.Parse(new string('5', 40));
   private static readonly CommitId SharedBranch = CommitId.Parse(new string('6', 40));

   /// <summary>The adapter under test.</summary>
   protected abstract IRepositoryPlatform Platform { get; }

   /// <summary>Texts that name the subclass's one repository, without a ref, in different forms. The first is the normalized form.</summary>
   protected abstract IReadOnlyList<string> RepositoryForms { get; }

   /// <summary>Texts that name other repositories of the platform, in the same scope and in each other scope that the platform has.</summary>
   protected abstract IReadOnlyList<string> OtherRepositories { get; }

   /// <summary>Texts that name the subclass's one repository but recognize as another URL, such as in another letter case where the platform ignores it.</summary>
   protected abstract IReadOnlyList<string> SameRepositoryForms { get; }

   /// <summary>A pull request URL of the subclass's repository, which names no repository.</summary>
   protected abstract string PullRequestOfTheRepository { get; }

   /// <summary>Returns a text that names the subclass's one repository at the ref, as a web page of the repository at a branch does.</summary>
   protected abstract string AtRef(string @ref);

   /// <summary>Makes the subclass's repository have these branches, tags and commits, which every later resolve reads.</summary>
   /// <param name="defaultBranch">The name of the default branch, which <paramref name="branches"/> holds.</param>
   /// <param name="branches">The branches by name, with the commit each points to.</param>
   /// <param name="tags">The tags by name, with the commit each names.</param>
   /// <param name="commits">The commits that no branch or tag points to, which the repository has too.</param>
   protected abstract void Arrange(string defaultBranch, IReadOnlyDictionary<string, CommitId> branches, IReadOnlyDictionary<string, CommitId> tags, IReadOnlyList<CommitId> commits);

   /// <summary>Makes every later resolve fail in this way.</summary>
   protected abstract void Fail(ResolveFailure failure);

   private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

   private Uri RepositoryUrl => Platform.RecognizeRepository(RepositoryForms[0])!.Url;

   [Fact]
   public void Every_form_of_the_repository_is_recognized_as_its_normalized_url_without_a_ref()
   {
      var normalized = Platform.RecognizeRepository(RepositoryForms[0]);

      Assert.NotNull(normalized);
      Assert.Equal(RepositoryForms[0], normalized.Url.AbsoluteUri);
      Assert.All(RepositoryForms, form => Assert.Equal(normalized with { Ref = null }, Platform.RecognizeRepository(form)));
   }

   [Fact]
   public void Recognized_url_is_recognized_again_as_itself()
   {
      Assert.All(RepositoryForms.Append(AtRef("main")), form =>
      {
         var recognized = Platform.RecognizeRepository(form)!;
         Assert.Equal(new RecognizedRepository(recognized.Url, null), Platform.RecognizeRepository(recognized.Url.AbsoluteUri));
      });
   }

   [Theory]
   [InlineData("main")]
   [InlineData("v1.2.0")]
   [InlineData("feature/greeting")]
   public void Url_at_a_ref_is_recognized_as_the_normalized_url_with_that_ref(string @ref)
   {
      Assert.Equal(new RecognizedRepository(RepositoryUrl, @ref), Platform.RecognizeRepository(AtRef(@ref)));
   }

   /// <summary>Each case breaks one of the rules of <see cref="RefName.IsValid"/>, written as a URL path encodes it.</summary>
   [Theory]
   [InlineData("%20")]
   [InlineData("x%2F..%2Fy")]
   [InlineData(".hidden")]
   [InlineData("v1.lock")]
   [InlineData("a~1")]
   [InlineData("a.")]
   public void Url_at_an_invalid_ref_is_not_recognized(string encodedRef)
   {
      Assert.Null(Platform.RecognizeRepository(AtRef(encodedRef)));
   }

   /// <summary>The registry reads an https URL on an owned host as no repository without a platform, so the platform's own URLs never become one.</summary>
   [Fact]
   public void Platform_owns_the_host_of_every_https_form_and_recognized_repository_url()
   {
      Assert.All(RepositoryForms.Concat(OtherRepositories).Append(AtRef("main")).Append(PullRequestOfTheRepository), form =>
      {
         Assert.True(!form.StartsWith("https://", StringComparison.Ordinal) || Platform.OwnsHost(new Uri(form)), form);
         Assert.True(!form.StartsWith("https://", StringComparison.Ordinal) || Platform.OwnsHost(PlatformContractTests.WithTrailingDot(new Uri(form))), form);
         Assert.True(Platform.RecognizeRepository(form) is not { } recognized || Platform.OwnsHost(recognized.Url), form);
      });
   }

   [Fact]
   public void Other_repositories_are_recognized_as_other_urls()
   {
      Assert.NotEmpty(OtherRepositories);
      Assert.All(OtherRepositories, text =>
      {
         var other = Platform.RecognizeRepository(text);
         Assert.NotNull(other);
         Assert.NotEqual(RepositoryUrl, other.Url);
      });
   }

   [Fact]
   public void Every_form_of_the_repository_names_the_same_repository_and_other_repositories_do_not()
   {
      Assert.NotEmpty(SameRepositoryForms);
      Assert.All(RepositoryForms.Concat(SameRepositoryForms), form => Assert.True(Platform.IsSameRepository(RepositoryUrl, Platform.RecognizeRepository(form)!.Url), $"'{form}' names another repository."));
      Assert.All(OtherRepositories, text => Assert.False(Platform.IsSameRepository(RepositoryUrl, Platform.RecognizeRepository(text)!.Url), $"'{text}' names the same repository."));
   }

   [Fact]
   public void Pull_request_url_is_no_repository_and_repository_url_is_no_pull_request()
   {
      Assert.Null(Platform.RecognizeRepository(PullRequestOfTheRepository));
      Assert.All(RepositoryForms.Append(AtRef("main")), form => Assert.Null(Platform.Recognize(form)));
   }

   [Fact]
   public void Every_recognized_url_has_a_label()
   {
      Assert.All(RepositoryForms, form => Assert.False(string.IsNullOrWhiteSpace(Platform.RepositoryLabel(Platform.RecognizeRepository(form)!.Url)), $"'{form}' has no label."));
   }

   [Fact]
   public void Url_of_another_host_is_neither_recognized_nor_labeled_nor_mentioned()
   {
      const string Other = "https://example.invalid/shop";

      Assert.Null(Platform.RecognizeRepository(Other));
      Assert.Null(Platform.RepositoryLabel(new Uri(Other)));
      Assert.Null(Platform.RepositoryMentionUrl(new Uri(Other), MentionKind.WorkItem, 7));
      Assert.Null(Platform.RepositoryMentionUrl(new Uri(Other), MentionKind.PullRequest, 7));
   }

   [Theory]
   [InlineData(MentionKind.WorkItem)]
   [InlineData(MentionKind.PullRequest)]
   public void Mention_of_a_recognized_repository_has_an_absolute_https_url(MentionKind kind)
   {
      Assert.All(RepositoryForms, form =>
      {
         var url = Platform.RepositoryMentionUrl(Platform.RecognizeRepository(form)!.Url, kind, 7);
         Assert.NotNull(url);
         Assert.True(url.IsAbsoluteUri, $"The mention URL for '{form}' is not absolute.");
         Assert.Equal(Uri.UriSchemeHttps, url.Scheme);
      });
   }

   [Fact]
   public void Mentions_of_different_kinds_and_ids_have_different_urls()
   {
      (MentionKind Kind, int Id)[] mentions = [(MentionKind.WorkItem, 7), (MentionKind.WorkItem, 8), (MentionKind.PullRequest, 7)];

      var urls = mentions.Select(mention => Platform.RepositoryMentionUrl(RepositoryUrl, mention.Kind, mention.Id)).ToList();

      Assert.Equal(mentions.Length, urls.Distinct().Count());
   }

   [Fact]
   public async Task Resolve_without_a_ref_reads_the_default_branch()
   {
      ArrangeRepository();

      var resolved = await Platform.ResolveRepositoryAsync(RepositoryUrl, null, Cancellation);

      Assert.Equal((DefaultBranch, RepositoryRefKind.Branch, TrunkHead), (resolved.Ref, resolved.Kind, resolved.Commit));
      Assert.True(resolved.RemoteUrl.IsAbsoluteUri, $"The remote URL {resolved.RemoteUrl} is not absolute.");
      Assert.Empty(resolved.RemoteUrl.UserInfo);
   }

   [Theory]
   [InlineData(DefaultBranch, RepositoryRefKind.Branch, '1')]
   [InlineData("feature/greeting", RepositoryRefKind.Branch, '2')]
   [InlineData("v1.2.0", RepositoryRefKind.Tag, '3')]
   [InlineData("both", RepositoryRefKind.Tag, '5')]
   public async Task Resolve_of_a_branch_or_a_tag_reads_its_commit(string @ref, RepositoryRefKind kind, char commit)
   {
      ArrangeRepository();

      var resolved = await Platform.ResolveRepositoryAsync(RepositoryUrl, @ref, Cancellation);

      Assert.Equal((@ref, kind, CommitId.Parse(new string(commit, 40))), (resolved.Ref, resolved.Kind, resolved.Commit));
   }

   [Theory]
   [InlineData('4')]
   [InlineData('2')]
   public async Task Resolve_of_a_full_commit_id_reads_that_commit(char commit)
   {
      ArrangeRepository();
      var id = CommitId.Parse(new string(commit, 40));

      var resolved = await Platform.ResolveRepositoryAsync(RepositoryUrl, id.Value.ToUpperInvariant(), Cancellation);

      Assert.Equal((id.Value, RepositoryRefKind.Commit, id), (resolved.Ref, resolved.Kind, resolved.Commit));
   }

   [Theory]
   [InlineData("gone")]
   [InlineData("feature")]
   [InlineData("4444444")]
   [InlineData("7777777777777777777777777777777777777777")]
   public async Task Resolve_of_a_missing_ref_throws_an_external_failure_with_a_reason(string @ref)
   {
      ArrangeRepository();

      var thrown = await Assert.ThrowsAnyAsync<ExternalFailureException>(() => Platform.ResolveRepositoryAsync(RepositoryUrl, @ref, Cancellation));

      Assert.False(string.IsNullOrWhiteSpace(thrown.Reason));
   }

   [Theory]
   [InlineData(" ")]
   [InlineData("x/../../heads/trunk")]
   [InlineData("trunk..x")]
   [InlineData("a~1")]
   public async Task Resolve_of_an_invalid_ref_throws_an_external_failure_with_a_reason(string @ref)
   {
      ArrangeRepository();

      var thrown = await Assert.ThrowsAnyAsync<ExternalFailureException>(() => Platform.ResolveRepositoryAsync(RepositoryUrl, @ref, Cancellation));

      Assert.False(string.IsNullOrWhiteSpace(thrown.Reason));
   }

   [Theory]
   [InlineData(ResolveFailure.MissingRepository)]
   [InlineData(ResolveFailure.RefusedToken)]
   [InlineData(ResolveFailure.NoToken)]
   [InlineData(ResolveFailure.UnreachableHost)]
   public async Task Resolve_that_fails_throws_an_external_failure_with_a_reason(ResolveFailure failure)
   {
      ArrangeRepository();
      Fail(failure);

      var thrown = await Assert.ThrowsAnyAsync<ExternalFailureException>(() => Platform.ResolveRepositoryAsync(RepositoryUrl, null, Cancellation));

      Assert.False(string.IsNullOrWhiteSpace(thrown.Reason));
   }

   [Fact]
   public void Git_credential_names_the_fix_for_a_refusal()
   {
      Assert.False(string.IsNullOrWhiteSpace(Platform.GitCredential.FixHint));
   }

   // "both" names a tag and a branch, so the tag wins.
   private void ArrangeRepository() => Arrange(
      DefaultBranch,
      new Dictionary<string, CommitId> { [DefaultBranch] = TrunkHead, ["feature/greeting"] = FeatureHead, ["both"] = SharedBranch },
      new Dictionary<string, CommitId> { ["v1.2.0"] = Release, ["both"] = SharedTag },
      [Older]);
}
