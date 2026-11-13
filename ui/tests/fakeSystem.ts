/** Returns a window whose media query list for prefers-color-scheme: light has the matches that the test sets, and which tells its change listeners. */
export function fakeSystem(light: boolean) {
   const listeners = new Set<() => void>();
   const query = {
      matches: light,
      addEventListener: (_type: string, listener: () => void) => listeners.add(listener),
      removeEventListener: (_type: string, listener: () => void) => listeners.delete(listener),
   };
   const change = (next: boolean) => {
      query.matches = next;
      listeners.forEach((listener) => listener());
   };
   return { window: { document, matchMedia: () => query as unknown as MediaQueryList }, change, listeners };
}
