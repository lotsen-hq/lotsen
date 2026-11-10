const credentialKey = 'lotsen.uiCredential';

/** What the page needs from the browser to start a session. */
export interface SessionEnvironment {
   fetch: typeof fetch;
   location: Pick<Location, 'hash' | 'pathname' | 'search'>;
   history: Pick<History, 'replaceState'>;
   /** Session storage, which keeps the UI credential for this tab alone and across its reloads. */
   storage: Storage;
}

/**
 * Returns the UI credential for this tab, the bearer credential that marks the page's requests as the UI's.
 *
 * A one-time code in the fragment from `lotsen open` is removed from the address bar and its history entry first, with the rest of the fragment kept, then exchanged once through
 * `POST /api/ui-credential` for the credential, which replaces any stored one.
 * Without a code, the credential stored by an earlier load of this tab is used.
 *
 * @returns The credential, or null when the page has none: no code and no stored credential, or a code the server refused.
 * @throws When the server cannot be reached or answers with an unexpected error.
 */
export async function startSession(environment: SessionEnvironment): Promise<string | null> {
   const { location, history, storage } = environment;
   const fragment = new URLSearchParams(location.hash.slice(1));
   const code = fragment.get('code');
   if (code === null) {
      return storage.getItem(credentialKey);
   }

   fragment.delete('code');
   const rest = fragment.toString();
   history.replaceState(null, '', location.pathname + location.search + (rest === '' ? '' : `#${rest}`));
   storage.removeItem(credentialKey);
   const response = await environment.fetch('/api/ui-credential', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ code }),
      cache: 'no-store',
   });
   if (response.status === 401) {
      return null;
   }

   if (!response.ok) {
      throw new Error(`The session exchange answered ${response.status}.`);
   }

   const { credential } = (await response.json()) as { credential?: unknown };
   if (typeof credential !== 'string' || credential === '') {
      throw new Error('The session exchange returned no UI credential.');
   }

   storage.setItem(credentialKey, credential);
   return credential;
}

/** Returns the one-time code that a link's fragment carries, or null when it carries none. */
export function linkCode(hash: string): string | null {
   return new URLSearchParams(hash.slice(1)).get('code');
}

/** Forgets the stored UI credential, after the server refused it. */
export function endSession(storage: Storage): void {
   storage.removeItem(credentialKey);
}
