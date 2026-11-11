import { StateEffect, StateField, type Extension } from '@codemirror/state';
import { Decoration, EditorView, ViewPlugin, WidgetType, type DecorationSet, type ViewUpdate } from '@codemirror/view';
import { editorWindow } from './editorWindow';

/** Where a block sits in an editor: after the line that ends at `pos`, or with `before`, above the line at `pos` and below the deleted lines that the inline view draws there. */
export interface BlockPosition {
   pos: number;
   before: boolean;
}

/** Mounts content into a block's element, and returns the function that removes it again. */
export type BlockRender = (host: HTMLElement) => () => void;

/**
 * One block that an editor shows, with the height it last measured.
 *
 * The editor draws a new element for the block each time the block comes back into its viewport, so the block outlives each element.
 */
export class ShownBlock {
   readonly render: BlockRender;
   /** The height of the block's element in pixels when it was last measured, or 0 before that. */
   height = 0;
   private readonly onheight: (height: number) => void;

   /** @param onheight Receives each new height of the block's element. */
   constructor(render: BlockRender, onheight: (height: number) => void = () => {}) {
      this.render = render;
      this.onheight = onheight;
   }

   /** Records the element's height, and lets the editor measure its lines again, because the editor does not watch the content of a widget. */
   resized(view: EditorView, height: number): void {
      if (Math.abs(height - this.height) < 0.5) {
         return;
      }

      this.height = height;
      this.onheight(height);
      view.requestMeasure();
   }
}

const blockClass = 'lotsen-explanation';
const spacerClass = 'lotsen-explanationSpacer';

/** The function that removes each drawn block element's content and stops watching its size. */
const drawnBlocks = new WeakMap<HTMLElement, () => void>();

class BlockWidget extends WidgetType {
   private readonly block: ShownBlock;

   constructor(block: ShownBlock) {
      super();
      this.block = block;
   }

   override eq(other: BlockWidget): boolean {
      return other.block === this.block;
   }

   override toDOM(view: EditorView): HTMLElement {
      const host = document.createElement('div');
      host.className = blockClass;
      const remove = this.block.render(host);
      const unwatch = view.plugin(editorWindow)?.watch(host, () => this.block.resized(view, host.getBoundingClientRect().height));
      drawnBlocks.set(host, () => {
         unwatch?.();
         remove();
      });
      view.plugin(blockWidth)?.schedule();
      return host;
   }

   override destroy(dom: HTMLElement): void {
      drawnBlocks.get(dom)?.();
      drawnBlocks.delete(dom);
   }

   override get estimatedHeight(): number {
      return this.block.height > 0 ? this.block.height : -1;
   }

   // The content handles its own clicks and keys, so the editor leaves them alone.
   override ignoreEvent(): boolean {
      return true;
   }
}

/** Empty space as high as a block in the other pane of a side-by-side diff, so the lines after both stay aligned. */
class SpacerWidget extends WidgetType {
   private readonly height: number;

   constructor(height: number) {
      super();
      this.height = height;
   }

   override eq(other: SpacerWidget): boolean {
      return other.height === this.height;
   }

   override toDOM(): HTMLElement {
      const element = document.createElement('div');
      element.className = spacerClass;
      element.setAttribute('aria-hidden', 'true');
      this.updateDOM(element);
      return element;
   }

   // The policy allows a height set through the CSSOM, as the merge package's own spacers set theirs.
   override updateDOM(dom: HTMLElement): boolean {
      dom.style.height = `${this.height}px`;
      return true;
   }

   override get estimatedHeight(): number {
      return this.height;
   }
}

const setBlock = StateEffect.define<{ position: BlockPosition; widget: WidgetType } | null>();

const blockField = StateField.define<DecorationSet>({
   create: () => Decoration.none,
   update: (value, transaction) => {
      const effect = transaction.effects.findLast((candidate) => candidate.is(setBlock));
      if (effect === undefined) {
         return value.map(transaction.changes);
      }

      if (effect.value === null) {
         return Decoration.none;
      }

      // A block widget with side 0 comes after the merge package's deletion widget, whose side is -1, and before the line.
      const { position, widget } = effect.value;
      return Decoration.set([Decoration.widget({ block: true, side: position.before ? 0 : 1, widget }).range(position.pos)]);
   },
   provide: (self) => EditorView.decorations.from(self),
});

/**
 * Keeps each block as wide as the editor's visible text area, and at its left edge when the text scrolls sideways.
 *
 * A block is a child of the content, which grows to the longest line, so without this a block's prose would run past the visible width.
 */
class BlockWidth {
   private readonly view: EditorView;

   constructor(view: EditorView) {
      this.view = view;
      this.schedule();
   }

   update(update: ViewUpdate): void {
      if (update.geometryChanged || update.viewportChanged) {
         this.schedule();
      }
   }

   schedule(): void {
      this.view.requestMeasure({
         key: this,
         read: (view) => {
            const gutters = view.dom.querySelector('.cm-gutters');
            const left = gutters instanceof HTMLElement ? gutters.offsetWidth : 0;
            return { left, width: view.scrollDOM.clientWidth - left };
         },
         write: ({ left, width }, view) => {
            // A hidden editor measures no width, and keeps the last one until it shows again.
            if (width <= 0) {
               return;
            }

            for (const host of view.contentDOM.querySelectorAll<HTMLElement>(`:scope > .${blockClass}`)) {
               host.style.left = `${left}px`;
               host.style.width = `${width}px`;
            }
         },
      });
   }
}

const blockWidth = ViewPlugin.fromClass(BlockWidth);

/** The extension that lets an editor show one block or one spacer, which {@link showBlock}, {@link showSpacer} and {@link clearBlock} set. */
export const blockWidgets: Extension = [blockField, blockWidth, editorWindow];

/** Shows a block at a position, in place of the editor's block or spacer. */
export function showBlock(view: EditorView, position: BlockPosition, block: ShownBlock): void {
   view.dispatch({ effects: setBlock.of({ position, widget: new BlockWidget(block) }) });
}

/** Shows a spacer of a height at a position, in place of the editor's block or spacer. */
export function showSpacer(view: EditorView, position: BlockPosition, height: number): void {
   view.dispatch({ effects: setBlock.of({ position, widget: new SpacerWidget(height) }) });
}

/** Removes the editor's block or spacer, which removes a block's content. */
export function clearBlock(view: EditorView): void {
   view.dispatch({ effects: setBlock.of(null) });
}
