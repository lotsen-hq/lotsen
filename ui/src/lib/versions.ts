import { versionOf } from './stops';
import type { Plan, Stop, StopVersion, WrittenStop } from './types';

/** An earlier version of a stop, as the page keeps it: the version with the ID of its stop and its number, from 1. */
export interface KnownVersion extends StopVersion {
   stopId: string;
   number: number;
}

/** Returns the earlier versions that the plan's written stops hold, each stop's oldest first, in plan order. */
export function versionsOf(plan: Plan | null | undefined): KnownVersion[] {
   return (plan?.stops ?? []).flatMap((stop) => (stop.stage === 'written' ? stop.earlier.map((version, index) => ({ ...version, stopId: stop.id, number: index + 1 })) : []));
}

/**
 * Returns the earlier versions with the added ones that they lack, each stop's oldest first, and the stops in the order the page first heard of them.
 *
 * A stored version never changes, so loads and events merge in any order. A version of a stop that a later plan dropped stays known, and no stop of the plan shows it.
 */
export function withEarlierVersions(known: readonly KnownVersion[], added: readonly KnownVersion[]): KnownVersion[] {
   const merged = [...known];
   for (const version of added) {
      if (!merged.some((candidate) => candidate.stopId === version.stopId && candidate.number === version.number)) {
         merged.push(version);
      }
   }

   const stops = [...new Set(merged.map((version) => version.stopId))];
   return stops.flatMap((stopId) => merged.filter((version) => version.stopId === stopId).sort((a, b) => a.number - b.number));
}

/** Returns the stop's earlier versions below its current version, oldest first, or none when the stop is null or pending. */
export function stopVersions(known: readonly KnownVersion[], stop: Stop | null): KnownVersion[] {
   return stop === null ? [] : known.filter((version) => version.stopId === stop.id && version.number < (versionOf(stop) ?? 0));
}
/**
 * Why the page shows an earlier stop version: the reviewer opened it, or a revision of the stop under view kept it on screen. A revision for an iteration update that named the stop names
 * the update's iteration.
 */
export type ShownReason = { kind: 'opened' } | { kind: 'revised' } | { kind: 'iterationUpdate'; iteration: number };

/** An earlier stop version of the open stop that the page shows in place of the current one. */
export interface ShownVersion {
   /** The number of the version shown. */
   number: number;
   /** The number of the stop's current version. */
   current: number;
   reason: ShownReason;
}

/**
 * Returns the stop as it was at an earlier version: the title, kind and content of that version, in the stop's place in the plan now, with the versions before it. An earlier version is never
 * stale, so it has no stale marker.
 */
export function stopAt(stop: WrittenStop, version: KnownVersion): WrittenStop {
   const earlier: WrittenStop = {
      ...stop,
      title: version.title,
      kind: version.kind,
      content: version.content,
      version: version.number,
      earlier: stop.earlier.slice(0, version.number - 1),
      status: version.number === 1 ? 'ready' : 'revised',
   };
   delete earlier.staleOn;
   return earlier;
}
