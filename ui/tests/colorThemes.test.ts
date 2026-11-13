import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import colorThemesCss from '../src/color-themes.css?raw';
import { applyColorTheme, colorThemeChoices } from '../src/lib/colorThemes';
import { Settings } from '../src/lib/settings.svelte';
import { fakeSystem } from './fakeSystem';

const root = document.documentElement;
const background = () => getComputedStyle(root).getPropertyValue('--background').trim().toLowerCase();
const shown = () => ({ family: root.dataset.colorTheme, dark: root.classList.contains('dark') });

// Latte's and Mocha's base, from color-themes.css.
const latteBase = '#eff1f5';
const mochaBase = '#1e1e2e';
// Light Modern's and Dark Modern's editor.background, from color-themes.css.
const lightModernBackground = '#ffffff';
const darkModernBackground = '#1f1f1f';

let settings: Settings | undefined;

beforeEach(() => {
   const sheet = document.createElement('style');
   sheet.textContent = colorThemesCss;
   document.head.append(sheet);
   return () => sheet.remove();
});

afterEach(() => {
   settings?.dispose();
   settings = undefined;
   localStorage.clear();
   root.classList.remove('dark');
   for (const key of ['colorScheme', 'colorTheme', 'syntaxColors', 'fontSize']) {
      delete root.dataset[key];
   }
});

describe('the color theme', () => {
   it.each(colorThemeChoices.map((choice) => choice.value))('sets data-color-theme to %s', (family) => {
      applyColorTheme(document, family);

      expect(root.dataset.colorTheme).toBe(family);
   });

   it('starts with the stored family, and leaves the dark class to the theme', () => {
      localStorage.setItem('lotsen.colorTheme', 'catppuccin');
      localStorage.setItem('lotsen.theme', 'dark');

      settings = new Settings(fakeSystem(true).window);

      expect(shown()).toEqual({ family: 'catppuccin', dark: true });
      expect(background()).toBe(mochaBase);
   });

   it('applies a new family at once and keeps the mode', () => {
      localStorage.setItem('lotsen.theme', 'light');
      settings = new Settings(fakeSystem(false).window);
      expect(shown()).toEqual({ family: 'lotsen', dark: false });

      settings.setColorTheme('catppuccin');

      expect(shown()).toEqual({ family: 'catppuccin', dark: false });
      expect(background()).toBe(latteBase);
      expect(localStorage.getItem('lotsen.colorTheme')).toBe('catppuccin');
   });

   it('switches the family between its light and dark palettes as the system changes, with the system theme', () => {
      localStorage.setItem('lotsen.colorTheme', 'catppuccin');
      const { window, change } = fakeSystem(true);
      settings = new Settings(window);
      expect(shown()).toEqual({ family: 'catppuccin', dark: false });
      expect(background()).toBe(latteBase);

      change(false);

      expect(shown()).toEqual({ family: 'catppuccin', dark: true });
      expect(background()).toBe(mochaBase);

      change(true);

      expect(shown()).toEqual({ family: 'catppuccin', dark: false });
      expect(background()).toBe(latteBase);
   });

   it('offers Visual Studio Code, whose palettes are Light Modern and Dark Modern', () => {
      expect(colorThemeChoices).toContainEqual({ value: 'vscode', label: 'Visual Studio Code' });
      localStorage.setItem('lotsen.theme', 'light');
      settings = new Settings(fakeSystem(false).window);

      settings.setColorTheme('vscode');

      expect(shown()).toEqual({ family: 'vscode', dark: false });
      expect(background()).toBe(lightModernBackground);

      settings.setTheme('dark');

      expect(shown()).toEqual({ family: 'vscode', dark: true });
      expect(background()).toBe(darkModernBackground);
   });

   it('starts with Lotsen when the stored family is not one', () => {
      localStorage.setItem('lotsen.colorTheme', 'dracula');

      settings = new Settings(fakeSystem(true).window);

      expect(settings.colorTheme).toBe('lotsen');
      expect(root.dataset.colorTheme).toBe('lotsen');
   });
});
