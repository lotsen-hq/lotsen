import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { afterEach, beforeEach, describe, expect, it, onTestFinished, vi } from 'vitest';
import { CodeMirrorSurface } from '../../src/lib/editor/codeMirrorSurface';
import syntaxColorsCss from '../../src/syntax-colors.css?raw';
import type { LotsenApp } from '../../src/lib/app.svelte';
import type { World } from '../backend/backend';
import { backend, seedTour, startApp } from '../harness';
import { openTourList, tourButton } from '../tourPicker';
import { filesPanel, resetPageEachTest, worldOf } from './appPanels';

resetPageEachTest();

const root = document.documentElement;
let systemLight: { matches: boolean; change(light: boolean): void };

beforeEach(() => {
   // A media query list for prefers-color-scheme: light, whose system color scheme the test changes.
   const listeners = new Set<() => void>();
   systemLight = {
      matches: true,
      change(light) {
         this.matches = light;
         listeners.forEach((listener) => listener());
      },
   };
   const query = {
      get matches() {
         return systemLight.matches;
      },
      addEventListener: (_type: string, listener: () => void) => listeners.add(listener),
      removeEventListener: (_type: string, listener: () => void) => listeners.delete(listener),
   };
   vi.stubGlobal('matchMedia', (media: string) => (media === '(prefers-color-scheme: light)' ? query : { matches: false, addEventListener: () => {}, removeEventListener: () => {} }));
});

afterEach(() => {
   vi.unstubAllGlobals();
   vi.restoreAllMocks();
});

// A pinned tour whose one changed file has a one-line change.
const oneFile = backend().template('settingsPanel-one-file', async (world) => {
   await seedTour(world, { base: { 'a.ts': 'a\n' }, head: { 'a.ts': 'b\n' }, bound: false });
});

const dark = () => root.classList.contains('dark');
const radio = (panel: ReturnType<typeof within>, group: string, name: string) => within(panel.getByRole('radiogroup', { name: group })).getByRole('radio', { name });
const checked = (panel: ReturnType<typeof within>, group: string, name: string) => radio(panel, group, name).getAttribute('aria-checked') === 'true';
const layoutToggle = () => within(screen.getByRole('main', { name: 'Diff' })).getByRole('button', { name: 'Side by side' });

let world: World;
let app: LotsenApp;

async function startShell(): Promise<void> {
   world = await backend().world();
   ({ app } = await startApp(world));
}

/** Starts the app again on the world, as the next load of the page does. */
async function restart(): Promise<void> {
   cleanup();
   app.stop();
   ({ app } = await startApp(world));
}

async function openSettings() {
   fireEvent.click(screen.getByRole('button', { name: 'Settings' }));
   return within(await screen.findByRole('dialog', { name: 'Settings' }));
}

async function openFile(): Promise<void> {
   (await (await openTourList()).findByRole('button', tourButton)).click();
   fireEvent.click(await (await filesPanel()).findByText('a.ts'));
}

async function openDiff(): Promise<void> {
   ({ world } = await worldOf(oneFile));
   ({ app } = await startApp(world));
   await openFile();
}

async function editors(count: number): Promise<HTMLElement[]> {
   return waitFor(() => {
      const found = [...screen.getByRole('main', { name: 'Diff' }).querySelectorAll<HTMLElement>('.cm-editor')];
      expect(found).toHaveLength(count);
      return found;
   });
}

describe('the settings panel', () => {
   it('opens from the settings button in the top bar, and closes with its close button and with Escape', async () => {
      await startShell();
      const button = screen.getByRole('button', { name: 'Settings' });

      const panel = await openSettings();
      fireEvent.click(panel.getByRole('button', { name: 'Close' }));

      await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
      expect(button.getAttribute('aria-expanded')).toBe('false');

      await openSettings();
      expect(button.getAttribute('aria-expanded')).toBe('true');
      fireEvent.keyDown(document.activeElement ?? document.body, { key: 'Escape' });

      await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
   });

   it('follows the system color scheme with the system theme, also while the panel is open', async () => {
      await startShell();
      expect(dark()).toBe(false);

      const panel = await openSettings();
      expect(checked(panel, 'Theme', 'System')).toBe(true);
      systemLight.change(false);

      expect(dark()).toBe(true);
      systemLight.change(true);
      expect(dark()).toBe(false);
   });

   it('is dark with the system theme when the system does not ask for light', async () => {
      systemLight.matches = false;

      await startShell();

      expect(dark()).toBe(true);
   });

   it('keeps a light or dark theme whatever the system says, and follows the system again with the system theme', async () => {
      await startShell();
      const panel = await openSettings();

      fireEvent.click(radio(panel, 'Theme', 'Dark'));
      expect(dark()).toBe(true);
      expect(checked(panel, 'Theme', 'Dark')).toBe(true);
      systemLight.change(false);
      systemLight.change(true);
      expect(dark()).toBe(true);

      fireEvent.click(radio(panel, 'Theme', 'Light'));
      expect(dark()).toBe(false);
      systemLight.change(false);
      expect(dark()).toBe(false);

      fireEvent.click(radio(panel, 'Theme', 'System'));
      expect(dark()).toBe(true);
   });

   it('keeps the theme in browser storage and opens with it the next time', async () => {
      await startShell();
      let panel = await openSettings();
      fireEvent.click(radio(panel, 'Theme', 'Dark'));
      expect(localStorage.getItem('lotsen.theme')).toBe('dark');
      root.classList.remove('dark');

      await restart();

      expect(dark()).toBe(true);
      panel = await openSettings();
      expect(checked(panel, 'Theme', 'Dark')).toBe(true);
      fireEvent.click(radio(panel, 'Theme', 'Light'));

      await restart();

      expect(dark()).toBe(false);
      panel = await openSettings();
      expect(checked(panel, 'Theme', 'Light')).toBe(true);
   });

   it('keeps the placement and the mixed threshold in browser storage and opens with them the next time', async () => {
      await startShell();
      let panel = await openSettings();
      expect(checked(panel, 'Placement', 'Mixed')).toBe(true);
      const threshold = () => panel.getByRole('spinbutton', { name: 'Mixed threshold' }) as HTMLInputElement;
      expect(threshold().value).toBe('600');

      fireEvent.click(radio(panel, 'Placement', 'Inline'));
      fireEvent.input(threshold(), { target: { value: '250' } });

      expect(localStorage.getItem('lotsen.placement')).toBe('inline');
      expect(localStorage.getItem('lotsen.mixedThreshold')).toBe('250');
      await restart();
      panel = await openSettings();
      expect(checked(panel, 'Placement', 'Inline')).toBe(true);
      expect(threshold().value).toBe('250');
   });

   it("shows the thread messages as chat bubbles by default, and keeps the reviewer's choice of full width in browser storage for the next time", async () => {
      await startShell();
      let panel = await openSettings();
      expect(checked(panel, 'Thread messages', 'Chat bubbles')).toBe(true);

      fireEvent.click(radio(panel, 'Thread messages', 'Full width'));

      expect(localStorage.getItem('lotsen.messageStyle')).toBe('fullWidth');
      await restart();
      panel = await openSettings();
      expect(checked(panel, 'Thread messages', 'Full width')).toBe(true);
      expect(checked(panel, 'Thread messages', 'Chat bubbles')).toBe(false);
   });

   it.each(['', '12.5', '-3', '100001', 'abc'])('keeps the last valid threshold and says what it takes when the reviewer enters %j', async (entry) => {
      await startShell();
      const panel = await openSettings();
      const threshold = panel.getByRole('spinbutton', { name: 'Mixed threshold' }) as HTMLInputElement;
      fireEvent.input(threshold, { target: { value: '300' } });

      fireEvent.input(threshold, { target: { value: entry } });

      expect(localStorage.getItem('lotsen.mixedThreshold')).toBe('300');
      expect(threshold.getAttribute('aria-invalid')).toBe('true');
      expect(panel.getByRole('alert').textContent).toBe('Enter a whole number from 0 to 100000.');
      fireEvent.input(threshold, { target: { value: '400' } });
      expect(localStorage.getItem('lotsen.mixedThreshold')).toBe('400');
      expect(threshold.getAttribute('aria-invalid')).toBe('false');
      expect(panel.queryByRole('alert')).toBeNull();
   });

   describe('syntax colors', () => {
      const roles = ['keyword', 'string', 'number', 'comment', 'type', 'function', 'property', 'tag', 'operator', 'invalid', 'control', 'constant'];
      const commentStyle = () => getComputedStyle(root).getPropertyValue('--syntax-comment-style').trim();
      const palette = () => roles.map((role) => getComputedStyle(root).getPropertyValue(`--syntax-${role}`).trim().toLowerCase());

      beforeEach(() => {
         const sheet = document.createElement('style');
         sheet.textContent = syntaxColorsCss;
         document.head.append(sheet);
         onTestFinished(() => {
            sheet.remove();
            root.classList.remove('dark');
            delete root.dataset.syntaxColors;
         });
      });

      it('gives each family its own light and dark palette, which the theme picks', async () => {
         await startShell();
         const panel = await openSettings();
         expect(checked(panel, 'Syntax colors', 'Lotsen')).toBe(true);
         const palettes = new Map<string, string[]>();

         for (const family of ['Lotsen', 'Visual Studio Code', 'Visual Studio', 'Catppuccin', 'Gruvbox']) {
            fireEvent.click(radio(panel, 'Syntax colors', family));
            expect(checked(panel, 'Syntax colors', family)).toBe(true);
            for (const theme of ['Light', 'Dark']) {
               fireEvent.click(radio(panel, 'Theme', theme));
               palettes.set(`${family} ${theme}`, palette());
               expect(commentStyle()).toBe(family.startsWith('Visual Studio') ? 'normal' : 'italic');
            }
         }

         for (const colors of palettes.values()) {
            expect(colors.every((color) => /^#[0-9a-f]{6}$/.test(color))).toBe(true);
         }
         expect(new Set([...palettes.values()].map(String)).size).toBe(10);
         expect(palettes.get('Lotsen Light')![0]).toBe('#8250df');
         expect(palettes.get('Visual Studio Code Light')![0]).toBe('#0000ff');
         expect(palettes.get('Visual Studio Code Dark')![4]).toBe('#4ec9b0');
         expect(palettes.get('Visual Studio Dark')![4]).toBe('#d4d4d4');
         expect(palettes.get('Visual Studio Code Light')!.slice(10)).toEqual(['#af00db', '#0000ff']);
         expect(palettes.get('Visual Studio Code Dark')!.slice(10)).toEqual(['#c586c0', '#569cd6']);
         expect(palettes.get('Catppuccin Light')![0]).toBe('#8839ef');
         expect(palettes.get('Catppuccin Dark')![5]).toBe('#89b4fa');
         expect(palettes.get('Gruvbox Light')![0]).toBe('#9d0006');
         expect(palettes.get('Gruvbox Dark')![4]).toBe('#fabd2f');
      });

      it('keeps the family in browser storage and opens with it the next time', async () => {
         await startShell();
         let panel = await openSettings();
         fireEvent.click(radio(panel, 'Theme', 'Dark'));
         fireEvent.click(radio(panel, 'Syntax colors', 'Visual Studio'));
         expect(localStorage.getItem('lotsen.syntaxColors')).toBe('visualStudio');
         delete root.dataset.syntaxColors;

         await restart();

         expect(palette()[4]).toBe('#d4d4d4');
         panel = await openSettings();
         expect(checked(panel, 'Syntax colors', 'Visual Studio')).toBe(true);
      });

      it('starts with the Lotsen colors when the stored family is not one', async () => {
         localStorage.setItem('lotsen.syntaxColors', 'monokai');

         await startShell();

         expect(palette()[0]).toBe('#8250df');
         expect(checked(await openSettings(), 'Syntax colors', 'Lotsen')).toBe(true);
      });
   });

   it('applies the color theme the reviewer chooses, and keeps it for the next time', async () => {
      onTestFinished(() => {
         delete root.dataset.colorTheme;
      });
      await startShell();
      let panel = await openSettings();
      expect(checked(panel, 'Color theme', 'Lotsen')).toBe(true);
      expect(root.dataset.colorTheme).toBe('lotsen');

      fireEvent.click(radio(panel, 'Color theme', 'Catppuccin'));

      expect(root.dataset.colorTheme).toBe('catppuccin');
      expect(localStorage.getItem('lotsen.colorTheme')).toBe('catppuccin');
      delete root.dataset.colorTheme;

      await restart();

      expect(root.dataset.colorTheme).toBe('catppuccin');
      panel = await openSettings();
      expect(checked(panel, 'Color theme', 'Catppuccin')).toBe(true);
   });

   describe('font size', () => {
      afterEach(() => {
         delete root.dataset.fontSize;
      });

      it('starts at Medium, and keeps the size the reviewer chooses for the next time', async () => {
         await startShell();
         let panel = await openSettings();
         expect(checked(panel, 'Font size', 'Medium')).toBe(true);
         expect(root.dataset.fontSize).toBe('medium');

         fireEvent.click(radio(panel, 'Font size', 'Large'));

         expect(root.dataset.fontSize).toBe('large');
         expect(checked(panel, 'Font size', 'Large')).toBe(true);
         expect(localStorage.getItem('lotsen.fontSize')).toBe('large');
         delete root.dataset.fontSize;

         await restart();

         expect(root.dataset.fontSize).toBe('large');
         panel = await openSettings();
         expect(checked(panel, 'Font size', 'Large')).toBe(true);
      });

      it('starts at Medium when the stored size is not one', async () => {
         localStorage.setItem('lotsen.fontSize', 'huge');

         await startShell();

         expect(root.dataset.fontSize).toBe('medium');
         expect(checked(await openSettings(), 'Font size', 'Medium')).toBe(true);
      });
   });

   it('starts with the system theme and side by side when browser storage throws', async () => {
      const { getItem, setItem } = Storage.prototype;
      vi.spyOn(Storage.prototype, 'getItem').mockImplementation(function (this: Storage, key: string) {
         if (this === localStorage) {
            throw new DOMException('Blocked', 'SecurityError');
         }

         return getItem.call(this, key);
      });
      // The UI credential stays in session storage, as the page that exchanged its code keeps it.
      vi.spyOn(Storage.prototype, 'setItem').mockImplementation(function (this: Storage, key: string, value: string) {
         if (this === localStorage) {
            throw new DOMException('Blocked', 'SecurityError');
         }

         setItem.call(this, key, value);
      });
      await openDiff();
      await editors(2);
      const panel = await openSettings();

      expect(checked(panel, 'Theme', 'System')).toBe(true);
      expect(checked(panel, 'Syntax colors', 'Lotsen')).toBe(true);
      expect(root.dataset.syntaxColors).toBe('lotsen');
      expect(layoutToggle().getAttribute('aria-pressed')).toBe('true');
      fireEvent.click(layoutToggle());
      await editors(1);
   });

   it('leaves the diff layout to the diff toolbar', async () => {
      await startShell();
      const panel = await openSettings();

      expect(panel.getByRole('radiogroup', { name: 'Theme' })).toBeTruthy();
      expect(panel.queryByRole('radiogroup', { name: 'Diff layout' })).toBeNull();
      expect(panel.queryByRole('button', { name: 'Side by side' })).toBeNull();
   });

   it('shows the diff in the layout that the toolbar toggle switches to', async () => {
      await openDiff();
      await editors(2);
      expect(screen.getByRole('main', { name: 'Diff' }).querySelector('.cm-mergeView')).not.toBeNull();
      expect(layoutToggle().getAttribute('aria-pressed')).toBe('true');

      fireEvent.click(layoutToggle());

      const [inline] = await editors(1);
      expect(inline!.querySelector('.cm-deletedChunk')?.textContent).toBe('a');
      expect(layoutToggle().getAttribute('aria-pressed')).toBe('false');
      expect(screen.getByRole('main', { name: 'Diff' }).querySelector('.cm-mergeView')).toBeNull();

      fireEvent.click(layoutToggle());

      await editors(2);
   });

   it('keeps the layout that the toolbar toggle chose in browser storage and opens with it the next time', async () => {
      await openDiff();
      await editors(2);
      fireEvent.click(layoutToggle());
      await editors(1);
      expect(localStorage.getItem('lotsen.diffLayout')).toBe('inline');

      await restart();
      await openFile();

      await editors(1);
      expect(layoutToggle().getAttribute('aria-pressed')).toBe('false');
   });

   it('keeps the open diff editors when the theme changes', async () => {
      await openDiff();
      const before = await editors(2);
      const destroy = vi.spyOn(CodeMirrorSurface.prototype, 'destroy');
      const panel = await openSettings();

      fireEvent.click(radio(panel, 'Theme', 'Dark'));
      fireEvent.click(radio(panel, 'Theme', 'Light'));
      await new Promise((resolve) => setTimeout(resolve, 20));

      expect(destroy).not.toHaveBeenCalled();
      expect(await editors(2)).toEqual(before);
   });
});
