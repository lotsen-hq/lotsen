/** The licenses that may ship in the UI bundle. A build that bundles any other fails. */
const permissiveLicenses = new Set(['0BSD', 'Apache-2.0', 'BSD-2-Clause', 'BSD-3-Clause', 'ISC', 'MIT']);

/** The licenses of packages whose package.json declares none, read from the LICENSE file that each one ships. */
const undeclaredLicenses = new Map([
   ['runed', 'MIT'],
   ['svelte-toolbelt', 'MIT'],
]);

const separator = `\n\n${'-'.repeat(80)}\n\n`;

function licenseOf(dependency) {
   return dependency.license ?? undeclaredLicenses.get(dependency.name) ?? null;
}

/**
 * Returns whether a package's license may ship in the UI bundle.
 *
 * @param {{ name: string, license: string | null }} dependency The package.
 * @returns {boolean} Whether the license, or an SPDX expression of several, is permissive. A package with no known license is not allowed.
 */
export function isAllowed(dependency) {
   const license = licenseOf(dependency);
   if (!license) {
      return false;
   }

   const expression = license.replace(/[()]/g, '');
   return expression.includes(' AND ') ? expression.split(' AND ').every((part) => permissiveLicenses.has(part)) : expression.split(' OR ').some((part) => permissiveLicenses.has(part));
}

/**
 * Returns the text of the notices file.
 *
 * @param {readonly { name: string, version: string, license: string | null, licenseText: string | null }[]} dependencies The packages in the UI bundle.
 * @param {readonly string[]} fixedSections The notices that the build does not generate, in the order to show them.
 * @returns {string} The packages' notices sorted by name, then the fixed sections.
 * @throws {Error} When a package ships no license text, because a notice has to reproduce it.
 */
export function formatNotices(dependencies, fixedSections) {
   const packages = [...dependencies]
      .sort((a, b) => a.name.localeCompare(b.name) || a.version.localeCompare(b.version))
      .map((dependency) => {
         if (!dependency.licenseText) {
            throw new Error(`${dependency.name} ${dependency.version} ships no license text.`);
         }

         return `${dependency.name} ${dependency.version} (${licenseOf(dependency)})\n\n${dependency.licenseText.trim()}`;
      });
   const header = 'Lotsen is licensed under the Apache License, Version 2.0. It includes the third-party software listed here.';
   return `${[header, ...packages, ...fixedSections.map((section) => section.trim())].join(separator)}\n`;
}
