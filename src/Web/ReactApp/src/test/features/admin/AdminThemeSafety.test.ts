import { describe, it, expect } from 'vitest';
import { readFileSync, readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join, resolve } from 'node:path';

/**
 * Theme safety for the admin surface.
 *
 * The admin surface is styled entirely from `--pf-*` tokens, which is what lets
 * it repaint per theme. Two things can break that silently:
 *
 *  1. A component hardcodes a colour, so one theme looks wrong and no build step
 *     complains. `local/no-hardcoded-colors` catches the common source form;
 *     this catches it in the shipped CSS of the admin surface too.
 *  2. A theme omits a token another theme defines, so a surface styled with it
 *     falls back to `unset` in that theme only. DESIGN-LANGUAGE (L814) calls for
 *     exactly this check and it had never been written.
 *
 * Note: the epic docs say "four themes". There are seven.
 */

const HERE = dirname(fileURLToPath(import.meta.url));
const THEMES_DIR = resolve(HERE, '../../../design-system/themes');

const themeFiles = readdirSync(THEMES_DIR).filter(
  (f) => f.endsWith('.css') && f !== 'base.css',
);

function declaredTokens(file: string): Set<string> {
  const css = readFileSync(join(THEMES_DIR, file), 'utf8');
  return new Set([...css.matchAll(/(--pf-[a-z0-9-]+)\s*:/g)].map((m) => m[1]));
}

describe('theme token parity (#1016)', () => {
  it('ships more than one theme', () => {
    // Without this the comparisons below would be vacuously true.
    expect(themeFiles.length).toBeGreaterThan(1);
  });

  it('finds tokens to compare', () => {
    expect(declaredTokens('dark.css').size).toBeGreaterThan(100);
  });

  it.each(themeFiles)('%s defines exactly the same tokens as dark.css', (file) => {
    const reference = declaredTokens('dark.css');
    const actual = declaredTokens(file);

    const missing = [...reference].filter((t) => !actual.has(t)).sort();
    const extra = [...actual].filter((t) => !reference.has(t)).sort();

    // Reported as names, not counts — a failure should say which token to add.
    expect({ missing, extra }).toEqual({ missing: [], extra: [] });
  });
});

describe('admin surface uses tokens, not literal colours (#1016)', () => {
  const ADMIN_GLOBS = [
    '../../../features/admin',
    '../../../features/settings',
    '../../../common/components/admin',
  ];

  /** Every .tsx under the admin surface. */
  function adminSources(): string[] {
    const out: string[] = [];
    const walk = (dir: string) => {
      for (const entry of readdirSync(dir, { withFileTypes: true })) {
        const full = join(dir, entry.name);
        if (entry.isDirectory()) {
          if (entry.name === '__tests__' || entry.name === 'node_modules') continue;
          walk(full);
        } else if (entry.name.endsWith('.tsx') && !entry.name.includes('.test.')) {
          out.push(full);
        }
      }
    };
    for (const g of ADMIN_GLOBS) walk(resolve(HERE, g));
    return out;
  }

  const sources = adminSources();

  it('has files to check', () => {
    expect(sources.length).toBeGreaterThan(30);
  });

  it('declares no literal hex colour in a className', () => {
    const offenders: string[] = [];

    for (const file of sources) {
      const src = readFileSync(file, 'utf8');
      // Tailwind arbitrary colour values: bg-[#fff], text-[#0d1117], border-[rgba(...)]
      for (const m of src.matchAll(
        /\b(?:bg|text|border|ring|fill|stroke|shadow|from|via|to)-\[(#[0-9a-fA-F]{3,8}|rgba?\([^\]]*\))\]/g,
      )) {
        offenders.push(`${file.split(/[\\/]/).slice(-2).join('/')}: ${m[0]}`);
      }
    }

    expect(offenders).toEqual([]);
  });

  it('declares no Tailwind palette colour where a token exists', () => {
    // The palette families the design system replaces outright. `pf-` utilities
    // and semantic names (white/black/transparent/current/inherit) are fine.
    const PALETTE =
      /\b(?:bg|text|border|ring|divide)-(?:slate|gray|zinc|neutral|stone|red|orange|amber|yellow|lime|green|emerald|teal|cyan|sky|blue|indigo|violet|purple|fuchsia|pink|rose)-(?:50|[1-9]00|950)\b/g;

    const offenders: string[] = [];
    for (const file of sources) {
      const src = readFileSync(file, 'utf8');
      for (const m of src.matchAll(PALETTE)) {
        offenders.push(`${file.split(/[\\/]/).slice(-2).join('/')}: ${m[0]}`);
      }
    }

    expect(offenders).toEqual([]);
  });
});
