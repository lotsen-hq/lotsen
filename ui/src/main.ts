import './app.css';
import { mount } from 'svelte';
import App from './App.svelte';
import { LotsenApp } from './lib/app.svelte';
import { linkCode } from './lib/session';

// A link pasted into this tab while it is open changes only the fragment, which does not load the page again.
window.addEventListener('hashchange', () => {
   if (linkCode(location.hash) !== null) {
      location.reload();
   }
});

const app = new LotsenApp({
   environment: { fetch: window.fetch.bind(window), location, history, storage: sessionStorage },
});

mount(App, { target: document.getElementById('app')!, props: { app } });
