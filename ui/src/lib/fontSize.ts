import { preference } from './preference';

/** The size of the page's text, the diff editors' code included. `app.css` gives each a `--font-scale`, and Medium keeps every size at its base. */
export type FontSize = 'small' | 'medium' | 'large';

/** The sizes in the order that the settings offer them, with their labels. */
export const fontSizeChoices: readonly { value: FontSize; label: string }[] = [
   { value: 'small', label: 'Small' },
   { value: 'medium', label: 'Medium' },
   { value: 'large', label: 'Large' },
];

/** The size the reviewer last chose, Medium by default. */
export const fontSizePreference = preference<FontSize>(
   'lotsen.fontSize',
   fontSizeChoices.map((choice) => choice.value),
   'medium',
);

/** Sets `data-font-size` on the page's root element, which selects the size's `--font-scale` in `app.css`. */
export function applyFontSize(document: Document, size: FontSize): void {
   document.documentElement.dataset.fontSize = size;
}
