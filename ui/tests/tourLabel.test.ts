import { describe, expect, it } from 'vitest';
import { splitLabel } from '../src/lib/tourLabel';

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
