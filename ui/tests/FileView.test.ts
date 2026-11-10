import { language } from '@codemirror/language';
import { EditorView } from '@codemirror/view';
import { render, waitFor } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import FileView from '../src/components/FileView.svelte';
import type { KnownText } from '../src/lib/viewState';
import { noPassageDecorations, type PassageDecorations } from '../src/lib/passageMarks';

const commit = 'b'.repeat(40);

function loaded(path: string, text: string): KnownText {
   return { state: 'loaded', side: 'head', path, text: { content: 'text', path, side: 'head', commit, text } };
}

async function editor(): Promise<EditorView> {
   return waitFor(() => {
      const found = [...document.querySelectorAll<HTMLElement>('.cm-editor')];
      expect(found).toHaveLength(1);
      return EditorView.findFromDOM(found[0]!)!;
   });
}

describe('the file view', () => {
   it('shows <script> and <style> in the code as text, read-only, with the side and the commit in a stack', async () => {
      const text = '<script>alert(1)</script>\n<style>body { display: none }</style>\n';
      const { container } = render(FileView, { props: { known: loaded('page.html', text), stacked: true } });

      const view = await editor();
      expect(view.state.doc.toString()).toBe(text);
      expect(view.state.readOnly).toBe(true);
      expect(container.querySelector('.editor script, .editor style')).toBeNull();
      expect(container.textContent).toContain('page.html');
      expect(container.textContent).toContain(`head ${commit.slice(0, 8)}`);
   });

   it('highlights a file in its language', async () => {
      render(FileView, { props: { known: loaded('Program.cs', 'class A {}\n') } });

      expect((await editor()).state.facet(language)?.name).toBe('csharp');
   });

   it('shows lines with CRLF breaks as lines', async () => {
      render(FileView, { props: { known: loaded('a.txt', 'one\r\ntwo\r\n') } });

      expect((await editor()).state.doc.lines).toBe(3);
   });

   it('dims the lines outside the file’s passages and highlights each passage with its label, and follows new decorations in the same editor', async () => {
      const decorations: PassageDecorations = { passages: [{ side: 'head', startLine: 2, endLine: 2, role: 'primary', label: 'the call' }], dim: true, highlight: true, collapse: false };
      const { rerender } = render(FileView, { props: { known: loaded('a.ts', 'one\r\ntwo\r\nthree\r\n'), decorations } });
      const view = await editor();
      const marks = () => [...view.contentDOM.querySelectorAll('.cm-line')].map((line) => `${line.textContent}:${line.className.replace('cm-line', '').trim()}`);

      await waitFor(() => expect(marks()).toEqual(['one:lotsen-dimmed', 'twothe call:lotsen-passage lotsen-passage-primary', 'three:lotsen-dimmed', ':lotsen-dimmed']));

      await rerender({ decorations: noPassageDecorations });

      expect(await editor()).toBe(view);
      expect(marks()).toEqual(['one:', 'two:', 'three:', ':']);
   });

   it('replaces the editor when another file opens, and removes it for a file without text', async () => {
      const { rerender } = render(FileView, { props: { known: loaded('a.ts', 'first\n') } });
      await editor();

      await rerender({ known: loaded('b.ts', 'second\n') });
      await waitFor(async () => expect((await editor()).state.doc.toString()).toBe('second\n'));

      await rerender({ known: { state: 'loaded', side: 'base', path: 'logo.png', text: { content: 'binary', path: 'logo.png', side: 'base', commit } } });
      await waitFor(() => expect(document.querySelector('.cm-editor')).toBeNull());
   });
});
