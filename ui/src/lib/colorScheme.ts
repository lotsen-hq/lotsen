/** The reviewer's theme: the system's color scheme, or light or dark whatever the system says. */
export type Theme = 'system' | 'light' | 'dark';

/** The window that a color scheme reads and changes. A window without `matchMedia` gives no system preference. */
export type ColorSchemeWindow = Pick<Window, 'document'> & Partial<Pick<Window, 'matchMedia'>>;

/**
 * Keeps the `dark` class on the page's root element in step with the theme.
 *
 * The dark palette in `app.css`, the `dark:` variant and the diff editors' colors key on that class. A `ColorScheme` also sets `data-color-scheme` to `dark` or `light`, which ends the
 * dark fallback that `app.css` gives the page before this runs.
 */
export class ColorScheme {
   private readonly root: HTMLElement;
   private readonly light: MediaQueryList | null;
   private theme: Theme;

   /** Applies the theme at once, and follows the system's changes while the theme is `system`. */
   constructor(window: ColorSchemeWindow, theme: Theme) {
      this.root = window.document.documentElement;
      // The page is dark unless the system asks for light, so a system without a preference gets dark.
      this.light = window.matchMedia?.('(prefers-color-scheme: light)') ?? null;
      this.theme = theme;
      this.light?.addEventListener('change', this.apply);
      this.apply();
   }

   setTheme(theme: Theme): void {
      this.theme = theme;
      this.apply();
   }

   /** Stops following the system's changes. The root keeps the class it has. */
   dispose(): void {
      this.light?.removeEventListener('change', this.apply);
   }

   private readonly apply = () => {
      const dark = this.theme === 'system' ? this.light?.matches !== true : this.theme === 'dark';
      this.root.classList.toggle('dark', dark);
      this.root.dataset.colorScheme = dark ? 'dark' : 'light';
   };
}
