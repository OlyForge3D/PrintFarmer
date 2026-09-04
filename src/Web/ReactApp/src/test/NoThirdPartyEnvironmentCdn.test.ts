import { describe, it, expect } from 'vitest';
import { readFileSync, globSync } from 'node:fs';
import { resolve } from 'node:path';

/**
 * Regression guard for #2405: the slicer and model viewers must not fetch
 * their 3D environment map from a third-party runtime host (drei's
 * `Environment preset="..."` resolves to a raw.githack.com CDN mirror of
 * pmndrs/drei-assets). The HDRI is bundled locally under
 * `public/assets/hdri/` and referenced via the `files` prop instead.
 */

const SRC = resolve(__dirname, '..');

describe('3D viewer environment assets stay local (#2405)', () => {
  it('contains no reference to drei-assets / githack.com third-party CDN hosts', () => {
    const files = globSync('**/*.{ts,tsx}', { cwd: SRC }).filter(
      (file) => !file.includes('__tests__') && !file.endsWith('.test.ts') && !file.endsWith('.test.tsx')
    );

    const offenders: string[] = [];
    for (const file of files) {
      const contents = readFileSync(resolve(SRC, file), 'utf8');
      if (/githack\.com|drei-assets/.test(contents)) {
        offenders.push(file);
      }
    }

    expect(offenders).toEqual([]);
  });

  it('does not use drei\'s Environment "preset" prop, which fetches from a third-party CDN', () => {
    const files = globSync('**/*.{ts,tsx}', { cwd: SRC }).filter(
      (file) => !file.includes('__tests__') && !file.endsWith('.test.ts') && !file.endsWith('.test.tsx')
    );

    const offenders: string[] = [];
    for (const file of files) {
      const contents = readFileSync(resolve(SRC, file), 'utf8');
      if (/<Environment\b[^>]*\bpreset=/.test(contents)) {
        offenders.push(file);
      }
    }

    expect(offenders).toEqual([]);
  });
});
