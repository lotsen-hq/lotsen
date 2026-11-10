import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { writtenOf } from './stops';
import { bodyLength, mixedThresholdPreference, placementModePreference, primaryPassage, resolvePlacement, withoutReferenceTargets, type PlacementOptions } from '../src/lib/placement';
import type { ChangedFile, Stop, StopContent, StopPassage } from '../src/lib/types';

function stopPassage(path: string, role: StopPassage['role'] = 'related', side: StopPassage['side'] = 'head'): StopPassage {
   return { side, path, startLine: 1, endLine: 2, role, commit: 'c'.repeat(40), text: 'text' };
}

function stop(kind: Stop['kind'], fields: Partial<Omit<StopContent, 'html'>> = {}): Stop {
   const content: StopContent = { headline: 'A headline.', passages: [stopPassage('a.ts', 'primary')], ...fields, html: { headline: '<p>A headline.</p>' } };
   return { id: 's1', title: 'A stop', kind, chapter: 'c1', stage: 'written', status: 'ready', version: 1, earlier: [], content };
}

/** Mixed mode with an inline diff and the default threshold, under which a short inspect stop goes inline. */
const mixed: PlacementOptions = { mode: 'mixed', threshold: 600, layout: 'inline' };

beforeEach(() => localStorage.clear());
afterEach(() => vi.restoreAllMocks());

describe('resolvePlacement', () => {
   it('puts a short inspect stop on one file inline, in mixed mode with an inline diff', () => {
      expect(resolvePlacement(stop('inspect', { how: 'Short.' }), mixed)).toBe('inline');
   });

   it.each(['context', 'inspect', 'question'] as const)('puts a %s stop inline when nothing else sends it to the side', (kind) => {
      expect(resolvePlacement(stop(kind), mixed)).toBe('inline');
   });

   describe('the rule of mixed mode', () => {
      it.each(['overview', 'risk'] as const)('sends an %s stop to the side', (kind) => {
         expect(resolvePlacement(stop(kind), mixed)).toBe('side');
      });

      it('sends a stop with checks to the side, and keeps one with an empty list of checks inline', () => {
         expect(resolvePlacement(stop('question', { checks: [{ id: 'c1', question: 'Is it right?' }] }), mixed)).toBe('side');
         expect(resolvePlacement(stop('question', { checks: [] }), mixed)).toBe('inline');
      });

      it('sends a stop with passages in two files to the side, and keeps one with several passages in one file at both sides inline', () => {
         expect(resolvePlacement(stop('inspect', { passages: [stopPassage('a.ts', 'primary'), stopPassage('b.ts')] }), mixed)).toBe('side');
         expect(resolvePlacement(stop('inspect', { passages: [stopPassage('a.ts', 'primary'), stopPassage('a.ts'), stopPassage('a.ts', 'related', 'base')] }), mixed)).toBe('inline');
      });

      it('sends a stop shown in a side-by-side diff to the side', () => {
         expect(resolvePlacement(stop('inspect'), { ...mixed, layout: 'sideBySide' })).toBe('side');
      });

      describe('with the changed files', () => {
         const renamed: ChangedFile = { path: 'b.ts', kind: 'renamed', oldPath: 'a.ts' };
         const sideBySide: PlacementOptions = { ...mixed, layout: 'sideBySide' };

         it('counts the base and head passages of a renamed file as one file, and the old path’s passage at the head as another', () => {
            const passages = [stopPassage('b.ts', 'primary'), stopPassage('a.ts', 'related', 'base')];

            expect(resolvePlacement(stop('inspect', { passages }), { ...mixed, files: [renamed] })).toBe('inline');
            expect(resolvePlacement(stop('inspect', { passages }), { ...mixed, files: null })).toBe('side');
            const added: ChangedFile = { path: 'a.ts', kind: 'added' };
            expect(resolvePlacement(stop('inspect', { passages: [stopPassage('b.ts', 'primary'), stopPassage('a.ts')] }), { ...mixed, files: [renamed, added] })).toBe('side');
         });

         it('treats a primary passage in a file that the pull request does not change as shown in one pane, whatever the diff layout', () => {
            expect(resolvePlacement(stop('inspect', { passages: [stopPassage('other.ts', 'primary')] }), { ...sideBySide, files: [renamed] })).toBe('inline');
            expect(resolvePlacement(stop('inspect', { passages: [stopPassage('other.ts', 'primary')] }), { ...sideBySide, files: null })).toBe('side');
         });

         it('treats a primary passage in a changed file as shown in the diff layout, at its old path too', () => {
            expect(resolvePlacement(stop('inspect', { passages: [stopPassage('b.ts', 'primary')] }), { ...sideBySide, files: [renamed] })).toBe('side');
            expect(resolvePlacement(stop('inspect', { passages: [stopPassage('a.ts', 'primary', 'base')] }), { ...sideBySide, files: [renamed] })).toBe('side');
            expect(resolvePlacement(stop('inspect', { passages: [stopPassage('a.ts', 'primary', 'head')] }), { ...sideBySide, files: [renamed] })).toBe('inline');
         });
      });

      it('sends a stop whose body is over the threshold to the side, and keeps one at the threshold inline', () => {
         const atThreshold = { how: 'a'.repeat(200), why: 'b'.repeat(100), designChoices: 'c'.repeat(100), pinnedBy: 'd'.repeat(100), scenario: 'e'.repeat(100) };

         expect(resolvePlacement(stop('inspect', atThreshold), mixed)).toBe('inline');
         expect(resolvePlacement(stop('inspect', { ...atThreshold, pinnedBy: 'd'.repeat(101) }), mixed)).toBe('side');
         expect(resolvePlacement(stop('inspect', atThreshold), { ...mixed, threshold: 599 })).toBe('side');
      });

      it('leaves the headline and the checks out of the body', () => {
         const content = { headline: 'h'.repeat(1000), how: 'a'.repeat(600) };

         expect(resolvePlacement(stop('inspect', content), mixed)).toBe('inline');
      });

      it('does not count the targets of ref: links toward the threshold', () => {
         const link = '[the caller](ref:head/src/a/very/long/path/to/the/caller.ts#L10-20)';
         const text = `${'a'.repeat(600 - '[the caller]'.length)}${link}`;

         expect(bodyLength(writtenOf(stop('inspect', { how: text })).content)).toBe(600);
         expect(resolvePlacement(stop('inspect', { how: text }), mixed)).toBe('inline');
         expect(resolvePlacement(stop('inspect', { how: `${text}b` }), mixed)).toBe('side');
      });
   });

   describe('Claude’s hint', () => {
      it('puts a stop where its placement hint says, over the rule', () => {
         expect(resolvePlacement(stop('overview', { placement: 'inline' }), mixed)).toBe('inline');
         expect(resolvePlacement(stop('inspect', { placement: 'side' }), mixed)).toBe('side');
         expect(resolvePlacement(stop('inspect', { placement: 'inline', checks: [{ id: 'c1', question: 'Right?' }] }), { ...mixed, layout: 'sideBySide' })).toBe('inline');
      });
   });

   describe('the reviewer’s toggle', () => {
      it('puts a stop where the reviewer moved it, over the hint and the rule', () => {
         expect(resolvePlacement(stop('inspect', { placement: 'inline' }), { ...mixed, override: 'side' })).toBe('side');
         expect(resolvePlacement(stop('risk', { placement: 'side' }), { ...mixed, override: 'inline' })).toBe('inline');
         expect(resolvePlacement(stop('inspect'), { ...mixed, override: 'side' })).toBe('side');
      });

      it('is ignored outside mixed mode', () => {
         expect(resolvePlacement(stop('inspect'), { ...mixed, mode: 'side', override: 'inline' })).toBe('side');
         expect(resolvePlacement(stop('risk'), { ...mixed, mode: 'inline', override: 'side' })).toBe('inline');
      });
   });

   describe('the side and inline modes', () => {
      it('puts every stop in the side panel in side mode, whatever its hint', () => {
         expect(resolvePlacement(stop('inspect', { placement: 'inline' }), { ...mixed, mode: 'side' })).toBe('side');
      });

      it('puts every stop with a primary passage inline in inline mode, whatever its hint, kind or diff layout', () => {
         expect(resolvePlacement(stop('overview', { placement: 'side', checks: [{ id: 'c1', question: 'Right?' }] }), { ...mixed, mode: 'inline', layout: 'sideBySide' })).toBe('inline');
      });
   });

   it('puts a stop without a primary passage, a pending stop and no stop in the side panel, in every mode', () => {
      for (const mode of ['side', 'inline', 'mixed'] as const) {
         const options: PlacementOptions = { ...mixed, mode, override: 'inline' };
         expect(resolvePlacement(stop('overview', { passages: [], placement: 'inline' }), options)).toBe('side');
         expect(resolvePlacement({ id: 's1', title: 'A stop', kind: 'inspect', chapter: 'c1', stage: 'pending', anchorHint: null, status: 'pending' }, options)).toBe('side');
         expect(resolvePlacement(null, options)).toBe('side');
      }
   });
});

describe('primaryPassage', () => {
   it('returns the primary passage wherever Claude placed it, or null for a stop without one', () => {
      const primary = stopPassage('b.ts', 'primary');

      expect(primaryPassage(stop('inspect', { passages: [stopPassage('a.ts'), primary] }))).toBe(primary);
      expect(primaryPassage(stop('overview', { passages: [stopPassage('a.ts')] }))).toBeNull();
      expect(primaryPassage(null)).toBeNull();
   });
});

describe('withoutReferenceTargets', () => {
   it.each([
      ['an inline link', 'See [the call](ref:head/src/a.ts#L1-2).', 'See [the call].'],
      ['a link in angle brackets with a title', 'See [the call](<ref:head/src/a b.ts#L1-2> "title").', 'See [the call].'],
      ['the scheme in another case', 'See [x](REF:base/a.ts#L1-1).', 'See [x].'],
      ['an autolink', 'See <ref:head/src/a.ts#L1-2>.', 'See .'],
   ])('removes the target of %s', (_case, markdown, expected) => {
      expect(withoutReferenceTargets(markdown)).toBe(expected);
   });

   it.each([
      ['a reference definition', 'See [the call][c].\n\n[c]: ref:head/src/a.ts#L1-2\n', 'See [the call][c].\n\n'],
      ['an indented reference definition in angle brackets with a title', 'See [x][c].\n\n   [c]: <REF:head/a b.ts#L1-2> "title"\nMore.', 'See [x][c].\n\nMore.'],
      ['a destination with balanced parentheses', 'See [x](ref:head/a(b).ts#L1-1).', 'See [x].'],
      ['an inline link after a code span', '`code` and [x](ref:head/a.ts#L1-1).', '`code` and [x].'],
      ['an inline link in a paragraph between two stray backticks', 'A stray ` tick.\n\nSee [y](ref:head/b.ts#L1-2).\n \nAnother ` tick.', 'A stray ` tick.\n\nSee [y].\n \nAnother ` tick.'],
      ['an inline link after an escaped backtick', 'a \\`b [x](ref:head/b.ts#L1-2) `c`', 'a \\`b [x] `c`'],
      ['an inline link after a code span that a backtick escaped by its own backslash opens', 'a \\\\`b` [x](ref:head/b.ts#L1-2)', 'a \\\\`b` [x]'],
      ['a CRLF fenced block’s following link', '```\r\ncode\r\n```\r\n[x](ref:head/a.ts#L1-1)', '```\r\ncode\r\n```\r\n[x]'],
   ])('removes the target of %s', (_case, markdown, expected) => {
      expect(withoutReferenceTargets(markdown)).toBe(expected);
   });

   it.each([
      ['an inline code span', 'Write `[x](ref:head/a.ts#L1-1)` or ``<ref:head/a.ts#L1-1>``.'],
      ['a code span after an escaped backtick', 'a \\` and `[x](ref:head/a.ts#L1-1)`'],
      ['a fenced code block', 'Write:\n\n```md\n[x](ref:head/a.ts#L1-1)\n[c]: ref:head/a.ts#L1-1\n```\n'],
      ['a tilde fence that a shorter fence does not close', '~~~~\n[x](ref:head/a.ts#L1-1)\n~~~\n<ref:head/a.ts#L1-1>\n'],
      ['an escaped bracket', 'See \\[x\\](ref:head/a.ts#L1-1) and \\<ref:head/a.ts#L1-1>.'],
      ['a reference definition with an https: target', 'See [x][c].\n\n[c]: https://example.com\n'],
   ])('keeps the text of %s, which the server shows as it is or which is no ref: link', (_case, markdown) => {
      expect(withoutReferenceTargets(markdown)).toBe(markdown);
   });

   it('keeps an https: link and text that only looks like a target', () => {
      const markdown = 'See [docs](https://example.com) and the word ref:head.';

      expect(withoutReferenceTargets(markdown)).toBe(markdown);
   });
});

describe('the placement preferences', () => {
   it('read mixed and 600 when nothing is stored', () => {
      expect(placementModePreference.read()).toBe('mixed');
      expect(mixedThresholdPreference.read()).toBe(600);
   });

   it('read what they wrote', () => {
      placementModePreference.write('inline');
      mixedThresholdPreference.write(250);

      expect(localStorage.getItem('lotsen.placement')).toBe('inline');
      expect(localStorage.getItem('lotsen.mixedThreshold')).toBe('250');
      expect(placementModePreference.read()).toBe('inline');
      expect(mixedThresholdPreference.read()).toBe(250);
   });

   it.each(['', 'abc', '12.5', '-1', '100001', '1e3'])('read the default threshold when storage holds %j', (stored) => {
      localStorage.setItem('lotsen.mixedThreshold', stored);

      expect(mixedThresholdPreference.read()).toBe(600);
   });
});
