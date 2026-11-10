/** The files that every build holds. */
const requiredFiles = ['THIRD-PARTY-NOTICES.txt', 'app.css', 'app.js', 'favicon.svg', 'index.html'];

/**
 * Returns what keeps a build from running under the server's content security policy or fitting how it is embedded.
 *
 * @param {readonly string[]} files The paths of the build's files, relative to dist and with forward slashes.
 * @param {(path: string) => string} read Returns the text of one of the files.
 * @returns {string[]} One message per problem, or an empty list when the build has none.
 */
export function findDistProblems(files, read) {
   const problems = [];
   const missing = requiredFiles.filter((path) => !files.includes(path));
   if (missing.length > 0) {
      problems.push(`dist lacks ${missing.join(', ')}.`);
   }

   // Code split from app.js, such as the languages of the diff view, goes to chunks/ and nothing else may. app.css loads fonts, such as the file icons' font, from the root.
   const chunks = files.filter((path) => /^chunks\/[\w.-]+\.js$/.test(path));
   const fonts = files.filter((path) => /^[\w.-]+\.woff$/.test(path));
   const unexpected = files.filter((path) => !requiredFiles.includes(path) && !chunks.includes(path) && !fonts.includes(path));
   if (unexpected.length > 0) {
      problems.push(`dist holds ${unexpected.join(', ')}, beyond ${requiredFiles.join(', ')}, scripts in chunks/ and .woff fonts.`);
   }

   if (files.includes('index.html')) {
      problems.push(...findPageProblems(read('index.html')));
   }

   // A component compiled with injected CSS carries its scoped selectors, such as .tabs.svelte-1ha9w09, in the script and adds them as a <style> element without the nonce, which the policy blocks.
   // CodeMirror's style rules carry no Svelte scope, so they pass, and the diff view gives their <style> element the nonce.
   for (const script of ['app.js', ...chunks].filter((path) => files.includes(path))) {
      const injected = read(script).match(/\.svelte-[a-z0-9]+/);
      if (injected) {
         problems.push(`${script} holds component CSS (${injected[0]}) to inject at run time; it must go to app.css.`);
      }
   }

   return problems;
}

function findPageProblems(html) {
   const problems = [];
   if (!/<meta name="csp-nonce" content="__LOTSEN_CSP_NONCE__"\s*\/?>/.test(html)) {
      problems.push('index.html lost the csp-nonce meta tag with the placeholder the server replaces.');
   }

   for (const script of html.matchAll(/<script\b[^>]*>/g)) {
      if (!/\bsrc="\/[^"]+"/.test(script[0])) {
         problems.push(`index.html has an inline script, which script-src 'self' blocks: ${script[0]}`);
      }
   }

   if (/<style\b/.test(html) || /\sstyle=/.test(html)) {
      problems.push('index.html has an inline style, which the style-src nonce does not cover.');
   }

   return problems;
}
