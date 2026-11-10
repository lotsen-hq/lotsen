import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import { formatNotices, isAllowed } from '../scripts/thirdPartyNotices.mjs';
import SettingsPanel from '../src/components/SettingsPanel.svelte';
import { Settings } from '../src/lib/settings.svelte';

const dependency = (overrides: object) => ({ name: 'pkg', version: '1.2.3', license: 'MIT', licenseText: 'The MIT text.', ...overrides });

describe('the allowed licenses of a UI package', () => {
   it.each(['MIT', 'ISC', '0BSD', 'Apache-2.0', 'BSD-2-Clause', 'BSD-3-Clause', '(MIT OR GPL-3.0)'])('allows %s', (license) => {
      expect(isAllowed(dependency({ license }))).toBe(true);
   });

   it.each(['GPL-3.0', 'AGPL-3.0-only', 'LGPL-2.1', '(GPL-3.0 AND MIT)'])('refuses %s', (license) => {
      expect(isAllowed(dependency({ license }))).toBe(false);
   });

   it('refuses a package that declares no license', () => {
      expect(isAllowed(dependency({ license: null }))).toBe(false);
   });

   it('allows svelte-toolbelt, whose package.json declares no license but whose LICENSE file is MIT', () => {
      expect(isAllowed(dependency({ name: 'svelte-toolbelt', license: null }))).toBe(true);
   });
});

describe('the third-party notices', () => {
   it('gives each package its name, version, license and text, and then the fixed sections', () => {
      const notices = formatNotices([dependency({})], ['The fixed section.']);

      expect(notices).toContain('pkg 1.2.3 (MIT)\n');
      expect(notices).toContain('The MIT text.');
      expect(notices.indexOf('The fixed section.')).toBeGreaterThan(notices.indexOf('The MIT text.'));
   });

   it('sorts the packages by name', () => {
      const notices = formatNotices([dependency({ name: 'zeta' }), dependency({ name: 'alpha' })], []);

      expect(notices.indexOf('alpha 1.2.3')).toBeLessThan(notices.indexOf('zeta 1.2.3'));
   });

   it('fails on a package without a license text, because a notice needs the text', () => {
      expect(() => formatNotices([dependency({ licenseText: null })], [])).toThrow('pkg 1.2.3 ships no license text');
   });

   it('is linked from the settings', async () => {
      render(SettingsPanel, { props: { settings: new Settings(window), onprune: () => Promise.resolve({ removed: 0 }) } });
      await fireEvent.click(screen.getByRole('button', { name: 'Settings' }));
      const settings = within(await screen.findByRole('dialog', { name: 'Settings' }));

      expect(settings.getByRole('link', { name: 'Third-party notices' }).getAttribute('href')).toBe('/THIRD-PARTY-NOTICES.txt');
   });
});
