import { render, waitFor } from '@testing-library/svelte';
import { flushSync, tick } from 'svelte';
import { describe, expect, it } from 'vitest';
import DiffView from '../src/components/DiffView.svelte';
import FileView from '../src/components/FileView.svelte';
import type { DiffSurfaceOptions, EditorSurfaces, FileSurfaceOptions } from '../src/lib/editor/surface';
import { filePassageDecorations } from '../src/lib/passageMarks';
import type { Stop, StopPassage, TextDiff } from '../src/lib/types';
import type { KnownDiff, KnownText } from '../src/lib/viewState';
import { explanationOf } from './explanation';
import { Reactive } from './reactive.svelte';
import { RecordingSurface, RecordingSurfaces } from './surface/recordingSurface';
import SurfaceHost from './surface/SurfaceHost.svelte';

const diff: TextDiff = { content: 'text', baseText: 'one\ntwo\n', headText: 'one\nTWO\n', changes: [{ fromA: 4, toA: 8, fromB: 4, toB: 8 }] };
const loadedDiff = (path: string): KnownDiff => ({ state: 'loaded', path, diff });
const loadedText = (path: string): KnownText => ({ state: 'loaded', side: 'head', path, text: { content: 'text', path, side: 'head', commit: 'b'.repeat(40), text: 'one\ntwo\n' } });
const passage = (path: string, side: StopPassage['side'] = 'head'): StopPassage => ({ side, path, startLine: 2, endLine: 2, role: 'primary', commit: 'b'.repeat(40), text: 'TWO' });

function stopOn(primary: StopPassage): Stop {
   return {
      id: 's1',
      title: 'The change',
      kind: 'inspect',
      chapter: 'c',
      stage: 'written',
      status: 'ready',
      version: 1,
      earlier: [],
      content: { headline: 'It changes.', passages: [primary], html: { headline: '<p>It changes.</p>\n' } },
   };
}

/** Renders the diff view of a file on recording surfaces, and waits for its editors to mount. */
async function renderDiff(props: Record<string, unknown>) {
   const surfaces = new RecordingSurfaces();
   const shown = new Reactive(props);
   render(SurfaceHost, { props: { surfaces, component: DiffView, props: shown } });
   await waitFor(() => expect(surfaces.mounted).toHaveLength(1));
   const rerender = async (next: Record<string, unknown>) => {
      shown.value = { ...props, ...next };
      await tick();
   };
   return { surfaces, rerender };
}

describe('the diff view through its editor surface', () => {
   it('mounts the file in its layout, and gives its decorations to the editors without mounting them again', async () => {
      const decorations = filePassageDecorations([passage('a.ts')], { base: 'a.ts', head: 'a.ts' }, { dim: true, highlight: true });
      const { surfaces, rerender } = await renderDiff({ path: 'a.ts', known: loadedDiff('a.ts'), layout: 'inline' });

      await rerender({ decorations });

      expect(surfaces.mounted).toHaveLength(1);
      expect(surfaces.last.options).toMatchObject({ path: 'a.ts', layout: 'inline', diff });
      expect(surfaces.last.callsOf('showPassages').at(-1)?.decorations).toBe(decorations);
   });

   it('mounts the editors again for another layout, and releases the ones before', async () => {
      const { surfaces, rerender } = await renderDiff({ path: 'a.ts', known: loadedDiff('a.ts'), layout: 'inline' });
      const first = surfaces.last;

      await rerender({ layout: 'sideBySide' });

      await waitFor(() => expect(surfaces.mounted).toHaveLength(2));
      expect(first.callsOf('destroy')).toHaveLength(1);
      expect(surfaces.last.options).toMatchObject({ layout: 'sideBySide' });
   });

   it('focuses the line of a focus on its file, and no line of a focus on another file', async () => {
      const { surfaces, rerender } = await renderDiff({ path: 'a.ts', known: loadedDiff('a.ts'), focus: { path: 'b.ts', side: 'head', line: 2 } });

      await rerender({ focus: { path: 'a.ts', side: 'base', line: 2 } });

      await waitFor(() => expect(surfaces.last.callsOf('focus').map((call) => call.line)).toEqual([{ side: 'base', line: 2 }]));
   });

   it('places an inline explanation below its inline passage in the file that holds it, which then shows inline', async () => {
      const { view } = explanationOf(stopOn(passage('a.ts')), { mode: 'inline' });
      const { surfaces } = await renderDiff({ path: 'a.ts', known: loadedDiff('a.ts'), explanation: view });

      await waitFor(() => expect(surfaces.last.callsOf('placeBlock').at(-1)?.below).toEqual(passage('a.ts')));
      expect(view.host).toBe('inline');
   });

   it('places no block for an explanation whose inline passage is in another file, or that belongs in the Stop panel', async () => {
      const elsewhere = explanationOf(stopOn(passage('b.ts')), { mode: 'inline' }).view;
      const side = explanationOf(stopOn(passage('a.ts')), { mode: 'side' }).view;
      const { surfaces, rerender } = await renderDiff({ path: 'a.ts', known: loadedDiff('a.ts'), explanation: elsewhere });

      await rerender({ explanation: side });

      expect(surfaces.last.callsOf('placeBlock').every((call) => call.key === null)).toBe(true);
      expect([elsewhere.host, side.host]).toEqual(['panel', 'panel']);
   });
});

describe('the file view through its editor surface', () => {
   it('focuses a line at its side of its file, and no line at the other side', async () => {
      const surfaces = new RecordingSurfaces();
      const props = new Reactive<Record<string, unknown>>({ known: loadedText('a.ts'), focus: { path: 'a.ts', side: 'base', line: 2 } });
      render(SurfaceHost, { props: { surfaces, component: FileView, props } });
      await waitFor(() => expect(surfaces.mounted).toHaveLength(1));

      props.value = { ...props.value, focus: { path: 'a.ts', side: 'head', line: 2 } };

      await waitFor(() => expect(surfaces.last.callsOf('focus').map((call) => call.line)).toEqual([{ side: 'head', line: 2 }]));
      expect(surfaces.last.options).toMatchObject({ path: 'a.ts', side: 'head', text: 'one\ntwo\n' });
   });
});

/** Editor surfaces that mount after one await, as CodeMirror's do after the language loads, and run a teardown once, after the first build and before its promise settles. */
class TeardownSurfaces implements EditorSurfaces {
   readonly built: RecordingSurface[] = [];
   private teardown: (() => void) | null;

   constructor(teardown: () => void) {
      this.teardown = teardown;
   }

   diff(options: DiffSurfaceOptions): Promise<RecordingSurface | null> {
      return this.mount(options);
   }

   file(options: FileSurfaceOptions): Promise<RecordingSurface | null> {
      return this.mount(options);
   }

   private async mount(options: DiffSurfaceOptions | FileSurfaceOptions): Promise<RecordingSurface | null> {
      await Promise.resolve();
      if (options.signal?.aborted) {
         return null;
      }

      const surface = new RecordingSurface(options);
      this.built.push(surface);
      const teardown = this.teardown;
      this.teardown = null;
      teardown?.();
      return surface;
   }
}

describe('a file view whose file changes after its editors are built and before their mount settles', () => {
   it.each([
      ['the diff view', DiffView, (path: string) => ({ path, known: loadedDiff(path) })],
      ['the file view', FileView, (path: string) => ({ known: loadedText(path) })],
   ] as const)('%s releases the editors it built, and keeps the next file’s', async (_, component, propsOf) => {
      const props = new Reactive<Record<string, unknown>>(propsOf('a.ts'));
      const surfaces = new TeardownSurfaces(() => {
         props.value = propsOf('b.ts');
         flushSync();
      });
      render(SurfaceHost, { props: { surfaces, component, props } });

      await waitFor(() => expect(surfaces.built.map((surface) => surface.options.path)).toEqual(['a.ts', 'b.ts']));
      await tick();

      expect(surfaces.built.map((surface) => surface.callsOf('destroy').length)).toEqual([1, 0]);
   });
});
