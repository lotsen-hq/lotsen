import { defineProject, mergeConfig } from 'vitest/config';
import viteConfig from './vite.config.ts';

// The pure modules and the rendering tests on golden fixtures, with no backend.
export default mergeConfig(viteConfig, defineProject({ test: { name: 'unit', include: ['src/**/*.test.ts', 'tests/**/*.test.ts'], exclude: ['tests/app/**'] } }));
