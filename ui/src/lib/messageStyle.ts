import { preference } from './preference';

/** How the thread view lays out the messages: chat bubbles, with the reviewer's on the right and Claude's on the left, or each message across the panel's width. */
export type MessageStyle = 'bubbles' | 'fullWidth';

/** The message styles in the order that the settings offer them, with their labels. */
export const messageStyles: readonly { value: MessageStyle; label: string }[] = [
   { value: 'bubbles', label: 'Chat bubbles' },
   { value: 'fullWidth', label: 'Full width' },
];

/** The message style that the settings name, chat bubbles when none is stored. */
export const messageStylePreference = preference<MessageStyle>(
   'lotsen.messageStyle',
   messageStyles.map((style) => style.value),
   'bubbles',
);
