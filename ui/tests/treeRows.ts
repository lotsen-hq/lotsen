/**
 * The parts of a file tree row in order, for comparing a row's layout.
 *
 * A part is its text, an icon is its Lucide name with "down" when the chevron is turned or "seti" and its Seti icon's name, and an empty column is ''.
 * A changed file's name and the reason under it are two parts.
 */
export function rowParts(row: HTMLElement): string[] {
   return [...row.querySelector('.row')!.children]
      .flatMap((part) => (part.classList.contains('content') ? [...part.children] : [part]))
      .map((part) => {
         if (part.classList.contains('seti')) {
            return `seti ${part.getAttribute('data-icon')}`;
         }

         const icon = part.querySelector('svg');
         if (icon === null) {
            return part.textContent.replace(/\s+/g, ' ').trim();
         }

         const name = [...icon.classList].find((name) => name.startsWith('lucide-') && name !== 'lucide-icon')!.slice('lucide-'.length);
         return icon.classList.contains('rotate-90') ? `${name} down` : name;
      });
}

/** The names of the rows that a tree marks as the open file's: by `aria-current` and by the row's look, with any row that has only one of them marked as such. */
export function currentRows(container: HTMLElement): string[] {
   return [...container.querySelectorAll<HTMLElement>('[role="treeitem"]')].flatMap((item) => {
      const announced = item.getAttribute('aria-current') === 'true';
      const shown = item.querySelector(':scope > .row')!.classList.contains('current');
      return announced || shown ? [`${item.getAttribute('aria-label')}${announced && shown ? '' : ' (half-marked)'}`] : [];
   });
}
