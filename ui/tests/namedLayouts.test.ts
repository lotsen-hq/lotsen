import { describe, expect, it } from 'vitest';
import { arrangementRecord } from '../src/lib/arrangements';
import { layoutVersion } from '../src/lib/layoutRecord';
import { checkName, namedLayouts, removeLayout, renameLayout, saveLayout } from '../src/lib/namedLayouts';
import { syncPreferences } from '../src/lib/preference';

const titles = { files: 'Files', plan: 'Plan', diff: 'Code', stop: 'Stop', threads: 'Threads' };
const columns = arrangementRecord('columns', titles);
const newer = { ...columns, version: layoutVersion + 1 };

/** Reads the stored texts into the page as the server's preferences, and returns the texts that the page writes after it, as the server holds them. */
async function serve(stored: Record<string, string>): Promise<Record<string, string>> {
   const server = { ...stored };
   await syncPreferences(
      () => Promise.resolve({ ...server }),
      async (key, text) => {
         if (text === null) {
            delete server[key];
         } else {
            server[key] = text;
         }
      },
   );
   return server;
}

const value = (name: unknown, record: unknown) => JSON.stringify({ name, record });

describe('the named layouts', () => {
   it('lists each stored layout by name, with no record for one this page cannot read', async () => {
      await serve({
         'workspaceLayouts/b': value('Reading', columns),
         'workspaceLayouts/a': value('Wide', newer),
         'workspaceLayouts/c': value(7, columns),
         'workspaceLayouts/d': 'not JSON',
      });

      expect(namedLayouts().map(({ id, name, record }) => ({ id, name, readable: record !== undefined }))).toEqual([
         { id: 'b', name: 'Reading', readable: true },
         { id: 'c', name: 'Unnamed layout', readable: true },
         { id: 'd', name: 'Unnamed layout', readable: false },
         { id: 'a', name: 'Wide', readable: false },
      ]);
   });

   it('saves a layout under a new id and the trimmed name', async () => {
      const server = await serve({});

      await saveLayout('  Reading ', columns);

      const [key, ...others] = Object.keys(server);
      expect(others).toEqual([]);
      expect(key).toMatch(/^workspaceLayouts\/[0-9a-f-]{36}$/);
      expect(JSON.parse(server[key!]!)).toEqual({ name: 'Reading', record: columns });
      expect(namedLayouts().map(({ name }) => name)).toEqual(['Reading']);
   });

   it('saves over the layout that has the name in another letter case, under its id and the new name', async () => {
      const server = await serve({ 'workspaceLayouts/a': value('reading', newer) });

      await saveLayout('Reading', columns);

      expect(server).toEqual({ 'workspaceLayouts/a': value('Reading', columns) });
   });

   it('renames a layout and keeps its record as stored, one this page cannot read included', async () => {
      const server = await serve({ 'workspaceLayouts/a': value('Wide', newer) });

      await renameLayout('a', 'Wider');

      expect(server).toEqual({ 'workspaceLayouts/a': value('Wider', newer) });
   });

   it('keeps every other member of the stored value through a rename, as a later version of lotsen may store more', async () => {
      const stored = JSON.stringify({ name: 'Wide', record: newer, pinned: true });
      const server = await serve({ 'workspaceLayouts/a': stored });

      await renameLayout('a', 'Wider');

      expect(JSON.parse(server['workspaceLayouts/a']!)).toEqual({ name: 'Wider', record: newer, pinned: true });
   });

   it('refuses to rename a layout whose stored value is not a JSON object, and writes nothing', async () => {
      const server = await serve({ 'workspaceLayouts/a': 'not JSON', 'workspaceLayouts/b': '[1]' });

      await expect(renameLayout('a', 'Wider')).rejects.toThrow('This layout cannot be renamed. Delete it instead.');
      await expect(renameLayout('b', 'Wider')).rejects.toThrow('This layout cannot be renamed. Delete it instead.');
      expect(server).toEqual({ 'workspaceLayouts/a': 'not JSON', 'workspaceLayouts/b': '[1]' });
   });

   it('refuses a rename to a name that another layout has, and writes nothing', async () => {
      const stored = { 'workspaceLayouts/a': value('Wide', columns), 'workspaceLayouts/b': value('Reading', columns) };
      const server = await serve(stored);

      await expect(renameLayout('a', 'READING')).rejects.toThrow('A layout named Reading already exists.');
      expect(server).toEqual(stored);
   });

   it('deletes a layout', async () => {
      const server = await serve({ 'workspaceLayouts/a': value('Wide', columns) });

      await removeLayout('a');

      expect(server).toEqual({});
      expect(namedLayouts()).toEqual([]);
   });
});

describe('the name of a named layout', () => {
   it.each([
      { why: 'empty', name: '' },
      { why: 'only spaces', name: '   ' },
      { why: 'longer than 60 characters', name: 'x'.repeat(61) },
   ])('is refused when it is $why', ({ name }) => {
      expect(checkName(name).kind).toBe('refused');
   });

   it('may have 60 characters, counted after the trim', () => {
      expect(checkName(` ${'x'.repeat(60)} `)).toEqual({ kind: 'free', name: 'x'.repeat(60) });
   });

   it('is taken by a layout that has it in another letter case, unless that layout is the one excepted', async () => {
      await serve({ 'workspaceLayouts/a': value('Reading', columns) });

      expect(checkName('reading ')).toMatchObject({ kind: 'taken', name: 'reading', layout: { id: 'a' } });
      expect(checkName('reading', 'a')).toEqual({ kind: 'free', name: 'reading' });
   });
});
