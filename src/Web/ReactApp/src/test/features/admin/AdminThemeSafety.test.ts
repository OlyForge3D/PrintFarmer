import { describe, it, expect } from 'vitest';
import { readFileSync, readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join, resolve } from 'node:path';
import {
  collectThemeSources,
  cssFacts,
  cssFactsByTheme,
  deadThemeReferencesByTheme,
  formatThemeFinding,
  scanThemeText,
  type CssFacts,
  type RawCustomPropertyAllowance,
} from './themeSafetyScanner';

/**
 * Theme safety for the admin surface.
 *
 * The admin surface is styled entirely from `--pf-*` tokens, which is what lets
 * it repaint per theme. Two things can break that silently:
 *
 *  1. A component hardcodes a colour, so one theme looks wrong and no build step
 *     complains. `local/no-hardcoded-colors` catches the common source form;
 *     this catches it in the shipped CSS of the admin surface too.
 *  2. A theme omits a token another theme defines, so that theme silently falls
 *     back to the dark `:root` value instead of its intended palette.
 *     DESIGN-LANGUAGE (L814) calls for exactly this check.
 *
 * Note: the epic docs say "four themes". There are eight.
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
 * `--color-pf-x` is registered in the `@theme` block. All eight themes defined
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
 * #1023 / #1086 — every theme reference must resolve.
 *
 * The scanner deliberately does not enumerate colour utility prefixes. It
 * parses TS/TSX string and template literals, removes variants, modifiers,
 * important markers and negative markers, then checks every class-shaped
 * `*-pf-*` token except the documented non-colour namespace allowlist.
 *
 * Direct `var(--pf-*)` / `var(--color-pf-*)` references and arbitrary values
 * are checked through the same alias resolver independently for every theme.
 * Shared CSS facts are merged into each theme without allowing one theme's
 * declaration or alias value to mask another's failure. CSS declarations are
 * facts, not usages. Runtime-assigned bridge properties are allowed only at
 * their exact file/token sites, so the same spelling elsewhere remains
 * actionable.
 *
 * Explicit exclusions: dynamic custom-property names whose complete spelling
 * does not exist in one source literal cannot be resolved statically, and
 * JavaScript-built class names with no literal `pf-` segment cannot be inferred.
 * Both shapes must use a documented runtime bridge or a literal token segment.
 */
describe('theme references resolve to live tokens (#1023, #1086)', () => {
  const SRC = resolve(HERE, '../../..');
  const sources = collectThemeSources(SRC);
  const facts = cssFacts(sources);
  const factsByTheme = cssFactsByTheme(sources);
  const references = sources.flatMap(({ file, text }) => scanThemeText(text, file));
  const asSingleTheme = (singleFacts: CssFacts): Map<string, CssFacts> =>
    new Map([['fixture.css', singleFacts]]);

  it('finds utilities to check', () => {
    expect([...factsByTheme.keys()].sort()).toEqual([...themeFiles].sort());
    expect(references.filter((reference) => reference.kind === 'utility').length).toBeGreaterThan(50);
    expect(references.filter((reference) => reference.kind === 'arbitrary-value').length).toBeGreaterThan(10);
    expect(references.filter((reference) => reference.kind === 'custom-property').length).toBeGreaterThan(50);
  });

  it('declares --pf-success-text in every theme and maps it', () => {
    expect(facts.declarations.has('--pf-success-text')).toBe(true);
    expect(facts.mappings.has('--color-pf-success-text')).toBe(true);
    // Parity is asserted generally above; this pins the specific token #1023
    // is about, so a theme dropping it fails here by name.
    for (const file of themeFiles) {
      expect({ file, has: declaredTokens(file).has('--pf-success-text') }).toEqual({
        file,
        has: true,
      });
    }
  });

  it('detects a formerly unenumerated prefix through variants, modifiers, and negatives', () => {
    const references = scanThemeText(
      "const classes = 'dark:enabled:hover:-mask-pf-missing/50!';",
      'Mutation.ts',
    );
    const findings = deadThemeReferencesByTheme(references, factsByTheme, []);
    expect(findings).toMatchObject([
      {
        file: 'Mutation.ts',
        token: 'pf-missing',
        kind: 'utility',
        utilityPrefix: 'mask',
      },
    ]);
  });

  it('ignores utility-shaped text inside regex literals in test sources', () => {
    const references = scanThemeText(
      String.raw`const match = source.match(/className="(bg-pf-bg-\d[^"]*max-h-48[^"]*)"/);`,
      'Component.test.tsx',
    );

    expect(references).toEqual([]);
  });

  it('detects legitimate utility strings in test sources', () => {
    const references = scanThemeText(
      "expect(element).toHaveClass('bg-pf-missing');",
      'Component.test.tsx',
    );

    expect(references).toMatchObject([
      {
        file: 'Component.test.tsx',
        token: 'pf-missing',
        kind: 'utility',
        utilityPrefix: 'bg',
      },
    ]);
  });

  it('detects dead inline custom properties and arbitrary values, including ring offsets', () => {
    const references = scanThemeText(
      [
        "const style = { color: 'var(--pf-dead-inline)' };",
        "const classes = 'bg-[var(--pf-dead-arbitrary)] ring-offset-[var(--pf-dead-offset)]';",
      ].join('\n'),
      'Mutation.tsx',
    );
    expect(
      deadThemeReferencesByTheme(references, factsByTheme, []).map((finding) => finding.token),
    ).toEqual(['--pf-dead-inline', '--pf-dead-arbitrary', '--pf-dead-offset']);
  });

  it('does not treat custom-property declarations as utility usages', () => {
    const references = scanThemeText(
      ':root { --pf-dead-declaration: #fff; --pf-live-alias: var(--pf-dead-declaration); }',
      'mutation.css',
    );
    expect(references.map((reference) => reference.token)).toEqual(['--pf-dead-declaration']);
    expect(
      deadThemeReferencesByTheme(
        references,
        asSingleTheme(
          cssFacts([
            {
              file: 'mutation.css',
              text: ':root { --pf-dead-declaration: #fff; --pf-live-alias: var(--pf-dead-declaration); }',
            },
          ]),
        ),
        [],
      ),
    ).toEqual([]);
  });

  it('requires live raw custom properties to be allowlisted at their exact site', () => {
    const references = scanThemeText(
      "const style = { marginRight: 'var(--pf-runtime-inset, 0px)' };",
      'RuntimeBridge.tsx',
    );
    const allowance: RawCustomPropertyAllowance[] = [
      {
        file: 'RuntimeBridge.tsx',
        token: '--pf-runtime-inset',
        reason: 'assigned by the embedding runtime',
      },
    ];
    expect(deadThemeReferencesByTheme(references, factsByTheme, [])).toHaveLength(1);
    expect(deadThemeReferencesByTheme(references, factsByTheme, allowance)).toEqual([]);
    expect(
      deadThemeReferencesByTheme(
        scanThemeText(
          "const style = { marginRight: 'var(--pf-runtime-inset, 0px)' };",
          'DifferentSite.tsx',
        ),
        factsByTheme,
        allowance,
      ),
    ).toHaveLength(1);
  });

  it('resolves aliases transitively and rejects alias cycles', () => {
    const aliasFacts: CssFacts = {
      declarations: new Map([
        ['--pf-live', '#fff'],
        ['--pf-alias', 'var(--pf-live)'],
        ['--pf-repeated-alias', 'linear-gradient(var(--pf-live), var(--pf-live))'],
        ['--pf-cycle-a', 'var(--pf-cycle-b)'],
        ['--pf-cycle-b', 'var(--pf-cycle-a)'],
      ]),
      mappings: new Map([
        ['--color-pf-live-alias', 'var(--pf-alias)'],
        ['--color-pf-repeated-alias', 'var(--pf-repeated-alias)'],
        ['--color-pf-cycle', 'var(--pf-cycle-a)'],
      ]),
    };
    const references = scanThemeText(
      "const classes = 'bg-pf-live-alias border-pf-repeated-alias text-pf-cycle';",
      'Aliases.tsx',
    );
    expect(
      deadThemeReferencesByTheme(references, asSingleTheme(aliasFacts), []).map(
        (finding) => finding.token,
      ),
    ).toEqual(['pf-cycle']);
  });

  it('isolates alias resolution per theme without duplicating the occurrence', () => {
    const references = scanThemeText("const classes = 'bg-pf-themed';", 'Themed.tsx');
    const themedFacts = new Map<string, CssFacts>([
      [
        'healthy.css',
        {
          declarations: new Map([['--pf-themed', '#fff']]),
          mappings: new Map([['--color-pf-themed', 'var(--pf-themed)']]),
        },
      ],
      [
        'cyclic.css',
        {
          declarations: new Map([
            ['--pf-themed', 'var(--pf-cycle)'],
            ['--pf-cycle', 'var(--pf-themed)'],
          ]),
          mappings: new Map([['--color-pf-themed', 'var(--pf-themed)']]),
        },
      ],
    ]);

    const findings = deadThemeReferencesByTheme(references, themedFacts, []);
    expect(findings).toHaveLength(1);
    expect(findings[0].reason).toContain('unresolved in cyclic.css');
    expect(findings[0].reason).not.toContain('healthy.css');
  });

  it('catches a token removed from one real theme even though collapsed facts remain healthy', () => {
    const mutatedSources = sources.map((source) => {
      if (source.file !== 'design-system/themes/light.css') return source;
      const text = source.text.replace(/^\s*--pf-success-text\s*:[^;]+;\s*$/m, '');
      expect(text).not.toBe(source.text);
      return { ...source, text };
    });
    expect(cssFacts(mutatedSources).declarations.has('--pf-success-text')).toBe(true);
    const findings = deadThemeReferencesByTheme(
      references,
      cssFactsByTheme(mutatedSources),
    ).filter((finding) => finding.token === 'pf-success-text');

    expect(findings.length).toBeGreaterThan(0);
    expect(findings.every((finding) => /unresolved in light\.css$/.test(finding.reason))).toBe(true);
  });

  it('reports each occurrence once with actionable location data', () => {
    const references = scanThemeText(
      "const classes = 'bg-[var(--pf-dead)] bg-[var(--pf-dead)]';",
      'Diagnostics.tsx',
    );
    const findings = deadThemeReferencesByTheme(references, factsByTheme, []);
    expect(findings).toHaveLength(2);
    expect(new Set(findings.map(formatThemeFinding)).size).toBe(2);
    expect(formatThemeFinding(findings[0])).toMatch(
      /^Diagnostics\.tsx:1:\d+ --pf-dead \(arbitrary-value\) - /,
    );
  });

  it('has no unexplained dead theme reference in the current source tree', () => {
    expect(deadThemeReferencesByTheme(references, factsByTheme).map(formatThemeFinding)).toEqual([]);
  });
});
