import { flushSync } from 'svelte';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { applyPreference, booleanPreference, detachPreferences, preference, syncPreferences, type PreferenceWriter } from '../src/lib/preference';

const fruit = preference<'apple' | 'pear'>('test.fruit', ['apple', 'pear'], 'apple');
const lit = booleanPreference('test.lit', true);

beforeEach(() => localStorage.clear());
afterEach(() => vi.restoreAllMocks());

/** Returns a writer that records each write and answers it once the test resolves or rejects it. */
function heldWriter() {
   const writes: { key: string; text: string | null; resolve: () => void; reject: () => void }[] = [];
   const write: PreferenceWriter = (key, text) => new Promise<void>((resolve, reject) => writes.push({ key, text, resolve, reject: () => reject(new Error('refused')) }));
   const sent = () => writes.map(({ key, text }) => ({ key, text }));
   return { write, writes, sent };
}

/** Returns a writer that answers each write at once and records it. */
function answeringWriter() {
   const sent: { key: string; text: string | null }[] = [];
   const write: PreferenceWriter = async (key, text) => void sent.push({ key, text });
   return { write, sent };
}

/** Reads the preferences that the server holds into the page, as a page does once its read succeeds. */
const sync = (stored: Record<string, string>, write: PreferenceWriter) => syncPreferences(() => Promise.resolve(stored), write);

/** Lets the writes that wait on an answered one go out. */
const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

describe('preference', () => {
   it('reads the choice it wrote, and mirrors it in browser storage', () => {
      fruit.write('pear');

      expect(localStorage.getItem('test.fruit')).toBe('pear');
      expect(fruit.read()).toBe('pear');
   });

   it.each<[string, string | null]>([
      ['nothing', null],
      ['a value that is not a choice', 'plum'],
   ])('reads the default when storage holds %s', (_case, stored) => {
      if (stored !== null) {
         localStorage.setItem('test.fruit', stored);
      }

      expect(fruit.read()).toBe('apple');
   });

   it('keeps the choice in the page when browser storage throws', () => {
      vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
         throw new DOMException('Blocked', 'SecurityError');
      });
      vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
         throw new DOMException('Blocked', 'SecurityError');
      });

      expect(fruit.read()).toBe('apple');
      expect(() => fruit.write('pear')).not.toThrow();
      expect(fruit.read()).toBe('pear');
   });

   it('follows a change from the server in a reactive read', () => {
      const reads: string[] = [];
      const stop = $effect.root(() => {
         $effect(() => void reads.push(fruit.read()));
      });
      flushSync();

      applyPreference('test.fruit', 'pear');
      flushSync();
      stop();

      expect(reads).toEqual(['apple', 'pear']);
   });
});

describe('booleanPreference', () => {
   it('reads the choice it wrote', () => {
      lit.write(false);

      expect(localStorage.getItem('test.lit')).toBe('false');
      expect(lit.read()).toBe(false);
      lit.write(true);
      expect(lit.read()).toBe(true);
   });

   it.each<[string, string | null]>([
      ['nothing', null],
      ['a value that is not true or false', 'yes'],
   ])('reads the default when storage holds %s', (_case, stored) => {
      if (stored !== null) {
         localStorage.setItem('test.lit', stored);
      }

      expect(lit.read()).toBe(true);
   });
});

describe('the settling of the browser’s texts with the server’s', () => {
   it('sends a text that browser storage holds for a key that the server lacks', async () => {
      const { write, sent } = answeringWriter();
      localStorage.setItem('test.fruit', 'pear');

      await sync({}, write);
      await settle();

      expect(sent).toEqual([{ key: 'test.fruit', text: 'pear' }]);
      expect(fruit.read()).toBe('pear');
   });

   it.each<[string, Record<string, string>, string | null]>([
      ['no key, and browser storage holds none', {}, null],
      ['the text that browser storage holds', { 'test.fruit': 'pear' }, 'pear'],
      ['a text, and browser storage holds none', { 'test.fruit': 'pear' }, null],
   ])('sends nothing when the server holds %s', async (_case, stored, browser) => {
      const { write, sent } = answeringWriter();
      if (browser !== null) {
         localStorage.setItem('test.fruit', browser);
      }

      await sync(stored, write);
      await settle();

      expect(sent).toEqual([]);
      expect(localStorage.getItem('test.fruit')).toBe(stored['test.fruit'] ?? null);
   });

   it('takes the server’s text over another text that browser storage holds, and stores it there', async () => {
      const { write, sent } = answeringWriter();
      localStorage.setItem('test.fruit', 'pear');
      localStorage.setItem('test.lit', 'false');

      await sync({ 'test.fruit': 'apple' }, write);
      await settle();

      expect(fruit.read()).toBe('apple');
      expect(localStorage.getItem('test.fruit')).toBe('apple');
      expect(lit.read()).toBe(false);
      expect(sent).toEqual([{ key: 'test.lit', text: 'false' }]);
   });

   it('takes another page’s change on the next load, over the text that browser storage kept from this page’s write', async () => {
      const server: Record<string, string> = {};
      const sent: { key: string; text: string | null }[] = [];
      const write: PreferenceWriter = async (key, text) => {
         sent.push({ key, text });
         if (text !== null) {
            server[key] = text;
         }
      };
      const read = () => Promise.resolve({ ...server });
      await syncPreferences(read, write);
      fruit.write('pear');
      await settle();
      detachPreferences();
      server['test.fruit'] = 'apple';

      await syncPreferences(read, write);
      await settle();

      expect(sent).toEqual([{ key: 'test.fruit', text: 'pear' }]);
      expect(server['test.fruit']).toBe('apple');
      expect(fruit.read()).toBe('apple');
      expect(localStorage.getItem('test.fruit')).toBe('apple');
   });

   it('takes another page’s change on a later read of the same page', async () => {
      const { write, sent } = answeringWriter();
      await sync({ 'test.fruit': 'pear' }, write);

      await sync({ 'test.fruit': 'apple' }, write);
      await settle();

      expect(fruit.read()).toBe('apple');
      expect(sent).toEqual([]);
   });

   it('keeps the page’s texts through a later read that fails, and settles them on the next read', async () => {
      const { write, sent } = answeringWriter();
      await sync({}, write);
      fruit.write('pear');
      await settle();

      await expect(syncPreferences(() => Promise.reject(new Error('unreachable')), write)).rejects.toThrow('unreachable');
      expect(fruit.read()).toBe('pear');
      await sync({ 'test.fruit': 'apple' }, write);
      await settle();

      expect(fruit.read()).toBe('apple');
      expect(sent).toEqual([{ key: 'test.fruit', text: 'pear' }]);
   });
});

describe('the page’s writes', () => {
   it('go to the server one at a time per key, and a burst sends its last text', async () => {
      const { write, writes, sent } = heldWriter();
      await sync({}, write);

      fruit.write('pear');
      fruit.write('apple');
      fruit.write('pear');
      lit.write(false);
      writes[0]!.resolve();
      await settle();

      expect(sent()).toEqual([
         { key: 'test.fruit', text: 'pear' },
         { key: 'test.lit', text: 'false' },
         { key: 'test.fruit', text: 'pear' },
      ]);
   });

   it('keep a key with a write in flight as the page wrote it, and take a change once the write is answered', async () => {
      const { write, writes } = heldWriter();
      await sync({}, write);
      fruit.write('pear');

      applyPreference('test.fruit', 'apple');
      await sync({ 'test.fruit': 'apple' }, write);
      expect(fruit.read()).toBe('pear');
      writes[0]!.resolve();
      await settle();
      applyPreference('test.fruit', 'apple');

      expect(fruit.read()).toBe('apple');
      expect(localStorage.getItem('test.fruit')).toBe('apple');
   });

   it('keep a text whose write failed through the next read, which sends it again', async () => {
      const { write, writes, sent } = heldWriter();
      await sync({}, write);
      fruit.write('pear');
      writes[0]!.reject();
      await settle();

      await sync({ 'test.fruit': 'apple' }, write);
      writes[1]!.resolve();
      await settle();
      await sync({ 'test.fruit': 'apple' }, write);

      expect(sent()).toEqual([
         { key: 'test.fruit', text: 'pear' },
         { key: 'test.fruit', text: 'pear' },
      ]);
      expect(fruit.read()).toBe('apple');
   });

   it.each<[string, () => Promise<void>]>([
      ['another page’s change of the key arrives', async () => applyPreference('test.fruit', 'apple')],
      ['a later write of the key succeeds', async () => fruit.write('apple')],
   ])('take the server’s text on the next read once %s after a failed write', async (_case, after) => {
      const { write, writes, sent } = heldWriter();
      await sync({}, write);
      fruit.write('pear');
      writes[0]!.reject();
      await settle();

      await after();
      writes[1]?.resolve();
      await settle();
      const before = sent().length;
      await sync({ 'test.fruit': 'pear' }, write);
      await settle();

      expect(fruit.read()).toBe('pear');
      expect(sent()).toHaveLength(before);
   });

   it('take the server’s text in the next session for a key whose write failed after the page detached the preferences', async () => {
      const { write, writes, sent } = heldWriter();
      await sync({}, write);
      fruit.write('pear');
      detachPreferences();
      writes[0]!.reject();
      await settle();

      await sync({ 'test.fruit': 'apple' }, write);
      await settle();

      expect(fruit.read()).toBe('apple');
      expect(sent()).toEqual([{ key: 'test.fruit', text: 'pear' }]);
   });

   it('keep a text that the page sent while a read was in flight, whose answer predates the write', async () => {
      const { write, sent } = answeringWriter();
      await sync({}, write);
      let answer!: (stored: Record<string, string>) => void;
      const reading = syncPreferences(() => new Promise((resolve) => (answer = resolve)), write);

      fruit.write('pear');
      await settle();
      answer({ 'test.fruit': 'apple' });
      await reading;

      expect(fruit.read()).toBe('pear');
      expect(sent).toEqual([{ key: 'test.fruit', text: 'pear' }]);
   });

   it('stay in the page before a read succeeds, and yield to the server’s text that the read brings', async () => {
      const { write, sent } = answeringWriter();
      await expect(syncPreferences(() => Promise.reject(new Error('unreachable')), write)).rejects.toThrow('unreachable');
      fruit.write('pear');
      lit.write(false);
      await settle();
      expect(sent).toEqual([]);

      await sync({ 'test.fruit': 'apple' }, write);
      await settle();

      expect(fruit.read()).toBe('apple');
      expect(lit.read()).toBe(false);
      expect(sent).toEqual([{ key: 'test.lit', text: 'false' }]);
   });

   it('stay in the page while the server refuses them, until the page detaches the preferences', async () => {
      await sync({}, () => Promise.reject(new Error('refused')));
      fruit.write('pear');
      await settle();

      expect(fruit.read()).toBe('pear');
      localStorage.clear();
      detachPreferences();
      expect(fruit.read()).toBe('apple');
   });
});
