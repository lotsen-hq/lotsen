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

describe('an editor that moves into another window', () => {
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

   it('measures the editor on a scroll of its scroller or of an element around it, after it moves into another window, and not on the scroll of an element beside it', () => {
      frame = document.body.appendChild(document.createElement('iframe'));
      const other = frame.contentWindow! as Window & typeof globalThis;
      const parent = document.body.appendChild(document.createElement('div'));
      editor = createDiffEditor({ parent, layout: 'inline', diff, language: null });
      const view = editor.head!;
      const beside = other.document.body.appendChild(other.document.createElement('div'));
      other.document.body.append(parent);
      editor.follow();
      const measure = vi.spyOn(EditorView.prototype, 'requestMeasure');

      beside.dispatchEvent(new Event('scroll'));
      expect(measure).not.toHaveBeenCalled();
      view.scrollDOM.dispatchEvent(new Event('scroll'));
      parent.dispatchEvent(new Event('scroll'));
      expect(measure).toHaveBeenCalledTimes(2);
   });

   for (const leave of ['follow', 'destroy'] as const) {
      it(`removes its scroll listener from the other window once the editor ${leave === 'follow' ? 'returns to the main page' : 'is destroyed there'}`, () => {
         frame = document.body.appendChild(document.createElement('iframe'));
         const other = frame.contentWindow! as Window & typeof globalThis;
         const parent = document.body.appendChild(document.createElement('div'));
         editor = createDiffEditor({ parent, layout: 'inline', diff, language: null });
         const added = vi.spyOn(other, 'addEventListener');
         const removed = vi.spyOn(other, 'removeEventListener');
         other.document.body.append(parent);
         editor.follow();
         // CodeMirror adds a scroll listener of its own to the window, without capture.
         const listener = added.mock.calls.find(([type, , options]) => type === 'scroll' && typeof options === 'object' && options.capture === true)![1];

         if (leave === 'follow') {
            document.body.append(parent);
            editor.follow();
         } else {
            editor.destroy();
            editor = null;
         }

         // An editor's destroy detaches its element, so no later scroll can show a listener left behind.
         expect(removed).toHaveBeenCalledWith('scroll', listener, { capture: true });
      });
   }
});
