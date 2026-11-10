import '../src/app.css';
import { mount } from 'svelte';
import App from '../src/App.svelte';
import { LotsenApp } from '../src/lib/app.svelte';
import { joinWorld } from '../tests/backend/backend';
import DemoPanel from './DemoPanel.svelte';
import { worldHeader, worldPath, type DemoWorld } from './pageWorld';
import { liven } from './seed';

const target = document.getElementById('app')!;
target.textContent = 'Starting a server with the sample tours…';

const answer = await fetch(worldPath, { method: 'POST' });
if (!answer.ok) {
   target.textContent = await answer.text();
   throw new Error(`The demo server answered ${answer.status}.`);
}

const demo = (await answer.json()) as DemoWorld;

// Every request of the page names its world, which the demo server forwards it to.
const demoFetch: typeof fetch = (input, init) => {
   const headers = new Headers(init?.headers);
   headers.set(worldHeader, demo.world);
   return fetch(input, { ...init, headers });
};

// The demo server adds the host's token to each control request, so the page holds none.
const world = joinWorld({ url: '', token: '' }, { world: demo.world, url: '', uiCredential: demo.uiCredential }, demo.seed, demoFetch);
const live = await liven(world, demo.seed);

// Each load starts the session from a code, as a link from lotsen open does, so the page holds no state from an earlier load but its history entry.
const fragment = new URLSearchParams(location.hash.slice(1));
fragment.set('code', demo.code);
history.replaceState(history.state, '', `${location.pathname}${location.search}#${fragment.toString()}`);
const app = new LotsenApp({ environment: { fetch: demoFetch, location, history, storage: sessionStorage } });

target.textContent = '';
mount(App, { target, props: { app } });
mount(DemoPanel, { target: document.getElementById('demo')!, props: { app, live } });
