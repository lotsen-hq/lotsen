import { languageExtensions, languageFileNames, languagePatterns } from '../seti/setiTheme';

/**
 * Returns the VS Code language ID of a file, from its path in any letter case.
 *
 * VS Code finds the language by file name, then by the longest glob pattern, then by the longest ending of the name, and last by the first line, which a path does not give:
 * https://github.com/microsoft/vscode/blob/79749f565febf2216982fce236448995e0c4bde9/src/vs/editor/common/services/languagesAssociations.ts#L164-L243
 *
 * @returns The language ID, such as `csharp` or `ignore`, or undefined when no built-in association of VS Code covers the file.
 */
export function languageOf(path: string): string | undefined {
   const lowerPath = path.toLowerCase();
   const name = lowerPath.slice(lowerPath.lastIndexOf('/') + 1);
   const byName = languageFileNames.get(name);
   if (byName !== undefined) {
      return byName;
   }

   const byPattern = languagePatterns.find(([pattern]) => pattern.test(lowerPath));
   if (byPattern !== undefined) {
      return byPattern[1];
   }

   // A language's extension is any ending of the name, such as .cs or .gitignore, and the longest one wins.
   for (let start = 0; start < name.length; start++) {
      const byExtension = languageExtensions.get(name.slice(start));
      if (byExtension !== undefined) {
         return byExtension;
      }
   }

   return undefined;
}
