import { describe, expect, it } from 'vitest';
import { Revisioned, RevisionedValue } from '../src/lib/revisioned.svelte';

describe('a revisioned store', () => {
   it('replaces a value from a higher revision and drops one from a lower revision', () => {
      const store = new Revisioned<string, string>();

      expect(store.put('a', 'first', 5)).toBe(true);
      expect(store.put('a', 'older', 4)).toBe(false);
      expect(store.put('a', 'newer', 6)).toBe(true);

      expect(store.get('a')).toBe('newer');
   });

   it('drops a value from an equal revision, which came from the same store', () => {
      const store = new Revisioned<string, string>();
      store.put('a', 'first', 5);

      expect(store.put('a', 'again', 5)).toBe(false);
      expect(store.get('a')).toBe('first');
   });

   it('keeps a tombstone that blocks an older value and yields to a newer one', () => {
      const store = new Revisioned<string, string>();
      store.put('a', 'first', 5);

      expect(store.remove('a', 7)).toBe(true);
      expect(store.put('a', 'older', 6)).toBe(false);
      expect(store.get('a')).toBeUndefined();
      expect(store.all()).toEqual([]);
      expect(store.put('a', 'newer', 8)).toBe(true);
      expect(store.get('a')).toBe('newer');
   });

   it('keeps a removal that arrives before the value', () => {
      const store = new Revisioned<string, string>();

      expect(store.remove('a', 7)).toBe(true);
      expect(store.put('a', 'older', 6)).toBe(false);
      expect(store.all()).toEqual([]);
   });

   it('drops a removal from a lower revision than the value', () => {
      const store = new Revisioned<string, string>();
      store.put('a', 'value', 7);

      expect(store.remove('a', 6)).toBe(false);
      expect(store.get('a')).toBe('value');
   });

   it('takes a snapshot over the entries known at a lower revision, and removes those of them that it lacks', () => {
      const store = new Revisioned<string, string>();
      store.put('kept', 'old', 3);
      store.put('dropped', 'old', 3);

      store.replaceAll(
         new Map([
            ['kept', 'snapshot'],
            ['added', 'snapshot'],
         ]),
         5,
      );

      expect(store.get('kept')).toBe('snapshot');
      expect(store.get('added')).toBe('snapshot');
      expect(store.get('dropped')).toBeUndefined();
      expect(store.put('dropped', 'older', 4)).toBe(false);
   });

   it('keeps the entries known at a higher revision than a snapshot, the values and the tombstones', () => {
      const store = new Revisioned<string, string>();
      store.put('newer', 'event', 9);
      store.put('only', 'event', 9);
      store.remove('removed', 9);

      store.replaceAll(
         new Map([
            ['newer', 'snapshot'],
            ['removed', 'snapshot'],
         ]),
         8,
      );

      expect(store.get('newer')).toBe('event');
      expect(store.get('only')).toBe('event');
      expect(store.get('removed')).toBeUndefined();
   });

   it("keeps an entry known at the snapshot's own revision", () => {
      const store = new Revisioned<string, string>();
      store.put('a', 'event', 8);

      store.replaceAll(new Map(), 8);

      expect(store.get('a')).toBe('event');
   });

   it("lists a snapshot's values in its order, then the newer values that it lacks in the order the store heard of them", () => {
      const store = new Revisioned<string, string>();
      store.put('event', 'e', 9);
      store.put('b', 'old b', 2);

      store.replaceAll(
         new Map([
            ['a', 'a'],
            ['b', 'b'],
         ]),
         5,
      );
      store.put('later', 'l', 10);

      expect(store.all()).toEqual(['a', 'b', 'e', 'l']);
   });

   it('lists a value that a newer revision replaces in its place', () => {
      const store = new Revisioned<string, string>();
      store.put('a', 'a', 1);
      store.put('b', 'b', 2);
      store.put('a', 'a2', 3);

      expect(store.all()).toEqual(['a2', 'b']);
   });

   it('returns the same list until a change', () => {
      const store = new Revisioned<string, string>();
      store.put('a', 'a', 1);
      const listed = store.all();

      store.put('a', 'older', 0);
      expect(store.all()).toBe(listed);
      store.put('a', 'newer', 2);
      expect(store.all()).not.toBe(listed);
   });
});

describe('a revisioned value', () => {
   it('holds nothing until a value arrives, and then keeps the value of the highest revision', () => {
      const value = new RevisionedValue<string | null>();
      expect(value.known).toBe(false);
      expect(value.value).toBeUndefined();

      expect(value.put(null, 4)).toBe(true);
      expect(value.known).toBe(true);
      expect(value.value).toBeNull();
      expect(value.put('older', 3)).toBe(false);
      expect(value.put('same', 4)).toBe(false);
      expect(value.put('newer', 5)).toBe(true);
      expect(value.value).toBe('newer');
   });
});
