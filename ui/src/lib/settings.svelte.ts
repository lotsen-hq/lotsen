import { ColorScheme, type ColorSchemeWindow, type Theme } from './colorScheme';
import { diffLayoutPreference, type DiffLayout } from './diffLayout';
import { messageStylePreference, type MessageStyle } from './messageStyle';
import { mixedThresholdPreference, placementModePreference, type PlacementMode } from './placement';
import { preference } from './preference';
import { applySyntaxColors, syntaxColorsPreference, type SyntaxColors } from './syntaxColors';
import { defaultViewModePreference, type ViewMode } from './viewMode';

const themePreference = preference<Theme>('lotsen.theme', ['system', 'light', 'dark'], 'system');

/**
 * Holds the reviewer's settings, which the settings panel and the diff toolbar change and the rest of the page reads.
 *
 * Each setting starts from the browser's stored choice, and each change stores the new one.
 */
export class Settings {
   private currentTheme = $state(themePreference.read());
   private currentSyntaxColors = $state(syntaxColorsPreference.read());
   private currentDiffLayout = $state(diffLayoutPreference.read());
   private currentDefaultViewMode = $state(defaultViewModePreference.read());
   private currentPlacement = $state(placementModePreference.read());
   private currentMixedThreshold = $state(mixedThresholdPreference.read());
   private currentMessageStyle = $state(messageStylePreference.read());
   private readonly colorScheme: ColorScheme;
   private readonly document: Document;

   /** Applies the stored theme and syntax colors to the window's page at once. */
   constructor(window: ColorSchemeWindow) {
      this.colorScheme = new ColorScheme(window, this.currentTheme);
      this.document = window.document;
      applySyntaxColors(this.document, this.currentSyntaxColors);
   }

   get theme(): Theme {
      return this.currentTheme;
   }

   setTheme(theme: Theme): void {
      this.currentTheme = theme;
      themePreference.write(theme);
      this.colorScheme.setTheme(theme);
   }

   /** The family of the diff editors' syntax colors. */
   get syntaxColors(): SyntaxColors {
      return this.currentSyntaxColors;
   }

   setSyntaxColors(colors: SyntaxColors): void {
      this.currentSyntaxColors = colors;
      syntaxColorsPreference.write(colors);
      applySyntaxColors(this.document, colors);
   }

   get diffLayout(): DiffLayout {
      return this.currentDiffLayout;
   }

   setDiffLayout(layout: DiffLayout): void {
      this.currentDiffLayout = layout;
      diffLayoutPreference.write(layout);
   }

   /** The view mode that each tour opens in. */
   get defaultViewMode(): ViewMode {
      return this.currentDefaultViewMode;
   }

   setDefaultViewMode(mode: ViewMode): void {
      this.currentDefaultViewMode = mode;
      defaultViewModePreference.write(mode);
   }

   /** Where stops' explanations show. */
   get placement(): PlacementMode {
      return this.currentPlacement;
   }

   setPlacement(mode: PlacementMode): void {
      this.currentPlacement = mode;
      placementModePreference.write(mode);
   }

   /** The body length in characters above which mixed placement shows a stop in the Stop panel. */
   get mixedThreshold(): number {
      return this.currentMixedThreshold;
   }

   /** Sets the threshold, which the caller has checked against `mixedThresholdBounds`. */
   setMixedThreshold(threshold: number): void {
      this.currentMixedThreshold = threshold;
      mixedThresholdPreference.write(threshold);
   }

   /** How the thread view lays out the messages. */
   get messageStyle(): MessageStyle {
      return this.currentMessageStyle;
   }

   setMessageStyle(style: MessageStyle): void {
      this.currentMessageStyle = style;
      messageStylePreference.write(style);
   }

   /** Stops following the system's color scheme, for when the page unmounts. */
   dispose(): void {
      this.colorScheme.dispose();
   }
}
