import { EditorView } from '@codemirror/view';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { createDiffEditor, type DiffEditor } from '../src/lib/editor/diffEditor';
import type { TextDiff } from '../src/lib/types';

const diff: TextDiff = { content: 'text', baseText: 'one\ntwo\n', headText: 'one\nTWO\n', changes: [{ fromA: 4, toA: 8, fromB: 4, toB: 8 }] };

/** A ResizeObserver class whose instances record the elements they watch now, and whose callbacks a test calls. */
function recordingObserver() {
   const observers: { callback: ResizeObserverCallback; watched: Set<Element> }[] = [];
   class Recording {
      private readonly record: { callback: ResizeObserverCallback; watched: Set<Element> };
      constructor(callback: ResizeObserverCallback) {
         this.record = { callback, watched: new Set() };
         observers.push(this.record);
      }
      observe(target: Element) {
         this.record.watched.add(target);
      }
      unobserve(target: Element) {
         this.record.watched.delete(target);
      }
      disconnect() {
         this.record.watched.clear();
      }
   }

   const watching = () => new Set(observers.flatMap((observer) => [...observer.watched]));
   const resize = (target: Element) => observers.filter((observer) => observer.watched.has(target)).forEach((observer) => observer.callback([{ target } as ResizeObserverEntry], {} as ResizeObserver));
   return { Recording, watching, resize };
}

let editor: DiffEditor | null = null;
let frame: HTMLIFrameElement | null = null;

afterEach(() => {
   editor?.destroy();
   editor = null;
   frame?.remove();
   frame = null;
   document.body.replaceChildren();
   vi.unstubAllGlobals();
   vi.restoreAllMocks();
});

describe("an editor's size watching", () => {
   it("watches the scroller and the explanation block from the observer of the window that holds them, after they move into another window's document", () => {
      const main = recordingObserver();
      vi.stubGlobal('ResizeObserver', main.Recording);
      frame = document.body.appendChild(document.createElement('iframe'));
      const other = frame.contentWindow! as Window & typeof globalThis;
      const popout = recordingObserver();
      other.ResizeObserver = popout.Recording;
      const parent = document.body.appendChild(document.createElement('div'));
      editor = createDiffEditor({ parent, layout: 'inline', diff, language: null });
      const view = editor.head!;
      editor.explain({ key: 'k', below: { side: 'head', startLine: 2, endLine: 2 }, render: () => () => {} });
      const block = view.contentDOM.querySelector('.lotsen-explanation')!;

      other.document.body.append(parent);
      editor.follow();

      expect(popout.watching()).toEqual(new Set([view.scrollDOM, block]));
      const measure = vi.spyOn(EditorView.prototype, 'requestMeasure');
      popout.resize(view.scrollDOM);
      expect(measure).toHaveBeenCalled();
   });
});
