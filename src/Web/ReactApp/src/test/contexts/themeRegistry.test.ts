/**
 * Theme registry consistency.
 *
 * PrintFarmer previously kept five independent theme lists: the `Theme` union,
 * the `toggleTheme` cycle, `ThemeToggle`'s options and their order,
 * `ThemeSwitcher`'s
 * `THEME_OPTIONS`, and the `VALID` array in the boot script in `index.html`.
 * They drifted. Three themes (`forge`, `github-dark`, `printfarmer-dark`) had
 * stylesheets and picker entries but no design-system tokens, so their colours
 * never painted; three real themes (`ratos`, `voron`, `farm`) were missing from
 * the boot script and so flashed as `dark` on every load until React hydrated.
 *
 * None of that was visible to the type checker, because each list was
 * independently well-typed. These tests exist to make drift fail loudly.
 */
import { describe, it, expect } from 'vitest';
import fs from 'node:fs';
import path from 'node:path';
import { SELECTABLE_THEMES } from '@/design-system/themes/registry';
import {
  cssFacts,
  hasThemeSelector,
  isPrintOnlyStylesheet,
  NON_SCREEN_STYLESHEETS,
} from '../features/admin/themeSafetyScanner';

const appRoot = path.resolve(__dirname, '../..', '..');
const read = (p: string) => fs.readFileSync(path.join(appRoot, p), 'utf8');

describe('theme registry', () => {
  it('has a design-system stylesheet for every selectable theme', () => {
    const dir = path.join(appRoot, 'src/design-system/themes');
    const files = fs.readdirSync(dir);
    for (const theme of SELECTABLE_THEMES) {
      expect(files, `missing design-system/themes/${theme}.css`).toContain(`${theme}.css`);
    }
  });

  it('declares every selectable theme in the index.css import list', () => {
    const indexCss = read('src/index.css');
    for (const theme of SELECTABLE_THEMES) {
      expect(indexCss, `${theme}.css is never imported`).toContain(`themes/${theme}.css`);
    }
  });

  it('lists every selectable theme in the index.html boot script', () => {
    // A theme missing here still works, but flashes `dark` until hydration —
    // which is exactly the bug this guards against, and is invisible in tests
    // that only exercise React.
    const html = read('index.html');
    const match = html.match(/var VALID = \[([^\]]*)\]/);
    expect(match, 'boot script VALID array not found in index.html').toBeTruthy();
    const valid = match![1].split(',').map((s) => s.trim().replace(/['"]/g, '')).filter(Boolean);
    expect([...valid].sort()).toEqual([...SELECTABLE_THEMES].sort());
  });

  it('offers every selectable theme in the settings ThemeSwitcher', () => {
    const src = read('src/common/components/ThemeSwitcher.tsx');
    const code = src.replace(/\/\*[\s\S]*?\*\//g, '').replace(/^\s*\/\/.*$/gm, '');
    const ids = [...code.matchAll(/^\s*id: '([a-z-]+)',$/gm)].map((m) => m[1]);
    expect([...ids].sort()).toEqual([...SELECTABLE_THEMES].sort());
  });

  it('lists every selectable theme in ThemeToggle, in registry order', () => {
    // ThemeToggle's array drives keyboard cycling, so unlike ThemeSwitcher the
    // ORDER is load-bearing, not just the membership. `system` is appended last
    // and is not a selectable theme.
    //
    // This assertion previously did not exist, even though both this file's
    // header and a comment in ThemeToggle.tsx claimed it did. An unenforced
    // claim of enforcement is worse than no claim: it is exactly what let the
    // registration points drift apart in the first place.
    const src = read('src/common/components/ThemeToggle.tsx');
    // Strip comments first. A commented-out entry still matches the value
    // pattern, so without this the guard would pass while the theme was
    // missing from the UI — the same "enforcement that does not enforce"
    // failure this test exists to prevent.
    const code = src.replace(/\/\*[\s\S]*?\*\//g, '').replace(/^\s*\/\/.*$/gm, '');
    const values = [...code.matchAll(/\{ value: '([a-z-]+)', label:/g)].map((m) => m[1]);
    expect(values).toEqual([...SELECTABLE_THEMES, 'system']);
  });

  it('does not reference retired themes anywhere in src', () => {
    // `github-dark` and `printfarmer-dark` had stylesheets that could never win
    // the cascade: they lived in layer(base) while the design-system themes are
    // unlayered, so they rendered the `dark` palette.
    //
    // `forge` is NOT retired. It carried plain rules (a copper glow on headings
    // and progress bars) that nothing competed with, and an unopposed layered
    // rule paints normally — so forge really did look different. It was
    // migrated to the design system rather than dropped.
    const retired = ['github-dark', 'printfarmer-dark'];
    // Files allowed to name them, and why. Anything else is a resurrection.
    const allowed = new Set([
      'src/design-system/themes/registry.ts',     // declares the migration map
      'src/test/contexts/ThemeContext.test.tsx',  // tests the migration
      'src/test/contexts/themeRegistry.test.ts',  // this file
    ]);
    const offenders: string[] = [];

    const walk = (dir: string) => {
      for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
        const full = path.join(dir, entry.name);
        if (entry.isDirectory()) {
          if (!/node_modules|dist/.test(entry.name)) walk(full);
          continue;
        }
        if (!/\.(ts|tsx|css)$/.test(entry.name)) continue;
        const rel = path.relative(appRoot, full).replace(/\\/g, '/');
        if (allowed.has(rel)) continue;
        const text = fs.readFileSync(full, 'utf8');
        for (const name of retired) {
          if (text.includes(`'${name}'`) || text.includes(`"${name}"`)) {
            offenders.push(`${rel} -> ${name}`);
          }
        }
      }
    };
    walk(path.join(appRoot, 'src'));

    expect(offenders).toEqual([]);
  });

  it('keeps every selectable theme visually distinct', () => {
    // The failure mode that hid the retired themes for so long was that they
    // resolved to a palette identical to `dark`. Comparing declared token
    // values catches a future theme file that is a copy of another.
    const palettes = new Map<string, string>();
    for (const theme of SELECTABLE_THEMES) {
      const css = read(`src/design-system/themes/${theme}.css`);
      const declarations = cssFacts([{ file: `${theme}.css`, text: css }]).declarations;
      const keys = ['--pf-bg-0', '--pf-bg-1', '--pf-accent', '--pf-text-primary'];
      const values = keys.map((k) => {
        const value = declarations.get(k);
        expect(value, `${theme}.css does not declare ${k}`).toBeTruthy();
        return value!;
      });
      palettes.set(theme, values.join('|'));
    }
    const seen = new Map<string, string>();
    for (const [theme, palette] of palettes) {
      const clash = seen.get(palette);
      expect(clash, `${theme} has the same core palette as ${clash}`).toBeUndefined();
      seen.set(palette, theme);
    }
  });

  it('declares the same token contract in every selectable theme', () => {
    // Legacy `forge` declared 97 of the 142 tokens the design-system themes
    // share, so it was missing `--pf-text-inverse`, `--pf-on-accent` and 47
    // others. A half-built theme does not fail typecheck, lint or build: it
    // just silently inherits whatever the previous theme left behind, because
    // custom properties cascade.
    const tokensOf = (theme: string) =>
      new Set(
        cssFacts([
          {
            file: `${theme}.css`,
            text: read(`src/design-system/themes/${theme}.css`),
          },
        ]).declarations.keys(),
      );

    const [reference, ...rest] = SELECTABLE_THEMES;
    const expected = tokensOf(reference);
    expect(expected.size, 'reference theme declares no tokens').toBeGreaterThan(100);

    for (const theme of rest) {
      const actual = tokensOf(theme);
      const missing = [...expected].filter((t) => !actual.has(t));
      const extra = [...actual].filter((t) => !expected.has(t));
      expect(missing, `${theme}.css is missing tokens declared by ${reference}.css`).toEqual([]);
      expect(extra, `${theme}.css declares tokens ${reference}.css does not`).toEqual([]);
    }
  });

  it('keeps theme rules out of the layered stylesheets', () => {
    // src/index.css imports src/styles/* into layer(base) but the design-system
    // themes unlayered, and layer order beats specificity. Any `[data-theme=...]`
    // token rule placed under src/styles/ is therefore silently dead — the exact
    // trap that hid three themes. Plain rules there are not dead, which makes
    // this even harder to spot by eye, so ban the selector outright.
    const offenders: string[] = [];
    const mediaOnlyStylesheets = new Set(
      [...NON_SCREEN_STYLESHEETS].map((stylesheet) => `src/${stylesheet}`),
    );
    const walk = (dir: string) => {
      if (!fs.existsSync(dir)) return;
      for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
        const full = path.join(dir, entry.name);
        if (entry.isDirectory()) {
          walk(full);
          continue;
        }
        if (!entry.name.endsWith('.css')) continue;
        const text = fs.readFileSync(full, 'utf8');
        if (hasThemeSelector(text, full)) {
          const relativePath = path.relative(appRoot, full).replace(/\\/g, '/');
          if (!mediaOnlyStylesheets.has(relativePath)) {
            offenders.push(relativePath);
          }
        }
      }
    };
    walk(path.join(appRoot, 'src/styles'));

    expect(offenders).toEqual([]);
    for (const stylesheet of mediaOnlyStylesheets) {
      const text = fs.readFileSync(path.join(appRoot, stylesheet), 'utf8');
      expect(isPrintOnlyStylesheet(text, stylesheet), `${stylesheet} must remain print-only`).toBe(true);
    }
  });

  it('imports the high-contrast mechanism file unlayered (#1297)', () => {
    // src/design-system/contrast.css must be imported the same way as the
    // design-system themes: unlayered. Wrapping it in layer(...) would make
    // [data-theme][data-contrast="high"] overrides lose to the base
    // [data-theme] declaration regardless of specificity — the exact bug
    // this issue fixes.
    //
    // Comments are stripped first and matching is anchored to actual
    // `@import` statements, not just any line mentioning the path — a
    // commented-out unlayered import followed by a real layered one would
    // otherwise slip past a naive "first matching line" check.
    const indexCss = read('src/index.css');
    const code = indexCss.replace(/\/\*[\s\S]*?\*\//g, '');
    const importStatements = [...code.matchAll(/@import\s+[^;]*design-system\/contrast\.css[^;]*;/g)].map(
      (m) => m[0],
    );
    expect(importStatements, 'contrast.css is imported exactly once').toHaveLength(1);
    expect(importStatements[0], 'contrast.css must not be imported into a layer').not.toMatch(/layer\(/);
  });

  it('keeps the high-contrast mechanism file itself unlayered (#1297)', () => {
    const contrastCss = read('src/design-system/contrast.css');
    // Strip comments first: the file's own documentation discusses @layer by
    // name, which would otherwise false-positive this check.
    const code = contrastCss.replace(/\/\*[\s\S]*?\*\//g, '');
    expect(code, 'contrast.css must not declare an @layer block').not.toMatch(/@layer\b/);
  });

  it('wins the cascade for a high-contrast override via getComputedStyle (#1297)', () => {
    // Proves the actual shipped mechanism file, injected verbatim, resolves a
    // [data-theme="dark"][data-contrast="high"] override correctly against an
    // unlayered base declaration — mirroring how src/design-system/themes/*.css
    // declares real tokens. This is the regression test the issue calls for.
    const contrastCss = read('src/design-system/contrast.css');

    const base = document.createElement('style');
    base.textContent = `
      [data-theme='dark'] {
        --pf-contrast-mechanism-canary: base-value;
      }
    `;
    const override = document.createElement('style');
    override.textContent = contrastCss;

    document.head.appendChild(base);
    document.head.appendChild(override);

    try {
      document.documentElement.setAttribute('data-theme', 'dark');

      document.documentElement.setAttribute('data-contrast', 'normal');
      expect(
        getComputedStyle(document.documentElement).getPropertyValue('--pf-contrast-mechanism-canary').trim(),
      ).toBe('base-value');

      document.documentElement.setAttribute('data-contrast', 'high');
      expect(
        getComputedStyle(document.documentElement).getPropertyValue('--pf-contrast-mechanism-canary').trim(),
      ).toBe('cascade-mechanism-verified');
    } finally {
      document.head.removeChild(base);
      document.head.removeChild(override);
      document.documentElement.removeAttribute('data-theme');
      document.documentElement.removeAttribute('data-contrast');
    }
  });

  it('would regress if the mechanism file were wrapped in a layer (#1297)', () => {
    // Documents WHY the static "must not contain @layer" check above matters:
    // reproduces the historical bug pattern by wrapping the real contrast.css
    // content in an explicit @layer, showing the override would silently lose
    // to an unlayered base declaration despite higher specificity.
    const contrastCss = read('src/design-system/contrast.css');

    const base = document.createElement('style');
    base.textContent = `
      [data-theme='dark'] {
        --pf-contrast-mechanism-canary: base-value;
      }
    `;
    const layeredOverride = document.createElement('style');
    layeredOverride.textContent = `@layer regressed {\n${contrastCss}\n}`;

    document.head.appendChild(base);
    document.head.appendChild(layeredOverride);

    try {
      document.documentElement.setAttribute('data-theme', 'dark');
      document.documentElement.setAttribute('data-contrast', 'high');

      // The layered, higher-specificity override loses to the unlayered base
      // rule — this is the bug, not the fix.
      expect(
        getComputedStyle(document.documentElement).getPropertyValue('--pf-contrast-mechanism-canary').trim(),
      ).toBe('base-value');
    } finally {
      document.head.removeChild(base);
      document.head.removeChild(layeredOverride);
      document.documentElement.removeAttribute('data-theme');
      document.documentElement.removeAttribute('data-contrast');
    }
  });

  it('rejects theme selectors outside a fully print-scoped stylesheet', () => {
    for (const selector of [
      '[data-theme]',
      '[data-theme="forge"]',
      '[data-theme~="forge"]',
      '[data-theme|="forge"]',
      '[data-theme^="forge"]',
      '[data-theme$="forge"]',
      '[data-theme*="forge"]',
      '[DATA-THEME="forge"]',
    ]) {
      expect(hasThemeSelector(`${selector} { color: red; }`), selector).toBe(true);
    }

    expect(isPrintOnlyStylesheet('@media print { [data-theme] { color: black; } }')).toBe(true);
    expect(
      isPrintOnlyStylesheet(
        '@media print { [data-theme] { color: black; } }\n[data-theme] { color: red; }',
      ),
    ).toBe(false);
  });
});
