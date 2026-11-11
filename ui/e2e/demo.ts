import { expect, type Locator, type Page } from '@playwright/test';

/** The settings that a test page starts with, by their keys in browser storage. */
export interface DemoSettings {
   placement?: 'side' | 'inline' | 'mixed';
   layout?: 'sideBySide' | 'inline';
   viewMode?: 'wholePr' | 'stopFiles' | 'stepThrough';
   theme?: 'system' | 'light' | 'dark';
   messageStyle?: 'bubbles' | 'fullWidth';
}

/** The checkout sample tour, which pins the storefront change with every kind of stop. */
export const checkoutTour = 'storefront #412';

/** Loads the demo with the settings, on a world of its own, and opens a sample tour's plan. */
export async function openDemoTour(page: Page, tour: string, settings: DemoSettings = {}): Promise<void> {
   const stored = {
      'lotsen.placement': settings.placement,
      'lotsen.diffLayout': settings.layout,
      'lotsen.defaultViewMode': settings.viewMode,
      'lotsen.theme': settings.theme,
      'lotsen.messageStyle': settings.messageStyle,
   };
   await page.addInitScript((values) => {
      for (const [key, value] of Object.entries(values)) {
         if (value !== undefined && localStorage.getItem(key) === null) {
            localStorage.setItem(key, value);
         }
      }
   }, stored);
   await page.goto('/');
   await page.getByRole('button', { name: /^(Choose a tour|Tour: .+, choose another)$/ }).click();
   await page
      .getByRole('navigation', { name: 'Tours' })
      .getByRole('button', { name: new RegExp(tour) })
      .first()
      .click();
   await page.getByRole('tab', { name: 'Plan' }).click();
}

/**
 * Opens a stop of the open tour from the Plan panel, and waits for the Stop panel to show it.
 *
 * The keyboard opens the stop's row, because the demo's own panel can cover the row and the page does not scroll it clear.
 */
export async function openStop(page: Page, title: string): Promise<void> {
   await page
      .getByRole('tabpanel', { name: 'Plan' })
      .getByRole('button', { name: new RegExp(title) })
      .first()
      .focus();
   await page.keyboard.press('Enter');
   await expect(page.getByRole('region', { name: 'Stop', exact: true }).getByRole('heading', { name: title })).toBeVisible();
}

/** Shows a hidden panel from the Panels menu, and closes the menu. */
export async function showPanel(page: Page, title: string): Promise<void> {
   await page.getByRole('button', { name: 'Panels' }).click();
   await page.getByRole('menuitemcheckbox', { name: title }).click();
   await page.keyboard.press('Escape');
   await expect(page.getByRole('region', { name: title, exact: true })).toBeVisible();
}

/** Drags a panel's tab with the mouse onto the middle of the target, which docks the panel there as a tab. */
export async function dragTab(page: Page, title: string, target: Locator): Promise<void> {
   const tab = (await page.getByRole('tab', { name: title }).boundingBox())!;
   const drop = (await target.boundingBox())!;
   await page.mouse.move(tab.x + tab.width / 2, tab.y + tab.height / 2);
   await page.mouse.down();
   await page.mouse.move(drop.x + drop.width / 2, drop.y + drop.height / 2, { steps: 15 });
   await page.mouse.up();
}

/** The center's view of the code. */
export const center = (page: Page): Locator => page.getByRole('main');

/** The line of an editor that shows the text, as CodeMirror draws it. */
export const editorLine = (scope: Locator, text: string): Locator => scope.locator('.cm-line', { hasText: text }).first();

/** Returns whether the element lies wholly inside the visible area of the editor scroller that holds it. */
export async function inScrollerView(element: Locator): Promise<boolean> {
   return element.evaluate((node) => {
      let scroller = node.parentElement;
      while (scroller !== null && !(scroller.scrollHeight > scroller.clientHeight && ['auto', 'scroll'].includes(getComputedStyle(scroller).overflowY))) {
         scroller = scroller.parentElement;
      }

      const box = node.getBoundingClientRect();
      const view = (scroller ?? document.documentElement).getBoundingClientRect();
      return box.height > 0 && box.top >= view.top && box.bottom <= view.bottom;
   });
}

/** Returns the color that a custom property of the theme resolves to inside the element, in the form that `getComputedStyle` gives the element's own colors. */
export async function themeColor(element: Locator, property: string): Promise<string> {
   return element.evaluate((node, name) => {
      const probe = node.appendChild(document.createElement('span'));
      probe.style.color = `var(${name})`;
      const resolved = getComputedStyle(probe).color;
      probe.remove();
      return resolved;
   }, property);
}

/** A color as sRGB channels from 0 to 255 and an alpha from 0 to 1. */
export type Rgba = [number, number, number, number];

/**
 * Returns the colors of the element's CSS properties as the page draws them, in sRGB, with the opaque surface behind the element.
 *
 * Computed colors can be oklch or oklab, so a canvas converts each one. The surface behind is the first opaque ancestor's background, with the translucent backgrounds between it and
 * the element laid over it.
 */
export async function drawnColors<P extends string>(element: Locator, properties: readonly P[]): Promise<{ own: Record<P, Rgba>; behind: Rgba }> {
   return element.evaluate((node, names) => {
      const context = document.createElement('canvas').getContext('2d', { willReadFrequently: true })!;
      const rgba = (css: string): Rgba => {
         context.clearRect(0, 0, 1, 1);
         context.fillStyle = css;
         context.fillRect(0, 0, 1, 1);
         const [r, g, b, a] = context.getImageData(0, 0, 1, 1).data;
         return [r!, g!, b!, a! / 255];
      };
      // This function runs in the page, so it cannot call the module's over.
      const laid = (top: Rgba, under: Rgba): Rgba => [top[0] * top[3] + under[0] * (1 - top[3]), top[1] * top[3] + under[1] * (1 - top[3]), top[2] * top[3] + under[2] * (1 - top[3]), 1];

      const layers: Rgba[] = [];
      let behind: Rgba = [255, 255, 255, 1];
      for (let ancestor = node.parentElement; ancestor !== null; ancestor = ancestor.parentElement) {
         const color = rgba(getComputedStyle(ancestor).backgroundColor);
         if (color[3] === 1) {
            behind = color;
            break;
         }
         if (color[3] > 0) {
            layers.push(color);
         }
      }
      behind = layers.reduceRight((under, layer) => laid(layer, under), behind);

      const style = getComputedStyle(node);
      return { own: Object.fromEntries(names.map((name) => [name, rgba(style.getPropertyValue(name))])) as Record<P, Rgba>, behind };
   }, properties);
}

/** Returns a color laid over an opaque one. */
export function over(top: Rgba, under: Rgba): Rgba {
   const [r, g, b] = [0, 1, 2].map((i) => top[i]! * top[3] + under[i]! * (1 - top[3]));
   return [r!, g!, b!, 1];
}

/** Returns the WCAG contrast ratio of two opaque colors. */
export function contrast(first: Rgba, second: Rgba): number {
   const luminance = ([r, g, b]: Rgba) => {
      const linear = (channel: number) => {
         const value = channel / 255;
         return value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
      };
      return 0.2126 * linear(r) + 0.7152 * linear(g) + 0.0722 * linear(b);
   };
   const [light, dark] = [luminance(first), luminance(second)].sort((a, b) => b - a);
   return (light! + 0.05) / (dark! + 0.05);
}
