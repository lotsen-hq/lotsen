import type { DockviewApi } from 'dockview';
import { describe, expect, it, vi } from 'vitest';
import { WorkspaceLayout } from '../src/lib/workspaceLayout.svelte';

/** Returns a Dockview API whose events never fire, with one panel in the main window and a pop-out that waits until the test settles it. */
function fakeApi(): { api: DockviewApi; addPopoutGroup: ReturnType<typeof vi.fn>; settle: (opened: boolean) => void } {
   let settle: (opened: boolean) => void = () => {};
   const addPopoutGroup = vi.fn(() => new Promise<boolean>((resolve) => (settle = resolve)));
   const panel = { api: { location: { type: 'grid' } } };
   const known: Record<string, unknown> = { groups: [], getPanel: () => panel, getPopouts: () => [], hasMaximizedGroup: () => false, addPopoutGroup };
   const api = new Proxy(known, { get: (target, key: string) => (key in target ? target[key] : () => ({ dispose: () => {} })) });
   return { api: api as unknown as DockviewApi, addPopoutGroup, settle: (opened) => settle(opened) };
}

describe('a pop-out', () => {
   it('opens one window for a second pop-out of the panel while its window opens, and another once that open failed', async () => {
      const layout = new WorkspaceLayout();
      const { api, addPopoutGroup, settle } = fakeApi();
      layout.attach(api, () => {});

      const first = layout.popOut('threads');
      await layout.popOut('threads');

      expect(addPopoutGroup).toHaveBeenCalledTimes(1);
      expect(layout.opening.threads).toBe(true);
      settle(false);
      await first;
      expect(layout.opening.threads).toBe(false);
      void layout.popOut('threads');
      expect(addPopoutGroup).toHaveBeenCalledTimes(2);
   });
});
