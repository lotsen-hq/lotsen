import FileSymlink from '@lucide/svelte/icons/file-symlink';
import FolderGit2 from '@lucide/svelte/icons/folder-git-2';
import type { Attachment } from 'svelte/attachments';
import { defaultIcon, fileExtensions, fileNames, icons, languageExtensions, languageFileNames, languageIcons, languagePatterns } from '../seti/setiTheme';
import type { RepositoryEntryType } from './types';

/** An icon of VS Code's Seti file icon theme: its name in the theme, its glyph in the Seti font, and its colors in the dark and the light theme. */
export interface SetiIcon {
   readonly name: string;
   readonly glyph: string;
   readonly dark: string;
   readonly light: string;
}

/** The icon of a file row: Seti's icon for the file's name, or a Lucide icon for a symbolic link or a submodule. */
export type FileIcon = { readonly kind: 'seti'; readonly icon: SetiIcon } | { readonly kind: 'lucide'; readonly component: typeof FileSymlink };

/** Returns the icon of a file row. */
export function fileIcon(path: string, entry: RepositoryEntryType = 'file'): FileIcon {
   if (entry === 'symlink') {
      return { kind: 'lucide', component: FileSymlink };
   }

   if (entry === 'submodule') {
      return { kind: 'lucide', component: FolderGit2 };
   }

   return { kind: 'seti', icon: setiIcon(path) };
}

/**
 * Returns the Seti icon that VS Code shows for a file: by the file's name, else by its longest extension in the theme, else by its language, else the default icon.
 *
 * VS Code compares names and extensions in lowercase, and the theme's rules for names are more specific than those for extensions, which beat those for languages:
 * https://github.com/microsoft/vscode/blob/79749f565febf2216982fce236448995e0c4bde9/src/vs/workbench/services/themes/browser/fileIconThemeData.ts#L335-L382
 * It finds the language by file name, then by the longest glob pattern, then by the longest ending of the name, and last by the first line, which a path does not give:
 * https://github.com/microsoft/vscode/blob/79749f565febf2216982fce236448995e0c4bde9/src/vs/editor/common/services/languagesAssociations.ts#L164-L243
 */
export function setiIcon(path: string): SetiIcon {
   const lowerPath = path.toLowerCase();
   const name = lowerPath.slice(lowerPath.lastIndexOf('/') + 1);
   const matches = [fileNames.get(name), extensionIcon(name), languageIcon(lowerPath, name), defaultIcon].flatMap((match) => (match === undefined ? [] : [match]));
   const definitions = matches.map((match) => icons.get(match)!);
   // VS Code's rule for an icon sets the color only when the theme gives one, so a color comes from the next match that has one. The default icon has both.
   const color = (theme: 1 | 2) => definitions.find((definition) => definition[theme] !== null)![theme]!;
   return { name: matches[0]!, glyph: definitions[0]![0], dark: color(1), light: color(2) };
}

/** Returns an attachment that gives an element the icon's colors in the custom properties that `.seti` in app.css reads. */
export function setiColors(icon: SetiIcon): Attachment<HTMLElement> {
   return (element) => {
      // The CSP blocks style attributes but not the CSSOM.
      element.style.setProperty('--seti-dark', icon.dark);
      element.style.setProperty('--seti-light', icon.light);
   };
}

// The extensions of a name are its text after each of its dots, longest first, so a leading dot counts too. VS Code gives a name longer than 255 characters none:
// https://github.com/microsoft/vscode/blob/79749f565febf2216982fce236448995e0c4bde9/src/vs/editor/common/services/getIconClasses.ts#L62-L74
function extensionIcon(name: string): string | undefined {
   if (name.length > 255) {
      return undefined;
   }

   for (let dot = name.indexOf('.'); dot !== -1; dot = name.indexOf('.', dot + 1)) {
      const icon = fileExtensions.get(name.slice(dot + 1));
      if (icon !== undefined) {
         return icon;
      }
   }

   return undefined;
}

function languageIcon(lowerPath: string, name: string): string | undefined {
   const language = languageOf(lowerPath, name);
   return language === undefined ? undefined : languageIcons.get(language);
}

function languageOf(lowerPath: string, name: string): string | undefined {
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
