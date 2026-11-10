// The flow tests' default wait for waitFor, the find queries and settled.

// A wait spans round trips to a world's real server, each a few git processes on the test host, and a loaded machine slows them several times over. Windows starts processes far slower still.
export const waitTimeout = 15_000;
