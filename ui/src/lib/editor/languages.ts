import { StreamLanguage, type Language, type StreamParser } from '@codemirror/language';
import { languageOf } from '../fileLanguage';

type LanguageLoader = () => Promise<Language>;

const legacy = <State>(parser: StreamParser<State>): Language => StreamLanguage.define(parser);

// Each loader imports its package dynamically, so the bundler splits it into a chunk that loads on first use.
// A Lezer package emits the richer tags that the syntax color roles read, so a legacy mode covers only a language that has no official CodeMirror language package.
const loaders = {
   csharp: async () => legacy((await import('@codemirror/legacy-modes/mode/clike')).csharp),
   javascript: async () => (await import('@codemirror/lang-javascript')).javascriptLanguage,
   jsx: async () => (await import('@codemirror/lang-javascript')).jsxLanguage,
   typescript: async () => (await import('@codemirror/lang-javascript')).typescriptLanguage,
   tsx: async () => (await import('@codemirror/lang-javascript')).tsxLanguage,
   json: async () => (await import('@codemirror/lang-json')).jsonLanguage,
   xml: async () => (await import('@codemirror/lang-xml')).xmlLanguage,
   yaml: async () => (await import('@codemirror/lang-yaml')).yamlLanguage,
   markdown: async () => (await import('@codemirror/lang-markdown')).markdownLanguage,
   css: async () => (await import('@codemirror/lang-css')).cssLanguage,
   html: async () => (await import('@codemirror/lang-html')).htmlLanguage,
   sql: async () => (await import('@codemirror/lang-sql')).MSSQL.language,
   cpp: async () => (await import('@codemirror/lang-cpp')).cppLanguage,
   python: async () => (await import('@codemirror/lang-python')).pythonLanguage,
   java: async () => (await import('@codemirror/lang-java')).javaLanguage,
   go: async () => (await import('@codemirror/lang-go')).goLanguage,
   rust: async () => (await import('@codemirror/lang-rust')).rustLanguage,
   // php() nests HTML around the PHP blocks, as a .php file mixes the two.
   php: async () => (await import('@codemirror/lang-php')).php().language,
   scss: async () => (await import('@codemirror/lang-sass')).sassLanguage,
   less: async () => (await import('@codemirror/lang-less')).lessLanguage,
   shell: async () => legacy((await import('@codemirror/legacy-modes/mode/shell')).shell),
   powershell: async () => legacy((await import('@codemirror/legacy-modes/mode/powershell')).powerShell),
   ruby: async () => legacy((await import('@codemirror/legacy-modes/mode/ruby')).ruby),
   swift: async () => legacy((await import('@codemirror/legacy-modes/mode/swift')).swift),
   lua: async () => legacy((await import('@codemirror/legacy-modes/mode/lua')).lua),
   perl: async () => legacy((await import('@codemirror/legacy-modes/mode/perl')).perl),
   r: async () => legacy((await import('@codemirror/legacy-modes/mode/r')).r),
   groovy: async () => legacy((await import('@codemirror/legacy-modes/mode/groovy')).groovy),
   dart: async () => legacy((await import('@codemirror/legacy-modes/mode/clike')).dart),
   objectivec: async () => legacy((await import('@codemirror/legacy-modes/mode/clike')).objectiveC),
   objectivecpp: async () => legacy((await import('@codemirror/legacy-modes/mode/clike')).objectiveCpp),
   kotlin: async () => legacy((await import('@codemirror/legacy-modes/mode/clike')).kotlin),
   scala: async () => legacy((await import('@codemirror/legacy-modes/mode/clike')).scala),
   dockerfile: async () => legacy((await import('@codemirror/legacy-modes/mode/dockerfile')).dockerFile),
   properties: async () => legacy((await import('@codemirror/legacy-modes/mode/properties')).properties),
   diff: async () => legacy((await import('@codemirror/legacy-modes/mode/diff')).diff),
   toml: async () => legacy((await import('@codemirror/legacy-modes/mode/toml')).toml),
   protobuf: async () => legacy((await import('@codemirror/legacy-modes/mode/protobuf')).protobuf),
   cmake: async () => legacy((await import('@codemirror/legacy-modes/mode/cmake')).cmake),
} satisfies Record<string, LanguageLoader>;

type LanguageName = keyof typeof loaders;

// The languages of VS Code's language IDs. An ID without one, such as makefile or ignore, shows as plain text.
// A map, so a name such as "constructor" finds nothing instead of an object's built-in member, here and in the tables below.
const byLanguageId = new Map(
   Object.entries<LanguageName>({
      c: 'cpp',
      cpp: 'cpp',
      'cuda-cpp': 'cpp',
      csharp: 'csharp',
      javascript: 'javascript',
      javascriptreact: 'jsx',
      typescript: 'typescript',
      typescriptreact: 'tsx',
      json: 'json',
      jsonc: 'json',
      jsonl: 'json',
      snippets: 'json',
      xml: 'xml',
      xsl: 'xml',
      yaml: 'yaml',
      dockercompose: 'yaml',
      markdown: 'markdown',
      // VS Code gives these Markdown files of coding agents their own IDs.
      skill: 'markdown',
      prompt: 'markdown',
      instructions: 'markdown',
      chatagent: 'markdown',
      css: 'css',
      html: 'html',
      sql: 'sql',
      python: 'python',
      java: 'java',
      go: 'go',
      rust: 'rust',
      php: 'php',
      scss: 'scss',
      less: 'less',
      shellscript: 'shell',
      powershell: 'powershell',
      ruby: 'ruby',
      swift: 'swift',
      lua: 'lua',
      perl: 'perl',
      r: 'r',
      groovy: 'groovy',
      dart: 'dart',
      'objective-c': 'objectivec',
      'objective-cpp': 'objectivecpp',
      dockerfile: 'dockerfile',
      ini: 'properties',
      properties: 'properties',
      diff: 'diff',
   }),
);

// The languages of files that VS Code's built-in associations leave without a language ID, by lowercase file name and by lowercase last extension.
const supplementaryNames = new Map<string, LanguageName>([['cmakelists.txt', 'cmake']]);
const supplementaryExtensions = new Map(
   Object.entries<LanguageName>({
      config: 'xml',
      resx: 'xml',
      kt: 'kotlin',
      kts: 'kotlin',
      scala: 'scala',
      sc: 'scala',
      toml: 'toml',
      proto: 'protobuf',
      cmake: 'cmake',
   }),
);

const loaded = new Map<LanguageName, Promise<Language | null>>();

/**
 * Returns the name of the language that highlights a file, from its path in any letter case.
 *
 * The file's VS Code language ID decides, as {@link languageOf} finds it, so the file's icon and its highlighting agree.
 * A file without a language ID takes its language from a short table of names and extensions that VS Code does not know.
 *
 * @returns The name, or null when no language covers the file, so the file shows as plain text.
 */
export function languageNameFor(path: string): LanguageName | null {
   const id = languageOf(path);
   if (id !== undefined) {
      return byLanguageId.get(id) ?? null;
   }

   const name = path.slice(path.lastIndexOf('/') + 1).toLowerCase();
   const dot = name.lastIndexOf('.');
   return supplementaryNames.get(name) ?? (dot > 0 ? supplementaryExtensions.get(name.slice(dot + 1)) : undefined) ?? null;
}

/**
 * Loads the language that highlights a file, once per language.
 *
 * @returns The language, or null when no language covers the file or its chunk failed to load. The file then shows as plain text.
 */
export function loadLanguageFor(path: string): Promise<Language | null> {
   const name = languageNameFor(path);
   if (name === null) {
      return Promise.resolve(null);
   }

   let language = loaded.get(name);
   if (!language) {
      // A failed load is not cached, so the next file in the language tries the chunk again.
      language = loaders[name]().catch(() => {
         loaded.delete(name);
         return null;
      });
      loaded.set(name, language);
   }

   return language;
}
