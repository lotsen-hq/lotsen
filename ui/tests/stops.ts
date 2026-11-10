import type { AnchorHint, Stop, StopContent, StopKind, StopVersion, WrittenStop } from '../src/lib/types';

/** The fields of a stop fixture: without content a pending stop, with it a written one, at version 1 unless the fixture gives another. */
export interface StopFixture {
   id: string;
   title: string;
   kind: StopKind;
   chapter: string;
   content: StopContent | null;
   version?: number;
   earlier?: StopVersion[];
   staleOn?: number;
   skipped?: boolean;
   anchorHint?: AnchorHint | null;
}

/** Returns the stop that the fixture describes, with the status that its stage and version give it. */
export function stopOf({ id, title, kind, chapter, content, version = 1, earlier = [], staleOn, skipped, anchorHint = null }: StopFixture): Stop {
   const base = { id, title, kind, chapter, ...(skipped === true ? { skipped } : {}) };
   return content === null
      ? { ...base, stage: 'pending', status: 'pending', anchorHint }
      : { ...base, stage: 'written', status: version === 1 ? 'ready' : 'revised', content, version, earlier, ...(staleOn === undefined ? {} : { staleOn }) };
}

/** Returns the stop as a written one, and fails the test when it is pending or missing. */
export function writtenOf(stop: Stop | null | undefined): WrittenStop {
   if (stop?.stage !== 'written') {
      throw new Error(`Expected a written stop, got ${stop === undefined || stop === null ? 'none' : `pending stop ${stop.id}`}.`);
   }

   return stop;
}
