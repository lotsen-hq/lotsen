import { StreamLanguage, type Language } from '@codemirror/language';

type LanguageLoader = () => Promise<Language>;

// Each loader imports its package dynamically, so the bundler splits it into a chunk that loads on first use.
const loaders: Record<string, LanguageLoader> = {
   csharp: async () => StreamLanguage.define((await import('@codemirror/legacy-modes/mode/clike')).csharp),
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
};

// A map, so an extension such as "constructor" finds nothing instead of an object's built-in member.
const byExtension = new Map(
   Object.entries<keyof typeof loaders>({
      cs: 'csharp',
      csx: 'csharp',
      js: 'javascript',
      mjs: 'javascript',
      cjs: 'javascript',
      jsx: 'jsx',
      ts: 'typescript',
      mts: 'typescript',
      cts: 'typescript',
      tsx: 'tsx',
      json: 'json',
      xml: 'xml',
      csproj: 'xml',
      props: 'xml',
      targets: 'xml',
      config: 'xml',
      resx: 'xml',
      xaml: 'xml',
      nuspec: 'xml',
      svg: 'xml',
      yml: 'yaml',
      yaml: 'yaml',
      md: 'markdown',
      markdown: 'markdown',
      css: 'css',
      html: 'html',
      htm: 'html',
      sql: 'sql',
   }),
);

const loaded = new Map<string, Promise<Language | null>>();

/**
 * Returns the name of the language that highlights a file, from its extension in any letter case.
 *
 * @returns The name, or null when no language covers the extension, so the file shows as plain text.
 */
export function languageNameFor(path: string): string | null {
   const name = path.slice(path.lastIndexOf('/') + 1);
   const dot = name.lastIndexOf('.');
   return dot > 0 ? (byExtension.get(name.slice(dot + 1).toLowerCase()) ?? null) : null;
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
      language = loaders[name]!().catch(() => {
         loaded.delete(name);
         return null;
      });
      loaded.set(name, language);
   }

   return language;
}
