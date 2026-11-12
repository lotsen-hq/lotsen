// The types of popoutNonce.mjs, which runs in Node only, for the Vite configuration that imports it.
import type { Plugin } from 'vite';

/** Returns the plugin that writes the nonce into the built `popout.html`, which the build copies from public/ as it is. */
export function popoutNonce(nonce: string): Plugin;
