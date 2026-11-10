using Lotsen.Operations;
using Lotsen.Prose;
using Lotsen.Tours;

namespace Lotsen.Tests;

/// <summary>Covers which Markdown constructs <see cref="ProseRenderer"/> renders as HTML, and that every other construct reaches the output only as escaped text.</summary>
[Trait("Category", "Markdown")]
public sealed class ProseRendererTests
{
   private const string Rel = "rel=\"noopener noreferrer\" target=\"_blank\"";

   [Theory]
   [InlineData("Plain text.", "<p>Plain text.</p>\n")]
   [InlineData("a < b & \"c\" > 'd'", "<p>a &lt; b &amp; &quot;c&quot; &gt; &#39;d&#39;</p>\n")]
   [InlineData("One\n\nTwo", "<p>One</p>\n<p>Two</p>\n")]
   [InlineData("soft\nbreak", "<p>soft\nbreak</p>\n")]
   [InlineData("hard  \nbreak", "<p>hard<br>\nbreak</p>\n")]
   [InlineData("*em* _em_ **strong** __strong__", "<p><em>em</em> <em>em</em> <strong>strong</strong> <strong>strong</strong></p>\n")]
   [InlineData("***both***", "<p><em><strong>both</strong></em></p>\n")]
   [InlineData("Call `Run(<T>)` now", "<p>Call <code>Run(&lt;T&gt;)</code> now</p>\n")]
   [InlineData("- a\n- b", "<ul>\n<li>a</li>\n<li>b</li>\n</ul>\n")]
   [InlineData("1. a\n2. b", "<ol>\n<li>a</li>\n<li>b</li>\n</ol>\n")]
   [InlineData("3. a\n4. b", "<ol start=\"3\">\n<li>a</li>\n<li>b</li>\n</ol>\n")]
   [InlineData("- a\n\n- b", "<ul>\n<li><p>a</p>\n</li>\n<li><p>b</p>\n</li>\n</ul>\n")]
   [InlineData("- a\n  - b", "<ul>\n<li>a<ul>\n<li>b</li>\n</ul>\n</li>\n</ul>\n")]
   [InlineData("- first\n  > second\n  >\n  > third", "<ul>\n<li>first<p>second</p>\n<p>third</p>\n</li>\n</ul>\n")]
   [InlineData("- a\n\n  > b", "<ul>\n<li><p>a</p>\n<p>b</p>\n</li>\n</ul>\n")]
   [InlineData("- a\n  > b\n  > - c\n  >\n  >   d", "<ul>\n<li>a<p>b</p>\n<ul>\n<li><p>c</p>\n<p>d</p>\n</li>\n</ul>\n</li>\n</ul>\n")]
   [InlineData("```csharp\nif (a < b) { }\n```", "<pre><code>if (a &lt; b) { }\n</code></pre>\n")]
   [InlineData("```\"><script>\nx\n```", "<pre><code>x\n</code></pre>\n")]
   [InlineData("    indented & code", "<pre><code>indented &amp; code\n</code></pre>\n")]
   public void Supported_construct_renders_as_html(string markdown, string html)
   {
      Assert.Equal(html, ProseRenderer.Render(markdown).Html);
   }

   [Theory]
   [InlineData("# Heading", "<p>Heading</p>\n")]
   [InlineData("Setext\n===", "<p>Setext</p>\n")]
   [InlineData("> quoted *text*", "<p>quoted <em>text</em></p>\n")]
   [InlineData("***", "<p>***</p>\n")]
   [InlineData("| a | b |\n|---|---|\n| 1 | 2 |", "<p>| a | b |\n|---|---|\n| 1 | 2 |</p>\n")]
   [InlineData("*x*{onclick=alert(1)}", "<p><em>x</em>{onclick=alert(1)}</p>\n")]
   [InlineData("~~struck~~", "<p>~~struck~~</p>\n")]
   [InlineData("See https://example.com/x now", "<p>See https://example.com/x now</p>\n")]
   [InlineData("- [ ] task", "<ul>\n<li>[ ] task</li>\n</ul>\n")]
   public void Construct_outside_the_subset_renders_as_its_text(string markdown, string html)
   {
      Assert.Equal(html, ProseRenderer.Render(markdown).Html);
   }

   [Theory]
   [InlineData("<script>alert(1)</script>", "&lt;script&gt;alert(1)&lt;/script&gt;")]
   [InlineData("<div onclick=\"alert(1)\">\n\nx\n\n</div>", "&lt;div onclick=&quot;alert(1)&quot;&gt;")]
   [InlineData("Text <img src=x onerror=alert(1)> here", "Text &lt;img src=x onerror=alert(1)&gt; here")]
   [InlineData("<!-- hidden -->", "&lt;!-- hidden --&gt;")]
   [InlineData("<![CDATA[x]]>", "&lt;![CDATA[x]]&gt;")]
   [InlineData("&lt;script&gt;alert(1)&lt;/script&gt;", "&lt;script&gt;alert(1)&lt;/script&gt;")]
   [InlineData("&#60;b&#62; and &#x3C;i&#x3E;", "&lt;b&gt; and &lt;i&gt;")]
   [InlineData("`<b>`", "<code>&lt;b&gt;</code>")]
   public void Raw_html_is_escaped(string markdown, string expectedFragment)
   {
      var html = ProseRenderer.Render(markdown).Html;

      Assert.Contains(expectedFragment, html, StringComparison.Ordinal);
      AssertOnlyAllowedTags(html);
   }

   [Theory]
   [InlineData("[docs](https://example.com/docs)", "<p><a href=\"https://example.com/docs\" " + Rel + ">docs</a> (https://example.com/docs)</p>\n")]
   [InlineData("[*docs*](HTTPS://Example.com/a?b=1&c=2)", "<p><a href=\"https://example.com/a?b=1&amp;c=2\" " + Rel + "><em>docs</em></a> (https://example.com/a?b=1&amp;c=2)</p>\n")]
   [InlineData("[https://example.com/a](https://example.com/a)", "<p><a href=\"https://example.com/a\" " + Rel + ">https://example.com/a</a></p>\n")]
   [InlineData("<https://example.com/a>", "<p><a href=\"https://example.com/a\" " + Rel + ">https://example.com/a</a></p>\n")]
   [InlineData("[](https://example.com/a)", "<p><a href=\"https://example.com/a\" " + Rel + ">https://example.com/a</a></p>\n")]
   [InlineData("[docs](https://example.com/a \"A title\")", "<p><a href=\"https://example.com/a\" " + Rel + ">docs</a> (https://example.com/a)</p>\n")]
   [InlineData("[docs][1]\n\n[1]: https://example.com/a", "<p><a href=\"https://example.com/a\" " + Rel + ">docs</a> (https://example.com/a)</p>\n")]
   [InlineData("[q](https://example.com/?q=\"x\"onmouseover=alert(1))", "<p><a href=\"https://example.com/?q=%22x%22onmouseover=alert(1)\" " + Rel + ">q</a> (https://example.com/?q=%22x%22onmouseover=alert(1))</p>\n")]
   [InlineData("<https://example.com:8443/a>", "<p><a href=\"https://example.com:8443/a\" " + Rel + ">https://example.com:8443/a</a></p>\n")]
   [InlineData("<https://example.com:443/a>", "<p><a href=\"https://example.com/a\" " + Rel + ">https://example.com/a</a></p>\n")]
   [InlineData("<https://example.com./a>", "<p><a href=\"https://example.com./a\" " + Rel + ">https://example.com./a</a></p>\n")]
   [InlineData("<https://0x7f.1/a>", "<p><a href=\"https://127.0.0.1/a\" " + Rel + ">https://127.0.0.1/a</a></p>\n")]
   [InlineData("<https://[::1]:8443/a>", "<p><a href=\"https://[::1]:8443/a\" " + Rel + ">https://[::1]:8443/a</a></p>\n")]
   [InlineData("<https://a.1e/>", "<p><a href=\"https://a.1e/\" " + Rel + ">https://a.1e/</a></p>\n")]
   [InlineData("<https://example.com/a/%2e%2e/b>", "<p><a href=\"https://example.com/b\" " + Rel + ">https://example.com/b</a></p>\n")]
   public void Https_link_renders_as_an_anchor_with_its_url_visible(string markdown, string html)
   {
      Assert.Equal(html, ProseRenderer.Render(markdown).Html);
   }

   /// <summary>
   /// Each URL holds user info, which a reader takes for the host, or Node's WHATWG <c>URL</c> reads it differently from <see cref="Uri"/>. The hosts with a joiner or a full-width or
   /// compatibility character are ones that <see cref="Uri"/> accepts and IDNA refuses.
   /// </summary>
   [Theory]
   [InlineData("[x](https://user@evil.example/)", "x")]
   [InlineData("[x](https://user:pass@evil.example/)", "x")]
   [InlineData("[x](https://@evil.example/)", "x")]
   [InlineData("[x](https://:@evil.example/)", "x")]
   [InlineData("[x](https://example.com\\@evil.example/)", "x")]
   [InlineData("<https://example.com@evil.example/>", "https://example.com@evil.example/")]
   [InlineData("[x](https://example.com/a\\b)", "x")]
   [InlineData("<https://example.com/a\\b>", "https://example.com/a\\b")]
   [InlineData("[x](<https://example.com/a\tb>)", "x")]
   [InlineData("[x](https://example.com/a&#9;b)", "x")]
   [InlineData("[x](https://example.com/a&#10;b)", "x")]
   [InlineData("[x](https://example.com/a&#13;b)", "x")]
   [InlineData("[x](https://ex&#xA0;ample.com/)", "x")]
   [InlineData("[x](https://example.123/)", "x")]
   [InlineData("[x](https://example.0x1/)", "x")]
   [InlineData("[x](https://example.08/)", "x")]
   [InlineData("[x](https://1.2.3.4.5/)", "x")]
   [InlineData("[x](https://127.0.0.1./)", "x")]
   [InlineData("[x](https://256.0.0.1/)", "x")]
   [InlineData("[x](https://[::ffff:127.0.0.1]/)", "x")]
   [InlineData("[x](https://exa\u200Dmple.com/)", "x")]
   [InlineData("[x](https://exa\u200Cmple.com/)", "x")]
   [InlineData("[x](https://example.com\uFF1A8443/)", "x")]
   [InlineData("[x](https://example.com\uFF0Fevil/)", "x")]
   [InlineData("[x](https://evil.com\uFF03.example.com/)", "x")]
   [InlineData("[x](https://evil\uFF20example.com/)", "x")]
   [InlineData("[x](https://\u2488.com/)", "x")]
   [InlineData("[x](https://a\u2024b.com/)", "x")]
   [InlineData("[x](https://example\u2025com/)", "x")]
   [InlineData("[x](https://\u2100.com/)", "x")]
   [InlineData("[x](https://a\uFE52b/)", "x")]
   [InlineData("<https://exa\u200Dmple.com/>", "https://exa\u200Dmple.com/")]
   public void Https_link_whose_visible_url_could_mislead_renders_as_text(string markdown, string text)
   {
      Assert.Equal($"<p>{text}</p>\n", ProseRenderer.Render(markdown).Html);
   }

   [Theory]
   [InlineData("[x](https://bücher.example/a)", "<p><a href=\"https://xn--bcher-kva.example/a\" " + Rel + ">x</a> (https://xn--bcher-kva.example/a)</p>\n")]
   [InlineData("<https://аpple.com/>", "<p><a href=\"https://xn--pple-43d.com/\" " + Rel + ">https://xn--pple-43d.com/</a></p>\n")]
   [InlineData("[https://bücher.example/](https://bücher.example/)", "<p><a href=\"https://xn--bcher-kva.example/\" " + Rel + ">https://xn--bcher-kva.example/</a></p>\n")]
   [InlineData("<https://exam\u200Bple.com/>", "<p><a href=\"https://example.com/\" " + Rel + ">https://example.com/</a></p>\n")]
   [InlineData("<https://ＥＸＡＭＰＬＥ.com/>", "<p><a href=\"https://example.com/\" " + Rel + ">https://example.com/</a></p>\n")]
   [InlineData("<https://bücher.example:8443/é?é#é>", "<p><a href=\"https://xn--bcher-kva.example:8443/%C3%A9?%C3%A9#%C3%A9\" " + Rel + ">https://xn--bcher-kva.example:8443/%C3%A9?%C3%A9#%C3%A9</a></p>\n")]
   public void Https_link_shows_an_internationalized_host_in_punycode(string markdown, string html)
   {
      Assert.Equal(html, ProseRenderer.Render(markdown).Html);
   }

   [Theory]
   [InlineData("[click](javascript:alert(1))", "click")]
   [InlineData("[click](JavaScript:alert(1))", "click")]
   [InlineData("[click](&#106;avascript:alert(1))", "click")]
   [InlineData("[click](java&#x09;script:alert(1))", "click")]
   [InlineData("[click](vbscript:msgbox)", "click")]
   [InlineData("[click](data:text/html;base64,PHNjcmlwdD4=)", "click")]
   [InlineData("[click](file:///etc/passwd)", "click")]
   [InlineData("[click](http://example.com)", "click")]
   [InlineData("[click](mailto:a@example.com)", "click")]
   [InlineData("[click](docs/readme.md)", "click")]
   [InlineData("[click](/etc/passwd)", "click")]
   [InlineData("[click](//evil.example.com/x)", "click")]
   [InlineData("[click](#top)", "click")]
   [InlineData("[click](https:evil.example.com)", "click")]
   [InlineData("[click](https:///path)", "click")]
   [InlineData("[](javascript:alert(1))", "javascript:alert(1)")]
   [InlineData("<javascript:alert(1)>", "javascript:alert(1)")]
   [InlineData("<http://example.com>", "http://example.com")]
   [InlineData("<a@example.com>", "a@example.com")]
   [InlineData("[click][x]\n\n[x]: javascript:alert(1)", "click")]
   [InlineData("[click]\n\n[click]: data:text/html,<script>", "click")]
   public void Link_with_another_scheme_renders_as_text(string markdown, string text)
   {
      var html = ProseRenderer.Render(markdown).Html;

      Assert.Equal($"<p>{text}</p>\n", html);
   }

   [Theory]
   [InlineData("![alt text](https://example.com/x.png)", "<p>alt text</p>\n")]
   [InlineData("![](data:image/png;base64,iVBORw0KGgo=)", "<p></p>\n")]
   [InlineData("![a *b*](https://example.com/x.png \"t\")", "<p>a <em>b</em></p>\n")]
   [InlineData("![img][1]\n\n[1]: https://example.com/x.png", "<p>img</p>\n")]
   [InlineData("![shot](ref:head/a.png#L1-1)", "<p>shot</p>\n")]
   public void Image_renders_as_its_alt_text(string markdown, string html)
   {
      Assert.Equal(html, ProseRenderer.Render(markdown).Html);
   }

   [Fact]
   public void Ref_link_renders_as_an_in_app_link_and_is_returned()
   {
      var prose = ProseRenderer.Render("See [the loop](ref:head/src/My%20File.cs#L3-5) and [`Main`](ref:base/Program.cs#L1-2).");

      Assert.Equal(
         "<p>See <a data-ref-side=\"head\" data-ref-path=\"src/My File.cs\" data-ref-start-line=\"3\" data-ref-end-line=\"5\">the loop</a> and "
         + "<a data-ref-side=\"base\" data-ref-path=\"Program.cs\" data-ref-start-line=\"1\" data-ref-end-line=\"2\"><code>Main</code></a>.</p>\n",
         prose.Html);
      Assert.Equal(
         [(FileSide.Head, "src/My File.cs", 3, 5), (FileSide.Base, "Program.cs", 1, 2)],
         prose.References.Select(reference => (reference.Side, reference.Path, reference.StartLine, reference.EndLine)));
      Assert.Empty(prose.MalformedReferences);
   }

   [Theory]
   [InlineData("<ref:head/a.cs#L1-2>", "<p><a data-ref-side=\"head\" data-ref-path=\"a.cs\" data-ref-start-line=\"1\" data-ref-end-line=\"2\">a.cs#L1-2</a></p>\n")]
   [InlineData("[](ref:head/a.cs#L1-2)", "<p><a data-ref-side=\"head\" data-ref-path=\"a.cs\" data-ref-start-line=\"1\" data-ref-end-line=\"2\">a.cs#L1-2</a></p>\n")]
   [InlineData("[x][r]\n\n[r]: ref:base/a.cs#L4-4", "<p><a data-ref-side=\"base\" data-ref-path=\"a.cs\" data-ref-start-line=\"4\" data-ref-end-line=\"4\">x</a></p>\n")]
   [InlineData("- [x](ref:head/a.cs#L1-2)", "<ul>\n<li><a data-ref-side=\"head\" data-ref-path=\"a.cs\" data-ref-start-line=\"1\" data-ref-end-line=\"2\">x</a></li>\n</ul>\n")]
   public void Ref_link_in_another_form_renders_as_an_in_app_link(string markdown, string html)
   {
      var prose = ProseRenderer.Render(markdown);

      Assert.Equal(html, prose.Html);
      Assert.Single(prose.References);
   }

   [Fact]
   public void Path_of_a_ref_link_is_escaped_in_its_attribute()
   {
      var prose = ProseRenderer.Render("[x](ref:head/a%22%3E%3Cscript%3Ealert(1)%3C%2Fscript%3E.cs#L1-1)");

      Assert.Equal(
         "<p><a data-ref-side=\"head\" data-ref-path=\"a&quot;&gt;&lt;script&gt;alert(1)&lt;/script&gt;.cs\" data-ref-start-line=\"1\" data-ref-end-line=\"1\">x</a></p>\n",
         prose.Html);
      Assert.Equal("a\"><script>alert(1)</script>.cs", Assert.Single(prose.References).Path);
   }

   [Theory]
   [InlineData("[a <https://inner.example/> b](https://outer.example/)", "<p><a href=\"https://outer.example/\" " + Rel + ">a https://inner.example/ b</a> (https://outer.example/)</p>\n", 0, 0)]
   [InlineData("[![x [y](https://a.example/)](https://b.example/i.png)](https://c.example/)", "<p><a href=\"https://c.example/\" " + Rel + ">x y</a> (https://c.example/)</p>\n", 0, 0)]
   [InlineData("[see <ref:head/a.cs#L1-2>](https://c.example/)", "<p><a href=\"https://c.example/\" " + Rel + ">see a.cs#L1-2</a> (https://c.example/)</p>\n", 1, 0)]
   [InlineData("[![x [y](ref:base/b.cs#L3-4)](https://b.example/i.png)](ref:head/a.cs#L1-2)", "<p><a data-ref-side=\"head\" data-ref-path=\"a.cs\" data-ref-start-line=\"1\" data-ref-end-line=\"2\">x y</a></p>\n", 2, 0)]
   [InlineData("[![x [y](ref:a.cs#L1-2)](https://b.example/i.png)](https://c.example/)", "<p><a href=\"https://c.example/\" " + Rel + ">x y</a> (https://c.example/)</p>\n", 0, 1)]
   [InlineData("[a](https://a.example/) [b <ref:head/b.cs#L1-1>](https://b.example/) <https://c.example/>", "<p><a href=\"https://a.example/\" " + Rel + ">a</a> (https://a.example/) <a href=\"https://b.example/\" " + Rel + ">b b.cs#L1-1</a> (https://b.example/) <a href=\"https://c.example/\" " + Rel + ">https://c.example/</a></p>\n", 1, 0)]
   [InlineData("![x [y](https://a.example/)](https://b.example/i.png)", "<p>x <a href=\"https://a.example/\" " + Rel + ">y</a> (https://a.example/)</p>\n", 0, 0)]
   public void Only_a_link_outside_any_link_label_renders_an_anchor(string markdown, string html, int references, int malformed)
   {
      var prose = ProseRenderer.Render(markdown);

      Assert.Equal(html, prose.Html);
      Assert.Equal(references, prose.References.Count);
      Assert.Equal(malformed, prose.MalformedReferences.Count);
   }

   [Fact]
   public void Malformed_ref_link_renders_as_text_and_is_reported()
   {
      var prose = ProseRenderer.Render("See [the loop](ref:head/a.cs#L5-3), [it](ref:a.cs#L1-2) and [ok](ref:head/b.cs#L1-1).");

      Assert.Equal("<p>See the loop, it and <a data-ref-side=\"head\" data-ref-path=\"b.cs\" data-ref-start-line=\"1\" data-ref-end-line=\"1\">ok</a>.</p>\n", prose.Html);
      Assert.Equal(["ref:head/a.cs#L5-3", "ref:a.cs#L1-2"], prose.MalformedReferences.Select(malformed => malformed.Link));
      Assert.All(prose.MalformedReferences, malformed => Assert.False(string.IsNullOrWhiteSpace(malformed.Reason)));
      Assert.Equal("b.cs", Assert.Single(prose.References).Path);
   }

   [Fact]
   public void Prose_without_links_returns_no_references()
   {
      var prose = ProseRenderer.Render("Nothing `ref:head/a.cs#L1-2` here, and ref:head/b.cs#L1-2 is not a link.");

      Assert.Empty(prose.References);
      Assert.Empty(prose.MalformedReferences);
   }

   [Fact]
   public void Null_prose_is_refused_as_null_and_not_as_too_deep()
   {
      Assert.Throws<ArgumentNullException>(() => ProseRenderer.Render(null!));
   }

   [Fact]
   public void Prose_as_text_is_one_paragraph_of_its_escaped_source()
   {
      Assert.Equal("<p>&gt; **a** &lt;b&gt;&amp;&quot;&#39;\nc</p>\n", ProseRenderer.AsText("> **a** <b>&\"'\nc"));
   }

   [Theory]
   [InlineData("")]
   [InlineData("   \n\n  ")]
   public void Empty_prose_renders_as_nothing(string markdown)
   {
      Assert.Equal("", ProseRenderer.Render(markdown).Html);
   }

   [Fact]
   public void Mixed_hostile_prose_yields_only_allowed_tags()
   {
      const string Markdown = """
         # <h1>Title</h1>

         <iframe src="https://evil.example.com"></iframe>

         - [a](javascript:alert(1)) <b onmouseover=x>b</b>
         - ![x](https://evil.example.com/x.png?leak=1)

         > <style>body{display:none}</style>

         [r]: <javascript:alert(1)> "t"
         [link][r] and <svg/onload=alert(1)> and &lt;img&gt; and [h](https://example.com "<b>")

         ```html
         <script>alert(1)</script>
         ```
         """;

      AssertOnlyAllowedTags(ProseRenderer.Render(Markdown).Html);
   }

   [Theory]
   [MemberData(nameof(NestingAtTheLimit))]
   public void Nesting_at_the_limit_renders(string markdown)
   {
      Assert.Contains("x", ProseRenderer.Render(markdown).Html, StringComparison.Ordinal);
   }

   [Theory]
   [MemberData(nameof(NestingPastTheLimit))]
   public void Nesting_past_the_limit_is_refused(string markdown)
   {
      var exception = Assert.Throws<ProseTooDeepException>(() => ProseRenderer.Render(markdown));
      Assert.Contains(ProseRenderer.MaxDepth.ToString(System.Globalization.CultureInfo.InvariantCulture), exception.Message, StringComparison.Ordinal);
   }

   /// <summary>
   /// Each case is far deeper than the limit, and the last three are past the limit of Markdig's parser too. The emphasis case once overflowed the stack of the process.
   /// It stays small enough that a writer without the limit returns, so the test fails rather than crashing the test host.
   /// </summary>
   [Theory]
   [InlineData(500, "**", "x", "**")]
   [InlineData(100_000, "[", "x", "")]
   [InlineData(100_000, ">", "x", "")]
   [InlineData(3000, "- ", "x", "")]
   public void Far_deeper_nesting_is_refused(int count, string open, string text, string close)
   {
      var markdown = string.Concat(Enumerable.Repeat(open, count)) + text + string.Concat(Enumerable.Repeat(close, count));

      Assert.Throws<ProseTooDeepException>(() => ProseRenderer.Render(markdown));
   }

   // Each block quote, list, list item, emphasis and link label nests one level, so "- " nests two.
   public static TheoryData<string> NestingAtTheLimit() =>
   [
      Repeat("> ", ProseRenderer.MaxDepth) + "x",
      Repeat("**", ProseRenderer.MaxDepth) + "x" + Repeat("**", ProseRenderer.MaxDepth),
      Repeat("- ", ProseRenderer.MaxDepth / 2) + "x",
      Repeat("> ", ProseRenderer.MaxDepth - 2) + "[**x**](https://example.com)",
      Repeat("- ", (ProseRenderer.MaxDepth / 2) - 1) + "> > x",
      Repeat("> ", ProseRenderer.MaxDepth - 1) + Repeat("*x* ", 100),
      Repeat("- a\n", 100) + Repeat("> ", ProseRenderer.MaxDepth) + "x",
   ];

   public static TheoryData<string> NestingPastTheLimit() =>
   [
      Repeat("> ", ProseRenderer.MaxDepth + 1) + "x",
      Repeat("**", ProseRenderer.MaxDepth + 1) + "x" + Repeat("**", ProseRenderer.MaxDepth + 1),
      Repeat("- ", ProseRenderer.MaxDepth / 2) + "> x",
      Repeat("> ", ProseRenderer.MaxDepth - 1) + "[**x**](https://example.com)",
      Repeat("> ", ProseRenderer.MaxDepth - 1) + "![*x*](https://example.com/x.png)",
   ];

   private static string Repeat(string text, int count) => string.Concat(Enumerable.Repeat(text, count));

   /// <summary>Asserts that every tag in the HTML is one the subset allows, with only the attributes the writer emits.</summary>
   private static void AssertOnlyAllowedTags(string html)
   {
      var allowed = new HashSet<string>(StringComparer.Ordinal) { "p", "ul", "ol", "li", "em", "strong", "code", "pre", "br", "a" };
      var tags = System.Text.RegularExpressions.Regex.Matches(html, "<(/?)([a-zA-Z][a-zA-Z0-9]*)([^>]*)>");
      foreach (System.Text.RegularExpressions.Match tag in tags)
      {
         var name = tag.Groups[2].Value;
         Assert.Contains(name, allowed);
         var attributes = tag.Groups[3].Value;
         if (name == "a")
         {
            Assert.Matches("^( (href|rel|target|data-ref-side|data-ref-path|data-ref-start-line|data-ref-end-line)=\"[^\"<>]*\")*$", attributes);
         }
         else if (name == "ol")
         {
            Assert.Matches("^( start=\"[0-9]+\")?$", attributes);
         }
         else
         {
            Assert.Equal("", attributes);
         }
      }

      Assert.DoesNotContain("<!", html, StringComparison.Ordinal);
   }
}
