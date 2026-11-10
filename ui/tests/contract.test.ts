import { describe, expect, it } from 'vitest';
import { readContract } from '../scripts/contract.mjs';

describe('the contract types', () => {
   it('are what the generator writes from the committed schema', async () => {
      const { committed, generated } = await readContract();
      expect(committed, 'contract.generated.ts is out of date. Run `just contract` from the repository root.').toBe(generated);
   });
});
