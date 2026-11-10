// Writes src/seti/: VS Code's Seti file icon theme, its font and notices, and the language associations it relies on, all from microsoft/vscode at one commit.
// Run it with `node scripts/generate-seti.mjs` from ui/. It needs network access to GitHub.
import { mkdir, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const commit = '79749f565febf2216982fce236448995e0c4bde9';
const raw = (path) => `https://raw.githubusercontent.com/microsoft/vscode/${commit}/${path}`;
const out = fileURLToPath(new URL('../src/seti/', import.meta.url));
// The folders of extensions/ that VS Code's build omits: excludedExtensions in build/lib/extensions.ts.
// https://github.com/microsoft/vscode/blob/79749f565febf2216982fce236448995e0c4bde9/build/lib/extensions.ts#L318-L326
const excludedExtensions = ['copilot', 'vscode-api-tests', 'vscode-colorize-tests', 'vscode-colorize-perf-tests', 'vscode-test-resolver'];

async function fetchOk(url) {
   const response = await fetch(url);
   if (!response.ok) {
      throw new Error(`${url} answered ${response.status}.`);
   }

   return response;
}

const fetchJson = async (url) => (await fetchOk(url)).json();
const fetchBytes = async (url) => new Uint8Array(await (await fetchOk(url)).arrayBuffer());

const theme = await fetchJson(raw('extensions/theme-seti/icons/vs-seti-icon-theme.json'));
const languages = await builtInLanguageAssociations();

await mkdir(out, { recursive: true });
await writeFile(`${out}seti.woff`, await fetchBytes(raw('extensions/theme-seti/icons/seti.woff')));
await writeFile(`${out}ThirdPartyNotices.txt`, await fetchBytes(raw('extensions/theme-seti/ThirdPartyNotices.txt')));
await writeFile(`${out}LICENSE.txt`, await fetchBytes(raw('LICENSE.txt')));
await writeFile(`${out}setiTheme.ts`, themeModule(theme, languages));
console.log(`generate-seti: wrote ${out} from microsoft/vscode at ${commit}.`);

/** Returns the file names, extensions and patterns that VS Code's built-in extensions associate with a language, in the order of the extensions' folder names. */
async function builtInLanguageAssociations() {
   const listing = await fetchJson(`https://api.github.com/repos/microsoft/vscode/contents/extensions?ref=${commit}`);
   const names = listing
      .filter((entry) => entry.type === 'dir' && !excludedExtensions.includes(entry.name))
      .map((entry) => entry.name)
      .sort();
   const associations = [];
   for (const name of names) {
      const response = await fetch(raw(`extensions/${name}/package.json`));
      if (response.status === 404) {
         continue;
      }

      if (!response.ok) {
         throw new Error(`extensions/${name}/package.json answered ${response.status}.`);
      }

      for (const language of (await response.json()).contributes?.languages ?? []) {
         associations.push({ id: language.id, filenames: language.filenames ?? [], extensions: language.extensions ?? [], patterns: language.filenamePatterns ?? [] });
      }
   }

   return associations;
}

/** Returns the TypeScript module that holds the theme's icons and associations and the language associations. */
function themeModule(theme, languages) {
   const definitions = theme.iconDefinitions;
   const light = theme.light;
   // Each icon pairs a dark definition with the light definition that the light section gives the same key, or with itself when the light section lacks the key.
   const lightOf = new Map();
   const pair = (dark, lightDefinition) => {
      const known = lightOf.get(dark);
      if (known !== undefined && known !== lightDefinition) {
         throw new Error(`${dark} has the light definitions ${known} and ${lightDefinition}.`);
      }

      lightOf.set(dark, lightDefinition);
   };
   pair(theme.file, light.file);
   for (const section of ['fileNames', 'fileExtensions', 'languageIds']) {
      for (const [key, dark] of Object.entries(theme[section])) {
         if (/[/\s]/.test(key)) {
            throw new Error(`The ${section} key ${key} holds a slash or a space, which the resolver does not handle.`);
         }

         pair(dark, light[section][key] ?? dark);
      }
   }

   const iconName = (definition) => {
      if (!/^_[\w-]+$/.test(definition)) {
         throw new Error(`The definition ${definition} does not have the form _name.`);
      }

      return definition.slice(1);
   };
   const icons = [...lightOf].map(([dark, lightDefinition]) => {
      const glyph = definitions[dark].fontCharacter;
      if (definitions[lightDefinition].fontCharacter !== glyph || !/^\\[0-9A-F]{4}$/.test(glyph)) {
         throw new Error(`${dark} and ${lightDefinition} do not share one glyph of the form \\XXXX.`);
      }

      return [iconName(dark), `\\u${glyph.slice(1)}`, definitions[dark].fontColor ?? null, definitions[lightDefinition].fontColor ?? null];
   });
   const lowercaseEntries = (record) => Object.entries(record).map(([key, definition]) => [key.toLowerCase(), iconName(definition)]);

   // VS Code takes the last registration of a name or an extension, and the order of the extensions' registration is not known here, so two languages must not share one.
   const languageFileNames = new Map();
   const languageExtensions = new Map();
   const associate = (map, key, id) => {
      if (map.has(key) && map.get(key) !== id) {
         throw new Error(`The languages ${map.get(key)} and ${id} both claim ${key}.`);
      }

      map.set(key, id);
   };
   const patterns = [];
   for (const language of languages) {
      language.filenames.forEach((name) => associate(languageFileNames, name.toLowerCase(), language.id));
      language.extensions.forEach((extension) => associate(languageExtensions, extension.toLowerCase(), language.id));
      language.patterns.forEach((pattern) => patterns.push([pattern, language.id]));
   }

   // VS Code takes the longest pattern that matches.
   const sortedPatterns = patterns.sort(([a], [b]) => b.length - a.length);
   const languageIcons = Object.entries(theme.languageIds).map(([id, definition]) => [id, iconName(definition)]);

   const map = (entries) => `new Map<string, string>([\n${entries.map(([key, value]) => `   [${quote(key)}, ${quote(value)}],`).join('\n')}\n])`;
   return `// scripts/generate-seti.mjs writes this file from microsoft/vscode at ${commit}. Do not edit it.
// Seti's icons and associations come from extensions/theme-seti/icons/vs-seti-icon-theme.json, the language associations from the package.json of each built-in extension.
// Seti UI is Copyright (c) 2014 Jesse Weed, MIT License (ThirdPartyNotices.txt). VS Code is Copyright (c) Microsoft Corporation, MIT License (LICENSE.txt).

/** Seti's icons by name: the glyph in seti.woff, then its colors in the dark and the light theme, or null when the theme gives it none. */
export const icons = new Map<string, readonly [glyph: string, dark: string | null, light: string | null]>([
${icons.map(([name, glyph, ...colors]) => `   [${quote(name)}, ['${glyph}', ${colors.map((color) => (color === null ? 'null' : quote(color))).join(', ')}]],`).join('\n')}
]);

/** The icon of a file that no association matches. */
export const defaultIcon = ${quote(iconName(theme.file))};

/** The icons of file names, in lowercase. */
export const fileNames = ${map(lowercaseEntries(theme.fileNames))};

/** The icons of file extensions without the leading dot, in lowercase, such as \`spec.ts\`. */
export const fileExtensions = ${map(lowercaseEntries(theme.fileExtensions))};

/** The icons of language IDs. */
export const languageIcons = ${map(languageIcons)};

/** The language IDs of file names, in lowercase. */
export const languageFileNames = ${map([...languageFileNames])};

/** The language IDs of the endings of file names, in lowercase, such as \`.cs\` or \`.gitignore\`. */
export const languageExtensions = ${map([...languageExtensions])};

/** The language IDs of glob patterns, as expressions that match a lowercase repository path, the longest pattern first. */
export const languagePatterns: readonly (readonly [RegExp, string])[] = [
${sortedPatterns.map(([pattern, id]) => `   [${globExpression(pattern)}, ${quote(id)}], // ${pattern}`).join('\n')}
];
`;
}

/** Returns a TypeScript string literal in single quotes. */
function quote(text) {
   return `'${text.replaceAll('\\', '\\\\').replaceAll("'", "\\'")}'`;
}

/**
 * Returns a regular expression literal that matches a lowercase repository path as VS Code's glob matches the pattern, ignoring case.
 *
 * VS Code matches a pattern with a slash against the whole path, which is absolute, so such a pattern starts with `**` + `/`. It matches any other pattern against the file name.
 * Only `**` + `/`, `*` and `?` are special, and the last two never match a slash:
 * https://github.com/microsoft/vscode/blob/79749f565febf2216982fce236448995e0c4bde9/src/vs/base/common/glob.ts#L47-L62
 */
function globExpression(pattern) {
   const lower = pattern.toLowerCase();
   if (/[{}[\]]|\*\*(?!\/)/.test(lower) || (lower.includes('/') && !lower.startsWith('**/'))) {
      throw new Error(`The pattern ${pattern} uses glob syntax that this script does not translate.`);
   }

   let source = '';
   for (let i = 0; i < lower.length; i++) {
      if (lower.startsWith('**/', i)) {
         source += '(?:.*\\/)?';
         i += 2;
      } else if (lower[i] === '*') {
         source += '[^/]*';
      } else if (lower[i] === '?') {
         source += '[^/]';
      } else {
         source += /[\w-]/.test(lower[i]) ? lower[i] : `\\${lower[i]}`;
      }
   }

   return `/${lower.includes('/') ? '^' : '(?:^|\\/)'}${source}$/`;
}
