export function isAllowed(dependency: { name: string; license: string | null }): boolean;
export function formatNotices(dependencies: readonly { name: string; version: string; license: string | null; licenseText: string | null }[], fixedSections: readonly string[]): string;
