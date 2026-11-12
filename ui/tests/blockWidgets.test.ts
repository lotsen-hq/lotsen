import { EditorView } from '@codemirror/view';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createDiffEditor, createFileEditor, type DiffEditor } from '../src/lib/editor/diffEditor';
import type { BlockHost } from '../src/lib/editor/surface';
import type { DiffLayout } from '../src/lib/diffLayout';
import type { LinesAtSide } from '../src/lib/passageMarks';
import type { TextDiff } from '../src/lib/types';

// Ten numbered lines at the base. The head replaces lines 4 and 5 with three lines and deletes line 8.
const base = Array.from({ length: 10 }, (_, index) => `line ${index + 1}`).join('\n') + '\n';
const head = ['line 1', 'line 2', 'line 3', 'new a', 'new b', 'new c', 'line 6', 'line 7', 'line 9', 'line 10'].join('\n') + '\n';
const offset = (text: string, line: number) =>
   text
      .split('\n')
      .slice(0, line - 1)
      .join('\n').length + (line > 1 ? 1 : 0);
const diff: TextDiff = {
   content: 'text',
   baseText: base,
   headText: head,
   changes: [
      { fromA: offset(base, 4), toA: offset(base, 6), fromB: offset(head, 4), toB: offset(head, 7) },
      { fromA: offset(base, 8), toA: offset(base, 9), fromB: offset(head, 9), toB: offset(head, 9) },
   ],
};

let parent: HTMLElement;
let editor: DiffEditor | null = null;
/** The hosts that a block's content is mounted in now. */
let mounted: Set<HTMLElement>;
let renders: number;

beforeEach(() => {
   parent = document.body.appendChild(document.createElement('div'));
   mounted = new Set();
   renders = 0;
});

afterEach(() => {
   editor?.destroy();
   editor = null;
   parent.remove();
   vi.unstubAllGlobals();
});

function mount(layout: DiffLayout): DiffEditor {
   editor = createDiffEditor({ parent, layout, diff, language: null });
   return editor;
}

function block(lines: LinesAtSide, key = `${lines.side}:${lines.startLine}-${lines.endLine}`): BlockHost {
   return {
      key,
      below: lines,
      render: (host) => {
         renders++;
         mounted.add(host);
         host.append(document.createElement('p'));
         host.firstElementChild!.textContent = `explanation ${key}`;
         return () => mounted.delete(host);
      },
   };
}

const hosts = (view: EditorView) => [...view.contentDOM.querySelectorAll<HTMLElement>(':scope > .lotsen-explanation')];
const spacers = (view: EditorView) => [...view.contentDOM.querySelectorAll<HTMLElement>(':scope > .lotsen-explanationSpacer')];
const lineAbove = (element: Element) => element.previousElementSibling?.textContent;

describe('an explanation block', () => {
   it('sits below the last line of head lines in the inline view, with its content mounted', () => {
      const view = mount('inline').head!;

      editor!.explain(block({ side: 'head', startLine: 4, endLine: 6 }));

      const [host] = hosts(view);
      expect(host).toBeDefined();
      expect(lineAbove(host!)).toBe('new c');
      expect(host!.textContent).toBe('explanation head:4-6');
      expect(host!.contentEditable).toBe('false');
      expect([...mounted]).toEqual([host]);
   });

   it('sits below the deleted lines of base lines that the inline view shows as deleted, above the head line they sit at', () => {
      const view = mount('inline').head!;

      editor!.explain(block({ side: 'base', startLine: 8, endLine: 8 }));

      const [host] = hosts(view);
      expect(host!.previousElementSibling?.classList.contains('cm-deletedChunk')).toBe(true);
      expect(host!.previousElementSibling?.textContent).toBe('line 8');
      expect(host!.nextElementSibling?.textContent).toBe('line 9');
   });

   it('sits below unchanged base lines at the head line that equals the last of them, in the inline view', () => {
      const view = mount('inline').head!;

      editor!.explain(block({ side: 'base', startLine: 6, endLine: 7 }));

      expect(lineAbove(hosts(view)[0]!)).toBe('line 7');
   });

   it('sits in the pane of its side when side by side, with an empty spacer in the other pane after the line of the same row', () => {
      const { base: a, head: b } = mount('sideBySide');

      editor!.explain(block({ side: 'head', startLine: 6, endLine: 6 }));

      expect(hosts(a!)).toEqual([]);
      expect(lineAbove(hosts(b!)[0]!)).toBe('new c');
      // The head's third changed line has no base line in its row, so the spacer follows the base's last changed line.
      const [spacer] = spacers(a!);
      expect(lineAbove(spacer!)).toBe('line 5');
      expect(spacer!.style.height).toBe('0px');
      expect(spacers(b!)).toEqual([]);
   });

   it('puts the spacer after the line of the same row in an unchanged region, and the block in the base pane for base lines', () => {
      const { base: a, head: b } = mount('sideBySide');

      editor!.explain(block({ side: 'base', startLine: 2, endLine: 3 }));

      expect(lineAbove(hosts(a!)[0]!)).toBe('line 3');
      expect(lineAbove(spacers(b!)[0]!)).toBe('line 3');
   });

   it('keeps the mounted content when the same block is shown again, and replaces it for another block', () => {
      const view = mount('inline').head!;
      editor!.explain(block({ side: 'head', startLine: 1, endLine: 1 }));
      const [first] = hosts(view);

      editor!.explain(block({ side: 'head', startLine: 1, endLine: 1 }));
      expect(hosts(view)).toEqual([first]);
      expect(renders).toBe(1);

      editor!.explain(block({ side: 'head', startLine: 2, endLine: 2 }));
      expect(lineAbove(hosts(view)[0]!)).toBe('line 2');
      expect(mounted.size).toBe(1);
      expect(mounted.has(first!)).toBe(false);
   });

   it.each<DiffLayout>(['inline', 'sideBySide'])('removes its content and the spacer when no block is shown, and when the editors go, %s', (layout) => {
      const shown = mount(layout);
      const views = [shown.base, shown.head].filter((view) => view !== null);
      shown.explain(block({ side: 'head', startLine: 2, endLine: 2 }));
      expect(mounted.size).toBe(1);

      shown.explain(null);
      expect(mounted.size).toBe(0);
      expect(views.flatMap((view) => [...hosts(view), ...spacers(view)])).toEqual([]);

      shown.explain(block({ side: 'head', startLine: 2, endLine: 2 }));
      expect(mounted.size).toBe(1);
      shown.destroy();
      editor = null;
      expect(mounted.size).toBe(0);
   });

   it('shows nothing for lines at a side that the editors do not show, and says whether it shows the block', () => {
      const file = createFileEditor({ parent, text: base, side: 'head', language: null });
      editor = file;

      expect(file.explain(block({ side: 'base', startLine: 2, endLine: 2 }))).toBe(false);
      expect(mounted.size).toBe(0);

      expect(file.explain(block({ side: 'head', startLine: 2, endLine: 2 }))).toBe(true);
      expect(lineAbove(hosts(file.head!)[0]!)).toBe('line 2');
      expect(file.explain(block({ side: 'head', startLine: 2, endLine: 2 }))).toBe(true);
      expect(file.explain(null)).toBe(false);
   });
});
