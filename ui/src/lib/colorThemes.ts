import { preference } from './preference';

/** A color theme family for the whole page, with a light and a dark palette. The theme picks the palette. */
export type ColorTheme = 'lotsen' | 'catppuccin';

/** The families in the order that the settings offer them, with their labels. */
export const colorThemeChoices: readonly { value: ColorTheme; label: string }[] = [
   { value: 'lotsen', label: 'Lotsen' },
   { value: 'catppuccin', label: 'Catppuccin' },
];

/** The family the reviewer last chose, Lotsen's own by default. */
export const colorThemePreference = preference<ColorTheme>(
   'lotsen.colorTheme',
   colorThemeChoices.map((choice) => choice.value),
   'lotsen',
);

/** Sets `data-color-theme` on the page's root element, which selects the family's palettes in `color-themes.css`. Lotsen's palettes are those in `app.css`. */
export function applyColorTheme(document: Document, theme: ColorTheme): void {
   document.documentElement.dataset.colorTheme = theme;
}
