import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { booleanPreference, preference } from '../src/lib/preference';

const fruit = preference<'apple' | 'pear'>('test.fruit', ['apple', 'pear'], 'apple');

beforeEach(() => localStorage.clear());
afterEach(() => vi.restoreAllMocks());

describe('preference', () => {
   it('reads the choice it wrote', () => {
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

   it('reads the default and writes nothing when browser storage throws', () => {
      vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
         throw new DOMException('Blocked', 'SecurityError');
      });
      vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
         throw new DOMException('Blocked', 'SecurityError');
      });

      expect(() => fruit.write('pear')).not.toThrow();
      expect(fruit.read()).toBe('apple');
   });
});

describe('booleanPreference', () => {
   const lit = booleanPreference('test.lit', true);

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
