using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Lotsen.Prose;

/// <summary>The HTML of a piece of prose, with the <c>ref:</c> links it holds.</summary>
/// <param name="Html">The HTML, safe to insert into the UI as it is.</param>
/// <param name="References">The code references of the well-formed <c>ref:</c> links, in the order the prose holds them.</param>
/// <param name="MalformedReferences">The <c>ref:</c> links that could not be read, in the order the prose holds them.</param>
internal sealed record RenderedProse(string Html, IReadOnlyList<CodeReference> References, IReadOnlyList<MalformedReference> MalformedReferences);

/// <summary>A <c>ref:</c> link that is not a well-formed code reference.</summary>
/// <param name="Link">The link's destination.</param>
/// <param name="Reason">Why <see cref="CodeReference.TryParse"/> refused the destination, for Claude.</param>
internal sealed record MalformedReference(string Link, string Reason);

/// <summary>A refusal of prose whose Markdown nests deeper than <see cref="ProseRenderer.MaxDepth"/> levels.</summary>
internal sealed class ProseTooDeepException : Exception
{
   public ProseTooDeepException(Exception? innerException = null)
      : base($"The text nests lists, quotes, emphasis or links more than {ProseRenderer.MaxDepth} levels deep. Flatten the nesting, then try again.", innerException)
   {
   }
}

/// <summary>
/// Renders prose, the restricted Markdown that tours are written in, to HTML.
/// </summary>
/// <remarks>
/// The writer emits markup only for paragraphs, lists, emphasis, inline code, code blocks and two kinds of link. It writes every other construct as its content or as its escaped source text, so
/// markup that Markdig parses but the subset lacks never reaches the output. An <c>https:</c> link becomes an anchor that shows its URL, when the URL is where a browser goes, as
/// <see cref="Writer.TryReadHttps"/> decides. A <c>ref:</c> link becomes an anchor with no <c>href</c> that holds its code reference in <c>data-ref-*</c> attributes, for the UI to open.
/// Any other link, and an image, become their text.
/// </remarks>
internal static class ProseRenderer
{
   /// <summary>The most levels that Markdown may nest. Each block quote, list, list item, emphasis, link label and image nests one level.</summary>
   public const int MaxDepth = 32;

   private const string HttpsPrefix = "https://";

   // The characters that TryReadHttps and the host checks it calls look for.
   private static readonly SearchValues<char> BrowserRewrites = SearchValues.Create("\\\t\n\r");
   private static readonly SearchValues<char> HostCharacters = SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._");
   private static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789abcdefABCDEF");

   // The core CommonMark parser, with no extensions, so Markdig parses no attribute, table or bare-URL syntax. Raw HTML parses as literal text.
   // Precise source locations give each inline its source span, as the doc of MarkdownPipelineBuilder.PreciseSourceLocation says. The writer reads it to write an unknown construct as text.
   private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().DisableHtml().UsePreciseSourceLocation().Build();

   /// <exception cref="ProseTooDeepException">The Markdown nests deeper than <see cref="MaxDepth"/> levels.</exception>
   public static RenderedProse Render(string markdown) => Write(markdown).Result();

   /// <summary>Returns the Markdown with new destinations for the code references that <see cref="Render"/> reads, and every other character as it was.</summary>
   /// <remarks>
   /// The same text in a code span, a code block, an image or an unused definition keeps its characters. So does a destination whose source is not <see cref="CodeReference.Link"/>, as with a
   /// backslash escape or an entity. Links that share a reference definition share its destination.
   /// </remarks>
   /// <param name="markdown">Prose that <see cref="Render"/> accepts.</param>
   /// <param name="destination">Returns the new destination for a code reference, or null to keep its link.</param>
   /// <exception cref="ProseTooDeepException">The Markdown nests deeper than <see cref="MaxDepth"/> levels.</exception>
   public static string RewriteReferences(string markdown, Func<CodeReference, string?> destination)
   {
      var writer = Write(markdown);
      var rewritten = new StringBuilder(markdown);
      foreach (var (reference, span) in writer.Destinations.DistinctBy(link => link.Span.Start).OrderByDescending(link => link.Span.Start))
      {
         if (span.Start < 0 || span.Length <= 0 || span.End >= markdown.Length || destination(reference) is not { } link)
         {
            continue;
         }

         var source = markdown.AsSpan(span.Start, span.Length);
         var bracketed = source is ['<', .., '>'];
         if ((bracketed ? source[1..^1] : source).SequenceEqual(reference.Link))
         {
            rewritten.Remove(span.Start, span.Length).Insert(span.Start, bracketed ? $"<{link}>" : link);
         }
      }

      return rewritten.ToString();
   }

   /// <exception cref="ProseTooDeepException">The Markdown nests deeper than <see cref="MaxDepth"/> levels.</exception>
   private static Writer Write(string markdown)
   {
      // The catch below takes any ArgumentException as too deep, so null is refused first, as Null_prose_is_refused_as_null_and_not_as_too_deep pins.
      ArgumentNullException.ThrowIfNull(markdown);
      MarkdownDocument document;
      try
      {
         document = Markdown.Parse(markdown, Pipeline);
      }
      catch (ArgumentException exception)
      {
         // Markdig's parser throws this when the syntax tree nests past the pipeline's MaximumNestingDepth, as Far_deeper_nesting_is_refused pins.
         throw new ProseTooDeepException(exception);
      }

      var writer = new Writer(markdown);
      writer.WriteBlocks(document, tight: false);
      return writer;
   }

   /// <summary>Returns the Markdown's source as one paragraph of escaped text, for prose that <see cref="Render"/> refuses.</summary>
   public static string AsText(string markdown)
   {
      var html = new StringBuilder("<p>");
      Writer.AppendEscaped(html, markdown);
      return html.Append("</p>\n").ToString();
   }

   private sealed class Writer
   {
      private readonly string _markdown;
      private readonly StringBuilder _html = new();
      private readonly List<CodeReference> _references = [];
      private readonly List<MalformedReference> _malformed = [];
      private readonly List<(CodeReference Reference, SourceSpan Span)> _destinations = [];

      // The writer recurses once per level, so the limit keeps the recursion short whatever depth the parser accepts.
      private int _depth;

      // A link inside a link's label writes no anchor of its own, because HTML does not nest anchors.
      private bool _inLabel;

      public Writer(string markdown)
      {
         _markdown = markdown;
      }

      public RenderedProse Result() => new(_html.ToString(), _references, _malformed);

      /// <summary>
      /// The code references in <see cref="RenderedProse.References"/>, each with the source span of its destination: in angle brackets for an autolink and for a destination written in them.
      /// </summary>
      public IReadOnlyList<(CodeReference Reference, SourceSpan Span)> Destinations => _destinations;

      /// <param name="tight">Whether the blocks are the children of an item of a tight list, whose paragraphs get no <c>p</c> element.</param>
      public void WriteBlocks(ContainerBlock container, bool tight)
      {
         foreach (var block in container)
         {
            WriteBlock(block, tight);
         }
      }

      private void WriteBlock(Block block, bool tight)
      {
         switch (block)
         {
            case ParagraphBlock paragraph when tight:
               WriteInlines(paragraph.Inline);
               break;
            case ListBlock list:
               WriteList(list);
               break;
            case CodeBlock code:
               WriteCode(code);
               break;
            case LinkReferenceDefinitionGroup or LinkReferenceDefinition:
               break;
            case ContainerBlock container:
               Enter();
               WriteBlocks(container, tight: false);
               Exit();
               break;
            case LeafBlock { Inline: { } inline }:
               WriteParagraph(inline);
               break;
            default:
               _html.Append("<p>");
               WriteSource(block.Span);
               _html.Append("</p>\n");
               break;
         }
      }

      private void WriteList(ListBlock list)
      {
         var tag = list.IsOrdered ? "ol" : "ul";
         _html.Append('<').Append(tag);
         if (list.IsOrdered && int.TryParse(list.OrderedStart, NumberStyles.None, CultureInfo.InvariantCulture, out var start) && start != 1)
         {
            _html.Append(CultureInfo.InvariantCulture, $" start=\"{start}\"");
         }

         _html.Append(">\n");
         Enter();
         foreach (var item in list)
         {
            _html.Append("<li>");
            Enter();
            WriteBlocks((ContainerBlock)item, tight: !list.IsLoose);
            Exit();
            _html.Append("</li>\n");
         }

         Exit();
         _html.Append("</").Append(tag).Append(">\n");
      }

      // The writer omits the info string of a fenced block, because the subset gives code blocks no language.
      private void WriteCode(CodeBlock code)
      {
         _html.Append("<pre><code>");
         foreach (var line in code.Lines.Lines.AsSpan(0, code.Lines.Count))
         {
            AppendEscaped(line.Slice.AsSpan());
            _html.Append('\n');
         }

         _html.Append("</code></pre>\n");
      }

      private void WriteParagraph(ContainerInline inline)
      {
         _html.Append("<p>");
         WriteInlines(inline);
         _html.Append("</p>\n");
      }

      private void WriteInlines(ContainerInline? container)
      {
         if (container is null)
         {
            return;
         }

         foreach (var inline in container)
         {
            WriteInline(inline);
         }
      }

      private void WriteInline(Inline inline)
      {
         switch (inline)
         {
            case LiteralInline literal:
               AppendEscaped(literal.Content.AsSpan());
               break;
            case HtmlEntityInline entity:
               AppendEscaped(entity.Transcoded.AsSpan());
               break;
            case CodeInline code:
               _html.Append("<code>");
               AppendEscaped(code.Content);
               _html.Append("</code>");
               break;
            case EmphasisInline emphasis:
               var tag = emphasis.DelimiterCount >= 2 ? "strong" : "em";
               _html.Append('<').Append(tag).Append('>');
               Enter();
               WriteInlines(emphasis);
               Exit();
               _html.Append("</").Append(tag).Append('>');
               break;
            case LineBreakInline lineBreak:
               _html.Append(lineBreak.IsHard ? "<br>\n" : "\n");
               break;
            case AutolinkInline autolink:
               WriteLink(autolink.Url, labelHtml: "", autolink.Span);
               break;
            case LinkInline { IsImage: true } image:
               Enter();
               WriteInlines(image);
               Exit();
               break;
            case LinkInline link:
               WriteLink(link.Url ?? "", CaptureLabel(link), link.UrlSpan);
               break;
            case ContainerInline container:
               Enter();
               WriteInlines(container);
               Exit();
               break;
            default:
               WriteSource(inline.Span);
               break;
         }
      }

      /// <param name="labelHtml">The link's text, already rendered, or empty when the link shows its destination, as an autolink does.</param>
      /// <param name="span">The source span of the destination, which a reference definition holds for a link that uses it.</param>
      private void WriteLink(string destination, string labelHtml, SourceSpan span)
      {
         if (CodeReference.HasScheme(destination))
         {
            if (!CodeReference.TryParse(destination, out var reference, out var reason))
            {
               _malformed.Add(new MalformedReference(destination, reason));
               WriteLabelOrDestination(labelHtml, destination);
               return;
            }

            _references.Add(reference);
            _destinations.Add((reference, span));
            var text = $"{reference.Path}#L{reference.StartLine}-{reference.EndLine}";
            if (_inLabel)
            {
               WriteLabelOrDestination(labelHtml, text);
               return;
            }

            _html.Append("<a data-ref-side=\"").Append(reference.SideName).Append("\" data-ref-path=\"");
            AppendEscaped(reference.Path);
            _html.Append(CultureInfo.InvariantCulture, $"\" data-ref-start-line=\"{reference.StartLine}\" data-ref-end-line=\"{reference.EndLine}\">");
            WriteLabelOrDestination(labelHtml, text);
            _html.Append("</a>");
            return;
         }

         if (_inLabel || !TryReadHttps(destination, out var url))
         {
            WriteLabelOrDestination(labelHtml, destination);
            return;
         }

         var urlHtml = Escaped(url);
         _html.Append("<a href=\"").Append(urlHtml).Append("\" rel=\"noopener noreferrer\" target=\"_blank\">");
         if (labelHtml.Length == 0 || labelHtml == urlHtml || labelHtml == Escaped(destination))
         {
            _html.Append(urlHtml).Append("</a>");
            return;
         }

         // The URL follows the label, so the reader sees where a link leads before following it.
         _html.Append(labelHtml).Append("</a> (").Append(urlHtml).Append(')');
      }

      private void WriteLabelOrDestination(string labelHtml, string destination)
      {
         if (labelHtml.Length > 0)
         {
            _html.Append(labelHtml);
         }
         else
         {
            AppendEscaped(destination);
         }
      }

      /// <summary>Renders a link's label to a string, and leaves the output as it was.</summary>
      private string CaptureLabel(LinkInline link)
      {
         var start = _html.Length;
         var outerInLabel = _inLabel;
         _inLabel = true;
         Enter();
         WriteInlines(link);
         Exit();
         _inLabel = outerInLabel;
         var html = _html.ToString(start, _html.Length - start);
         _html.Length = start;
         return html;
      }

      /// <summary>Counts one more level of nesting.</summary>
      /// <exception cref="ProseTooDeepException">The level is deeper than <see cref="MaxDepth"/>.</exception>
      private void Enter()
      {
         if (++_depth > MaxDepth)
         {
            throw new ProseTooDeepException();
         }
      }

      private void Exit() => _depth--;

      private void WriteSource(SourceSpan span)
      {
         if (span.Start >= 0 && span.Length > 0 && span.End < _markdown.Length)
         {
            AppendEscaped(_markdown.AsSpan(span.Start, span.Length));
         }
      }

      private void AppendEscaped(ReadOnlySpan<char> text) => AppendEscaped(_html, text);

      private static string Escaped(string text)
      {
         var html = new StringBuilder(text.Length);
         AppendEscaped(html, text);
         return html.ToString();
      }

      /// <summary>Reads an <c>https:</c> destination whose visible URL is where a browser goes, as the anchor's <c>href</c> and its visible text are one string.</summary>
      /// <remarks>
      /// It refuses user info, because a reader takes the user info for the host. It refuses a backslash, a tab, a line feed and a carriage return anywhere, because a
      /// browser reads a backslash in the host as a slash and drops the other three, where <see cref="Uri"/> refuses or encodes them. It also refuses a host that
      /// IDNA refuses, and one that <see cref="BrowserReadsHostAlike"/> refuses.
      /// </remarks>
      /// <returns>Whether the destination is such a URL. <paramref name="url"/> holds it normalized and percent-encoded, with the host in punycode, so a host of look-alike letters shows its true name.</returns>
      private static bool TryReadHttps(string destination, [NotNullWhen(true)] out string? url)
      {
         url = null;
         if (!destination.StartsWith(HttpsPrefix, StringComparison.OrdinalIgnoreCase)
            || destination.AsSpan().ContainsAny(BrowserRewrites)
            || !Uri.TryCreate(destination, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.Host.Length == 0
            || uri.GetComponents(UriComponents.UserInfo | UriComponents.KeepDelimiter, UriFormat.UriEscaped).Length > 0)
         {
            return false;
         }

         if (!TryReadHost(uri, out var host) || !BrowserReadsHostAlike(uri.HostNameType, host))
         {
            return false;
         }

         url = uri.IsDefaultPort
            ? $"{HttpsPrefix}{host}{uri.PathAndQuery}{uri.Fragment}"
            : string.Create(CultureInfo.InvariantCulture, $"{HttpsPrefix}{host}:{uri.Port}{uri.PathAndQuery}{uri.Fragment}");
         return true;
      }

      /// <returns>Whether IDNA accepts the host. <paramref name="host"/> holds its ASCII form, or an IPv6 address in brackets.</returns>
      private static bool TryReadHost(Uri uri, [NotNullWhen(true)] out string? host)
      {
         if (uri.HostNameType == UriHostNameType.IPv6)
         {
            // IdnHost drops the brackets of an IPv6 address, which Host keeps.
            host = uri.Host;
            return true;
         }

         try
         {
            host = uri.IdnHost;
            return true;
         }
         catch (UriFormatException)
         {
            // TryCreate accepts some hosts that IDNA refuses, such as one with a zero-width joiner, and IdnHost throws for those.
            host = null;
            return false;
         }
      }

      /// <summary>Returns whether a browser writes the host as <see cref="Uri"/> does, after IDNA.</summary>
      /// <remarks>
      /// A browser writes an IPv4 address inside an IPv6 one as hexadecimal groups. It refuses a host with a space, which IDNA makes of a no-break space, so a name
      /// may hold only ASCII letters, digits, hyphens, underscores and dots. It reads a name that ends in a number as an IPv4 address, and refuses it when it is none.
      /// </remarks>
      private static bool BrowserReadsHostAlike(UriHostNameType type, string host) => type switch
      {
         UriHostNameType.IPv6 => !host.Contains('.', StringComparison.Ordinal),
         UriHostNameType.IPv4 => true,
         _ => !host.AsSpan().ContainsAnyExcept(HostCharacters) && !EndsInANumber(host),
      };

      /// <summary>Returns whether the last label of the host, before one trailing dot, is decimal digits, or <c>0x</c> and hexadecimal digits.</summary>
      private static bool EndsInANumber(string host)
      {
         var labels = host.AsSpan();
         if (labels.EndsWith('.'))
         {
            labels = labels[..^1];
         }

         var last = labels[(labels.LastIndexOf('.') + 1)..];
         return last.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? !last[2..].ContainsAnyExcept(HexDigits)
            : last.Length > 0 && !last.ContainsAnyExceptInRange('0', '9');
      }

      /// <summary>Appends the text with the five characters that HTML gives meaning, in text and in a quoted attribute, as character references.</summary>
      public static void AppendEscaped(StringBuilder html, ReadOnlySpan<char> text)
      {
         foreach (var character in text)
         {
            _ = character switch
            {
               '&' => html.Append("&amp;"),
               '<' => html.Append("&lt;"),
               '>' => html.Append("&gt;"),
               '"' => html.Append("&quot;"),
               '\'' => html.Append("&#39;"),
               _ => html.Append(character),
            };
         }
      }
   }
}
