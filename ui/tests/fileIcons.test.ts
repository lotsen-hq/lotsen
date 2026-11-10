import FileSymlink from '@lucide/svelte/icons/file-symlink';
import FolderGit2 from '@lucide/svelte/icons/folder-git-2';
import { describe, expect, it } from 'vitest';
import { fileIcon, setiIcon } from '../src/lib/fileIcons';

describe('setiIcon', () => {
   // The glyphs and colors are the iconDefinitions of vs-seti-icon-theme.json, the light ones from the definitions that end in _light.
   it.each([
      ['src/Program.cs', { name: 'c-sharp', glyph: '', dark: '#519aba', light: '#498ba7' }],
      ['README.md', { name: 'info', glyph: '', dark: '#519aba', light: '#498ba7' }],
      ['docs/guide.md', { name: 'markdown', glyph: '', dark: '#519aba', light: '#498ba7' }],
      ['package.json', { name: 'json', glyph: '', dark: '#cbcb41', light: '#b7b73b' }],
      ['.gitignore', { name: 'git', glyph: '', dark: '#41535b', light: '#3b4b52' }],
      ['notes.xyz', { name: 'default', glyph: '', dark: '#d4d7d6', light: '#bfc2c1' }],
   ])('gives %s its icon and colors from the theme', (path, icon) => {
      expect(setiIcon(path)).toEqual(icon);
   });

   it.each([
      // A file name wins over the extension and the language.
      ['ui/vite.config.ts', 'vite'],
      ['docs/README.md', 'info'],
      // The longest extension in the theme wins.
      ['src/app.spec.ts', 'typescript_1'],
      // The theme lists no .ts or .cs, so the language decides, whatever the letter case.
      ['src/app.ts', 'typescript'],
      ['Views/Home.CS', 'c-sharp'],
      ['LICENSE', 'license'],
      // A language's file name wins over its patterns, and a pattern over an extension.
      ['Dockerfile', 'docker'],
      ['deploy/docker-compose.yml', 'docker_3'],
      ['.github/workflows/ci.yml', 'yml'],
      // A language without a Seti icon gets the default icon.
      ['logs/build.log', 'default'],
      ['Makefile.unknown', 'default'],
   ])('gives %s the icon %s', (path, name) => {
      expect(setiIcon(path).name).toBe(name);
   });

   // Seti's todo icon has no color, so each theme's color comes from the next match: the markdown language, else the default icon.
   it.each([
      ['todo.md', '#519aba', '#498ba7'],
      ['docs/TODO.md', '#519aba', '#498ba7'],
      ['todo', '#d4d7d6', '#bfc2c1'],
      ['todo.txt', '#d4d7d6', '#bfc2c1'],
   ])('draws %s with the todo glyph in the colors of the next match', (path, dark, light) => {
      expect(setiIcon(path)).toEqual({ name: 'todo', glyph: '\uE096', dark, light });
   });
});

describe('fileIcon', () => {
   it('gives a symbolic link and a submodule their own Lucide icons, whatever their names', () => {
      expect(fileIcon('link.ts', 'symlink')).toEqual({ kind: 'lucide', component: FileSymlink });
      expect(fileIcon('lib', 'submodule')).toEqual({ kind: 'lucide', component: FolderGit2 });
   });

   it('gives any other file its Seti icon', () => {
      expect(fileIcon('src/Program.cs')).toEqual({ kind: 'seti', icon: setiIcon('src/Program.cs') });
   });
});
