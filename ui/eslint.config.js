import js from '@eslint/js';
import betterTailwind from 'eslint-plugin-better-tailwindcss';
import svelte from 'eslint-plugin-svelte';
import prettier from 'eslint-config-prettier';
import { defineConfig, globalIgnores } from 'eslint/config';
import globals from 'globals';
import ts from 'typescript-eslint';

// The page runs under a CSP whose style-src has no 'unsafe-inline', and only src/components/Prose.svelte inserts HTML, the prose that the server rendered.
const asText = 'Insert code and text as text nodes, never as HTML. Show prose that the server rendered with the Prose component.';
const htmlAndStyleInsertion = [
   { selector: "CallExpression[callee.property.name='setAttribute'][arguments.0.value='style']", message: 'An inline style breaks the CSP. Set a class, or a property through the CSSOM.' },
   { selector: 'AssignmentExpression[left.property.name=/^(innerHTML|outerHTML|srcdoc)$/]', message: asText },
   { selector: 'CallExpression[callee.property.name=/^(insertAdjacentHTML|setHTMLUnsafe|parseHTMLUnsafe|createContextualFragment)$/]', message: asText },
   { selector: "CallExpression[callee.property.name=/^(write|writeln)$/]:matches([callee.object.name='document'], [callee.object.property.name='document'])", message: asText },
   { selector: ':matches(SvelteAttribute, SvelteShorthandAttribute)[key.name=/^(innerHTML|outerHTML|srcdoc)$/]', message: asText },
   { selector: 'SvelteDirective[kind="Binding"][key.name.name=/^(innerHTML|outerHTML|srcdoc)$/]', message: asText },
   // A spread can carry srcdoc under any expression, so an iframe takes no spread at all.
   { selector: "SvelteSpreadAttribute Property[key.name='srcdoc'], SvelteElement[name.name='iframe'] > SvelteStartTag > SvelteSpreadAttribute", message: asText },
];

// The theme in src/app.css has no palette colors, so no-unknown-classes rejects them. Our own classes need no arbitrary values or variants, so any bracket or (--var) shorthand is an error.
const arbitrarySyntax = { pattern: '[\\[(]', message: 'Use a value or variant from the theme, or add one to src/app.css, not an arbitrary value or variant.' };

const cspBreakingImports = { name: 'bits-ui', importNames: ['Command', 'PinInput'], message: "It breaks the page's CSP: Command renders a style attribute and PinInput a <style> without the nonce." };

// A popped-out panel lives in a popout window's document, so code that can run in a panel takes its document and window from panelDocument(), or from mainPage when it means the main page.
const panelPlace = "A popped-out panel runs in another window. Use panelDocument() for the panel's document and window, or mainPage for the main page.";
const mainPageGlobals = [
   'document',
   'window',
   'requestAnimationFrame',
   'cancelAnimationFrame',
   'ResizeObserver',
   'IntersectionObserver',
   'MutationObserver',
   'getComputedStyle',
   'getSelection',
   'matchMedia',
   'navigator',
   'addEventListener',
   'removeEventListener',
   'innerWidth',
   'innerHeight',
   'devicePixelRatio',
   'scrollX',
   'scrollY',
   'visualViewport',
].map((name) => ({ name, message: panelPlace }));
const mainPageSyntax = [
   { selector: "SvelteElement[kind='special'][name.name=/^svelte:(window|document|body)$/]", message: panelPlace },
   // Svelte observes these with the main page's ResizeObserver (bindings/size.js in svelte 5.57.2), which misses resizes in a popout window.
   {
      selector: "SvelteDirective[kind='Binding'][key.name.name=/^(clientWidth|clientHeight|offsetWidth|offsetHeight|contentRect|contentBoxSize|borderBoxSize|devicePixelContentBoxSize)$/]",
      message: `${panelPlace} Observe the size with the panel window's ResizeObserver.`,
   },
];

// The pin rules, the explanation model and the components stay apart from editor geometry, so only src/lib/editor/ talks to CodeMirror.
const editorImports = { group: ['@codemirror/*'], message: 'Only src/lib/editor/ imports CodeMirror. Ask the editor through its EditorSurface.' };

export default defineConfig(
   globalIgnores(['dist/', 'demo-dist/', 'node_modules/']),
   js.configs.recommended,
   ts.configs.recommended,
   svelte.configs.recommended,
   prettier,
   svelte.configs.prettier,
   {
      languageOptions: { globals: { ...globals.browser, ...globals.node } },
      rules: {
         // TypeScript already reports undefined names.
         'no-undef': 'off',
         // A leading underscore marks a parameter that a signature needs and the body does not use.
         '@typescript-eslint/no-unused-vars': ['error', { argsIgnorePattern: '^_' }],
         'no-restricted-syntax': ['error', ...htmlAndStyleInsertion],
         'no-restricted-imports': ['error', { paths: [cspBreakingImports] }],
         'svelte/no-at-html-tags': 'error',
         'svelte/no-inline-styles': 'error',
      },
   },
   {
      files: ['src/**/*.svelte', 'src/**/*.ts', 'demo/**/*.svelte', 'demo/**/*.ts'],
      plugins: { 'better-tailwindcss': betterTailwind },
      settings: { 'better-tailwindcss': { entryPoint: 'src/app.css', detectComponentClasses: true } },
      rules: {
         'better-tailwindcss/no-unknown-classes': 'error',
         'better-tailwindcss/no-conflicting-classes': 'error',
         'better-tailwindcss/no-duplicate-classes': 'error',
         'better-tailwindcss/no-deprecated-classes': 'error',
         'better-tailwindcss/no-restricted-classes': ['error', { restrict: [arbitrarySyntax] }],
      },
   },
   {
      files: ['src/**/*.ts', 'src/**/*.svelte'],
      ignores: ['src/lib/editor/**'],
      rules: { 'no-restricted-imports': ['error', { paths: [cspBreakingImports], patterns: [editorImports] }] },
   },
   // Every file but the ones that run only on the main page, so a new panel file is covered without a change here.
   {
      files: ['src/**/*.ts', 'src/**/*.svelte'],
      ignores: ['src/main.ts', 'src/App.svelte', 'src/components/Workspace.svelte', 'src/lib/workspaceLayout.svelte.ts', 'src/lib/panelDocument.svelte.ts'],
      rules: {
         'no-restricted-globals': ['error', ...mainPageGlobals],
         'no-restricted-properties': ['error', ...['document', 'window'].map((property) => ({ object: 'globalThis', property, message: panelPlace }))],
         'no-restricted-syntax': ['error', ...htmlAndStyleInsertion, ...mainPageSyntax],
      },
   },
   // The contract's schema gives an operation without input the empty object, which json-schema-to-typescript writes as an empty interface.
   {
      files: ['src/lib/contract.generated.ts'],
      rules: { '@typescript-eslint/no-empty-object-type': 'off' },
   },
   // Prose may insert its html prop, and nothing else, as HTML.
   {
      files: ['src/components/Prose.svelte'],
      rules: {
         'svelte/no-at-html-tags': 'off',
         // Directly inside a top-level element, no block or {@const} can shadow the html prop.
         'no-restricted-syntax': [
            'error',
            ...htmlAndStyleInsertion,
            ...mainPageSyntax,
            {
               selector: "SvelteMustacheTag[kind='raw']:not(Program > SvelteElement > SvelteMustacheTag[expression.type='Identifier'][expression.name='html'])",
               message: 'Prose inserts only its html prop as HTML, directly in its root element.',
            },
         ],
      },
   },
   // The shadcn-svelte components keep the classes the CLI writes.
   {
      files: ['src/lib/components/ui/**'],
      rules: { 'better-tailwindcss/no-restricted-classes': 'off' },
   },
   {
      files: ['**/*.svelte', '**/*.svelte.ts'],
      languageOptions: {
         parserOptions: {
            projectService: true,
            extraFileExtensions: ['.svelte'],
            parser: ts.parser,
         },
      },
   },
);
