import { fireEvent, screen, waitFor, within } from '@testing-library/svelte';
import { afterEach, beforeEach, describe, expect, it, onTestFinished, vi } from 'vitest';
import type { World } from '../backend/backend';
import { arrangementRecord } from '../../src/lib/arrangements';
import { LotsenApp } from '../../src/lib/app.svelte';
import { backend, renderApp, seedTour, startApp } from '../harness';
import { resetPageEachTest } from './appPanels';

resetPageEachTest();

const root = document.documentElement;
const dark = () => root.classList.contains('dark');

beforeEach(() => {
   // The system asks for light, so only a theme of dark makes the page dark.
   vi.stubGlobal('matchMedia', () => ({ matches: true, addEventListener: () => {}, removeEventListener: () => {} }));
   root.classList.remove('dark');
});

afterEach(() => vi.unstubAllGlobals());

// A tour with one stop, which the address can name.
const oneTour = backend().template('preferences-one-tour', async (world) => {
   const { tourId } = await seedTour(world, { chapters: [{ title: 'Cart', stops: [{ title: 'Totals', kind: 'inspect' }] }] });
   return tourId;
});

const stored = async (world: World) => (await world.reviewer.call('read_preferences', {})).preferences;

async function openSettings() {
   fireEvent.click(screen.getByRole('button', { name: 'Settings' }));
   return within(await screen.findByRole('dialog', { name: 'Settings' }));
}

describe('the preferences on the server', () => {
   it('give the page their theme at start over the theme that browser storage holds', async () => {
      const world = await backend().world();
      await world.reviewer.call('write_preference', { key: 'lotsen.theme', value: 'dark' });
      localStorage.setItem('lotsen.theme', 'light');

      await startApp(world);

      await waitFor(() => expect(dark()).toBe(true));
      expect(localStorage.getItem('lotsen.theme')).toBe('dark');
   });

   it('take the choices that browser storage holds when the server holds none', async () => {
      const world = await backend().world();
      localStorage.setItem('lotsen.diffLayout', 'inline');
      localStorage.setItem('lotsen.mixedThreshold', '250');

      await startApp(world);

      await waitFor(async () => expect(await stored(world)).toMatchObject({ 'lotsen.diffLayout': 'inline', 'lotsen.mixedThreshold': '250' }));
   });

   it('take a change of a setting', async () => {
      const world = await backend().world();
      await startApp(world);

      fireEvent.click(within((await openSettings()).getByRole('radiogroup', { name: 'Theme' })).getByRole('radio', { name: 'Dark' }));

      expect(dark()).toBe(true);
      await waitFor(async () => expect((await stored(world))['lotsen.theme']).toBe('dark'));
   });

   it('change the page’s theme when another page changes it', async () => {
      const world = await backend().world();
      await startApp(world);
      expect(dark()).toBe(false);

      await world.reviewer.call('write_preference', { key: 'lotsen.theme', value: 'dark' });

      await waitFor(() => expect(dark()).toBe(true));
      await world.reviewer.call('write_preference', { key: 'lotsen.theme', value: null });
      await waitFor(() => expect(dark()).toBe(false));
   });

   it('change the page’s font size when another page changes it', async () => {
      onTestFinished(() => {
         delete root.dataset.fontSize;
      });
      const world = await backend().world();
      await world.reviewer.call('write_preference', { key: 'lotsen.fontSize', value: 'small' });
      await startApp(world);
      await waitFor(() => expect(root.dataset.fontSize).toBe('small'));

      await world.reviewer.call('write_preference', { key: 'lotsen.fontSize', value: 'large' });

      await waitFor(() => expect(root.dataset.fontSize).toBe('large'));
      await world.reviewer.call('write_preference', { key: 'lotsen.fontSize', value: null });
      await waitFor(() => expect(root.dataset.fontSize).toBe('medium'));
   });

   it('open a tour that the address names in the server’s default view mode', async () => {
      const world = await backend().world({ template: oneTour });
      const tourId = world.seed as string;
      await world.reviewer.call('write_preference', { key: 'lotsen.defaultViewMode', value: 'stepThrough' });
      history.replaceState(null, '', `/#tour=${tourId}`);

      const { app } = await startApp(world);

      await waitFor(() => expect(app.selectedTourId).toBe(tourId));
      expect(app.viewMode).toBe('stepThrough');
   });

   it('keep the page unreachable when the first read fails, so the page shows no workspace and writes nothing', async () => {
      const world = await backend().world();
      const saved = JSON.stringify(arrangementRecord('columns', { files: 'Files', plan: 'Plan', diff: 'Code', stop: 'Stop', threads: 'Threads' }));
      await world.reviewer.call('write_preference', { key: 'lotsen.workspaceLayout', value: saved });
      localStorage.setItem('lotsen.diffLayout', 'inline');
      const transport = world.transport();
      transport.fail('read_preferences', 'network');

      const app = load(world, transport.fetch);

      expect((await screen.findByRole('alert')).textContent).toContain('could not be reached');
      await transport.requestsDone();
      expect(app.phase).toBe('unreachable');
      expect(screen.queryByRole('tab', { name: 'Files' })).toBeNull();
      expect(transport.requests.filter((request) => request.operation === 'write_preference' || request.path === '/api/events')).toEqual([]);
      expect(await stored(world)).toEqual({ 'lotsen.workspaceLayout': saved });
   });

   it('sign the page out when the server refuses its credential at the first read', async () => {
      const world = await backend().world();
      const transport = world.transport();

      const app = load(world, transport.fetch, 'not-a-credential');

      expect((await screen.findByRole('alert')).textContent).toContain('has no session');
      expect(app.phase).toBe('signedOut');
      expect(sessionStorage.getItem('lotsen.uiCredential')).toBeNull();
   });
});

/** Renders a new app on the fetch with a stored UI credential, the world's unless another is given, as a reload of a signed-in page does. */
function load(world: World, fetch: typeof window.fetch, credential = world.uiCredential): LotsenApp {
   sessionStorage.setItem('lotsen.uiCredential', credential);
   const app = new LotsenApp({ environment: { fetch, location, history, storage: sessionStorage }, retryDelaysMs: [5] });
   onTestFinished(() => app.stop());
   renderApp(app);
   return app;
}
