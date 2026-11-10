import { afterEach, describe, expect, it } from 'vitest';
import { ColorScheme, type Theme } from '../src/lib/colorScheme';

// A media query list for prefers-color-scheme: light, whose matches the test sets, and which tells its change listeners.
function fakeSystem(light: boolean) {
   const listeners = new Set<() => void>();
   const query = {
      matches: light,
      addEventListener: (_type: string, listener: () => void) => listeners.add(listener),
      removeEventListener: (_type: string, listener: () => void) => listeners.delete(listener),
   };
   const change = (next: boolean) => {
      query.matches = next;
      listeners.forEach((listener) => listener());
   };
   return { window: { document, matchMedia: () => query as unknown as MediaQueryList }, change, listeners };
}

const root = document.documentElement;

function scheme(): string | undefined {
   expect(root.classList.contains('dark')).toBe(root.dataset.colorScheme === 'dark');
   return root.dataset.colorScheme;
}

afterEach(() => {
   root.classList.remove('dark');
   delete root.dataset.colorScheme;
});

describe('ColorScheme', () => {
   it.each<[boolean, string]>([
      [true, 'light'],
      [false, 'dark'],
   ])('follows a system that asks for light, %s, with the system theme', (light, expected) => {
      new ColorScheme(fakeSystem(light).window, 'system');

      expect(scheme()).toBe(expected);
   });

   it('is dark with the system theme when the window cannot match media queries', () => {
      new ColorScheme({ document }, 'system');

      expect(scheme()).toBe('dark');
   });

   it('follows a change of the system color scheme with the system theme', () => {
      const { window, change } = fakeSystem(false);
      new ColorScheme(window, 'system');

      change(true);
      expect(scheme()).toBe('light');
      change(false);
      expect(scheme()).toBe('dark');
   });

   it.each<[Theme, boolean, string]>([
      ['light', false, 'light'],
      ['dark', true, 'dark'],
   ])('keeps the %s theme whatever the system says', (theme, light, expected) => {
      const { window, change } = fakeSystem(light);
      new ColorScheme(window, theme);
      expect(scheme()).toBe(expected);

      change(!light);

      expect(scheme()).toBe(expected);
   });

   it('applies a new theme at once, and follows the system again when the theme returns to system', () => {
      const { window, change } = fakeSystem(true);
      const colorScheme = new ColorScheme(window, 'system');

      colorScheme.setTheme('dark');
      expect(scheme()).toBe('dark');
      change(false);
      change(true);
      expect(scheme()).toBe('dark');

      colorScheme.setTheme('system');
      expect(scheme()).toBe('light');
   });

   it('stops following the system once disposed', () => {
      const { window, change, listeners } = fakeSystem(true);
      new ColorScheme(window, 'system').dispose();

      change(false);

      expect(listeners.size).toBe(0);
      expect(scheme()).toBe('light');
   });
});
