import { flushSync, mount, unmount } from 'svelte';
import { afterEach, describe, expect, it } from 'vitest';
import PanelContent from '../src/components/PanelContent.svelte';
import { PanelDocument, type DocumentPlace } from '../src/lib/panelDocument.svelte';
import PanelReader from './fixtures/PanelReader.svelte';
import { places, readsPanel } from './fixtures/PanelContentReader.svelte';

afterEach(() => {
   document.body.replaceChildren();
   places.length = 0;
});

describe('a panel document', () => {
   it('keeps its portal target in the body of the document that holds the panel, after the panel moves to another document', () => {
      const element = document.body.appendChild(document.createElement('div'));
      const panel = new PanelDocument(element);
      expect(panel.document).toBe(document);
      expect(panel.portalTarget.parentNode).toBe(document.body);

      const other = document.implementation.createHTMLDocument();
      other.body.append(element);
      panel.follow();

      expect(panel.document).toBe(other);
      expect(panel.portalTarget.parentNode).toBe(other.body);
      // A document that createHTMLDocument makes has no window, as a closed popout window's document has none.
      expect(panel.window).toBe(window);

      document.body.append(element);
      panel.follow();
      expect(panel.portalTarget.parentNode).toBe(document.body);
      panel.dispose();
      expect(panel.portalTarget.isConnected).toBe(false);
   });

   it("gives each panel's content its own panel document and portal target, though the panels share the page's context map", () => {
      const contexts = new Map<unknown, unknown>();
      const elements = [0, 1].map(() => document.body.appendChild(document.createElement('div')));
      const panels = elements.map((element) => new PanelDocument(element));

      const roots = panels.map((panel, index) => mount(PanelContent, { target: elements[index]!, props: { content: readsPanel, panel }, context: contexts }));
      flushSync();

      expect(places).toHaveLength(2);
      places.forEach((place, index) => expect(place).toBe(panels[index]));
      panels.forEach((panel) => expect(panel.portalTarget.textContent).toContain('In the panel'));
      expect(contexts.size).toBe(0);
      roots.forEach((root) => void unmount(root));
   });

   it("is the main page's document and window outside a panel", () => {
      const read: DocumentPlace[] = [];

      const root = mount(PanelReader, { target: document.body, props: { read: (place) => read.push(place) } });
      flushSync();

      expect(read).toHaveLength(1);
      expect(read[0]!.document).toBe(document);
      expect(read[0]!.window).toBe(window);
      void unmount(root);
   });
});
