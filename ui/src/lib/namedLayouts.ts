import { refusalReason } from './api';
import { layoutFrom, type LayoutRecord } from './layoutRecord';
import { preferenceFamily } from './preference';

/** The longest name of a named layout, in characters. */
export const maxNameLength = 60;

/** The name that the page shows for a layout whose stored value holds no name. */
const unnamed = 'Unnamed layout';

/** A named layout's stored value as the page read it, with each member as some version of lotsen saved it. */
interface StoredLayout {
   name: unknown;
   record: unknown;
}

/** A layout that the reviewer saved under a name. */
export interface NamedLayout {
   /** The id in the layout's key, which a rename keeps. */
   id: string;
   /** The name that the reviewer gave it, or a stand-in when the stored value holds none. */
   name: string;
   /** The stored record as some version of lotsen saved it, which a rename stores again unchanged. */
   raw: unknown;
   /** The record, or undefined when this page cannot read it, as for one that another version of lotsen saved. */
   record: LayoutRecord | undefined;
}

/** The outcome of a check of a layout's name: the trimmed name, the reason it is refused, or the layout that already has it. */
export type NameCheck = { kind: 'free'; name: string } | { kind: 'refused'; reason: string } | { kind: 'taken'; name: string; layout: NamedLayout };

/** A save or rename that the page refuses before it writes, with the reason for the reviewer. */
export class LayoutRefusal extends Error {}

// Each named layout is its own key, so a save writes only its layout, and two pages that save different layouts never overwrite each other.
const prefix = 'workspaceLayouts/';

const family = preferenceFamily<StoredLayout>(prefix, readStored, JSON.stringify);

/** Returns the named layouts, sorted by name. A read in a reactive context follows their changes, other pages' included. */
export function namedLayouts(): NamedLayout[] {
   return family
      .entries()
      .map(({ key, value }) => ({
         id: key.slice(prefix.length),
         name: typeof value?.name === 'string' ? value.name : unnamed,
         raw: value?.record,
         record: layoutFrom(value?.record),
      }))
      .sort((a, b) => a.name.localeCompare(b.name));
}

/**
 * Checks a name for a named layout: its trimmed form has 1 to {@link maxNameLength} characters, and no other layout has it in any letter case.
 *
 * @param exceptId The id of a layout whose own name does not count, as for its rename.
 */
export function checkName(name: string, exceptId?: string): NameCheck {
   const trimmed = name.trim();
   if (trimmed === '') {
      return { kind: 'refused', reason: 'Enter a name.' };
   }

   if ([...trimmed].length > maxNameLength) {
      return { kind: 'refused', reason: `Use at most ${maxNameLength} characters.` };
   }

   const layout = namedLayouts().find((candidate) => candidate.id !== exceptId && candidate.name.localeCompare(trimmed, undefined, { sensitivity: 'accent' }) === 0);
   return layout === undefined ? { kind: 'free', name: trimmed } : { kind: 'taken', name: trimmed, layout };
}

/**
 * Saves the record under the name, in place of the layout that has the name, which then takes the name's new letter case.
 *
 * The caller asks the reviewer before it replaces a layout.
 *
 * @throws A {@link LayoutRefusal} when the name is refused, or the server's refusal.
 */
export async function saveLayout(name: string, record: LayoutRecord): Promise<void> {
   const check = checkName(name);
   if (check.kind === 'refused') {
      throw new LayoutRefusal(check.reason);
   }

   const id = check.kind === 'taken' ? check.layout.id : crypto.randomUUID();
   await family.write(prefix + id, { name: check.name, record });
}

/**
 * Gives the layout another name and keeps its record as stored.
 *
 * @throws A {@link LayoutRefusal} when the name is refused, another layout has it or the layout is gone, or the server's refusal.
 */
export async function renameLayout(id: string, name: string): Promise<void> {
   const check = checkName(name, id);
   if (check.kind === 'refused') {
      throw new LayoutRefusal(check.reason);
   }

   if (check.kind === 'taken') {
      throw new LayoutRefusal(`A layout named ${check.layout.name} already exists.`);
   }

   const layout = namedLayouts().find((candidate) => candidate.id === id);
   if (layout === undefined) {
      throw new LayoutRefusal('The layout no longer exists.');
   }

   await family.write(prefix + id, { name: check.name, record: layout.raw });
}

/**
 * Deletes the layout.
 *
 * @throws The server's refusal.
 */
export async function removeLayout(id: string): Promise<void> {
   await family.write(prefix + id, null);
}

/** Returns the reason that a save, rename or delete failed, for the reviewer. */
export function failureReason(error: unknown): string {
   return error instanceof LayoutRefusal ? error.message : refusalReason(error, 'The server did not store the change. Try again.');
}

// Returns undefined for a text that is not a JSON object. The layout still lists, so the reviewer can delete it.
function readStored(text: string): StoredLayout | undefined {
   let value: unknown;
   try {
      value = JSON.parse(text);
   } catch {
      return undefined;
   }

   return typeof value === 'object' && value !== null && !Array.isArray(value) ? { name: (value as StoredLayout).name, record: (value as StoredLayout).record } : undefined;
}
