import type { BlockHost, DiffSurfaceOptions, EditorSurface, EditorSurfaces, FileSurfaceOptions, SideLine } from '../../src/lib/editor/surface';
import type { LinesAtSide, PassageDecorations } from '../../src/lib/passageMarks';

/** One call on a recorded surface, by its method and its argument. */
export type SurfaceCall =
   { call: 'showPassages'; decorations: PassageDecorations } | { call: 'focus'; line: SideLine } | { call: 'placeBlock'; key: string | null; below: LinesAtSide | null } | { call: 'destroy' };

/** A surface that records the calls it receives, which shows every line and every block. */
export class RecordingSurface implements EditorSurface {
   readonly options: DiffSurfaceOptions | FileSurfaceOptions;
   readonly calls: SurfaceCall[] = [];
   readonly panes = null;
   /** The sides that the surface shows, which decide whether it shows a line or a block. */
   private readonly sides: ReadonlySet<'base' | 'head'>;

   constructor(options: DiffSurfaceOptions | FileSurfaceOptions) {
      this.options = options;
      this.sides = new Set('side' in options ? [options.side] : (['base', 'head'] as const).filter((side) => (side === 'base' ? options.diff.baseText : options.diff.headText) !== null));
   }

   showPassages(decorations: PassageDecorations): void {
      this.calls.push({ call: 'showPassages', decorations });
   }

   focus(line: SideLine): boolean {
      this.calls.push({ call: 'focus', line });
      return this.sides.has(line.side);
   }

   placeBlock(block: BlockHost | null): boolean {
      this.calls.push({ call: 'placeBlock', key: block?.key ?? null, below: block?.below ?? null });
      return block !== null && this.sides.has(block.below.side);
   }

   selection(): null {
      return null;
   }

   lineText(): null {
      return null;
   }

   destroy(): void {
      this.calls.push({ call: 'destroy' });
   }

   /** The calls of one method, in order. */
   callsOf<K extends SurfaceCall['call']>(call: K): Extract<SurfaceCall, { call: K }>[] {
      return this.calls.filter((candidate): candidate is Extract<SurfaceCall, { call: K }> => candidate.call === call);
   }
}

/** Editor surfaces that mount a {@link RecordingSurface} at once for each file, and keep every surface they mounted. */
export class RecordingSurfaces implements EditorSurfaces {
   readonly mounted: RecordingSurface[] = [];

   async diff(options: DiffSurfaceOptions): Promise<RecordingSurface> {
      return this.add(new RecordingSurface(options));
   }

   async file(options: FileSurfaceOptions): Promise<RecordingSurface> {
      return this.add(new RecordingSurface(options));
   }

   /** The surface mounted last, which fails the test when none is. */
   get last(): RecordingSurface {
      const surface = this.mounted.at(-1);
      if (surface === undefined) {
         throw new Error('No surface is mounted.');
      }

      return surface;
   }

   private add(surface: RecordingSurface): RecordingSurface {
      this.mounted.push(surface);
      return surface;
   }
}
