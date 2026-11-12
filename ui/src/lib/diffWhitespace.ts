import { booleanPreference } from './preference';

/** Whether the reviewer last chose to ignore whitespace in diffs, false by default. */
export const ignoreWhitespacePreference = booleanPreference('lotsen.ignoreWhitespace', false);
