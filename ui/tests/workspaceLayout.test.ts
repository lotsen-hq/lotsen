import type { DockviewApi, PopoutWindowFailure } from 'dockview';
import { describe, expect, it, vi } from 'vitest';
import { popoutFailureNotice, WorkspaceLayout } from '../src/lib/workspaceLayout.svelte';

/**
 * Returns a Dockview API with one panel in the main window and a pop-out that waits until the test settles it. Of its events, only the failure of a popout window fires, when
 * the test fails the pop-out.
 */
function fakeApi(): { api: DockviewApi; addPopoutGroup: ReturnType<typeof vi.fn>; settle: (opened: boolean) => void; fail: (failure: PopoutWindowFailure) => void } {
   let settle: (opened: boolean) => void = () => {};
   let failed: (failure: PopoutWindowFailure) => void = () => {};
   const addPopoutGroup = vi.fn(() => new Promise<boolean>((resolve) => (settle = resolve)));
   const panel = { api: { location: { type: 'grid' } } };
   const onDidOpenPopoutWindowFail = (listener: (failure: PopoutWindowFailure) => void) => ((failed = listener), { dispose: () => {} });
   const known: Record<string, unknown> = { groups: [], getPanel: () => panel, getPopouts: () => [], hasMaximizedGroup: () => false, addPopoutGroup, onDidOpenPopoutWindowFail };
   const api = new Proxy(known, { get: (target, key: string) => (key in target ? target[key] : () => ({ dispose: () => {} })) });
   const fail = (failure: PopoutWindowFailure) => {
      failed(failure);
      settle(false);
   };
   return { api: api as unknown as DockviewApi, addPopoutGroup, settle: (opened) => settle(opened), fail };
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
