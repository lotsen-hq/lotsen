import { fireEvent, render, screen, waitFor, within } from '@testing-library/svelte';
import { createRawSnippet } from 'svelte';
import { describe, expect, it, onTestFinished, vi } from 'vitest';
import TopBar from '../src/components/TopBar.svelte';
import { idleAnswerSend, type AnswerSendState } from '../src/lib/answers';
import type { ConnectionState } from '../src/lib/eventStream';
import type { GenerationStatus } from '../src/lib/generationStatus';
import { attachmentTexts, type Attachment } from '../src/lib/labels';
import { Settings } from '../src/lib/settings.svelte';
import type { Offer } from '../src/lib/types';
import { WorkspaceLayout } from '../src/lib/workspaceLayout.svelte';

const empty = createRawSnippet(() => ({ render: () => '<span></span>' }));
const noSession: Offer = { state: 'closed', code: 'noSessionAttached', reason: 'The tour is attached to no Claude Code session' };

const refused: AnswerSendState = { sending: false, refusal: { control: 'topBar', reason: 'The answers could not be sent.' } };

function renderBar(props: {
   sendAnswers?: Offer;
   unsentAnswers?: number;
   answerSend?: AnswerSendState;
   onsendanswers?: () => Promise<string | null>;
   connection?: ConnectionState;
   attachment?: Attachment | null;
   generation?: GenerationStatus | null;
}) {
   const settings = new Settings(window);
   onTestFinished(() => settings.dispose());
   const result = render(TopBar, {
      props: {
         connection: 'live',
         attachment: 'confirmed',
         layout: new WorkspaceLayout(),
         settings,
         onprune: async () => ({ removed: 0 }),
         onversion: async () => null,
         picker: empty,
         navigation: empty,
         answerSend: idleAnswerSend,
         ...props,
      },
   });
   return Object.assign(within(screen.getByRole('banner')), { rerender: result.rerender });
}

/** Returns the texts of the elements that describe the element, in their order. */
const descriptions = (element: Element) =>
   (element.getAttribute('aria-describedby') ?? '')
      .split(' ')
      .filter((id) => id !== '')
      .map((id) => document.getElementById(id)!.textContent.trim());

/** Text that a screen reader alone gets, which the bar's visible text leaves out. */
const visibleOnly = { ignore: 'script, style, .sr-only' };

describe("the top bar's send of the answers", () => {
   it('shows one Send answers button with the count inside it, and no count beside it', () => {
      const bar = renderBar({ sendAnswers: { state: 'open' }, unsentAnswers: 3 });

      const button = bar.getByRole('button', { name: 'Send answers' });
      expect(within(button).getByText('3')).toBeTruthy();
      expect(bar.getAllByText(/^3$/, visibleOnly)).toEqual([within(button).getByText('3')]);
      expect(bar.queryByText(/unsent answers?/, visibleOnly)).toBeNull();
      expect(descriptions(button)).toEqual(['3 unsent answers']);
   });

   it('reads Sending N… with a spinner while a send is in flight', () => {
      const bar = renderBar({ sendAnswers: { state: 'open' }, unsentAnswers: 3, answerSend: { sending: true, refusal: null } });

      const button = bar.getByRole('button', { name: 'Sending 3…' });
      expect(button.getAttribute('aria-disabled')).toBe('true');
      expect(button.querySelector('svg.animate-spin')).not.toBeNull();
      expect(bar.queryByRole('button', { name: 'Send answers' })).toBeNull();
   });

   it('marks the button disabled while the send is unavailable, with the reason in its tooltip and its description and not as text in the bar', async () => {
      const bar = renderBar({ sendAnswers: noSession, unsentAnswers: 1 });

      const button = bar.getByRole('button', { name: 'Send answers' }) as HTMLButtonElement;
      expect(button.getAttribute('aria-disabled')).toBe('true');
      expect(descriptions(button)).toEqual(['1 unsent answer', 'The tour is attached to no Claude Code session']);
      expect(bar.queryByText(/attached to no Claude Code session/, visibleOnly)).toBeNull();
      // The button's own description replaces the tooltip's, so the tooltip is found by its content.
      await fireEvent.pointerEnter(button, { pointerType: 'mouse' });
      await waitFor(() => expect(document.querySelector('[data-tooltip-content]')?.textContent.trim()).toBe('The tour is attached to no Claude Code session'));
      expect(descriptions(button)).toEqual(['1 unsent answer', 'The tour is attached to no Claude Code session']);
   });

   it('keeps an unavailable button in the tab order, and sends nothing on its click', async () => {
      const onsendanswers = vi.fn(async () => null);
      const bar = renderBar({ sendAnswers: noSession, unsentAnswers: 1, onsendanswers });
      const button = bar.getByRole('button', { name: 'Send answers' }) as HTMLButtonElement;

      button.focus();
      await fireEvent.click(button);

      expect(button.disabled).toBe(false);
      expect(document.activeElement).toBe(button);
      expect(onsendanswers).not.toHaveBeenCalled();
   });

   it('keeps the focus on Send answers when the send turns available', async () => {
      const bar = renderBar({ sendAnswers: noSession, unsentAnswers: 1 });
      const button = bar.getByRole('button', { name: 'Send answers' });
      button.focus();

      await bar.rerender({ sendAnswers: { state: 'open' } });

      expect(button.getAttribute('aria-disabled')).toBe('false');
      expect(document.activeElement).toBe(button);
   });

   it.each([
      { name: 'available', offer: { state: 'open' } satisfies Offer, sending: false, disabled: 'false' },
      { name: 'available while sending', offer: { state: 'open' } satisfies Offer, sending: true, disabled: 'true' },
      { name: 'unavailable', offer: noSession, sending: false, disabled: 'true' },
      { name: 'unavailable while sending', offer: noSession, sending: true, disabled: 'true' },
   ])('reads the button as disabled exactly when the send is unavailable or in flight: $name', ({ offer, sending, disabled }) => {
      const bar = renderBar({ sendAnswers: offer, unsentAnswers: 2, answerSend: { sending, refusal: null } });

      expect(bar.getByRole('button', { name: /^(Send answers|Sending 2…)$/ }).getAttribute('aria-disabled')).toBe(disabled);
   });

   it('adds a standing refusal to the button’s description, after the count and the reason it is unavailable', () => {
      const bar = renderBar({ sendAnswers: noSession, unsentAnswers: 1, answerSend: refused });

      expect(descriptions(bar.getByRole('button', { name: 'Send answers' }))).toEqual(['1 unsent answer', 'The tour is attached to no Claude Code session', 'The answers could not be sent.']);
   });

   it.each([
      { name: 'while the send is open', offer: { state: 'open' } satisfies Offer },
      { name: 'while the send is unavailable', offer: noSession },
   ])('moves the focus from Dismiss to Send answers $name', async ({ offer }) => {
      const bar = renderBar({ sendAnswers: offer, unsentAnswers: 1, answerSend: refused });
      const dismiss = screen.getByRole('button', { name: 'Dismiss' });
      dismiss.focus();

      await fireEvent.click(dismiss);

      expect(document.activeElement).toBe(bar.getByRole('button', { name: 'Send answers' }));
   });

   it('moves the focus from Dismiss to the bar when the bar shows no Send answers', async () => {
      renderBar({ sendAnswers: undefined, unsentAnswers: 1, answerSend: refused });
      const dismiss = screen.getByRole('button', { name: 'Dismiss' });
      dismiss.focus();

      await fireEvent.click(dismiss);

      expect(document.activeElement).toBe(screen.getByRole('banner'));
   });

   it.each([
      { name: 'before the page has the controls', offer: undefined },
      { name: 'while the server holds no answer to send', offer: { state: 'closed', code: 'noAnswersToSend', reason: 'No answer waits to be sent' } satisfies Offer },
   ])('shows the count as a badge that is not a button $name', ({ offer }) => {
      const bar = renderBar({ sendAnswers: offer, unsentAnswers: 3 });

      expect(bar.queryByRole('button', { name: /^Send/ })).toBeNull();
      const badge = bar.getByText('3', visibleOnly);
      expect(badge.closest('button')).toBeNull();
      expect(document.querySelector('[data-unsent-answers]')?.textContent).toBe('3 unsent answers');
      expect(document.querySelector('[data-unsent-answers]')?.getAttribute('role')).toBe('status');
   });

   it('shows neither a count nor a button with no unsent answers', () => {
      const bar = renderBar({ sendAnswers: { state: 'closed', code: 'noAnswersToSend', reason: 'No answer waits to be sent' }, unsentAnswers: 0 });

      expect(bar.queryByRole('button', { name: /^Send/ })).toBeNull();
      expect(document.querySelector('[data-unsent-answers]')).toBeNull();
   });
});

const writing: GenerationStatus = { phase: 'writing', line: 'Claude is writing stop 2 of 3…' };

/** The bar's status chip, found by its accessible name. */
const chip = (bar: ReturnType<typeof renderBar>, name: string | RegExp = /^(Claude session|Connection): /) => bar.getByRole('button', { name });

/** Returns the popover of the status chip once it shows, by its rows' texts. */
async function popoverRows(): Promise<Record<string, string>> {
   const popover = await screen.findByRole('dialog', { name: 'Claude session and connection' });
   return Object.fromEntries([...popover.querySelectorAll('[data-status-row]')].map((row) => [row.getAttribute('data-status-row'), row.textContent.trim()]));
}

describe("the top bar's status chip", () => {
   it.each([
      { attachment: 'confirmed', label: 'Confirmed', icon: 'lucide-circle-check', tone: 'text-success' },
      { attachment: 'unconfirmed', label: 'Unconfirmed', icon: 'lucide-circle-dashed', tone: 'text-muted-foreground' },
      { attachment: 'none', label: 'No session', icon: 'lucide-unplug', tone: 'text-muted-foreground' },
   ] as const)('shows the session state $label while the connection is live, and no Live in the bar', ({ attachment, label, icon, tone }) => {
      const bar = renderBar({ attachment });

      const state = chip(bar, `Claude session: ${label}`);
      expect(state.querySelector('[data-chip-label]')?.textContent).toBe(label);
      expect(state.querySelector(`svg.${icon}`)).not.toBeNull();
      expect(state.classList).toContain(tone);
      expect(state.classList).not.toContain('bg-warning-surface');
      expect(bar.queryByText(/Live/, visibleOnly)).toBeNull();
      expect(descriptions(state)).toEqual([attachmentTexts[attachment].explanation]);
   });

   it.each([
      { connection: 'connecting', label: 'Connecting…' },
      { connection: 'reconnecting', label: 'Reconnecting…' },
   ] as const)('turns amber and reads $label in place of the session state while the connection is $connection', ({ connection, label }) => {
      const bar = renderBar({ connection, attachment: 'confirmed' });

      const state = chip(bar, `Connection: ${label}`);
      expect(state.querySelector('[data-chip-label]')?.textContent).toBe(label);
      expect(state.querySelector('svg.animate-spin.text-warning-mark')).not.toBeNull();
      expect([...state.classList]).toEqual(expect.arrayContaining(['bg-warning-surface', 'border-warning']));
      expect(bar.queryByText('Confirmed', visibleOnly)).toBeNull();
      expect(descriptions(state)).toEqual(['Claude session: Confirmed. Questions reach Claude.']);
   });

   it.each([
      { name: 'live', props: { attachment: 'unconfirmed' }, text: `Claude session: Unconfirmed\n${attachmentTexts.unconfirmed.explanation}` },
      { name: 'reconnecting', props: { connection: 'reconnecting', attachment: 'confirmed' }, text: 'Connection: Reconnecting…\nClaude session: Confirmed. Questions reach Claude.' },
      { name: 'reconnecting with no tour open', props: { connection: 'reconnecting', attachment: null }, text: 'Connection: Reconnecting…' },
   ] as const)('names what the chip shows on hover, and what works on a second line: $name', async ({ props, text }) => {
      const bar = renderBar(props);

      await fireEvent.pointerEnter(chip(bar), { pointerType: 'mouse' });

      await waitFor(() => expect(document.querySelector('[data-tooltip-content]')?.textContent.trim()).toBe(text));
   });

   it('opens a popover on click with the session and its explanation, the connection and what Claude is doing', async () => {
      const bar = renderBar({ attachment: 'unconfirmed', generation: writing });

      await fireEvent.click(chip(bar));

      expect(await popoverRows()).toEqual({
         session: `Unconfirmed. ${attachmentTexts.unconfirmed.explanation}`,
         connection: 'Live. Updates arrive as Claude writes.',
         generation: 'Claude is writing stop 2 of 3…',
      });
   });

   it('opens the popover on Enter, and lists a connection that is not live by its label', async () => {
      const bar = renderBar({ connection: 'reconnecting', attachment: 'none' });

      await fireEvent.keyDown(chip(bar), { key: 'Enter' });

      expect(await popoverRows()).toEqual({ session: `No session. ${attachmentTexts.none.explanation}`, connection: 'Reconnecting…' });
   });

   it('announces each change of the connection in a status region that stays mounted, and keeps the focus on the chip', async () => {
      const bar = renderBar({ connection: 'live', attachment: 'confirmed' });
      const region = document.querySelector('[data-connection-status]')!;
      const state = chip(bar);
      state.focus();
      expect(region.getAttribute('role')).toBe('status');
      expect(region.textContent).toBe('Live');

      await bar.rerender({ connection: 'reconnecting' });
      expect(document.querySelector('[data-connection-status]')).toBe(region);
      expect(region.textContent).toBe('Reconnecting…');
      expect(state.getAttribute('aria-label')).toBe('Connection: Reconnecting…');

      await bar.rerender({ connection: 'live' });
      expect(region.textContent).toBe('Live');
      expect(document.activeElement).toBe(state);
      expect(state.getAttribute('aria-label')).toBe('Claude session: Confirmed');
   });

   it('shows only a connection that is not live while no tour is open', async () => {
      const bar = renderBar({ connection: 'live', attachment: null });
      expect(bar.queryByRole('button', { name: /^(Claude session|Connection): / })).toBeNull();
      expect(document.querySelector('[data-connection-status]')?.textContent).toBe('Live');

      await bar.rerender({ connection: 'reconnecting' });
      const state = chip(bar, 'Connection: Reconnecting…');
      expect(state.getAttribute('aria-describedby')).toBeNull();
      await fireEvent.click(state);

      expect(await popoverRows()).toEqual({ connection: 'Reconnecting…' });
   });

   it.each([
      { name: 'the connection turns live with no tour open', open: { connection: 'reconnecting', attachment: null }, away: { connection: 'live' }, back: { connection: 'reconnecting' } },
      { name: 'the open tour closes', open: { connection: 'live', attachment: 'confirmed' }, away: { attachment: null }, back: { attachment: 'confirmed' } },
      { name: 'the tour closes and the chip returns while the page connects', open: { connection: 'live', attachment: 'confirmed' }, away: { attachment: null }, back: { connection: 'connecting' } },
   ] as const)('shows the chip closed again, with the focus where it was, after it left with its popover open: $name', async ({ open, away, back }) => {
      const bar = renderBar(open);
      await fireEvent.click(chip(bar));
      await screen.findByRole('dialog', { name: 'Claude session and connection' });

      await bar.rerender(away);
      expect(bar.queryByRole('button', { name: /^(Claude session|Connection): / })).toBeNull();
      expect(screen.queryByRole('dialog')).toBeNull();
      const focused = document.activeElement;
      await bar.rerender(back);

      const state = chip(bar);
      expect(state.getAttribute('aria-expanded')).toBe('false');
      expect(screen.queryByRole('dialog')).toBeNull();
      expect(document.activeElement).toBe(focused);
      await fireEvent.pointerEnter(state, { pointerType: 'mouse' });
      await waitFor(() => expect(document.querySelector('[data-tooltip-content]')).not.toBeNull());
   });

   it('shows the chip closed after it left and returned with its popover closed', async () => {
      const bar = renderBar({ connection: 'live', attachment: 'confirmed' });
      await bar.rerender({ attachment: null });
      await bar.rerender({ attachment: 'confirmed' });

      expect(chip(bar).getAttribute('aria-expanded')).toBe('false');
      expect(screen.queryByRole('dialog')).toBeNull();
   });

   it('keeps the generation line as an item of its own beside the chip', () => {
      const bar = renderBar({ attachment: 'confirmed', generation: writing });

      const line = document.querySelector('[data-generation-status]')!;
      expect(line.textContent.trim()).toBe('Claude is writing stop 2 of 3…');
      expect(chip(bar).contains(line)).toBe(false);
   });
});
