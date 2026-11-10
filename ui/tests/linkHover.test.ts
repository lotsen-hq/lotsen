import { describe, expect, it } from 'vitest';
import { LinkHover } from '../src/lib/linkHover';
import type { CodeReference } from '../src/lib/types';

const reference = (startLine: number): CodeReference => ({ side: 'head', path: 'src/cart.ts', startLine, endLine: startLine });

function tracked() {
   const reported: (number | null)[] = [];
   const hover = new LinkHover((hovered) => reported.push(hovered?.startLine ?? null));
   return { hover, reported };
}

describe('LinkHover', () => {
   const a = {};
   const b = {};

   it('reports the pointer’s link over the focused one, and the focused one once the pointer leaves', () => {
      const { hover, reported } = tracked();

      hover.enter('focus', a, reference(1));
      hover.enter('pointer', b, reference(2));
      hover.enter('focus', b, reference(2));
      hover.leave('pointer', b);

      expect(reported).toEqual([1, 2, 2]);
   });

   it('ignores a link leaving a source that another link holds', () => {
      const { hover, reported } = tracked();
      hover.enter('pointer', a, reference(1));
      hover.enter('pointer', b, reference(2));

      hover.leave('pointer', a);
      hover.leave('focus', b);

      expect(reported).toEqual([1, 2]);
   });

   it('reports null once every source has left, and a malformed link as null', () => {
      const { hover, reported } = tracked();

      hover.enter('focus', a, null);
      hover.enter('pointer', b, reference(2));
      hover.leave('pointer', b);
      hover.leave('focus', a);

      expect(reported).toEqual([null, 2, null, null]);
   });
});
