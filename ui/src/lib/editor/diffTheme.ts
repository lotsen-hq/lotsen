import { HighlightStyle, syntaxHighlighting } from '@codemirror/language';
import type { Extension } from '@codemirror/state';
import { EditorView } from '@codemirror/view';
import { tags } from '@lezer/highlight';

/** The height of a collapsed region's marker in pixels, fixed so that the lines after a region's markers stay aligned in both panes. */
export const collapsedMarkerHeight = 26;

// The colors are the page's theme variables, which switch with its light and dark palettes, so the theme also replaces the base themes' light-only and dark-only colors.
// It sits on each editor's own class, so its rules win over the merge package's base theme.
// Each fixed text size and line height multiplies by the page's `--font-scale` from `app.css`, so the code follows the reviewer's font size.
const theme = EditorView.theme({
   '&': {
      height: '100%',
      backgroundColor: 'var(--background)',
      color: 'var(--foreground)',
      fontSize: 'calc(13px * var(--font-scale))',
   },
   '.cm-scroller': {
      fontFamily: 'ui-monospace, SFMono-Regular, Menlo, Consolas, monospace',
      lineHeight: '1.5',
   },
   '.cm-content': { caretColor: 'var(--foreground)' },
   '.cm-cursor, .cm-dropCursor': { borderLeftColor: 'var(--foreground)' },
   '.cm-gutters': {
      backgroundColor: 'var(--background)',
      color: 'var(--muted-foreground)',
      borderRight: '1px solid var(--border)',
   },
   '.cm-content ::selection, .cm-line::selection': { backgroundColor: 'var(--code-selection)' },
   '.cm-specialChar': { color: 'var(--code-special-char)' },
   '&.cm-merge-a .cm-changedLine, .cm-deletedChunk, &.lotsen-deletedFile .cm-line': { backgroundColor: 'var(--diff-deleted-line)' },
   '&.cm-merge-b .cm-changedLine': { backgroundColor: 'var(--diff-inserted-line)' },
   '&.cm-merge-a .cm-changedLineGutter, .cm-deletedLineGutter': { background: 'var(--diff-deleted-marker)' },
   '&.cm-merge-b .cm-changedLineGutter': { background: 'var(--diff-inserted-marker)' },
   '.cm-changeGutter': { width: '4px' },
   // A passage tints its lines with a translucent image over the line's own background, so a changed line keeps its diff color beneath the tint.
   '.cm-line.lotsen-passage-primary, .cm-deletedLine.lotsen-passage-primary': {
      backgroundImage: 'linear-gradient(var(--passage-primary-line), var(--passage-primary-line))',
      boxShadow: 'inset 3px 0 0 var(--passage-primary-marker)',
   },
   '.cm-line.lotsen-passage-related, .cm-deletedLine.lotsen-passage-related': {
      backgroundImage: 'linear-gradient(var(--passage-related-line), var(--passage-related-line))',
      boxShadow: 'inset 3px 0 0 var(--passage-related-marker)',
   },
   // The focused passage of step through keeps its role's colors with a wider bar.
   '.cm-line.lotsen-passage-primary.lotsen-passage-focused, .cm-deletedLine.lotsen-passage-primary.lotsen-passage-focused': { boxShadow: 'inset 6px 0 0 var(--passage-primary-marker)' },
   '.cm-line.lotsen-passage-related.lotsen-passage-focused, .cm-deletedLine.lotsen-passage-related.lotsen-passage-focused': { boxShadow: 'inset 6px 0 0 var(--passage-related-marker)' },
   // The lines of the hovered reference take a tint and a bar of their own, over a passage's tint when they are inside one.
   '.cm-line.lotsen-reference-hovered, .cm-deletedLine.lotsen-reference-hovered': {
      backgroundImage: 'linear-gradient(var(--reference-hovered-line), var(--reference-hovered-line))',
      boxShadow: 'inset 3px 0 0 var(--reference-hovered-marker)',
   },
   '.cm-lineNumbers .cm-gutterElement.lotsen-passage-focused': { fontWeight: '600' },
   '.cm-lineNumbers .cm-gutterElement.lotsen-passage-primary': { color: 'var(--passage-primary-marker)' },
   '.cm-lineNumbers .cm-gutterElement.lotsen-passage-related': { color: 'var(--passage-related-marker)' },
   '.cm-lineNumbers .cm-gutterElement.lotsen-reference-hovered': { color: 'var(--reference-hovered-marker)' },
   '.cm-line.lotsen-dimmed, .cm-deletedLine.lotsen-dimmed, .cm-gutterElement.lotsen-dimmed': { opacity: 'var(--dimmed-opacity)' },
   // CodeMirror's buffer beside a widget is 1em tall on the baseline, which reaches above the line at a fractional font size and makes the line taller, so it starts at the text's top.
   '.cm-widgetBuffer': { verticalAlign: 'text-top' },
   // A label sticks to the right edge of the view when its line runs past it, so a long line in a narrow pane does not hide it.
   '.lotsen-passageLabels': { display: 'inline-block', position: 'sticky', right: '0', marginLeft: '2em' },
   // A line's thread markers stick to the right edge of the view, as its labels do.
   // A marker over the code is narrower than elsewhere, so it hides less of a long line.
   // The markers hang from the line's top, because on the baseline their box reaches above the line and makes it taller.
   '.lotsen-threadMarkers': { display: 'inline-flex', gap: '4px', verticalAlign: 'top', position: 'sticky', right: '0', marginLeft: '1em' },
   '.lotsen-threadMarkers .thread-marker': { maxWidth: '14em' },
   '.cm-lineNumbers .cm-gutterElement.lotsen-thread': { boxShadow: 'inset -3px 0 0 var(--thread-marker)' },
   // A deleted chunk has one gutter element for all its lines, so a deleted line in a thread draws the stripe in the chunk's left padding, clear of a passage's bar.
   '.cm-deletedLine.lotsen-thread': { position: 'relative' },
   '.cm-deletedLine.lotsen-thread::before': {
      content: '""',
      position: 'absolute',
      top: '0',
      bottom: '0',
      right: '100%',
      width: '3px',
      backgroundColor: 'var(--thread-marker)',
      pointerEvents: 'none',
   },
   '.lotsen-passageLabel': {
      display: 'inline-block',
      marginRight: '0.5em',
      padding: '0 0.5em',
      borderRadius: '3px',
      fontFamily: 'ui-sans-serif, system-ui, sans-serif',
      fontSize: 'calc(11px * var(--font-scale))',
      lineHeight: '1.5',
      verticalAlign: 'top',
      color: 'var(--passage-label-foreground)',
   },
   '.lotsen-collapsed': {
      display: 'flex',
      alignItems: 'center',
      gap: '0.75em',
      boxSizing: 'border-box',
      height: `${collapsedMarkerHeight}px`,
      padding: '0 0.75em',
      overflow: 'hidden',
      whiteSpace: 'nowrap',
      borderTop: '1px solid var(--border)',
      borderBottom: '1px solid var(--border)',
      backgroundColor: 'var(--muted)',
      color: 'var(--muted-foreground)',
      fontFamily: 'ui-sans-serif, system-ui, sans-serif',
      fontSize: 'calc(12px * var(--font-scale))',
   },
   '.lotsen-expand': {
      padding: '0 0.5em',
      border: '1px solid var(--border)',
      borderRadius: '3px',
      backgroundColor: 'var(--background)',
      color: 'var(--foreground)',
      font: 'inherit',
      lineHeight: 'calc(18px * var(--font-scale))',
      cursor: 'pointer',
   },
   '.lotsen-expand:hover': { backgroundColor: 'var(--accent)' },
   // The page's base :focus-visible rule draws the ring. The zero offset keeps the forced-colors outline inside the clipped marker.
   '.lotsen-expand:focus-visible': { borderColor: 'var(--ring)', outlineOffset: '0px' },
   // A block holds the page's prose, so it drops the code's font and line handling. Padding instead of margins keeps its measured height its whole height.
   '.lotsen-explanation': {
      position: 'sticky',
      boxSizing: 'border-box',
      padding: '6px 12px 8px 8px',
      whiteSpace: 'normal',
      fontFamily: 'ui-sans-serif, system-ui, sans-serif',
      fontSize: 'calc(14px * var(--font-scale))',
      lineHeight: '1.5',
      cursor: 'auto',
   },
   '.lotsen-passageLabel-primary': { backgroundColor: 'var(--passage-primary-marker)' },
   '.lotsen-passageLabel-related': { backgroundColor: 'var(--passage-related-marker)' },
});

const highlightStyle = HighlightStyle.define([
   { tag: [tags.keyword, tags.modifier, tags.operatorKeyword], color: 'var(--syntax-keyword)' },
   { tag: tags.controlKeyword, color: 'var(--syntax-control)' },
   { tag: [tags.string, tags.special(tags.string), tags.character], color: 'var(--syntax-string)' },
   { tag: tags.number, color: 'var(--syntax-number)' },
   { tag: [tags.bool, tags.null, tags.atom], color: 'var(--syntax-constant)' },
   { tag: [tags.comment, tags.meta, tags.documentMeta], color: 'var(--syntax-comment)', fontStyle: 'var(--syntax-comment-style)' },
   { tag: [tags.typeName, tags.className, tags.namespace], color: 'var(--syntax-type)' },
   { tag: [tags.function(tags.variableName), tags.function(tags.propertyName)], color: 'var(--syntax-function)' },
   { tag: [tags.propertyName, tags.attributeName], color: 'var(--syntax-property)' },
   { tag: [tags.tagName, tags.angleBracket], color: 'var(--syntax-tag)' },
   { tag: [tags.operator, tags.regexp, tags.escape], color: 'var(--syntax-operator)' },
   { tag: tags.heading, color: 'var(--syntax-tag)', fontWeight: 'bold' },
   { tag: tags.emphasis, fontStyle: 'italic' },
   { tag: tags.strong, fontWeight: 'bold' },
   { tag: [tags.link, tags.url], color: 'var(--syntax-function)', textDecoration: 'underline' },
   { tag: tags.invalid, color: 'var(--syntax-invalid)' },
]);

/** The theme and syntax colors of the diff editors, in the page's light or dark palette. */
export const diffTheme: Extension = [theme, syntaxHighlighting(highlightStyle)];
