import { fireEvent, render, screen, waitFor, within } from '@testing-library/svelte';
import { describe, expect, it, vi } from 'vitest';
import DiffView from '../src/components/DiffView.svelte';
import RepositoryTree from '../src/components/RepositoryTree.svelte';
import type { KnownDirectory } from '../src/lib/viewState';
import type { DirectoryListing } from '../src/lib/contract.generated';
import { outputFixtures } from './contract/fixtures.generated';
import { rowParts } from './treeRows';

// The repository's tree and the diff view, for the listings and failures that the test host's repository cannot hold or cause: entries other than files and folders, and refusals with a reason.

function listing(entries: DirectoryListing['entries']): KnownDirectory {
   return { state: 'loaded', listing: { ...(outputFixtures.list_directory as DirectoryListing), entries } };
}

async function renderTree(listDirectory: (path: string | null) => Promise<KnownDirectory>) {
   const onopen = vi.fn();
   render(RepositoryTree, { props: { listDirectory, changed: new Map(), selectedPath: null, onopen } });
   return { tree: within(await screen.findByRole('tree', { name: 'Repository files' })), onopen };
}

describe('the repository tree', () => {
   it('tags a symbolic link and a submodule, and opens the link but not the submodule', async () => {
      const { tree, onopen } = await renderTree(async () =>
         listing([
            { path: 'lib', type: 'submodule' },
            { path: 'link', type: 'symlink' },
         ]),
      );

      const submodule = await tree.findByRole('treeitem', { name: 'lib, submodule' });
      expect(submodule.getAttribute('aria-disabled')).toBe('true');
      expect(rowParts(submodule)).toEqual(['', 'folder-git-2', 'submodule', 'lib']);
      const link = tree.getByRole('treeitem', { name: 'link, symbolic link' });
      expect(rowParts(link)).toEqual(['', 'file-symlink', 'symbolic link', 'link']);
      await fireEvent.click(within(submodule).getByText('lib'));
      await fireEvent.click(within(link).getByText('link'));

      expect(onopen.mock.calls).toEqual([['link']]);
   });

   it("shows the server's reason under a folder that could not be listed", async () => {
      const { tree } = await renderTree(async (path) =>
         path === null ? listing([{ path: 'src', type: 'directory' }]) : { state: 'failed', message: 'The folder could not be listed.', reason: 'Git could not reach the repository.' },
      );

      await fireEvent.click(within(await tree.findByRole('treeitem', { name: 'src' })).getByText('src'));

      expect(await tree.findByText('The folder could not be listed.')).toBeTruthy();
      expect(tree.getByText('Git could not reach the repository.')).toBeTruthy();
   });
});

describe('the diff view of a refused diff', () => {
   it('shows the pin that the server gives when it refuses the diff because the tour is not pinned', async () => {
      render(DiffView, { props: { path: 'a.ts', known: { state: 'notPinned', path: 'a.ts', pin: { state: 'failed', reason: 'Pin it again.' } } } });

      const alert = await screen.findByRole('alert');
      expect(alert.textContent).toContain('could not be pinned');
      expect(alert.textContent).toContain('Pin it again.');
   });

   it("shows the server's reason under a diff that could not be loaded", async () => {
      render(DiffView, { props: { path: 'a.ts', known: { state: 'failed', path: 'a.ts', message: 'The diff could not be loaded.', reason: 'Git could not reach the repository.' } } });

      const alert = await waitFor(() => screen.getByRole('alert'));
      expect(alert.textContent).toContain('The diff could not be loaded.');
      expect(alert.textContent).toContain('Git could not reach the repository.');
   });
});
