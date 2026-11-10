// Indentation and line width come from the repository's .editorconfig, as they do for the C#.
/** @type {import("prettier").Config} */
const config = {
   singleQuote: true,
   plugins: ['prettier-plugin-svelte', 'prettier-plugin-tailwindcss'],
   overrides: [{ files: '*.svelte', options: { parser: 'svelte' } }],
   tailwindStylesheet: './src/app.css',
};

export default config;
