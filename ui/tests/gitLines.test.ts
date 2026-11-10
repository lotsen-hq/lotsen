import { describe, expect, it } from 'vitest';
import { gitLineText } from '../src/lib/gitLines';

describe('gitLineText', () => {
   it.each([
      ['one\ntwo\nthree\n', 2, 'two'],
      ['one\ntwo', 2, 'two'],
      ['one\r\ntwo\r\n', 1, 'one'],
      // A lone CR ends no git line.
      ['one\rtwo\n', 1, 'one\rtwo'],
      ['\n\nthree\n', 2, ''],
   ])('returns line %#: %j at %i', (text, line, expected) => {
      expect(gitLineText(text, line)).toBe(expected);
   });

   it.each([
      ['one\n', 2],
      ['one\ntwo', 3],
   ])('returns null past the last line of %j', (text, line) => {
      expect(gitLineText(text, line)).toBeNull();
   });
});
