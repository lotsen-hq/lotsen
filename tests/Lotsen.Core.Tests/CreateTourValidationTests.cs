using System.Net;
using System.Text.Json.Nodes;
using Lotsen.Git;
using Lotsen.Tours;

namespace Lotsen.Tests;

/// <summary>Covers how <c>create_tour</c> checks its target: exactly one of a pull request URL and a repository, a repository that a platform recognizes, its ref and a repository tour's fields.</summary>
[Trait("Category", "Validation")]
public sealed class CreateTourValidationTests : IAsyncLifetime
{
   private const string Repository = $"https://{TestPlatform.DefaultHost}/shop";

   private static readonly string LongestTitle = new('t', TourRules.MaxTitleLength);

   private ServerHarness _harness = null!;

   public async ValueTask InitializeAsync() => _harness = await ServerHarness.StartAsync();

   public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

   public static TheoryData<string, string?, string> Refused => new()
   {
      { $$"""{ "prUrl": "{{TestPlatform.PrUrl}}", {{RepositoryProperty()}}, "familiarity": "some" }""", "repository", "give either prUrl" },
      { """{ "familiarity": "some" }""", null, "give prUrl" },
      { $$"""{ {{RepositoryProperty(url: "git@third.test:shop.git")}}, "familiarity": "some" }""", "repository.url", "is not a repository URL that lotsen recognizes" },
      { $$"""{ {{RepositoryProperty(url: "http://third.test/shop")}}, "familiarity": "some" }""", "repository.url", "is not a repository URL that lotsen recognizes" },
      { $$"""{ {{RepositoryProperty(url: TestPlatform.PrUrl)}}, "familiarity": "some" }""", "repository.url", "is not a repository URL that lotsen recognizes" },
      { $$"""{ {{RepositoryProperty(@ref: "a..b")}}, "familiarity": "some" }""", "repository.ref", "is not a branch, tag or full commit ID" },
      { $$"""{ {{RepositoryProperty(url: Repository + "/tree/main", @ref: "feature")}}, "familiarity": "some" }""", "repository.ref", "Give the ref once" },
      { $$"""{ {{RepositoryProperty(title: "")}}, "familiarity": "some" }""", "repository.title", "needs a title" },
      { $$"""{ {{RepositoryProperty(title: "   ")}}, "familiarity": "some" }""", "repository.title", "needs a title" },
      { $$"""{ {{RepositoryProperty(title: "Two\nlines")}}, "familiarity": "some" }""", "repository.title", "one line of plain text" },
      { $$"""{ {{RepositoryProperty(title: LongestTitle + "t")}}, "familiarity": "some" }""", "repository.title", $"at most {TourRules.MaxTitleLength} characters" },
      { $$"""{ {{RepositoryProperty(words: " ")}}, "familiarity": "some" }""", "repository.words", "omit words" },
      { $$"""{ "repository": { "url": "{{Repository}}", "title": "T" }, "familiarity": "some" }""", "repository.purpose", "missing required property" },
      { $$"""{ "repository": { "url": "{{Repository}}", "purpose": "orient" }, "familiarity": "some" }""", "repository.title", "missing required property" },
      { $$"""{ {{RepositoryProperty(purpose: "explore")}}, "familiarity": "some" }""", "repository.purpose", "" },
      { $$"""{ "repository": { "url": "{{Repository}}", "purpose": "orient", "title": "T", "branch": "main" }, "familiarity": "some" }""", "repository.branch", "" },
   };

   [Theory]
   [MemberData(nameof(Refused))]
   public async Task Create_tour_with_an_invalid_target_is_refused_with_its_field_and_nothing_is_stored(string body, string? field, string reason)
   {
      using var response = await _harness.AgentSendJsonAsync("create_tour", body);

      var error = await ThreadCalls.ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal(field, (string?)error["field"]);
      Assert.Contains(reason, (string)error["reason"]!, StringComparison.Ordinal);
      Assert.Empty((await _harness.CallAsync("list_tours"))["tours"]!.AsArray());
   }

   [Fact]
   public async Task Repository_url_that_no_platform_recognizes_is_refused_with_every_platforms_repository_forms()
   {
      var first = new TestPlatform("first", "first.test");
      var second = new TestPlatform("second", "second.test");
      await using var harness = await ServerHarness.StartAsync(configure: options => options with { Platforms = [_ => first, _ => second] });

      using var response = await harness.AgentSendJsonAsync("create_tour", $$"""{ {{RepositoryProperty(url: "git@third.test:shop.git")}}, "familiarity": "some" }""");

      var error = await ThreadCalls.ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
      Assert.Equal(
         $"repository.url: 'git@third.test:shop.git' is not a repository URL that lotsen recognizes. Expected {first.ExpectedRepositoryForms}; or {second.ExpectedRepositoryForms}; or "
         + $"{PlatformFreeRepositories.ExpectedForms}.",
         (string)error["reason"]!);
   }

   /// <summary>A URL with user information can hold a token, which a refusal must not repeat to Claude.</summary>
   [Theory]
   [InlineData("https://user:s3cr3t-token@third.test/shop")]
   [InlineData("https://s3cr3t-token@github.com/octo/hello")]
   [InlineData("ssh://git:s3cr3t-token@platform.test/shop")]
   public async Task Repository_url_with_user_information_is_refused_without_repeating_it(string url)
   {
      using var created = await _harness.AgentSendJsonAsync("create_tour", $$"""{ {{RepositoryProperty(url: url)}}, "familiarity": "some" }""");
      using var found = await _harness.AgentSendJsonAsync("find_tours", $$"""{ "repository": { "url": "{{url}}" } }""");

      foreach (var response in new[] { created, found })
      {
         var error = await ThreadCalls.ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
         Assert.Equal("repository.url", (string)error["field"]!);
         Assert.DoesNotContain("s3cr3t-token", (string)error["reason"]!, StringComparison.Ordinal);
         Assert.Contains("user information", (string)error["reason"]!, StringComparison.Ordinal);
      }
   }

   /// <summary>A pull request URL with user information can hold a token too, so its refusal keeps to the same rule.</summary>
   [Theory]
   [InlineData("https://user:s3cr3t@platform.test/shop/issues/42")]
   [InlineData("https://s3cr3t@github.com/octo/hello/pull/1")]
   [InlineData("ssh://git:s3cr3t@platform.test/shop/pull/42")]
   public async Task Pull_request_url_with_user_information_is_refused_without_repeating_it(string url)
   {
      using var created = await _harness.AgentSendJsonAsync("create_tour", $$"""{ "prUrl": "{{url}}", "familiarity": "some" }""");
      using var found = await _harness.AgentSendJsonAsync("find_tours", $$"""{ "prUrl": "{{url}}" }""");

      foreach (var response in new[] { created, found })
      {
         var error = await ThreadCalls.ErrorAsync(response, HttpStatusCode.BadRequest, "invalidInput");
         Assert.Equal("prUrl", (string)error["field"]!);
         Assert.DoesNotContain("s3cr3t", (string)error["reason"]!, StringComparison.Ordinal);
         Assert.Contains("user information", (string)error["reason"]!, StringComparison.Ordinal);
         Assert.Contains("is not a pull request URL that lotsen recognizes", (string)error["reason"]!, StringComparison.Ordinal);
      }
   }

   [Fact]
   public async Task Title_of_the_longest_length_and_a_ref_that_the_url_names_too_are_accepted()
   {
      var created = await _harness.AgentCallAsync("create_tour", $$"""{ {{RepositoryProperty(url: Repository + "/tree/feature", @ref: "feature", title: LongestTitle)}}, "familiarity": "some" }""");

      Assert.Equal(LongestTitle, (string)created["title"]!);
      Assert.Equal(("feature", Repository), ((string)created["target"]!["ref"]!, (string)created["target"]!["url"]!));
   }

   [Fact]
   public async Task Ref_that_the_url_names_is_the_tours_ref()
   {
      var created = await _harness.AgentCallAsync("create_tour", $$"""{ {{RepositoryProperty(url: Repository + "/tree/release/1.0")}}, "familiarity": "some" }""");

      Assert.True(
         JsonNode.DeepEquals(JsonNode.Parse($$"""{ "kind": "repository", "url": "{{Repository}}", "ref": "release/1.0", "platform": "{{TestPlatform.DefaultId}}" }"""), created["target"]),
         created["target"]!.ToJsonString());
   }

   private static string RepositoryProperty(string url = Repository, string? @ref = null, string purpose = "orient", string title = "How the shop is built", string? words = null)
   {
      var repository = new JsonObject { ["url"] = url, ["purpose"] = purpose, ["title"] = title };
      if (@ref is not null)
      {
         repository["ref"] = @ref;
      }

      if (words is not null)
      {
         repository["words"] = words;
      }

      return $"\"repository\": {repository.ToJsonString()}";
   }
}
