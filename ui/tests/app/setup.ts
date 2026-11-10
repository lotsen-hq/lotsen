// The flow tests' default wait for waitFor and the find queries.
import { configure } from '@testing-library/svelte';
import { waitTimeout } from './waitTimeout.mjs';

// Most waits of a flow test span round trips to a world's real server, which a loaded machine slows past the library's default.
configure({ asyncUtilTimeout: waitTimeout });
