import { mount, unmount } from 'svelte';
import ExplanationBlock from '../components/ExplanationBlock.svelte';
import type { BlockHost, EditorSurface } from './editor/surface';
import type { ExplanationView } from './explanationView.svelte';
import type { FilePaths } from './pin';

/**
 * Places the explanation's block in one file's editors when its inline passage lies in the file, and mounts `ExplanationBlock` in each element that the editors draw for it.
 *
 * The block's key changes only with the stop and its inline passage, so a new focused passage or new prose updates the mounted content in place.
 */
export class InlineHost {
   /** What identifies the block to {@link EditorSurface.placeBlock}, or null when the file shows none. */
   readonly key = $derived.by(() => {
      const view = this.view();
      const passage = view?.inlinePassage ?? null;
      const paths = this.paths();
      if (view === null || passage === null || view.stop === null || paths[passage.side] !== passage.path) {
         return null;
      }

      const { side, path, startLine, endLine } = passage;
      return JSON.stringify([view.stop.id, side, path, startLine, endLine]);
   });

   private readonly view: () => ExplanationView | null;
   private readonly paths: () => FilePaths;

   /**
    * @param view - Returns the page's explanation, or null when the file shows none.
    * @param paths - Returns the file's paths at each side, which decide whether the explanation's inline passage lies in the file.
    */
   constructor(view: () => ExplanationView | null, paths: () => FilePaths) {
      this.view = view;
      this.paths = paths;
   }

   /**
    * Shows the block in the editors, or removes it, and reports to the explanation that they show it. The Stop panel shows the explanation while no editor reports its block.
    *
    * A caller runs it in an effect whose cleanup is the returned function, so a change of the editors or of the block reports the block gone first.
    *
    * @param surface - The file's editors, or null while none are mounted.
    * @returns The function that reports that the editors no longer show the block, or undefined when they do not show it.
    */
   show(surface: EditorSurface | null): (() => void) | undefined {
      const view = this.view();
      const block = this.block(view);
      return surface?.placeBlock(block) && view !== null && block !== null ? view.place() : undefined;
   }

   private block(view: ExplanationView | null): BlockHost | null {
      const key = this.key;
      const passage = view?.inlinePassage ?? null;
      if (view === null || key === null || passage === null) {
         return null;
      }

      return {
         key,
         below: passage,
         render: (target) => {
            const instance = mount(ExplanationBlock, { target, props: { view, host: 'inline' } });
            return () => void unmount(instance);
         },
      };
   }
}
