import { preference } from './preference';

/** A syntax color family for the diff editors, with a light and a dark palette in `syntax-colors.css`. The theme picks the palette. */
export type SyntaxColors = 'lotsen' | 'vscode' | 'visualStudio' | 'catppuccin' | 'gruvbox';

/** The families in the order that the settings offer them, with their labels. */
export const syntaxColorChoices: readonly { value: SyntaxColors; label: string }[] = [
   { value: 'lotsen', label: 'Lotsen' },
   { value: 'vscode', label: 'Visual Studio Code' },
   { value: 'visualStudio', label: 'Visual Studio' },
   { value: 'catppuccin', label: 'Catppuccin' },
   { value: 'gruvbox', label: 'Gruvbox' },
];

/** The family the reviewer last chose, Lotsen's own by default. */
export const syntaxColorsPreference = preference<SyntaxColors>(
   'lotsen.syntaxColors',
   syntaxColorChoices.map((choice) => choice.value),
   'lotsen',
);

/** Sets `data-syntax-colors` on the page's root element, which selects the family's palettes in `syntax-colors.css`. */
export function applySyntaxColors(document: Document, colors: SyntaxColors): void {
   document.documentElement.dataset.syntaxColors = colors;
}
