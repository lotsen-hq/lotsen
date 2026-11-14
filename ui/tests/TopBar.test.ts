import { fireEvent, render, screen, waitFor, within } from '@testing-library/svelte';
import { createRawSnippet } from 'svelte';
import { describe, expect, it, onTestFinished, vi } from 'vitest';
import TopBar from '../src/components/TopBar.svelte';
import { idleAnswerSend, type AnswerSendState } from '../src/lib/answers';
import { Settings } from '../src/lib/settings.svelte';
import type { Offer } from '../src/lib/types';
import { WorkspaceLayout } from '../src/lib/workspaceLayout.svelte';

const empty = createRawSnippet(() => ({ render: () => '<span></span>' }));
const noSession: Offer = { state: 'closed', code: 'noSessionAttached', reason: 'The tour is attached to no Claude Code session' };

const refused: AnswerSendState = { sending: false, refusal: { control: 'topBar', reason: 'The answers could not be sent.' } };

function renderBar(props: { sendAnswers?: Offer; unsentAnswers: number; answerSend?: AnswerSendState; onsendanswers?: () => Promise<string | null> }) {
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
