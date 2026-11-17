import { describe, expect, it } from 'vitest';
import { repositoryHeading, splitLabel } from '../src/lib/tourLabel';

describe('the split of a tour label', () => {
   it('splits an Azure DevOps label into its repository and its number', () => {
      expect(splitLabel('contoso/shop/web #1234')).toEqual({ repository: 'contoso/shop/web', number: '#1234' });
   });

   it('splits a GitHub label into its repository and its number', () => {
      expect(splitLabel('octo/storefront #123')).toEqual({ repository: 'octo/storefront', number: '#123' });
   });

   it('leaves a URL whole', () => {
      expect(splitLabel('https://platform.test/web/pull/9')).toBeNull();
      expect(splitLabel('https://platform.test/web/pull/9#12')).toBeNull();
   });

   it('leaves a label whole whose "#" has no digits after it', () => {
      expect(splitLabel('octo/storefront #')).toBeNull();
      expect(splitLabel('octo/storefront #draft')).toBeNull();
      expect(splitLabel('octo/storefront #12a')).toBeNull();
   });

   it('leaves a label whole with no space before its "#"', () => {
      expect(splitLabel('octo/storefront#123')).toBeNull();
   });

   it('leaves a label whole that holds only the number', () => {
      expect(splitLabel('#123')).toBeNull();
      expect(splitLabel(' #123')).toBeNull();
   });
});

describe('the heading of a repository tour', () => {
   const target = { kind: 'repository', platform: 'test', url: 'https://platform.test/shop' } as const;

   it('takes the repository from the label without its ref', () => {
      expect(repositoryHeading({ target: { ...target, ref: 'v1.2.0' }, label: 'shop@v1.2.0', title: 'Cart' })).toEqual({ title: 'Cart', repository: 'shop', ref: 'v1.2.0' });
   });

   it('has no ref for the default branch', () => {
      expect(repositoryHeading({ target, label: 'shop', title: 'Cart' })).toEqual({ title: 'Cart', repository: 'shop', ref: null });
   });

   it('keeps an @ inside the repository name', () => {
      expect(repositoryHeading({ target: { ...target, ref: 'main' }, label: 'team@shop@main', title: 'Cart' })).toEqual({ title: 'Cart', repository: 'team@shop', ref: 'main' });
   });

   it('shows the repository as the title of a summary without one', () => {
      expect(repositoryHeading({ target, label: 'shop' })?.title).toBe('shop');
   });

   it('is absent for a pull request tour', () => {
      expect(repositoryHeading({ target: { kind: 'pullRequest', platform: 'test', url: 'https://platform.test/shop/pull/1' }, label: 'shop #1' })).toBeNull();
   });
});
