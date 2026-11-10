import { describe, expect, it } from 'vitest';
import type { DecorationSwitches } from '../src/lib/passageMarks';
import { scrolledToEnd, stackFileKey, stopFileStack, shownStackFile, type StackFile } from '../src/lib/stopFiles';
import type { ChangedFile, StopPassage } from '../src/lib/types';

function stopPassage(side: StopPassage['side'], path: string, startLine: number, endLine: number, role: StopPassage['role'] = 'related'): StopPassage {
   return { side, path, startLine, endLine, role, commit: 'c'.repeat(40), text: 'text' };
}

const files: ChangedFile[] = [
   { path: 'src/cart.ts', kind: 'modified' },
   { path: 'src/pay.ts', kind: 'modified' },
   { path: 'src/checkout.ts', oldPath: 'src/order.ts', kind: 'renamed' },
   { path: 'src/new.ts', kind: 'added' },
   { path: 'src/old.ts', kind: 'deleted' },
];

const on: DecorationSwitches = { dim: true, highlight: true };

const summary = (stack: StackFile[]) => stack.map((file) => `${file.key}${file.outside ? ' outside' : ''}`);

describe('stopFileStack', () => {
   it('stacks the stop’s files in step through’s order, the primary passage’s file first, each file once', () => {
      const passages = [
         stopPassage('head', 'src/pay.ts', 30, 31),
         stopPassage('head', 'src/cart.ts', 5, 7, 'primary'),
         stopPassage('head', 'src/pay.ts', 2, 3),
         stopPassage('base', 'src/cart.ts', 20, 20),
      ];

      expect(summary(stopFileStack(passages, files, [], on))).toEqual(['diff:src/cart.ts', 'diff:src/pay.ts']);
   });

   it('stacks a renamed file once for a passage at its old path at the base and one at its path at the head', () => {
      const passages = [stopPassage('base', 'src/order.ts', 4, 4, 'primary'), stopPassage('head', 'src/checkout.ts', 9, 9)];

      const [renamed, ...rest] = stopFileStack(passages, files, [], on);

      expect(rest).toEqual([]);
      expect(renamed).toMatchObject({ kind: 'diff', key: 'diff:src/checkout.ts', file: files[2], paths: { base: 'src/order.ts', head: 'src/checkout.ts' } });
   });

   it('stacks a file that the pull request does not change as its text at the passage’s side, each side apart', () => {
      const passages = [stopPassage('head', 'src/catalog.ts', 3, 4, 'primary'), stopPassage('base', 'src/catalog.ts', 3, 4), stopPassage('head', 'src/new.ts', 1, 2)];

      const stack = stopFileStack(passages, files, [], on);

      expect(summary(stack)).toEqual(['text:head:src/catalog.ts', 'text:base:src/catalog.ts', 'diff:src/new.ts']);
      expect(stack[0]).toMatchObject({ kind: 'text', side: 'head', path: 'src/catalog.ts', paths: { base: null, head: 'src/catalog.ts' } });
   });

   it('stacks every file as its text at its side when the changed files are not known', () => {
      const passages = [stopPassage('head', 'src/cart.ts', 5, 7, 'primary'), stopPassage('base', 'src/cart.ts', 20, 20)];

      expect(summary(stopFileStack(passages, null, [], on))).toEqual(['text:head:src/cart.ts', 'text:base:src/cart.ts']);
   });

   it('collapses each file to its own passages and decorates them', () => {
      const passages = [stopPassage('head', 'src/cart.ts', 5, 7, 'primary'), stopPassage('head', 'src/pay.ts', 30, 31), stopPassage('base', 'src/cart.ts', 20, 20)];

      const [cart, pay] = stopFileStack(passages, files, [], on);

      expect(cart!.decorations).toEqual({
         passages: [
            { side: 'head', startLine: 5, endLine: 7, role: 'primary' },
            { side: 'base', startLine: 20, endLine: 20, role: 'related' },
         ],
         dim: true,
         highlight: true,
         collapse: true,
      });
      expect(pay!.decorations.passages).toEqual([{ side: 'head', startLine: 30, endLine: 31, role: 'related' }]);
      expect(pay!.decorations.collapse).toBe(true);
   });

   it('dims no stacked file when dimming is switched off, and keeps its passages', () => {
      const passages = [stopPassage('head', 'src/cart.ts', 5, 7, 'primary'), stopPassage('head', 'src/catalog.ts', 3, 4)];

      const stack = stopFileStack(passages, files, [], { dim: false, highlight: true });

      expect(stack.map((file) => [file.decorations.dim, file.decorations.passages.length])).toEqual([
         [false, 1],
         [false, 1],
      ]);
   });

   it('adds the files opened outside the stop at the bottom, in the order opened, marked outside, once each', () => {
      const passages = [stopPassage('head', 'src/cart.ts', 5, 7, 'primary')];
      const added = [
         { side: 'head', path: 'src/pay.ts' },
         { side: 'head', path: 'src/catalog.ts' },
         { side: 'base', path: 'src/pay.ts' },
      ] as const;

      const stack = stopFileStack(passages, files, added, on);

      expect(summary(stack)).toEqual(['diff:src/cart.ts', 'diff:src/pay.ts outside', 'text:head:src/catalog.ts outside']);
      expect(stack[1]!.decorations).toEqual({ passages: [], dim: false, highlight: true, collapse: false });
   });

   it('leaves out an added file that the stop’s passages already stack', () => {
      const passages = [stopPassage('head', 'src/cart.ts', 5, 7, 'primary'), stopPassage('head', 'src/checkout.ts', 1, 1)];

      expect(
         summary(
            stopFileStack(
               passages,
               files,
               [
                  { side: 'base', path: 'src/order.ts' },
                  { side: 'base', path: 'src/cart.ts' },
               ],
               on,
            ),
         ),
      ).toEqual(['diff:src/cart.ts', 'diff:src/checkout.ts']);
   });

   it('stacks only the added files for a stop without passages', () => {
      expect(summary(stopFileStack([], files, [{ side: 'base', path: 'src/old.ts' }], on))).toEqual(['diff:src/old.ts outside']);
   });
});

describe('stackFileKey', () => {
   it('names a changed file by its path at the head, from either side', () => {
      expect(stackFileKey(files, { side: 'base', path: 'src/order.ts' })).toBe('diff:src/checkout.ts');
      expect(stackFileKey(files, { side: 'head', path: 'src/checkout.ts' })).toBe('diff:src/checkout.ts');
      expect(stackFileKey(files, { side: 'base', path: 'src/old.ts' })).toBe('diff:src/old.ts');
   });

   it('names any other file by its side and path', () => {
      expect(stackFileKey(files, { side: 'head', path: 'src/old.ts' })).toBe('text:head:src/old.ts');
      expect(stackFileKey(null, { side: 'head', path: 'src/cart.ts' })).toBe('text:head:src/cart.ts');
   });
});

describe('shownStackFile', () => {
   // Three stacked files of 100px each, below a 20px note, in a 150px viewport from 0 to 150.
   const extents = (scrolled: number) => ['a', 'b', 'c'].map((key, index) => ({ key, top: 20 + index * 100 - scrolled, bottom: 20 + (index + 1) * 100 - scrolled }));
   const viewport = { top: 0, bottom: 150, atEnd: false };
   const top = (scrolled: number) => shownStackFile(extents(scrolled), viewport, null);

   it('names the file whose part shows at the viewport’s top', () => {
      expect(top(0)).toBe('a');
      expect(top(110)).toBe('a');
      expect(top(130)).toBe('b');
      expect(top(250)).toBe('c');
   });

   it('passes over a file of which only a sliver, such as its bottom border, still shows', () => {
      expect(top(116)).toBe('b');
   });

   it('measures from the viewport’s own top', () => {
      expect(shownStackFile(extents(0), { top: 180, bottom: 330, atEnd: false }, null)).toBe('b');
   });

   it('names the opened file while any part of it shows, and the top file once none does', () => {
      // At 170, the viewport shows the end of b at its top and all of c below it.
      expect(shownStackFile(extents(170), viewport, 'c')).toBe('c');
      expect(shownStackFile(extents(0), viewport, 'b')).toBe('b');
      expect(shownStackFile(extents(0), viewport, 'c')).toBe('a');
      expect(shownStackFile(extents(170), viewport, 'a')).toBe('b');
      expect(shownStackFile(extents(170), viewport, 'gone')).toBe('b');
   });

   it('names no file before layout, or in an empty stack', () => {
      expect(
         shownStackFile(
            ['a', 'b'].map((key) => ({ key, top: 0, bottom: 0 })),
            { top: 0, bottom: 0, atEnd: false },
            null,
         ),
      ).toBeNull();
      expect(shownStackFile([], viewport, null)).toBeNull();
      expect(shownStackFile([], { ...viewport, atEnd: true }, null)).toBeNull();
   });

   it('names the last file once the viewport is at the stack’s end, where the last file never reaches the viewport’s top', () => {
      // 170 is the end: 320px of content in a 150px viewport.
      expect(shownStackFile(extents(170), { ...viewport, atEnd: true }, null)).toBe('c');
   });

   it('names the opened file at the stack’s end while any part of it shows', () => {
      expect(shownStackFile(extents(170), { ...viewport, atEnd: true }, 'b')).toBe('b');
      expect(shownStackFile(extents(170), { ...viewport, atEnd: true }, 'a')).toBe('c');
   });
});

describe('scrolledToEnd', () => {
   const at = (scrollTop: number, scrollHeight = 320, clientHeight = 150) => scrolledToEnd({ scrollTop, scrollHeight, clientHeight });

   it('holds at the end and within one pixel of it, as a fractional scroll position leaves it', () => {
      expect(at(170)).toBe(true);
      expect(at(169.5)).toBe(true);
      expect(at(169)).toBe(true);
   });

   it('does not hold short of the end, or at the top', () => {
      expect(at(168)).toBe(false);
      expect(at(0)).toBe(false);
   });

   it('does not hold for a viewport that cannot scroll, or only within that pixel', () => {
      expect(at(0, 150)).toBe(false);
      expect(at(0, 151)).toBe(false);
      expect(at(0, 0, 0)).toBe(false);
   });
});
