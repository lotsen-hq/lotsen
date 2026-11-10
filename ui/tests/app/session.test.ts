import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { afterEach, describe, expect, it } from 'vitest';
import { LotsenApp } from '../../src/lib/app.svelte';
import type { World } from '../backend/backend';
import { backend, prUrl, renderApp, startApp } from '../harness';
import { hoverTooltip } from '../tooltip';
import { tourSwitcherName } from '../tourPicker';
import { resetPageEachTest } from './appPanels';

const credentialKey = 'lotsen.uiCredential';
const started: LotsenApp[] = [];

resetPageEachTest();
afterEach(() => {
   for (const app of started.splice(0)) {
      app.stop();
   }
});

function newApp(fetch: typeof window.fetch): LotsenApp {
   const app = new LotsenApp({ environment: { fetch, location, history, storage: sessionStorage }, retryDelaysMs: [5] });
   started.push(app);
   return app;
}

/** Renders the app at the address, on the fetch, as a page load does. */
function load(path: string, fetch: typeof window.fetch): LotsenApp {
   history.replaceState(null, '', path);
   const app = newApp(fetch);
   renderApp(app);
   return app;
}

const streamRequests = (requests: readonly { path: string }[]) => requests.filter((request) => request.path === '/api/events');

describe('starting a session', () => {
   it('exchanges the code from the link for the UI credential and keeps the credential out of the URL', async () => {
      const world = await backend().world();
      const code = await world.uiCode();
      const transport = world.transport();
      const bodies: { path: string; body: unknown }[] = [];
      const fetch: typeof window.fetch = (input, init) => {
         bodies.push({ path: String(input), body: typeof init?.body === 'string' ? JSON.parse(init.body) : null });
         return transport.fetch(input, init);
      };

      load(`/#code=${code}`, fetch);

      // The fragment is gone before the exchange answers.
      expect(location.hash).toBe('');
      expect(location.href).not.toContain(code);
      await screen.findByRole('button', { name: tourSwitcherName });
      expect(sessionStorage.getItem(credentialKey)).toBe(world.uiCredential);
      expect(location.href).not.toContain(world.uiCredential);
      expect(bodies.find((request) => request.path === '/api/ui-credential')?.body).toEqual({ code });
      for (const { path } of transport.requests) {
         expect(path).not.toContain(world.uiCredential);
      }
   });

   it('opens with the tour of the link selected and the code gone from the address bar, and its Plan panel shows the plan that Claude sets while the tour pins', async () => {
      const world = await backend().world();
      const claude = await world.claude();
      const { id: tourId } = (await claude.call('create_tour', { prUrl, familiarity: 'some' })) as { id: string };
      const code = await world.uiCode();
      const transport = world.transport();

      const app = load(`/#code=${code}&tour=${tourId}`, transport.fetch);

      expect(location.href).not.toContain(code);
      expect(location.hash).toBe(`#tour=${tourId}`);
      await waitFor(() => expect(app.selectedTourId).toBe(tourId));
      await screen.findByRole('tabpanel', { name: 'Plan' });
      await claude.call('set_plan', { tourId, chapters: [{ id: 'c1', title: 'Cart' }], stops: [{ title: 'Cart totals', kind: 'inspect', chapter: 'c1' }] });
      expect(await within(await screen.findByRole('tabpanel', { name: 'Plan' })).findByRole('button', { name: /Cart totals/ })).toBeTruthy();
      expect(location.hash).toBe(`#tour=${tourId}`);
   });

   it('opens no event stream for a start that the page stopped while the session started, nor for a start that a later one replaced', async () => {
      const world = await backend().world();
      const transport = world.transport();
      sessionStorage.setItem(credentialKey, world.uiCredential);
      const stopped = newApp(transport.fetch);
      const starting = stopped.start('wholePr');
      stopped.stop();
      const app = newApp(transport.fetch);
      const replaced = app.start('wholePr');
      const current = app.start('wholePr');

      await Promise.all([starting, replaced, current]);

      await waitFor(() => expect(app.connection).toBe('live'), { timeout: 10_000 });
      await new Promise((resolve) => setTimeout(resolve, 20));
      expect(streamRequests(transport.requests)).toHaveLength(1);
      expect(stopped.phase).toBe('starting');
   });

   it('keeps the current start ready when a start that it replaced cannot reach the server for its code exchange', async () => {
      const world = await backend().world();
      const transport = world.transport();
      let rejectExchange!: (error: Error) => void;
      const fetch: typeof window.fetch = (input, init) => (input === '/api/ui-credential' ? new Promise((_, reject) => (rejectExchange = reject)) : transport.fetch(input, init));
      history.replaceState(null, '', `/#code=${await world.uiCode()}`);
      const app = newApp(fetch);
      const replaced = app.start('wholePr');
      app.stop();
      sessionStorage.setItem(credentialKey, world.uiCredential);
      await app.start('wholePr');
      expect(app.phase).toBe('ready');

      rejectExchange(new TypeError('Failed to fetch'));
      await replaced;

      expect(app.phase).toBe('ready');
   });

   it('reuses the UI credential stored by an earlier load of the tab', async () => {
      const world = await backend().world();
      const transport = world.transport();
      sessionStorage.setItem(credentialKey, world.uiCredential);

      load('/', transport.fetch);

      await screen.findByRole('button', { name: tourSwitcherName });
      expect(transport.requests.some((request) => request.path === '/api/ui-credential')).toBe(false);
   });

   it('tells the user to run lotsen open when the link has no code and the tab no UI credential', async () => {
      const world = await backend().world();
      const transport = world.transport();

      load('/', transport.fetch);

      const alert = await screen.findByRole('alert');
      expect(alert.textContent).toContain('lotsen open');
      expect(transport.requests).toEqual([]);
   });

   it('tells the user to run lotsen open when the code was already used', async () => {
      const world = await backend().world();
      const code = await world.uiCode();
      await exchange(world, code);

      load(`/#code=${code}`, world.transport().fetch);

      expect((await screen.findByRole('alert')).textContent).toContain('lotsen open');
      expect(sessionStorage.getItem(credentialKey)).toBeNull();
   });

   it('drops the stored UI credential and tells the user to run lotsen open when the server refuses it', async () => {
      const world = await backend().world();
      sessionStorage.setItem(credentialKey, 'credential-from-an-earlier-server');

      load('/', world.transport().fetch);

      expect((await screen.findByRole('alert')).textContent).toContain('lotsen open');
      expect(sessionStorage.getItem(credentialKey)).toBeNull();
   });
});

/** Spends the code, as another page that exchanged it first does. */
async function exchange(world: World, code: string): Promise<void> {
   const response = await fetch(`${world.url}/api/ui-credential`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ code }) });
   expect(response.ok).toBe(true);
}

describe('the tooltips', () => {
   const pause = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));
   const described = (element: Element) => element.hasAttribute('aria-describedby');

   it('open the next tooltip at once while the pointer moves along the top bar', async () => {
      await startApp(await backend().world());
      const tours = screen.getByRole('button', { name: tourSwitcherName });
      const settings = screen.getByRole('button', { name: 'Settings' });
      await hoverTooltip(tours);

      await fireEvent.pointerLeave(tours, { pointerType: 'mouse', relatedTarget: settings });
      await fireEvent.pointerEnter(settings, { pointerType: 'mouse' });
      await pause(100);

      expect(described(settings)).toBe(true);
   });

   it('show one tooltip at a time, so hovering a button closes the tooltip that keyboard focus opened', async () => {
      await startApp(await backend().world());
      const tours = screen.getByRole('button', { name: tourSwitcherName });
      const settings = screen.getByRole('button', { name: 'Settings' });
      tours.focus();
      await waitFor(() => expect(described(tours)).toBe(true));

      await hoverTooltip(settings);

      await waitFor(() => expect(described(tours)).toBe(false));
      await waitFor(() => expect(document.querySelectorAll('[data-tooltip-content]')).toHaveLength(1));
   });
});
