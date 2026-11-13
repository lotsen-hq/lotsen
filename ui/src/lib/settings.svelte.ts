import { ColorScheme, type ColorSchemeWindow, type Theme } from './colorScheme';
import { applyColorTheme, colorThemePreference, type ColorTheme } from './colorThemes';
import { diffLayoutPreference, type DiffLayout } from './diffLayout';
import { applyFontSize, fontSizePreference, type FontSize } from './fontSize';
import { messageStylePreference, type MessageStyle } from './messageStyle';
import { mixedThresholdPreference, placementModePreference, type PlacementMode } from './placement';
import { preference } from './preference';
import { applySyntaxColors, syntaxColorsPreference, type SyntaxColors } from './syntaxColors';
import { defaultViewModePreference, type ViewMode } from './viewMode';

const themePreference = preference<Theme>('lotsen.theme', ['system', 'light', 'dark'], 'system');

/**
 * Holds the reviewer's settings, which the settings panel and the diff toolbar change and the rest of the page reads.
 *
 * Each setting reads its preference, so a change in another page shows here too, and each change stores the new choice.
 */
export class Settings {
   private readonly colorScheme: ColorScheme;
   private readonly document: Document;
   private readonly disposeEffects: () => void;

   /** Applies the stored theme, color theme, syntax colors and font size to the window's page at once, and again whenever their preferences change. */
   constructor(window: ColorSchemeWindow) {
      this.document = window.document;
      // The family and the mode apply in one task, so no paint shows one without the other.
      applyColorTheme(this.document, this.colorTheme);
      this.colorScheme = new ColorScheme(window, this.theme);
      applySyntaxColors(this.document, this.syntaxColors);
      applyFontSize(this.document, this.fontSize);
      this.disposeEffects = $effect.root(() => {
         $effect(() => this.colorScheme.setTheme(this.theme));
         $effect(() => applyColorTheme(this.document, this.colorTheme));
         $effect(() => applySyntaxColors(this.document, this.syntaxColors));
         $effect(() => applyFontSize(this.document, this.fontSize));
      });
   }

   get theme(): Theme {
      return themePreference.read();
   }

   setTheme(theme: Theme): void {
      themePreference.write(theme);
      this.colorScheme.setTheme(theme);
   }

   /** The family of the page's light and dark palettes. */
   get colorTheme(): ColorTheme {
      return colorThemePreference.read();
   }

   setColorTheme(theme: ColorTheme): void {
      colorThemePreference.write(theme);
      applyColorTheme(this.document, theme);
   }

   /** The family of the diff editors' syntax colors. */
   get syntaxColors(): SyntaxColors {
      return syntaxColorsPreference.read();
   }

   setSyntaxColors(colors: SyntaxColors): void {
      syntaxColorsPreference.write(colors);
      applySyntaxColors(this.document, colors);
   }

   /** The size of the page's text, the diff editors' code included. */
   get fontSize(): FontSize {
      return fontSizePreference.read();
   }

   setFontSize(size: FontSize): void {
      fontSizePreference.write(size);
      applyFontSize(this.document, size);
   }

   get diffLayout(): DiffLayout {
      return diffLayoutPreference.read();
   }

   setDiffLayout(layout: DiffLayout): void {
      diffLayoutPreference.write(layout);
   }

   /** The view mode that each tour opens in. */
   get defaultViewMode(): ViewMode {
      return defaultViewModePreference.read();
   }

   setDefaultViewMode(mode: ViewMode): void {
      defaultViewModePreference.write(mode);
   }

   /** Where stops' explanations show. */
   get placement(): PlacementMode {
      return placementModePreference.read();
   }

   setPlacement(mode: PlacementMode): void {
      placementModePreference.write(mode);
   }

   /** The body length in characters above which mixed placement shows a stop in the Stop panel. */
   get mixedThreshold(): number {
      return mixedThresholdPreference.read();
   }

   /** Sets the threshold, which the caller has checked against `mixedThresholdBounds`. */
   setMixedThreshold(threshold: number): void {
      mixedThresholdPreference.write(threshold);
   }

   /** How the thread view lays out the messages. */
   get messageStyle(): MessageStyle {
      return messageStylePreference.read();
   }

   setMessageStyle(style: MessageStyle): void {
      messageStylePreference.write(style);
   }

   /** Stops following the system's color scheme and the preferences' changes, for when the page unmounts. */
   dispose(): void {
      this.disposeEffects();
      this.colorScheme.dispose();
   }
}
