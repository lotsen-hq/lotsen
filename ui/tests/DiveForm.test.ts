import { fireEvent, render, screen, within } from '@testing-library/svelte';
import { describe, expect, it, vi } from 'vitest';
import DiveForm from '../src/components/DiveForm.svelte';
import { diveNeedsWords, type DiveChoice } from '../src/lib/tourActions';

const from: DiveChoice = { purpose: 'evaluate', familiarity: 'some' };

function renderForm(props: { stopTitle?: string | null; unavailable?: string | null; sending?: boolean; refusal?: string | null } = {}) {
   const onsend = vi.fn<(choice: DiveChoice, text: string) => Promise<string | null>>(async () => props.refusal ?? null);
   const onclose = vi.fn();
   const result = render(DiveForm, { props: { from, stopTitle: props.stopTitle ?? null, unavailable: props.unavailable ?? null, sending: props.sending ?? false, onsend, onclose } });
   return { ...result, onsend, onclose };
}

const option = (group: string, name: string) => within(screen.getByRole('radiogroup', { name: group })).getByRole('radio', { name });
const checked = (group: string) =>
   within(screen.getByRole('radiogroup', { name: group }))
      .getAllByRole('radio')
      .filter((radio) => radio.getAttribute('aria-checked') === 'true')
      .map((radio) => document.querySelector(`label[for="${radio.id}"]`)?.textContent);
const send = () => screen.getByRole('button', { name: 'Send to Claude' }) as HTMLButtonElement;
const words = () => screen.getByRole('textbox', { name: /^What should the dive cover\?/ }) as HTMLTextAreaElement;

describe('the dive form', () => {
   it("selects the parent's purpose and level", () => {
      renderForm();

      expect(checked('Purpose')).toEqual(['Evaluate']);
      expect(checked('Your familiarity')).toEqual(['Knows some of the code']);
   });

   it("follows the parent's purpose and level until the reviewer picks others", async () => {
      const { rerender } = renderForm();

      await fireEvent.click(option('Purpose', 'Orient'));
      await rerender({ from: { purpose: 'prepare', familiarity: 'new' } });

      expect(checked('Purpose')).toEqual(['Orient']);
      expect(checked('Your familiarity')).toEqual(['New to the code']);
   });

   it('needs words from the whole tour, and says so beside the disabled Send', async () => {
      renderForm();

      expect(send().disabled).toBe(true);
      expect(document.getElementById(send().getAttribute('aria-describedby')!)?.textContent).toBe(diveNeedsWords);
      expect(words().placeholder).toBe('');

      await fireEvent.input(words(), { target: { value: '   ' } });
      expect(send().disabled).toBe(true);

      await fireEvent.input(words(), { target: { value: 'The cache.' } });
      expect(send().disabled).toBe(false);
      expect(send().hasAttribute('aria-describedby')).toBe(false);
   });

   it('takes optional words from a stop, with a placeholder that names the stop', () => {
      renderForm({ stopTitle: 'Rounding' });

      expect(words().placeholder).toBe('Leave it empty and the dive covers the whole stop "Rounding".');
      expect(screen.getByText('Optional')).toBeTruthy();
      expect(send().disabled).toBe(false);
      expect(screen.queryByText(diveNeedsWords)).toBeNull();
   });

   it('sends the picked purpose and level with the words', async () => {
      const { onsend } = renderForm({ stopTitle: 'Rounding' });

      await fireEvent.click(option('Purpose', 'Prepare'));
      await fireEvent.click(option('Your familiarity', 'New to the code'));
      await fireEvent.input(words(), { target: { value: 'The edge cases.' } });
      await fireEvent.click(send());

      expect(onsend).toHaveBeenCalledExactlyOnceWith({ purpose: 'prepare', familiarity: 'new' }, 'The edge cases.');
   });

   it("shows the server's refusal in place", async () => {
      renderForm({ stopTitle: 'Rounding', refusal: 'The tour is not pinned to a commit yet.' });

      await fireEvent.click(send());

      expect((await screen.findByRole('alert')).textContent).toBe('The tour is not pinned to a commit yet.');
   });

   it('sends nothing while a dive is on its way or the controls close it', async () => {
      const sending = renderForm({ stopTitle: 'Rounding', sending: true });
      expect(send().getAttribute('aria-disabled')).toBe('true');
      await fireEvent.click(send());
      expect(sending.onsend).not.toHaveBeenCalled();
      sending.unmount();

      const closed = renderForm({ stopTitle: 'Rounding', unavailable: 'The tour is attached to no Claude Code session.' });
      expect(send().disabled).toBe(true);
      await fireEvent.submit(send().closest('form')!);
      expect(closed.onsend).not.toHaveBeenCalled();
   });

   it('closes on Cancel', async () => {
      const { onclose } = renderForm();

      await fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));

      expect(onclose).toHaveBeenCalledOnce();
   });
});
