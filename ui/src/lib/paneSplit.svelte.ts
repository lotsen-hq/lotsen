import { getContext, hasContext, setContext } from 'svelte';
import { numberPreference } from './preference';

/** The least and the greatest share of a side-by-side diff's width that the base pane takes, in percent. */
export const paneSplitBounds = { min: 20, max: 80 } as const;

const half = 50;

/** The move of one arrow key, in percent. */
const keyStep = 10;

// The preference holds whole numbers, so it stores tenths of a percent, and a split from a drag reads back to a tenth of a percent.
const stored = numberPreference('lotsen.paneSplit', { min: paneSplitBounds.min * 10, max: paneSplitBounds.max * 10 }, half * 10);

/** Holds the base pane's share of a side-by-side diff's width, and keeps it in the browser, so it survives a remount and a reload. */
export class PaneSplit {
   private current = $state(stored.read() / 10);

   /** The base pane's share of the width in percent, within {@link paneSplitBounds}. The head pane takes the rest. */
   get percent(): number {
      return this.current;
   }

   /** Sets the base pane's share, clamped to {@link paneSplitBounds} and rounded to a tenth of a percent, and stores it. A value that is not a finite number changes nothing. */
   set(percent: number): void {
      if (!Number.isFinite(percent)) {
         return;
      }

      const tenths = Math.round(Math.min(Math.max(percent, paneSplitBounds.min), paneSplitBounds.max) * 10);
      this.current = tenths / 10;
      stored.write(tenths);
   }

   /** Gives each pane half the width. */
   reset(): void {
      this.set(half);
   }

   /**
    * Moves the split for a key: ArrowLeft and ArrowRight by 10 percent, or with Shift to the bound in their direction, and Home and End to the bounds.
    *
    * The keys and the steps match the workspace's column splitters, as `getDeltaPercentage` in paneforge 1.0.2's `dist/internal/helpers.js` moves them.
    *
    * @param shift Whether Shift is held with the key.
    * @returns Whether the key moves the split, so the caller stops the key's default action.
    */
   key(key: string, shift = false): boolean {
      const step = shift ? 100 : keyStep;
      const targets: Record<string, number> = { ArrowLeft: this.current - step, ArrowRight: this.current + step, Home: paneSplitBounds.min, End: paneSplitBounds.max };
      if (!Object.hasOwn(targets, key)) {
         return false;
      }

      this.set(targets[key]!);
      return true;
   }
}

const contextKey = Symbol('pane split');

/** Gives the calling component's descendants one split, during its initialization, so every side-by-side diff on the page moves together. */
export function providePaneSplit(): void {
   setContext(contextKey, new PaneSplit());
}

/** Returns the split that an ancestor provides, during the calling component's initialization, or a new split that starts from the stored one when no ancestor provides one. */
export function paneSplit(): PaneSplit {
   return hasContext(contextKey) ? getContext<PaneSplit>(contextKey) : new PaneSplit();
}
