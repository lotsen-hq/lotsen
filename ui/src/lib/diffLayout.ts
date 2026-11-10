import { preference } from './preference';

/** How the diff shows a file: base and head side by side, or the head with the base's lines inline. */
export type DiffLayout = 'sideBySide' | 'inline';

/** The diff layout the reviewer last chose in this browser, side by side by default. */
export const diffLayoutPreference = preference<DiffLayout>('lotsen.diffLayout', ['sideBySide', 'inline'], 'sideBySide');
