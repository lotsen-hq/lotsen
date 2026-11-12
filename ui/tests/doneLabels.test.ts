import { describe, expect, it } from 'vitest';
import { doneClick } from '../src/lib/doneClick';
import { doneCheckboxLabel, doneRowLabel, doneTooltip, stopDoneCheckboxLabel, stopDoneTooltip } from '../src/lib/labels';
import type { DoneState } from '../src/lib/tourRecord.svelte';

const none: DoneState = { state: 'none' };
const done: DoneState = { state: 'done' };
const earlier: DoneState = { state: 'earlier', version: 2 };

describe('a click on the done checkbox', () => {
   it('targets the current version of a stop that is not the stop under view', () => {
      expect(doneClick(none, 3, null)).toEqual({ version: 3, clears: false, earlier: false });
      expect(doneClick(done, 3, null)).toEqual({ version: 3, clears: true, earlier: false });
      expect(doneClick(earlier, 3, null)).toEqual({ version: 3, clears: false, earlier: false });
   });

   it('targets the version that the page shows of the stop under view', () => {
      expect(doneClick(none, 3, 1)).toEqual({ version: 1, clears: false, earlier: true });
      expect(doneClick(earlier, 3, 2)).toEqual({ version: 2, clears: true, earlier: true });
      expect(doneClick(earlier, 3, 1)).toEqual({ version: 1, clears: false, earlier: true });
      expect(doneClick(done, 3, 1)).toEqual({ version: 1, clears: false, earlier: true });
      expect(doneClick(done, 3, 3)).toEqual({ version: 3, clears: true, earlier: false });
   });
});

describe('the done labels', () => {
   it('name the version that a mark on an earlier version covers', () => {
      const click = doneClick(earlier, 3, null);

      expect(doneCheckboxLabel(4, earlier, click)).toBe('Stop 4 done. Done on version 2. Claude revised it since. Mark the current version done.');
      expect(doneRowLabel(earlier)).toBe('done on version 2');
      expect(doneTooltip(earlier, click)).toBe('Done on version 2. Claude revised it since. Mark the current version done.');
      expect(stopDoneCheckboxLabel(earlier, click)).toBe('Done. Done on version 2. Claude revised it since. Mark the current version done.');
   });

   it('name no version for a click on the current version of a stop that is done or not done', () => {
      expect([doneCheckboxLabel(4, done, doneClick(done, 3, null)), doneCheckboxLabel(4, none, doneClick(none, 3, null))]).toEqual(['Stop 4 done', 'Stop 4 done']);
      expect([doneRowLabel(done), doneRowLabel(none)]).toEqual(['done', null]);
      expect([stopDoneCheckboxLabel(done, doneClick(done, 3, 3)), stopDoneCheckboxLabel(none, doneClick(none, 3, 3))]).toEqual(['Done', 'Done']);
      expect([doneTooltip(done, doneClick(done, 3, null)), stopDoneTooltip(done, doneClick(done, 3, 3))]).toEqual(['Done. Take the mark back.', 'Done. Take the mark back.']);
      expect(doneTooltip(none, doneClick(none, 3, null))).not.toMatch(/version \d/);
   });

   it('say that a click marks the earlier version that the page shows', () => {
      const click = doneClick(none, 3, 1);

      expect(doneTooltip(none, click)).toBe('Mark version 1 done. Claude revised it since.');
      expect(doneCheckboxLabel(4, none, click)).toBe('Stop 4 done. Mark version 1 done. Claude revised it since.');
      expect(doneTooltip(earlier, doneClick(earlier, 3, 1))).toBe('Done on version 2. Claude revised it since. Mark version 1 done.');
      expect(doneTooltip(done, doneClick(done, 3, 1))).toBe('Done. Mark version 1 done. Claude revised it since.');
   });

   it('say that a click takes back the mark on the earlier version that the page shows', () => {
      const click = doneClick(earlier, 3, 2);

      expect(doneTooltip(earlier, click)).toBe('Done on version 2. Claude revised it since. Take the mark back.');
      expect(doneCheckboxLabel(4, earlier, click)).toBe('Stop 4 done. Done on version 2. Claude revised it since. Take the mark back.');
   });

   it("say in the Stop panel that an earlier version that it shows takes no mark, and name the stop's done state", () => {
      expect(stopDoneTooltip(none, doneClick(none, 3, 1))).toBe('Open the current version to mark the stop done.');
      expect(stopDoneTooltip(earlier, doneClick(earlier, 3, 2))).toBe('Done on version 2. Claude revised it since. Open the current version to mark the stop done.');
      expect(stopDoneTooltip(done, doneClick(done, 3, 1))).toBe('Done. Open the current version to take the mark back.');
      expect(stopDoneCheckboxLabel(none, doneClick(none, 3, 1))).toBe('Done. Open the current version to mark the stop done.');
   });
});
