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

/**
 * Vasquez #1 — a theme token that no `@theme` entry maps is invisible.
 *
 * Tailwind v4 only emits a `bg-pf-x` / `text-pf-x` / `border-pf-x` rule when
 * `--color-pf-x` is registered in the `@theme` block. All seven themes defined
 * `--pf-warning-bg` and `--pf-warning-border`, every status surface in the app
 * used `bg-pf-warning-bg`, and none of it painted: the mapping was simply
 * absent, so the class compiled to nothing. No build step complains, no lint
 * rule fires, and the element just renders transparent.
 *
 * `--color-pf-error-bg` was mapped, which is why Error badges filled and
 * Warning badges did not — the severity split this epic introduced made the
 * asymmetry visible for the first time.
 */
describe('theme tokens used by utilities are registered in @theme (Vasquez #1)', () => {
  const INDEX_CSS = resolve(HERE, '../../../index.css');
  const SRC = resolve(HERE, '../../..');

  /** `--color-pf-*` names registered in the `@theme` block. */
  function mappedColors(): Set<string> {
    const css = readFileSync(INDEX_CSS, 'utf8');
    return new Set([...css.matchAll(/(--color-pf-[a-z0-9-]+)\s*:/g)].map((m) => m[1]));
  }

  /** Every `--pf-*` token any theme declares. */
  function allThemeTokens(): Set<string> {
    const all = new Set<string>();
    for (const f of themeFiles) for (const t of declaredTokens(f)) all.add(t);
    return all;
  }

  /** `pf-*` colour utilities actually used in source. */
  function usedColorUtilities(): Set<string> {
    const found = new Set<string>();
    const walk = (dir: string) => {
      for (const entry of readdirSync(dir, { withFileTypes: true })) {
        const full = join(dir, entry.name);
        if (entry.isDirectory()) {
          if (entry.name === 'node_modules' || entry.name === 'dist') continue;
          walk(full);
          continue;
        }
        if (!/\.(tsx?|css)$/.test(entry.name)) continue;
        const text = readFileSync(full, 'utf8');
        for (const m of text.matchAll(/\b(?:bg|text|border|ring|fill|stroke)-(pf-[a-z0-9-]+)/g)) {
          found.add(m[1]);
        }
      }
    };
    walk(SRC);
    return found;
  }

  it('maps every theme token that a utility class consumes', () => {
    const mapped = mappedColors();
    const themeTokens = allThemeTokens();

    // Only tokens the themes actually define. A utility naming something no
    // theme declares is a different bug (a missing *definition*, not a missing
    // mapping) and is covered by the ratchet below.
    const unmapped = [...usedColorUtilities()]
      .filter((u) => themeTokens.has(`--${u}`))
      .filter((u) => !mapped.has(`--color-${u}`))
      .sort();

    expect(unmapped).toEqual([]);
  });
});

/**
 * #1023 — a `pf-*` colour utility can be dead in two different ways.
 *
 * For `text-pf-success-text` to paint, both halves have to be present:
 *
 *   1. `--pf-success-text` is *declared* by a stylesheet, and
 *   2. `--color-pf-success-text` is *mapped* in the `@theme` block.
 *
 * Miss the mapping and Tailwind never emits the rule. Miss the declaration and
 * it emits `color: var(--pf-success-text)`, which resolves to nothing, so the
 * element silently inherits its parent's colour. Neither failure produces a
 * build error, a lint error, or a visible crash — the element just renders in
 * the wrong colour, which is why `--pf-success-text` survived in `Badge.tsx`
 * across seven themes without anyone noticing.
 *
 * The block above deliberately only checks half of this: it excludes utilities
 * whose token no theme declares, because at the time it was written 31 such
 * utilities already existed and it was scoped to the mapping bug.
 *
 * `@theme` may alias — `--color-pf-card: var(--pf-card-bg)` — so "does the
 * token exist" has to be asked of the alias *target*, not of the utility's own
 * name. Checking the name directly reports `pf-card` and `pf-sidebar` as dead
 * when both paint correctly.
 *
 * This is the other half, written as a ratchet rather than a clean assertion.
 * The pre-existing offenders are pinned by name below and the comparison is an
 * exact set equality, so:
 *
 *   - introducing a new dead utility fails (it is not in the list), and
 *   - fixing a pinned one also fails, forcing its line to be deleted.
 *
 * The list can therefore only shrink. Burning it down is tracked separately —
 * it spans 105 call sites across features unrelated to any settings work, and
 * each one needs a human decision about which existing token it meant.
 */
describe('colour utilities name a token that exists (#1023)', () => {
  const KNOWN_DEAD = [
    'pf-accent-dark',
    'pf-background',
    'pf-background-secondary',
    'pf-bg',
    'pf-bg-3',
    'pf-bg-card',
    'pf-bg-dark',
    'pf-bg-hover',
    'pf-bg-primary',
    'pf-bg-secondary',
    'pf-bg-tertiary',
    'pf-border-hover',
    'pf-hover',
    'pf-info-text',
    'pf-input',
    'pf-muted',
    'pf-panel-hover',
    'pf-panel-secondary',
    'pf-primary',
    'pf-primary-hover',
    'pf-status-danger',
    'pf-surface',
    'pf-surface-elevated',
    'pf-surface-hover',
    'pf-surface-secondary',
    'pf-surface-tertiary',
    'pf-table-header',
    'pf-table-header-text',
    'pf-text',
    'pf-text-0',
    'pf-text-1',
  ];

  const SRC = resolve(HERE, '../../..');

  /**
   * Every `--pf-*` declaration, and every `--color-pf-*` mapping with its value.
   *
   * The mapping value matters. `@theme` may alias — `--color-pf-card:
   * var(--pf-card-bg)` — so the token that has to exist is `--pf-card-bg`, not
   * `--pf-card`. Comparing the utility's own name against `declared` marks
   * every aliased utility dead even though it paints correctly.
   */
  function cssFacts(): { declared: Set<string>; mapped: Map<string, string> } {
    const declared = new Set<string>();
    const mapped = new Map<string, string>();
    const walk = (dir: string) => {
      for (const entry of readdirSync(dir, { withFileTypes: true })) {
        const full = join(dir, entry.name);
        if (entry.isDirectory()) {
          if (entry.name === 'node_modules' || entry.name === 'dist') continue;
          walk(full);
          continue;
        }
        if (!entry.name.endsWith('.css')) continue;
        const css = readFileSync(full, 'utf8');
        for (const m of css.matchAll(/(--pf-[a-z0-9-]+)\s*:/g)) declared.add(m[1]);
        for (const m of css.matchAll(/(--color-pf-[a-z0-9-]+)\s*:([^;]*);/g)) {
          mapped.set(m[1], m[2].trim());
        }
      }
    };
    walk(SRC);
    return { declared, mapped };
  }

  /** `pf-*` colour utilities used in source, excluding this file's own examples. */
  function usedUtilities(): Set<string> {
    const found = new Set<string>();
    const walk = (dir: string) => {
      for (const entry of readdirSync(dir, { withFileTypes: true })) {
        const full = join(dir, entry.name);
        if (entry.isDirectory()) {
          if (entry.name === 'node_modules' || entry.name === 'dist') continue;
          walk(full);
          continue;
        }
        if (!/\.(tsx?|css)$/.test(entry.name)) continue;
        if (entry.name === 'AdminThemeSafety.test.ts') continue;
        const text = readFileSync(full, 'utf8');
        for (const m of text.matchAll(
          /\b(?:bg|text|border|ring|fill|stroke|divide)-(pf-[a-z0-9-]+)/g,
        )) {
          found.add(m[1]);
        }
      }
    };
    walk(SRC);
    return found;
  }

  /**
   * A utility is dead when Tailwind emits nothing for it (no `@theme` mapping),
   * or when it emits a rule whose value resolves to nothing (the mapping points
   * at a token no stylesheet declares). Aliases are followed to their target.
   */
  function deadUtilities(): string[] {
    const { declared, mapped } = cssFacts();

    const resolves = (utility: string): boolean => {
      let value = mapped.get(`--color-${utility}`);
      if (value === undefined) return false; // no rule emitted at all

      // Follow `var(--pf-x)` aliases; a literal colour always resolves.
      const seen = new Set<string>();
      let alias = /^var\((--pf-[a-z0-9-]+)\)$/.exec(value);
      while (alias) {
        const target = alias[1];
        if (seen.has(target)) return false; // cycle
        seen.add(target);
        if (declared.has(target)) return true;
        value = mapped.get(`--color-${target.slice(2)}`);
        if (value === undefined) return false;
        alias = /^var\((--pf-[a-z0-9-]+)\)$/.exec(value);
      }
      return true;
    };

    return [...usedUtilities()].filter((u) => !resolves(u)).sort();
  }

  it('finds utilities to check', () => {
    // Without this the comparison below could pass on an empty scan.
    expect(usedUtilities().size).toBeGreaterThan(50);
  });

  it('declares --pf-success-text in every theme and maps it', () => {
    const { declared, mapped } = cssFacts();
    expect(declared.has('--pf-success-text')).toBe(true);
    expect(mapped.has('--color-pf-success-text')).toBe(true);
    // Parity is asserted generally above; this pins the specific token #1023
    // is about, so a theme dropping it fails here by name.
    for (const file of themeFiles) {
      expect({ file, has: declaredTokens(file).has('--pf-success-text') }).toEqual({
        file,
        has: true,
      });
    }
  });

  it('introduces no new dead colour utility', () => {
    expect(deadUtilities()).toEqual([...KNOWN_DEAD].sort());
  });
});
