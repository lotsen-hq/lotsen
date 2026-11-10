import Space from '@lucide/svelte/icons/space';
import { fireEvent, render, screen, waitFor } from '@testing-library/svelte';
import { describe, expect, it, onTestFinished, vi } from 'vitest';
import DiffHeading from '../src/components/DiffHeading.svelte';
import FileHeading from '../src/components/FileHeading.svelte';
import FileTree from '../src/components/FileTree.svelte';
import IconToggle from '../src/components/IconToggle.svelte';
import PanelActions from '../src/components/PanelActions.svelte';
import SettingsPanel from '../src/components/SettingsPanel.svelte';
import TooltipButton from '../src/components/TooltipButton.svelte';
import { buildFileTree } from '../src/lib/fileTree';
import { Settings } from '../src/lib/settings.svelte';
import type { FileMark } from '../src/lib/types';
import { WorkspaceLayout } from '../src/lib/workspaceLayout.svelte';
import { hoverTooltip, openTooltip } from './tooltip';

function renderPanelActions() {
   render(PanelActions, { props: { layout: new WorkspaceLayout(), panel: 'diff', title: 'the code' } });
   return screen.getByRole('button', { name: 'Maximize the code' });
}

const pause = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

describe('an icon button tooltip', () => {
   it('opens after a delay on hover and leaves the button its accessible name', async () => {
      const button = renderPanelActions();

      await fireEvent.pointerEnter(button, { pointerType: 'mouse' });
      await pause(100);
      expect(button.hasAttribute('aria-describedby')).toBe(false);

      expect(await openTooltip(button)).toBe('Maximize');
      expect(screen.getByRole('button', { name: 'Maximize the code' })).toBe(button);
   });

   it('closes when the pointer leaves', async () => {
      const button = renderPanelActions();
      await hoverTooltip(button);

      await fireEvent.pointerLeave(button, { pointerType: 'mouse' });

      await waitFor(() => expect(button.hasAttribute('aria-describedby')).toBe(false));
   });

   it('opens at once on keyboard focus and closes on blur', async () => {
      const button = renderPanelActions();

      button.focus();
      await pause(50);
      expect(button.hasAttribute('aria-describedby')).toBe(true);
      expect(await openTooltip(button)).toBe('Maximize');

      button.blur();
      await waitFor(() => expect(button.hasAttribute('aria-describedby')).toBe(false));
   });

   it('stays closed when a pointer press focuses the button', async () => {
      const button = renderPanelActions();

      await fireEvent.pointerDown(button, { pointerType: 'mouse' });
      button.focus();
      await pause(50);

      expect(button.hasAttribute('aria-describedby')).toBe(false);
   });

   it('keeps the handlers of an outer trigger, so the settings button opens the settings and shows its tooltip', async () => {
      const settings = new Settings(window);
      onTestFinished(() => settings.dispose());
      render(SettingsPanel, { props: { settings, onprune: async () => ({ removed: 0 }) } });
      const button = screen.getByRole('button', { name: 'Settings' });

      expect(await hoverTooltip(button)).toBe('Settings');

      await fireEvent.click(button);
      expect(await screen.findByRole('dialog', { name: 'Settings' })).toBeTruthy();
   });

   it('keeps the type that its caller gives the button, so an icon Send submits its form', async () => {
      render(TooltipButton, { props: { tooltip: 'Send', 'aria-label': 'Send', type: 'submit' } });
      const button = screen.getByRole('button', { name: 'Send' }) as HTMLButtonElement;

      expect(button.type).toBe('submit');
      expect(await hoverTooltip(button)).toBe('Send');
   });
});

describe('an icon toggle', () => {
   it('is named by its label, which its tooltip shows, and leaves its icon out of the accessibility tree', async () => {
      render(IconToggle, { props: { label: 'Ignore whitespace', icon: Space, pressed: false, onclick: vi.fn() } });
      const toggle = screen.getByRole('button', { name: 'Ignore whitespace' });

      expect(await hoverTooltip(toggle)).toBe('Ignore whitespace');
      expect(toggle.textContent?.trim()).toBe('');
      expect(toggle.querySelector('svg')?.getAttribute('aria-hidden')).toBe('true');
   });

   it('shows the pressed state its parent passes in and reports a click, with its tooltip open', async () => {
      const onclick = vi.fn();
      const view = render(IconToggle, { props: { label: 'Ignore whitespace', icon: Space, pressed: false, onclick } });
      const toggle = screen.getByRole('button', { name: 'Ignore whitespace' });
      await hoverTooltip(toggle);

      await fireEvent.click(toggle);

      expect(onclick).toHaveBeenCalledOnce();
      expect(toggle.getAttribute('aria-pressed')).toBe('false');
      await view.rerender({ pressed: true });
      expect(toggle.getAttribute('aria-pressed')).toBe('true');
   });
});

describe('the file tree tooltips', () => {
   const mark: FileMark = { path: 'client.g.ts', mark: 'generated', reason: 'A tool writes it.' };

   function renderTree() {
      const nodes = buildFileTree(
         [
            { path: 'src/new.ts', kind: 'renamed', oldPath: 'lib/old.ts' },
            { path: 'client.g.ts', kind: 'modified' },
         ],
         new Map([[mark.path, mark]]),
      );
      render(FileTree, { props: { nodes, selectedPath: null, onselect: vi.fn(), showReasons: true } });
   }

   // The tooltip opens for the row, the treeitem's first child.
   const row = (name: string) => screen.getByRole('treeitem', { name }).firstElementChild!;

   it("spell out a row's paths and its mark's reason, all rows sharing one tooltip", async () => {
      renderTree();

      const renamed = row('new.ts, Renamed from lib/old.ts');
      const marked = row('client.g.ts, Modified, generated: A tool writes it.');

      expect(await hoverTooltip(renamed)).toBe('lib/old.ts → src/new.ts');
      const shared = renamed.getAttribute('aria-describedby');
      await fireEvent.pointerLeave(renamed, { pointerType: 'mouse' });
      expect(await hoverTooltip(marked)).toBe('client.g.ts\ngenerated: A tool writes it.');
      expect(marked.getAttribute('aria-describedby')).toBe(shared);
      await fireEvent.pointerLeave(marked, { pointerType: 'mouse' });
      expect(await hoverTooltip(row('src'))).toBe('src');
   });

   it('leave the rows out of the tab order', () => {
      renderTree();

      expect(row('src').hasAttribute('tabindex')).toBe(false);
   });
});

describe('the path tooltips', () => {
   it("spell out the whole path of a diff, a renamed file's old path first, as the heading's text holds it", async () => {
      const { container } = render(DiffHeading, { props: { path: 'src/pricing/totals.ts', oldPath: 'src/checkout/totals.ts' } });
      const path = container.querySelector('code')!;
      expect(path.textContent).toBe('src/checkout/totals.ts → src/pricing/totals.ts');

      expect(await hoverTooltip(path)).toBe('src/checkout/totals.ts → src/pricing/totals.ts');
   });

   it('spell out the whole path of a file view', async () => {
      const { container } = render(FileHeading, { props: { known: { state: 'loading', side: 'head', path: 'src/lib/catalog.ts' } } });
      const path = container.querySelector('code')!;
      expect(path.textContent).toBe('src/lib/catalog.ts');

      expect(await hoverTooltip(path)).toBe('src/lib/catalog.ts');
   });
});
