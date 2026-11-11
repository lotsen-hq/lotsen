/** A panel of the workspace. */
export type PanelId = 'files' | 'plan' | 'diff' | 'stop' | 'threads';

/** The workspace's panels, in the order of the Panels menu. */
export const panelIds: readonly PanelId[] = ['files', 'plan', 'diff', 'stop', 'threads'];
