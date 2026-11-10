// The world of the demo's sample tours, which the flow tests of the sample tours share, and the panels and readers that more than one of those files uses.
import { fireEvent, screen, within } from '@testing-library/svelte';
import { pinningTour, sampleTours } from '../../demo/sampleTours';
import { labelOf, liven, sampleTemplateName, seedSampleTours, type LiveSamples, type SampleSeed, type SeededTour } from '../../demo/seed';
import type { LotsenApp } from '../../src/lib/app.svelte';
import type { QuestionThread, Stop, StopContent, ThreadMessage } from '../../src/lib/contract.generated';
import type { World } from '../backend/backend';
import { backend, openTour } from '../harness';

// Every test starts from the template that the demo starts from, with the sessions and live steps of a world that the demo gives a page.
const samples = backend().template(sampleTemplateName, seedSampleTours);

export interface SampleWorld extends LiveSamples {
   world: World;
   tour(key: string): SeededTour;
}

export async function sampleWorld(): Promise<SampleWorld> {
   const world = await backend().world({ template: samples, clockStart: new Date().toISOString() });
   const live = await liven(world, world.seed as SampleSeed);
   return { ...live, world, tour: (key) => live.seed.tours[key]! };
}

export const specs = sampleTours();
const labelOfKey = (key: string) => labelOf(specs.find((spec) => spec.key === key) ?? pinningTour);
export const alerts = () => screen.queryAllByRole('alert').map((alert) => alert.textContent);
export const threadsPanel = () => within(screen.getByRole('region', { name: 'Threads' }));

// The list shows the hidden tours too, so every sample tour can be selected.
export async function select(app: LotsenApp, key: string): Promise<void> {
   app.setShowHiddenTours(true);
   await openTour(app, labelOfKey(key));
}

// Waits for the panel, because a click turns the tab only on the next update.
export async function showTab(name: 'Files' | 'Plan') {
   await fireEvent.click(await screen.findByRole('tab', { name }));
   return within(await screen.findByRole('tabpanel', { name }));
}

export const contentOf = (stop: Stop): StopContent | null => (stop.stage === 'written' ? stop.content : null);
export const messagesOf = (thread: QuestionThread): ThreadMessage[] => (thread.kind === 'note' ? [] : thread.messages);
export const questionsOf = (thread: QuestionThread) => messagesOf(thread).filter((message) => message.kind === 'question');
