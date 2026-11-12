import { describe, expect, it } from 'vitest';
import { layoutVersion, parseLayout } from '../src/lib/layoutRecord';

const panels = Object.fromEntries(['files', 'plan', 'diff', 'stop', 'threads'].map((id) => [id, { id }]));

/** Returns the text of a record with the workspace's panels, with the changes. */
function record(changes: Record<string, unknown> = {}): string {
   return JSON.stringify({ version: layoutVersion, dockview: { grid: {}, panels }, hidden: [], ...changes });
}

describe('a saved layout record', () => {
   it('reads a record with the workspace panels and its hidden panels', () => {
      const text = record({
         hidden: [
            { id: 'plan', home: { groupId: '1', index: 1 } },
            { id: 'stop', home: null },
         ],
      });

      expect(parseLayout(text)?.hidden).toEqual([
         { id: 'plan', home: { groupId: '1', index: 1 } },
         { id: 'stop', home: null },
      ]);
   });

   it.each([
      { why: 'text that is not JSON', text: '{' },
      { why: 'JSON that is not an object', text: '[]' },
      { why: 'another version', text: record({ version: layoutVersion + 1 }) },
      { why: 'a missing panel', text: record({ dockview: { grid: {}, panels: { ...panels, stop: undefined } } }) },
      { why: 'an extra panel', text: record({ dockview: { grid: {}, panels: { ...panels, notes: { id: 'notes' } } } }) },
      { why: 'no grid', text: record({ dockview: { panels } }) },
      { why: 'a hidden panel the workspace lacks', text: record({ hidden: [{ id: 'notes', home: null }] }) },
      {
         why: 'a panel hidden twice',
         text: record({
            hidden: [
               { id: 'stop', home: null },
               { id: 'stop', home: null },
            ],
         }),
      },
      { why: 'a home with a negative index', text: record({ hidden: [{ id: 'plan', home: { groupId: '1', index: -1 } }] }) },
      { why: 'a hidden panel without a home', text: record({ hidden: [{ id: 'plan' }] }) },
   ])('refuses $why', ({ text }) => {
      expect(parseLayout(text)).toBeUndefined();
   });
});
