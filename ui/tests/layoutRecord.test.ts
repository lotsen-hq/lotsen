import { Orientation, type SerializedDockview, type SerializedPopoutGroup } from 'dockview';
import { describe, expect, it } from 'vitest';
import { layoutFrom, layoutVersion, parseLayout, withoutPopouts } from '../src/lib/layoutRecord';

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

describe('a layout record in a value', () => {
   it('reads the record that a parsed value holds', () => {
      const value = JSON.parse(record());

      expect(layoutFrom(value)).toEqual(value);
   });

   it('reads a record that holds a popout window with the panel of the window in its place', () => {
      const root = { type: 'branch', data: [{ type: 'leaf', data: { views: ['files'], activeView: 'files', id: '1' }, size: 100 }], size: 100 };
      const popoutGroups = [{ gridReferenceGroup: '1', position: null, data: { views: ['plan'], activeView: 'plan', id: '2' } }];
      const value = JSON.parse(record({ dockview: { grid: { root }, panels, popoutGroups } }));

      const read = layoutFrom(value);

      expect(read?.dockview).not.toHaveProperty('popoutGroups');
      expect(read?.dockview.grid.root.data).toEqual([{ type: 'leaf', data: { views: ['files', 'plan'], activeView: 'plan', id: '1' }, size: 100 }]);
   });

   it.each([
      { why: 'undefined', value: undefined },
      { why: 'a string of a record', value: record() },
      { why: 'a popout group without its group', value: JSON.parse(record({ dockview: { grid: { root: { type: 'branch', data: [] } }, panels, popoutGroups: [{ data: null }] } })) },
   ])('refuses $why', ({ value }) => {
      expect(layoutFrom(value)).toBeUndefined();
   });
});

describe('a layout without popouts', () => {
   const leaf = (id: string, views: string[], extra: Record<string, unknown> = {}) => ({ type: 'leaf' as const, data: { views, activeView: views[0], id }, size: 100, ...extra });

   /** Returns a serialized layout of a row of the nodes, with the popout groups. */
   function layout(nodes: unknown[], popoutGroups?: SerializedPopoutGroup[], activeGroup?: string): SerializedDockview {
      return {
         grid: { root: { type: 'branch', data: nodes as SerializedDockview['grid']['root'][], size: 600 }, width: 1000, height: 600, orientation: Orientation.HORIZONTAL },
         panels: {},
         activeGroup,
         popoutGroups,
      };
   }

   it('returns a layout without popout groups as it is', () => {
      const dockview = layout([leaf('1', ['files', 'plan'])]);

      expect(withoutPopouts(dockview)).toEqual(dockview);
   });

   it('puts a popped-out panel at the end of the group it left, which shows again with the branch that hid with it, as the active tab and group', () => {
      const dockview = layout(
         [leaf('1', ['files']), { type: 'branch', data: [leaf('3', ['stop'], { visible: false }), leaf('4', [], { visible: false })], size: 300, visible: false }],
         [{ gridReferenceGroup: '4', position: null, data: { views: ['threads'], activeView: 'threads', id: '5' } }],
         '5',
      );

      const result = withoutPopouts(dockview);

      expect(result).not.toHaveProperty('popoutGroups');
      expect(result.activeGroup).toBe('4');
      expect(result.grid.root.data).toEqual([
         leaf('1', ['files']),
         { type: 'branch', data: [leaf('3', ['stop'], { visible: false }), { type: 'leaf', data: { views: ['threads'], activeView: 'threads', id: '4' }, size: 100 }], size: 300 },
      ]);
      expect(dockview.popoutGroups).toHaveLength(1);
   });

   it('puts a panel that left a shared group after the panels that stayed', () => {
      const dockview = layout([leaf('1', ['files'])], [{ gridReferenceGroup: '1', position: null, data: { views: ['plan'], activeView: 'plan', id: '2' } }]);

      expect(withoutPopouts(dockview).grid.root.data).toEqual([{ type: 'leaf', data: { views: ['files', 'plan'], activeView: 'plan', id: '1' }, size: 100 }]);
   });

   it("puts a panel that left a shared group back at its tab's place there, which the places give", () => {
      const dockview = layout([leaf('1', ['plan', 'diff'])], [{ gridReferenceGroup: '1', position: null, data: { views: ['files'], activeView: 'files', id: '2' } }]);

      expect(withoutPopouts(dockview, { files: { groupId: '1', index: 0 } }).grid.root.data).toEqual([
         { type: 'leaf', data: { views: ['files', 'plan', 'diff'], activeView: 'files', id: '1' }, size: 100 },
      ]);
      expect(withoutPopouts(dockview, { files: { groupId: '1', index: 5 } }).grid.root.data).toEqual([
         { type: 'leaf', data: { views: ['plan', 'diff', 'files'], activeView: 'files', id: '1' }, size: 100 },
      ]);
      expect(withoutPopouts(dockview, { files: { groupId: '3', index: 0 } }).grid.root.data).toEqual([
         { type: 'leaf', data: { views: ['plan', 'diff', 'files'], activeView: 'files', id: '1' }, size: 100 },
      ]);
   });

   it('gives the panels of a popout whose group is gone a group of their own at the start of the grid', () => {
      const dockview = layout(
         [leaf('1', ['files']), leaf('2', ['diff'], { size: 300 })],
         [
            {
               gridReferenceGroup: '9',
               position: null,
               grid: {
                  root: { type: 'branch', data: [leaf('5', ['stop']), leaf('6', ['threads'])] },
                  width: 100,
                  height: 100,
                  orientation: Orientation.VERTICAL,
               },
            },
         ],
      );

      expect(withoutPopouts(dockview).grid.root.data).toEqual([
         { type: 'leaf', data: { views: ['stop', 'threads'], activeView: 'stop', id: '5' }, size: 200 },
         leaf('1', ['files']),
         leaf('2', ['diff'], { size: 300 }),
      ]);
   });
});
