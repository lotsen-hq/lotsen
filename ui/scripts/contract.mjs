// Generates the UI's contract types from the schema document that the server's ContractTests write from the C# types.
import { readFile, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { compile } from 'json-schema-to-typescript';

const schemaFile = 'src/lib/contract.schema.json';
const typesFile = 'src/lib/contract.generated.ts';

const banner = `// Generated from contract.schema.json by \`just contract\`. Do not edit: change the C# types, then run the recipe.`;

// The generator notes each definition's place in the schema above its type, which says nothing to a reader of the TypeScript. It may write the note after the brace that ends the type before.
const definitionNote = /(^|[ \t]+)\/\*\*\n \* This interface was referenced by `EventPayloads`'s JSON-Schema\n \* via the `definition` "\w+"\.\n \*\/\n/gm;

// A path joins as a string, because jsdom's URL, which the UI tests run with, resolves a relative URL against its page.
const at = (path) => join(import.meta.dirname, '..', path);

async function generate() {
   const types = await compile(JSON.parse(await readFile(at(schemaFile), 'utf8')), 'EventPayloads', {
      additionalProperties: false,
      bannerComment: banner,
      strictIndexSignatures: true,
      unknownAny: true,
      unreachableDefinitions: true,
      style: { singleQuote: true, tabWidth: 3, printWidth: 200 },
   });
   return types.replace(definitionNote, (_, lead) => (lead === '' ? '' : '\n'));
}

export async function readContract() {
   return { committed: await readFile(at(typesFile), 'utf8'), generated: await generate() };
}

export async function writeContract() {
   await writeFile(at(typesFile), await generate());
   return typesFile;
}
