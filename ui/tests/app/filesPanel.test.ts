import { fireEvent, waitFor } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import type { World } from '../backend/backend';
import { backend, openTour, seedTour, startApp } from '../harness';
import { filesPanel, resetPageEachTest, tab, worldOf } from './appPanels';

resetPageEachTest();

const total = 'export function total(lines: number[]): number {\n   return lines.reduce((sum, line) => sum + line, 0);\n}\n';

/** The pull request's files at the base, one for each kind of change that the head makes. */
const base: Record<string, string> = {
   'src/cart.ts': 'export const cart = 1;\n',
   'src/pay.ts': 'export const pay = 1;\n',
   'README.md': '# Shop\n',
   'src/total.ts': total,
   'src/client.g.ts': 'export const client = 1;\n',
   'crlf.txt': 'one\ntwo\n',
   'src/legacy.ts': 'export const legacy = 1;\n',
   'assets/logo.bin': 'LOGO\u0000\u0001',
};

const head: Record<string, string | null> = {
   'src/cart.ts': 'export const cart = 2;\n',
   'src/pay.ts': 'export const pay = 2;\n',
   'README.md': '# Shop\n\nSells things.\n',
   'src/app/main.ts': 'export const main = 1;\n',
   'src/total.ts': null,
   'lib/total.ts': total,
   'src/client.g.ts': 'export const client = 2;\n',
   'crlf.txt': 'one\r\ntwo\r\n',
   'src/legacy.ts': null,
   'assets/logo.bin': 'LOGO\u0000\u0002',
   // Over the server's 1 MiB limit, so the list names the file large.
   'data/big.txt': `${'x'.repeat(99)}\n`.repeat(11_000),
};

// The rename's two paths make one changed file.
const fileCount = Object.keys(head).length - 1;

const kinds = backend().template('filesPanel-kinds', async (world) => {
   const seeded = await seedTour(world, { base, head });
   await seeded.claude!.call('set_plan', {
      tourId: seeded.tourId,
      chapters: [{ id: 'c1', title: 'Cart' }],
      stops: [{ title: 'Cart', kind: 'inspect', chapter: 'c1' }],
      fileMarks: [{ path: 'src/client.g.ts', mark: 'generated', reason: 'A tool writes it.' }],
   });
   await seeded.claude!.drop();
   return { tourId: seeded.tourId };
});

async function openFiles(world: World): Promise<Awaited<ReturnType<typeof filesPanel>>> {
   const { app } = await startApp(world);
   await openTour(app);
   await fireEvent.click(tab('Files'));
   const panel = await filesPanel();
   await waitFor(() => expect(panel.getByRole('tree', { name: 'Changed files' })).toBeTruthy());
   return panel;
}

const rowLabels = (panel: Awaited<ReturnType<typeof filesPanel>>) => panel.getAllByRole('treeitem').map((item) => item.getAttribute('aria-label'));

describe('the Files panel on the changed files that git reports', () => {
   it.each(['Tree', 'Flat'])("shows each kind of change, the marks, the line-endings tag and a renamed file's old path in the %s view", async (layout) => {
      const { world, seed } = await worldOf(kinds);
      const listed = (await world.reviewer.call('list_changed_files', { tourId: seed.tourId })).files;
      expect(
         listed
            .filter((file) => file.binary === true || file.large !== undefined)
            .map((file) => file.path)
            .sort(),
      ).toEqual(['assets/logo.bin', 'data/big.txt']);
      const panel = await openFiles(world);
      await fireEvent.click(panel.getByRole('button', { name: layout }));
      const name = (path: string) => (layout === 'Flat' ? path : path.slice(path.lastIndexOf('/') + 1));

      const marked = panel.getByRole('treeitem', { name: `${name('src/client.g.ts')}, Modified, generated: A tool writes it.` });
      expect(marked.textContent).toContain('generated');
      expect(panel.getByRole('treeitem', { name: 'crlf.txt, Modified, line endings only' }).textContent).toContain('line endings only');
      expect(panel.getByRole('treeitem', { name: `${name('lib/total.ts')}, Renamed from src/total.ts` }).textContent).toContain('src/total.ts →');
      expect(rowLabels(panel)).toEqual(
         expect.arrayContaining([`${name('src/legacy.ts')}, Deleted`, `${name('src/app/main.ts')}, Added`, `${name('assets/logo.bin')}, Modified`, `${name('data/big.txt')}, Added`]),
      );
      expect(panel.getAllByRole('treeitem').filter((item) => !item.dataset.id!.endsWith('/'))).toHaveLength(fileCount);
   });

   it("sets and clears a file's review mark from its checkbox", async () => {
      const { world, seed } = await worldOf(kinds);
      const panel = await openFiles(world);
      await fireEvent.click(panel.getByRole('button', { name: 'Flat' }));
      const reviewed = async () => (await world.reviewer.call('list_changed_files', { tourId: seed.tourId })).reviewedPaths;

      await fireEvent.click(panel.getByRole('treeitem', { name: 'README.md, Modified' }).querySelector('.check')!);

      await waitFor(async () => expect(await reviewed()).toEqual(['README.md']));
      await waitFor(() => expect(panel.getByText(`1 of ${fileCount} reviewed`)).toBeTruthy());

      await fireEvent.click(panel.getByRole('treeitem', { name: 'README.md, Modified' }).querySelector('.check')!);

      await waitFor(async () => expect(await reviewed()).toEqual([]));
      await waitFor(() => expect(panel.getByText(`0 of ${fileCount} reviewed`)).toBeTruthy());
   });
});
