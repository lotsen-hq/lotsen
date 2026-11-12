import type { DockviewApi, PopoutWindowFailure } from 'dockview';
import { describe, expect, it, vi } from 'vitest';
import { popoutFailureNotice, WorkspaceLayout } from '../src/lib/workspaceLayout.svelte';

/** A fake Dockview API, with the test's handles on it. */
interface FakeApi {
   api: DockviewApi;
   addPopoutGroup: ReturnType<typeof vi.fn>;
   /** The panel that pops out. */
   panel: { api: { location: { type: string }; moveTo: ReturnType<typeof vi.fn> }; group: { id: string; panels: unknown[] } };
   /** Settles the pop-out, with whether its window opened. */
   settle: (opened: boolean) => void;
   /** Settles the pop-out as a window that did not open, and fires the failure that Dockview fires then. */
   fail: (failure: PopoutWindowFailure) => void;
   /** Fires a Dockview event that the layout listens to, with no argument. */
   fire: (event: string) => void;
}

/**
 * Returns a Dockview API with one panel in a group of the main window, and a pop-out that waits until the test settles it. Its events fire only when the test fires them.
 *
 * @param shared The number of other panels that share the panel's group, after the panel.
 */
function fakeApi(shared = 0): FakeApi {
   let settle: (opened: boolean) => void = () => {};
   const listeners: Record<string, (argument?: unknown) => void> = {};
   const addPopoutGroup = vi.fn(() => new Promise<boolean>((resolve) => (settle = resolve)));
   const panel: FakeApi['panel'] = { api: { location: { type: 'grid' }, moveTo: vi.fn() }, group: { id: '1', panels: [] } };
   panel.group.panels.push(panel, ...Array.from({ length: shared }, () => ({})));
   const known: Record<string, unknown> = { groups: [], getPanel: () => panel, getPopouts: () => [], hasMaximizedGroup: () => false, addPopoutGroup };
   const api = new Proxy(known, {
      get: (target, key: string) => (key in target ? target[key] : (listener: (argument?: unknown) => void) => ((listeners[key] = listener), { dispose: () => {} })),
   });
   const fail = (failure: PopoutWindowFailure) => {
      listeners.onDidOpenPopoutWindowFail?.(failure);
      settle(false);
   };
   return { api: api as unknown as DockviewApi, addPopoutGroup, panel, settle: (opened) => settle(opened), fail, fire: (event) => listeners[event]?.() };
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

   it('tells the reviewer about a popout window that the browser blocked until the next pop-out', async () => {
      const layout = new WorkspaceLayout();
      const { api, fail, settle } = fakeApi();
      layout.attach(api, () => {});

      const blocked = layout.popOut('threads');
      fail({ reason: 'blocked' });
      await blocked;
      expect(layout.popoutFailure).toBe(popoutFailureNotice({ reason: 'blocked' }));

      const next = layout.popOut('threads');
      expect(layout.popoutFailure).toBeNull();
      settle(true);
      await next;
      expect(layout.popoutFailure).toBeNull();
   });
});

describe("a popped-out panel's tab place", () => {
   it('is forgotten when the window did not open, so a later return of another window leaves the panel where the reviewer moved it', async () => {
      const layout = new WorkspaceLayout();
      const { api, panel, fail, fire } = fakeApi(1);
      layout.attach(api, () => {});
      const blocked = layout.popOut('threads');
      fail({ reason: 'blocked' });
      await blocked;
      panel.group.panels.reverse();

      fire('onDidRemovePopoutGroup');
      await Promise.resolve();

      expect(panel.api.moveTo).not.toHaveBeenCalled();
   });

   it('moves the panel back to it once the window returns the panel as the last tab of its group', async () => {
      const layout = new WorkspaceLayout();
      const { api, panel, settle, fire } = fakeApi(1);
      layout.attach(api, () => {});
      const opened = layout.popOut('threads');
      settle(true);
      await opened;
      panel.group.panels.reverse();

      fire('onDidRemovePopoutGroup');
      await Promise.resolve();

      expect(panel.api.moveTo).toHaveBeenCalledWith({ group: panel.group, index: 0 });
   });
});

describe('the notice about a popout window that did not open', () => {
   it('asks the reviewer to allow pop-ups when the browser blocked the window', () => {
      expect(popoutFailureNotice({ reason: 'blocked' })).toContain('Allow pop-ups for this page');
   });

   for (const reason of ['unscriptable', 'url-refused'] as const) {
      it(`points the reviewer at the browser console when the window failed as ${reason}`, () => {
         expect(popoutFailureNotice({ reason, error: new Error('refused') })).toBe('The popout window did not open. The browser console says why.');
      });
   }

   it('says nothing about a window that closed before it loaded', () => {
      expect(popoutFailureNotice({ reason: 'closed' })).toBeNull();
   });
});
