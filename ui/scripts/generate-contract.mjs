// Writes src/lib/contract.generated.ts from src/lib/contract.schema.json. Run it with `npm run contract` from ui/, or `just contract` from the repository root, which writes the schema first.
import { writeContract } from './contract.mjs';

console.log(`contract: wrote ${await writeContract()}.`);
